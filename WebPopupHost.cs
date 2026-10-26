using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace ClaudeLocalBridge;

// Keep window.open's opener relationship intact. Navigating the opener to the
// popup URL breaks sign-in flows that complete via postMessage/window.close.
public sealed class WebPopupHost : IDisposable
{
    private readonly CoreWebView2Environment environment;
    private readonly Window owner;
    private readonly HashSet<Window> windows = [];
    private readonly List<CoreWebView2> attached = [];
    private bool disposed;
    public event Action? PopupClosed;
    public event Action? PopupFailed;
    public int OpenCount => windows.Count;
    public WebPopupHost(CoreWebView2Environment environment,Window owner)
    { this.environment=environment;this.owner=owner; }
    public void Attach(CoreWebView2 source)
    { source.NewWindowRequested+=Open;attached.Add(source); }
    private async void Open(object? sender,CoreWebView2NewWindowRequestedEventArgs args)
    {
        using var deferral=args.GetDeferral();
        args.Handled=true;
        if(disposed||windows.Count>=4)return;
        if(args.Uri!="about:blank"&&(!Uri.TryCreate(args.Uri,UriKind.Absolute,out var uri)||uri.Scheme!="https"))return;
        var view=new WebView2();
        var window=new Window
        {
            Title="Sign in to Claude — browser window",Owner=owner,Content=view,
            Width=620,Height=760,MinWidth=420,MinHeight=480,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=owner.Background,Icon=owner.Icon
        };
        windows.Add(window);
        window.Closed+=(_,_)=>
        {
            windows.Remove(window);
            if(view.CoreWebView2 is { } core){core.NewWindowRequested-=Open;attached.Remove(core);}
            view.Dispose();
            if(!disposed&&windows.Count==0)PopupClosed?.Invoke();
        };
        try
        {
            window.Show();
            // Same environment AND profile, without navigating the child first.
            // WebView2 assigns the requested URL when NewWindow is supplied.
            await view.EnsureCoreWebView2Async(environment);
            if(disposed||!windows.Contains(window))return;
            if(sender is CoreWebView2 opener)view.CoreWebView2.Profile.PreferredColorScheme=opener.Profile.PreferredColorScheme;
            Attach(view.CoreWebView2);
            view.CoreWebView2.WindowCloseRequested+=(_,_)=>window.Close();
            args.NewWindow=view.CoreWebView2;
        }
        catch(Exception)
        {
            if(windows.Contains(window))window.Close();
            if(!disposed)PopupFailed?.Invoke();
        }
    }
    public void Dispose()
    {
        disposed=true;
        foreach(var source in attached.ToArray())source.NewWindowRequested-=Open;
        attached.Clear();
        foreach(var window in windows.ToArray())window.Close();
    }
}
