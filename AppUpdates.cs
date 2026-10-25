using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed class AppUpdateClient:IDisposable
{
    public const long MaximumPackageSize=512L*1024*1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string root;
    public AppUpdateClient(HttpClient? client=null,string? dataRoot=null)
    {
        http=client??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromMinutes(10)};
        ownsHttp=client is null;root=Path.GetFullPath(dataRoot??UpdatePaths.DataRoot);
    }
    public async Task<UpdateRelease?> CheckAsync(Version current,CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response=await GetAsync(new Uri("https://api.github.com/repos/sidosgr/LocalClaudeBridge/releases/latest"),true,timeout.Token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)throw new UpdateException("github_rate_limit","GitHub declined the update check. Try again later.");
        if(!response.IsSuccessStatusCode)throw new UpdateException("check_failed","The update service could not be reached.");
        using var memory=new MemoryStream();await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token),memory,2*1024*1024,null,timeout.Token);
        using var json=JsonDocument.Parse(memory.ToArray());return ParseRelease(json.RootElement,current);
    }
    public static UpdateRelease? ParseRelease(JsonElement json,Version current)
    {
        try
        {
            if(json.GetProperty("draft").GetBoolean()||json.GetProperty("prerelease").GetBoolean())return null;
            var tag=json.GetProperty("tag_name").GetString()??"";
            if(!tag.StartsWith('v'))throw new UpdateException("invalid_version","The release tag is invalid.");
            var version=UpdatePaths.ParseVersion(tag[1..]);
            if(version<=UpdatePaths.Normalize(current))return null;
            var assets=json.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==UpdatePaths.AssetName).ToArray();
            if(assets.Length!=1)throw new UpdateException("missing_package","This release has no supported Windows x64 update package.");
            var asset=assets[0];var size=asset.GetProperty("size").GetInt64();
            if(size<1||size>MaximumPackageSize)throw new UpdateException("invalid_size","The update package size is invalid.");
            var digest=asset.GetProperty("digest").GetString()??"";
            if(!Regex.IsMatch(digest,@"^sha256:[a-fA-F0-9]{64}$"))throw new UpdateException("missing_digest","The release is missing a valid SHA-256 digest.");
            var url=new Uri(asset.GetProperty("browser_download_url").GetString()??"",UriKind.Absolute);
            if(url.Scheme!="https"||!url.IsDefaultPort||url.Host!="github.com"||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0||url.AbsolutePath!="/sidosgr/LocalClaudeBridge/releases/download/"+tag+"/"+UpdatePaths.AssetName)
                throw new UpdateException("invalid_url","The update package URL does not match this project's release.");
            return new(version.ToString(3),tag,url,size,digest[7..].ToLowerInvariant());
        }
        catch(UpdateException){throw;}
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or FormatException or OverflowException)
        {throw new UpdateException("invalid_release","GitHub returned incomplete update metadata.");}
    }
    public string PackagePath(UpdateRelease release)
    {
        UpdatePaths.ParseVersion(release.Version);
        var path=Path.Combine(UpdatePaths.UpdatesRoot(root),"downloads",release.Version,UpdatePaths.AssetName);
        UpdatePaths.RequireChild(root,path);return path;
    }
    public async Task<string> DownloadAsync(UpdateRelease release,IProgress<UpdateProgress>? progress=null,CancellationToken cancellation=default)
    {
        var target=PackagePath(release);
        if(await VerifyAsync(target,release.Size,release.Sha256,cancellation)){progress?.Report(new(release.Size,release.Size));return target;}
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);UpdatePaths.RequireChild(root,target);
        var partial=target+".partial";UpdatePaths.RequireChild(root,partial);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response=await GetAsync(release.DownloadUrl,false,timeout.Token);
            if(!response.IsSuccessStatusCode)throw new UpdateException("download_failed","The update download failed. Retry when the connection is available.");
            if(response.Content.Headers.ContentLength is long length&&length!=release.Size)throw new UpdateException("size_mismatch","The update download size did not match its release metadata.");
            await using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
            {
                await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token),output,release.Size,bytes=>progress?.Report(new(bytes,release.Size)),timeout.Token);
                await output.FlushAsync(timeout.Token);
            }
            if(!await VerifyAsync(partial,release.Size,release.Sha256,timeout.Token))throw new UpdateException("verification_failed","The update failed size or SHA-256 verification. The downloaded file was not installed.");
            File.Move(partial,target,true);return target;
        }
        finally{if(File.Exists(partial)){UpdatePaths.RequireChild(root,partial);File.Delete(partial);}}
    }
    public static async Task<bool> VerifyAsync(string path,long size,string sha256,CancellationToken cancellation=default)
    {
        if(size<1||size>MaximumPackageSize||!Regex.IsMatch(sha256,@"^[a-fA-F0-9]{64}$")||!File.Exists(path)||new FileInfo(path).Length!=size)return false;
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,true);
        var actual=await SHA256.HashDataAsync(stream,cancellation);return CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(sha256));
    }
    public async Task<string> PrepareInstallAsync(UpdateRelease release,string packagePath,bool restartEndpoint,CancellationToken cancellation=default)
    {
        UpdatePaths.RequireChild(UpdatePaths.UpdatesRoot(root),packagePath);
        if(!await VerifyAsync(packagePath,release.Size,release.Sha256,cancellation))throw new UpdateException("verification_failed","The cached update changed. Download it again before installing.");
        var helper=Path.Combine(AppContext.BaseDirectory,UpdatePaths.HelperExecutable);
        if(!File.Exists(helper))throw new UpdateException("helper_missing","The update helper is missing. Extract the complete Windows package and try again.");
        var operation=Guid.NewGuid().ToString("N");var operationFolder=Path.Combine(UpdatePaths.UpdatesRoot(root),"operations",operation);
        UpdatePaths.RequireChild(root,operationFolder);Directory.CreateDirectory(operationFolder);
        var copiedHelper=Path.Combine(operationFolder,UpdatePaths.HelperExecutable);File.Copy(helper,copiedHelper,false);
        string helperHash;
        await using(var source=File.OpenRead(helper))helperHash=Convert.ToHexString(await SHA256.HashDataAsync(source,cancellation));
        if(!await VerifyAsync(copiedHelper,new FileInfo(helper).Length,helperHash,cancellation))throw new UpdateException("helper_copy_failed","The update helper could not be prepared.");
        using var process=Process.GetCurrentProcess();
        var plan=new UpdatePlan(1,operation,process.Id,process.StartTime.ToUniversalTime().Ticks,Path.GetFullPath(packagePath),release.Size,release.Sha256,release.Version,Environment.ProcessPath??Path.Combine(AppContext.BaseDirectory,UpdatePaths.MainExecutable),restartEndpoint);
        var planPath=Path.Combine(operationFolder,"plan.json");await File.WriteAllTextAsync(planPath,JsonSerializer.Serialize(plan),cancellation);return planPath;
    }
    public static void LaunchInstaller(string planPath)
    {
        var helper=Path.Combine(Path.GetDirectoryName(planPath)!,UpdatePaths.HelperExecutable);
        var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(helper)!};start.ArgumentList.Add(planPath);
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]=Path.Combine(UpdatePaths.UpdatesRoot(UpdatePaths.DataRoot),"runtime");
        if(Process.Start(start)is null)throw new UpdateException("helper_start_failed","The update helper did not start.");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri url,bool metadata,CancellationToken cancellation)
    {
        for(var hop=0;hop<=5;hop++)
        {
            if(url.Scheme!="https"||!url.IsDefaultPort||url.UserInfo.Length!=0||url.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))throw new UpdateException("untrusted_redirect","The update download redirected to an unapproved host.");
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            request.Headers.UserAgent.ParseAdd("LocalClaudeBridge-Updater/"+UpdatePaths.CurrentVersion.ToString(3));
            request.Headers.Accept.ParseAdd(metadata?"application/vnd.github+json":"application/octet-stream");
            if(metadata)request.Headers.Add("X-GitHub-Api-Version","2026-03-10");
            var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
            if(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location=response.Headers.Location;response.Dispose();
                if(location is null)throw new UpdateException("invalid_redirect","The update service returned an invalid redirect.");
                url=location.IsAbsoluteUri?location:new Uri(url,location);continue;
            }
            return response;
        }
        throw new UpdateException("too_many_redirects","The update service returned too many redirects.");
    }
    private static async Task CopyBoundedAsync(Stream source,Stream destination,long maximum,Action<long>? progress,CancellationToken cancellation)
    {
        await using(source)
        {
            var buffer=new byte[131072];long total=0;int count;
            while((count=await source.ReadAsync(buffer,cancellation))!=0)
            {total+=count;if(total>maximum)throw new UpdateException("size_mismatch","The update response exceeded the expected size.");await destination.WriteAsync(buffer.AsMemory(0,count),cancellation);progress?.Invoke(total);}
        }
    }
    public void Dispose(){if(ownsHttp)http.Dispose();}
}
