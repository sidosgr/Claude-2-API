using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed record UpdateRelease(string Version,string Tag,Uri DownloadUrl,long Size,string Sha256);
public sealed record UpdateProgress(long Bytes,long Total) { public double Percent=>Total>0?100d*Bytes/Total:0; }
public sealed record UpdatePlan(int Schema,string OperationId,int ParentProcessId,long ParentStartTicks,string PackagePath,long Size,string Sha256,string Version,string SourceExecutable,bool RestartEndpoint);
public sealed record UpdateOutcome(bool Succeeded,string Version,string Code,DateTimeOffset Utc);
public sealed class UpdateException(string code,string message):Exception(message) { public string Code{get;}=code; }
public static class UpdatePaths
{
    public const string AssetName="LocalClaudeBridge-Windows-x64.zip";
    public const string MainExecutable="ClaudeLocalBridge.exe";
    public const string HelperExecutable="LocalClaudeBridge.Updater.exe";
    public static string DataRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalClaudeBridge");
    public static string InstallRoot(string root)=>Path.Combine(root,"App");
    public static string UpdatesRoot(string root)=>Path.Combine(root,"Updates");
    public static string StartupMarker(string root,string operation)=>Path.Combine(UpdatesRoot(root),"startup-"+operation+".json");
    public static Version Normalize(Version version)=>new(version.Major,version.Minor,Math.Max(0,version.Build));
    public static Version CurrentVersion=>Normalize(Assembly.GetExecutingAssembly().GetName().Version??new Version(0,0,0));
    public static Version ParseVersion(string version)
    {
        if(!Regex.IsMatch(version,@"^\d+\.\d+\.\d+$")||!Version.TryParse(version,out var parsed))throw new UpdateException("invalid_version","The update version is invalid.");
        return Normalize(parsed);
    }
    public static void RequireChild(string root,string target)
    {
        root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        target=Path.GetFullPath(target);
        if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UpdateException("unsafe_path","The update path is outside its owned folder.");
        for(string? current=target;current is not null && current.Length>=root.Length;current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new UpdateException("unsafe_path","Update folders cannot contain symbolic links or junctions.");
            if(current.Equals(root,StringComparison.OrdinalIgnoreCase))break;
        }
    }
    public static async Task WaitForParentAsync(UpdatePlan plan,CancellationToken cancellation)
    {
        try
        {
            using var parent=Process.GetProcessById(plan.ParentProcessId);
            if(parent.StartTime.ToUniversalTime().Ticks!=plan.ParentStartTicks)return;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch(ArgumentException) { }
        catch(InvalidOperationException) { }
    }
}
