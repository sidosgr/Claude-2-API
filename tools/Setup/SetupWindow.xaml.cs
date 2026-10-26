using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
namespace ClaudeLocalBridge;
public partial class SetupWindow:Window
{
    private bool installing;
    private readonly string root=UpdatePaths.DataRoot;
    public SetupWindow()
    {
        InitializeComponent();DestinationText.Text="Location: "+UpdatePaths.InstallRoot(root);
        Closing+=(_,e)=>{if(installing)e.Cancel=true;};
        Loaded+=async(_,_)=>{if(Environment.GetCommandLineArgs().Contains("--install"))await InstallAsync();};
    }
    private async void Install_Click(object sender,RoutedEventArgs e)=>await InstallAsync();
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private async Task InstallAsync()
    {
        if(installing)return;installing=true;InstallButton.IsEnabled=CloseButton.IsEnabled=false;Progress.Visibility=Visibility.Visible;
        try
        {
            using var metadataStream=Assembly.GetExecutingAssembly().GetManifestResourceStream("BridgePackage.json")??throw new IOException("Package metadata is missing.");
            var metadata=await JsonSerializer.DeserializeAsync<Package>(metadataStream)??throw new IOException("Package metadata is invalid.");
            var operation=Guid.NewGuid().ToString("N");var directory=Path.Combine(UpdatePaths.UpdatesRoot(root),"setup-"+operation);
            UpdatePaths.RequireChild(root,directory);Directory.CreateDirectory(directory);
            var package=Path.Combine(directory,UpdatePaths.AssetName);
            Status.Text="Preparing and verifying the bundled Windows package…";
            await using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("BridgePackage.zip")??throw new IOException("Package is missing."))
            await using(var output=new FileStream(package,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))await input.CopyToAsync(output);
            if(!await AppUpdateClient.VerifyAsync(package,metadata.Size,metadata.Sha256,CancellationToken.None))throw new IOException("Package verification failed.");
            Status.Text="Closing the existing app and installing…";
            var target=Path.Combine(UpdatePaths.InstallRoot(root),UpdatePaths.MainExecutable);
            foreach(var process in Process.GetProcessesByName("ClaudeLocalBridge"))
            {
                using(process)
                {
                    string? path;try{path=process.MainModule?.FileName;}catch{continue;}
                    if(path is null)continue;
                    if(!string.Equals(Path.GetFileName(path),UpdatePaths.MainExecutable,StringComparison.OrdinalIgnoreCase))continue;
                    // Ask the app to close; never force-kill a process holding a user's session.
                    if(!process.CloseMainWindow())throw new IOException("Close Local Claude Bridge, then retry installation.");
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            var plan=new UpdatePlan(1,operation,0,0,package,metadata.Size,metadata.Sha256,metadata.Version,Environment.ProcessPath!,false);
            await new UpdateInstaller(root).ApplyAsync(plan);
            CreateShortcut(target);
            Status.Text="Installed successfully. Local Claude Bridge is open. Your existing login and settings were retained.";
            File.WriteAllText(Path.Combine(UpdatePaths.UpdatesRoot(root),"setup-result.json"),JsonSerializer.Serialize(new{Succeeded=true,metadata.Version,Utc=DateTimeOffset.UtcNow}));
            InstallButton.Content="Installed";
        }
        catch(Exception ex)
        {
            Status.Text=ex is UpdateException update?update.Message:"Installation could not finish ("+ex.GetType().Name+"). Close the app, check disk space, and retry. Existing settings are retained.";
            InstallButton.IsEnabled=true;
            try{File.WriteAllText(Path.Combine(UpdatePaths.UpdatesRoot(root),"setup-result.json"),JsonSerializer.Serialize(new{Succeeded=false,Code=ex is UpdateException u?u.Code:ex.GetType().Name,Utc=DateTimeOffset.UtcNow}));}catch{ }
        }
        finally{installing=false;CloseButton.IsEnabled=true;Progress.Visibility=Visibility.Hidden;}
    }
    private static void CreateShortcut(string executable)
    {
        object? shell=null,shortcut=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);dynamic automation=shell!;
            shortcut=automation.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Local Claude Bridge.lnk"));dynamic link=shortcut;
            link.TargetPath=executable;link.WorkingDirectory=Path.GetDirectoryName(executable);link.IconLocation=executable;link.Description="Local Claude Bridge";link.Save();
        }
        finally{if(shortcut is not null&&Marshal.IsComObject(shortcut))Marshal.FinalReleaseComObject(shortcut);if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
    }
    private sealed record Package(string Version,long Size,string Sha256);
}
