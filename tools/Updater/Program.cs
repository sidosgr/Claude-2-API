using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeLocalBridge;

var root=UpdatePaths.DataRoot;UpdatePlan? plan=null;bool parentExited=false;
try
{
    if(args.Length!=1)throw new UpdateException("invalid_plan","No update operation was supplied.");
    var planPath=Path.GetFullPath(args[0]);UpdatePaths.RequireChild(UpdatePaths.UpdatesRoot(root),planPath);
    if(new FileInfo(planPath).Length>16384)throw new UpdateException("invalid_plan","Update metadata is too large.");
    plan=JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath))??throw new UpdateException("invalid_plan","Invalid update metadata.");
    await UpdatePaths.WaitForParentAsync(plan,CancellationToken.None);
    parentExited=true;
    await new UpdateInstaller(root).ApplyAsync(plan);
    var target=Path.Combine(UpdatePaths.InstallRoot(root),UpdatePaths.MainExecutable);
    TryShortcut(target);
    await WriteOutcome(new(true,plan.Version,"updated",DateTimeOffset.UtcNow));
}
catch(Exception ex)
{
    await WriteOutcome(new(false,plan?.Version??"unknown",ex is UpdateException update?update.Code:"install_failed",DateTimeOffset.UtcNow));
    // The rollback install is preferred. A portable source copy is left untouched.
    var restored=Path.Combine(UpdatePaths.InstallRoot(root),UpdatePaths.MainExecutable);
    var original=plan?.SourceExecutable;
    var fallback=File.Exists(restored)?restored:original;
    if(parentExited&&fallback is not null&&Path.GetFileName(fallback)==UpdatePaths.MainExecutable&&File.Exists(fallback))
    {
        try{var restart=new ProcessStartInfo(fallback){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(fallback)!};if(plan?.RestartEndpoint==true)restart.ArgumentList.Add("--start-endpoint");Process.Start(restart);}catch(Exception){ }
    }
    Environment.ExitCode=1;
}
async Task WriteOutcome(UpdateOutcome result)
{
    try{Directory.CreateDirectory(UpdatePaths.UpdatesRoot(root));await File.WriteAllTextAsync(Path.Combine(UpdatePaths.UpdatesRoot(root),"update-result.json"),JsonSerializer.Serialize(result));}catch(Exception){ }
}
void TryShortcut(string executable)
{
    var thread=new Thread(()=>
    {
        object? shell=null,shortcut=null;
        try
        {
            var directory=Environment.GetFolderPath(Environment.SpecialFolder.Programs);Directory.CreateDirectory(directory);
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic automation=shell!;shortcut=automation.CreateShortcut(Path.Combine(directory,"Local Claude Bridge.lnk"));dynamic link=shortcut;
            link.TargetPath=executable;link.WorkingDirectory=Path.GetDirectoryName(executable);link.IconLocation=executable;link.Description="Local Claude Bridge";link.Save();
        }
        catch(Exception){ }
        finally{if(shortcut is not null&&Marshal.IsComObject(shortcut))Marshal.FinalReleaseComObject(shortcut);if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
    });
    thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
}
