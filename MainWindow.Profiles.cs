using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
namespace ClaudeLocalBridge;

public partial class MainWindow
{
    private WebView2 Browser=null!;
    private ProfileStore profiles=null!;
    private ProfileRouter profileRouter=null!;
    private readonly Dictionary<string,ProfileSession> profileSessions=[];
    private bool routing,changingProfile,internalTest;
    private sealed class ProfileSession(string id,WebView2 view,WebSession session,ModelCatalog catalog)
    {
        public string Id{get;}=id;
        public WebView2 View{get;}=view;
        public WebSession Session{get;}=session;
        public ModelCatalog Catalog{get;}=catalog;
        public IReadOnlyList<Organization> Organizations{get;set;}=[];
        public TaskCompletionSource Loaded{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private async Task<ProfileSession> EnsureProfileAsync(string id,CancellationToken cancellation,bool waitForPage=true)
    {
        if(profileSessions.TryGetValue(id,out var existing))return existing;
        var view=new WebView2{Visibility=Visibility.Hidden};BrowserHost.Children.Add(view);
        var catalog=new ModelCatalog();var web=new WebSession(view,catalog);var entry=new ProfileSession(id,view,web,catalog);
        profileSessions.Add(id,entry);
        web.ConnectionChanged+=()=>{if(session==web){if(!web.Ready)models.Clear();UpdateConnectionStatus();}};
        web.LoginWindowClosed+=async()=>
        {
            await Task.Delay(750);
            if(closing||web.Ready)return;
            Log("login","Sign-in window closed. Checking the profile's saved session…");
            view.CoreWebView2.Navigate("https://claude.ai/new");
        };
        web.LoginWindowFailed+=()=>Log("login","The sign-in window could not open. Try email sign-in in the internal browser.");
        view.NavigationCompleted+=async(_,args)=>
        {
            if(args.IsSuccess&&view.Source?.Host=="claude.ai")entry.Loaded.TrySetResult();
            if(!initialized||closing||busy||routing||Browser!=view)return;
            if(!args.IsSuccess){Log("browser","The page did not load. Use Open Claude to retry.");return;}
            if(view.Source?.Host!="claude.ai")return;
            if(view.Source.AbsolutePath is not "/new" and not "/login"&&!view.Source.AbsolutePath.StartsWith("/chat/"))return;
            await Guard(async()=>{await CheckSession();await ResumeEndpointAsync();});
        };
        try
        {
            await web.InitializeAsync(profiles.BrowserFolder(id));web.SetDarkMode(state.Settings.DarkMode);
            view.CoreWebView2.SourceChanged+=(_,_)=>{if(Browser==view)BrowserLocationText.Text=view.Source?.GetLeftPart(UriPartial.Path)??"Internal browser";};
            if(waitForPage)
            {
                try{await entry.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(30),cancellation);}
                catch(TimeoutException){ /* Keep the browser usable for a manual retry. */ }
            }
            return entry;
        }
        catch{profileSessions.Remove(id);BrowserHost.Children.Remove(view);web.Dispose();throw;}
    }
    private void SelectProfile(ProfileSession entry)
    {
        foreach(var other in profileSessions.Values)other.View.Visibility=other==entry?Visibility.Visible:Visibility.Hidden;
        Browser=entry.View;session=entry.Session;profiles.Activate(entry.Id);models.Replace(entry.Catalog.Snapshot);
        ModelPicker.ItemsSource=entry.Catalog.Snapshot;
        ModelPicker.SelectedItem=entry.Catalog.Snapshot.FirstOrDefault(m=>m.Id==ModelBox.Text);
        ProfilesBox.SelectedItem=null;
        changingProfile=true;
        try{OrgBox.ItemsSource=entry.Organizations;OrgBox.SelectedItem=entry.Organizations.FirstOrDefault(o=>o.Id==session.OrganizationId);}
        finally{changingProfile=false;}
        BrowserLocationText.Text=Browser.Source?.GetLeftPart(UriPartial.Path)??"Internal browser";
        RefreshProfiles();UpdateConnectionStatus();SetBrowserVisible(state.Settings.ShowBrowser);
    }
    private async Task ResumeEndpointAsync()
    {
        if(startupTestPending&&session.Ready){startupTestPending=false;await RunQuickTest();}
        else if(session.Ready&&(Environment.GetCommandLineArgs().Contains("--start-endpoint")||state.Settings.AutoStartEndpoint&&state.Settings.EndpointEnabled))await StartEndpoint();
    }
    private Task<Completion> CompleteProfilesAsync(string prompt,string model,int seconds,ThinkingOptions thinking,CancellationToken cancellation)
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(()=>CompleteProfilesAsync(prompt,model,seconds,thinking,cancellation)).Task.Unwrap();
        return RouteOnUiAsync();
        async Task<Completion> RouteOnUiAsync()
        {
            while(busy&&!internalTest){await Task.Delay(100,cancellation);}
            routing=true;
            try
            {
                return await profileRouter.CompleteAsync(state.Settings.AutomaticProfileFailover,state.Settings.ProfileCooldownMinutes,
                    async(profile,token)=>
                    {
                        ProfileSession entry;
                        try
                        {
                            entry=await EnsureProfileAsync(profile.Id,token);
                            if(!entry.Session.Ready)
                            {
                                entry.Organizations=await entry.Session.CheckAsync(token);
                                entry.Session.OrganizationId=entry.Organizations.FirstOrDefault()?.Id;
                            }
                            if(!entry.Session.Ready)return false;
                            if(entry.Catalog.Snapshot.Count==0)await entry.Session.DiscoverModelsAsync(token);
                            if(!entry.Catalog.Snapshot.Any(m=>m.Id==model))return false;
                        }
                        catch(BridgeException){return false;}
                        catch(TimeoutException){return false;}
                        SelectProfile(entry);return true;
                    },(profile,token)=>profileSessions[profile.Id].Session.CompleteWithThinkingAsync(prompt,model,seconds,thinking,token),
                    _=>{RefreshProfiles();Log("profile_limit","Claude reported a usage limit. This profile is paused; trying the next eligible profile.");},cancellation);
            }
            finally{routing=false;RefreshProfiles();UpdateConnectionStatus();}
        }
    }
    private void RefreshProfiles()
    {
        if(profiles is null)return;
        var selected=(ProfilesBox.SelectedItem as AccountProfile)?.Id??profiles.ActiveId;
        changingProfile=true;
        try
        {
            ProfilesBox.ItemsSource=profiles.Profiles;
            ProfilesBox.SelectedItem=profiles.Profiles.FirstOrDefault(p=>p.Id==selected);
            ProfileStatusText.Text="Active profile: "+profiles.Profiles.First(p=>p.Id==profiles.ActiveId).Name;
            HeaderProfileText.Text="Profile · "+profiles.Profiles.First(p=>p.Id==profiles.ActiveId).Name;
            ProfileList_Changed(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
        }
        finally{changingProfile=false;}
    }
    private static readonly RoutedEvent SelectorEvent=System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
    private void ProfileList_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)return;
        ProfileNameBox.Text=profile.Name;ProfileEnabledBox.IsChecked=profile.Enabled;
    }
    private async void AddProfile_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        var profile=profiles.Add(ProfileNameBox.Text);RevealInternalBrowser();
        var entry=await EnsureProfileAsync(profile.Id,CancellationToken.None);SelectProfile(entry);await CheckSession();
    });
    private async void RenameProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)throw new ArgumentException("Select a profile.");
        profiles.Rename(profile.Id,ProfileNameBox.Text);RefreshProfiles();return Task.CompletedTask;
    });
    private async Task UseSavedProfileAsync(string id)
    {
        var entry=await EnsureProfileAsync(id,CancellationToken.None);SelectProfile(entry);
        try{await CheckSession();}finally{if(!session.Ready)RevealInternalBrowser();}
        await ResumeEndpointAsync();
    }
    private async void UseProfile_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)throw new ArgumentException("Select a profile.");
        await UseSavedProfileAsync(profile.Id);
    });
    private async void EnableProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Enable(profile.Id,ProfileEnabledBox.IsChecked==true);
        RefreshProfiles();return Task.CompletedTask;
    });
    private async void ResetProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Reset(profile.Id);
        RefreshProfiles();return Task.CompletedTask;
    });
    private void MoveProfile(int offset)
    {
        if(busy||routing)return;
        try{if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Move(profile.Id,offset);RefreshProfiles();}
        catch(Exception){Log("profiles","The profile order could not be saved.");}
    }
    private void MoveProfileUp_Click(object sender,RoutedEventArgs e)=>MoveProfile(-1);
    private void MoveProfileDown_Click(object sender,RoutedEventArgs e)=>MoveProfile(1);
    private async void FailoverPreference_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(!int.TryParse(ProfileCooldownBox.Text,out var minutes))throw new ArgumentException("Enter a whole number of cooldown minutes.");
        state.Update(state.Settings with{AutomaticProfileFailover=ProfileFailoverBox.IsChecked==true,ProfileCooldownMinutes=minutes});
        Log("profiles","Profile failover preferences saved.");return Task.CompletedTask;
    });
    private void RevealInternalBrowser()
    {
        ShowBrowserBox.IsChecked=true;state.Update(state.Settings with{ShowBrowser=true});SetBrowserVisible(true);
    }
}
