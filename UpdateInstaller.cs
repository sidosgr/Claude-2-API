using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed class UpdateInstaller
{
    private readonly string root;
    public UpdateInstaller(string dataRoot){root=Path.GetFullPath(dataRoot);}
    public async Task ApplyAsync(UpdatePlan plan,Func<string,UpdatePlan,CancellationToken,Task<bool>>? launchAndConfirm=null,CancellationToken cancellation=default)
    {
        Validate(plan);
        if(!await AppUpdateClient.VerifyAsync(plan.PackagePath,plan.Size,plan.Sha256,cancellation))throw new UpdateException("verification_failed","The update package failed verification.");
        var updates=UpdatePaths.UpdatesRoot(root);Directory.CreateDirectory(updates);
        await using var installLock=new FileStream(Path.Combine(updates,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var stage=Path.Combine(updates,"stage-"+plan.OperationId);var backup=Path.Combine(updates,"rollback-"+plan.OperationId);var failed=Path.Combine(updates,"failed-"+plan.OperationId);var target=UpdatePaths.InstallRoot(root);
        foreach(var path in new[]{stage,backup,failed,target})UpdatePaths.RequireChild(root,path);
        if(Directory.Exists(stage)||Directory.Exists(backup)||Directory.Exists(failed))throw new UpdateException("operation_reused","This update operation has already been used.");
        Directory.CreateDirectory(stage);Extract(plan.PackagePath,stage);
        var executable=Path.Combine(stage,UpdatePaths.MainExecutable);
        if(!File.Exists(executable)||!File.Exists(Path.Combine(stage,UpdatePaths.HelperExecutable)))throw new UpdateException("incomplete_package","The update package is missing required application files.");
        var fileVersion=FileVersionInfo.GetVersionInfo(executable).FileVersion?.Split('+')[0];
        if(!Version.TryParse(fileVersion,out var version)||UpdatePaths.Normalize(version)!=UpdatePaths.ParseVersion(plan.Version))throw new UpdateException("version_mismatch","The package executable version does not match the release.");
        bool backedUp=false,installed=false;
        try
        {
            if(Directory.Exists(target)){Directory.Move(target,backup);backedUp=true;}
            Directory.Move(stage,target);installed=true;
            if(!await(launchAndConfirm??LaunchAndConfirmAsync)(Path.Combine(target,UpdatePaths.MainExecutable),plan,cancellation))throw new UpdateException("startup_failed","The updated app did not confirm a successful startup.");
        }
        catch
        {
            // Moves are restricted to owned update folders and leave failed files available for diagnosis.
            UpdatePaths.RequireChild(root,target);UpdatePaths.RequireChild(root,failed);UpdatePaths.RequireChild(root,backup);
            if(installed&&Directory.Exists(target))Directory.Move(target,failed);
            if(backedUp&&Directory.Exists(backup))Directory.Move(backup,target);
            throw;
        }
    }
    private void Validate(UpdatePlan plan)
    {
        if(plan.Schema!=1||!Regex.IsMatch(plan.OperationId??"",@"^[a-f0-9]{32}$"))throw new UpdateException("invalid_plan","The update operation is invalid.");
        UpdatePaths.ParseVersion(plan.Version);UpdatePaths.RequireChild(UpdatePaths.UpdatesRoot(root),plan.PackagePath);
        if(plan.Size<1||plan.Size>AppUpdateClient.MaximumPackageSize||!Regex.IsMatch(plan.Sha256??"",@"^[a-fA-F0-9]{64}$"))throw new UpdateException("invalid_plan","The update verification metadata is invalid.");
    }
    private static void Extract(string package,string stage)
    {
        using var archive=ZipFile.OpenRead(package);
        if(archive.Entries.Count>10000)throw new UpdateException("unsafe_package","The update contains too many files.");
        long expanded=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            var name=entry.FullName.Replace('\\','/');
            if(name.StartsWith('/')||name.Contains(':')||name.Split('/').Any(part=>part is "." or "..")||!names.Add(name.TrimEnd('/'))||((entry.ExternalAttributes>>16)&0xF000)==0xA000||(entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0)
                throw new UpdateException("unsafe_package","The update contains an unsafe file path.");
            expanded+=entry.Length;if(expanded>2L*1024*1024*1024||entry.Length>AppUpdateClient.MaximumPackageSize)throw new UpdateException("unsafe_package","The expanded update is too large.");
            var target=Path.GetFullPath(Path.Combine(stage,name));UpdatePaths.RequireChild(stage,target);
            if(name.EndsWith('/')){Directory.CreateDirectory(target);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input=entry.Open();using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);
            if(output.Length!=entry.Length)throw new UpdateException("unsafe_package","An update file was truncated.");
        }
    }
    private async Task<bool> LaunchAndConfirmAsync(string executable,UpdatePlan plan,CancellationToken cancellation)
    {
        var marker=UpdatePaths.StartupMarker(root,plan.OperationId);UpdatePaths.RequireChild(root,marker);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!};
        start.ArgumentList.Add("--update-token");start.ArgumentList.Add(plan.OperationId);if(plan.RestartEndpoint)start.ArgumentList.Add("--start-endpoint");
        using var child=Process.Start(start);if(child is null)return false;bool confirmed=false;
        try
        {
            for(var attempt=0;attempt<120;attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(child.HasExited)return false;
                if(File.Exists(marker))
                {
                    using var json=JsonDocument.Parse(await File.ReadAllTextAsync(marker,cancellation));
                    if(json.RootElement.GetProperty("version").GetString()==plan.Version){confirmed=true;return true;}
                }
                await Task.Delay(250,cancellation);
            }
        }
        catch(JsonException){ }
        finally
        {
            if(!confirmed&&!child.HasExited){child.Kill(true);await child.WaitForExitAsync(CancellationToken.None);}
        }
        return false;
    }
}
