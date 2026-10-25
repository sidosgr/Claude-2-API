using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
namespace ClaudeLocalBridge;

public partial class MainWindow
{
    private readonly AppUpdateClient updater=new();
    private CancellationTokenSource? updateCancellation;
    private UpdateRelease? availableUpdate;
    private string? downloadedUpdate;
    private void InitializeUpdates()
    {
        VersionText.Text="Claude web session · v"+UpdatePaths.CurrentVersion.ToString(3);
        AutoUpdateBox.IsChecked=state.Settings.CheckUpdatesOnStartup;
        try
        {
            var outcomePath=Path.Combine(UpdatePaths.UpdatesRoot(UpdatePaths.DataRoot),"update-result.json");
            if(File.Exists(outcomePath))
            {
                var outcome=JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(outcomePath));
                if(outcome?.Succeeded==false){UpdateStatusText.Text="The previous update did not finish. Your previous app copy was kept. Check again to retry.";return;}
            }
        }
        catch(Exception){ }
        if(state.Settings.CheckUpdatesOnStartup&&!Environment.GetCommandLineArgs().Contains("--install-update"))_=CheckUpdatesAsync();
    }
    private async void UpdateCheck_Click(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync();
    private async Task CheckUpdatesAsync()
    {
        if(updateCancellation is not null)return;
        using var cancellation=new CancellationTokenSource();updateCancellation=cancellation;
        downloadedUpdate=null;
        UpdateCheckButton.IsEnabled=false;UpdateCancelButton.Visibility=Visibility.Visible;
        UpdateStatusText.Text="Checking GitHub for updates…";
        try
        {
            availableUpdate=await updater.CheckAsync(UpdatePaths.CurrentVersion,cancellation.Token);downloadedUpdate=null;
            UpdateDownloadButton.Visibility=availableUpdate is null?Visibility.Collapsed:Visibility.Visible;UpdateInstallButton.Visibility=Visibility.Collapsed;
            UpdateStatusText.Text=availableUpdate is null?"No newer stable release is available.":"Version "+availableUpdate.Version+" is available. Download it to update in the app.";
        }
        catch(OperationCanceledException){UpdateStatusText.Text="Update check cancelled. You can try again.";}
        catch(UpdateException ex){UpdateStatusText.Text=ex.Message;}
        catch(Exception){UpdateStatusText.Text="Could not check for updates. Check your connection and try again.";}
        finally{updateCancellation=null;UpdateCheckButton.IsEnabled=true;UpdateCancelButton.Visibility=Visibility.Collapsed;}
    }
    private async void UpdateDownload_Click(object sender,RoutedEventArgs e)=>await DownloadUpdateAsync();
    private async Task DownloadUpdateAsync()
    {
        if(updateCancellation is not null||availableUpdate is null)return;
        using var cancellation=new CancellationTokenSource();updateCancellation=cancellation;
        UpdateCheckButton.IsEnabled=UpdateDownloadButton.IsEnabled=false;UpdateCancelButton.Visibility=UpdateProgressBar.Visibility=Visibility.Visible;
        UpdateProgressBar.Value=0;UpdateStatusText.Text="Downloading and verifying version "+availableUpdate.Version+"…";
        try
        {
            var progress=new Progress<UpdateProgress>(p=>{if(downloadedUpdate is not null||updateCancellation!=cancellation)return;UpdateProgressBar.Value=p.Percent;UpdateStatusText.Text=$"Downloading update: {p.Bytes/1048576d:F1} / {p.Total/1048576d:F1} MB ({p.Percent:F0}%)";});
            downloadedUpdate=await updater.DownloadAsync(availableUpdate,progress,cancellation.Token);
            UpdateInstallButton.Visibility=Visibility.Visible;UpdateDownloadButton.Visibility=Visibility.Collapsed;
            UpdateStatusText.Text="Version "+availableUpdate.Version+" downloaded and verified. Ready to install and restart.";
        }
        catch(OperationCanceledException){UpdateStatusText.Text="Download cancelled. The partial file was removed.";}
        catch(UpdateException ex){UpdateStatusText.Text=ex.Message;}
        catch(Exception){UpdateStatusText.Text="Download failed. Check your connection or available disk space, then retry.";}
        finally{updateCancellation=null;UpdateCheckButton.IsEnabled=UpdateDownloadButton.IsEnabled=true;UpdateCancelButton.Visibility=UpdateProgressBar.Visibility=Visibility.Collapsed;}
    }
    private async void UpdateInstall_Click(object sender,RoutedEventArgs e)=>await InstallUpdateAsync();
    private async Task InstallUpdateAsync()
    {
        if(updateCancellation is not null||availableUpdate is null||downloadedUpdate is null)return;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(45));updateCancellation=cancellation;
        UpdateInstallButton.IsEnabled=UpdateCheckButton.IsEnabled=UpdateDownloadButton.IsEnabled=false;
        try
        {
            UpdateStatusText.Text="Rechecking the package and preparing installation…";
            if(state is not null)state.Update(ReadSettings());
            var restart=server?.Running==true||Environment.GetCommandLineArgs().Contains("--start-endpoint");
            var planPath=await updater.PrepareInstallAsync(availableUpdate,downloadedUpdate,restart,cancellation.Token);
            if(server is not null)await server.DisposeAsync();
            AppUpdateClient.LaunchInstaller(planPath);Close();
        }
        catch(UpdateException ex){UpdateStatusText.Text=ex.Message;}
        catch(ArgumentException ex){UpdateStatusText.Text=ex.Message;}
        catch(Exception){UpdateStatusText.Text="Could not start installation. Your current app files were kept; retry or extract the complete package.";}
        finally{updateCancellation=null;UpdateInstallButton.IsEnabled=UpdateCheckButton.IsEnabled=UpdateDownloadButton.IsEnabled=true;}
    }
    private void UpdateCancel_Click(object sender,RoutedEventArgs e)=>updateCancellation?.Cancel();
    private async Task RunUpdateCommandAsync()
    {
        await CheckUpdatesAsync();
        if(availableUpdate is null)return;
        await DownloadUpdateAsync();
        if(downloadedUpdate is not null)await InstallUpdateAsync();
    }
    private void AutoUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(state is null)return;
        try{state.Update(state.Settings with{CheckUpdatesOnStartup=AutoUpdateBox.IsChecked==true});}
        catch(Exception){UpdateStatusText.Text="Could not save the automatic update-check preference.";}
    }
    private void ConfirmUpdateStartup()
    {
        var args=Environment.GetCommandLineArgs();var index=Array.IndexOf(args,"--update-token");
        if(index<0||index+1>=args.Length||!Regex.IsMatch(args[index+1],@"^[a-f0-9]{32}$"))return;
        var marker=UpdatePaths.StartupMarker(UpdatePaths.DataRoot,args[index+1]);UpdatePaths.RequireChild(UpdatePaths.DataRoot,marker);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker+".tmp",JsonSerializer.Serialize(new{version=UpdatePaths.CurrentVersion.ToString(3)}));File.Move(marker+".tmp",marker,true);
        UpdateStatusText.Text="Updated to version "+UpdatePaths.CurrentVersion.ToString(3)+". Your settings and session were retained.";
    }
}
