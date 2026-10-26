using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeLocalBridge;

var count=0;
void Check(bool value,string description){if(!value)throw new Exception(description);count++;}
async Task ExpectFailure(Func<Task> action,string code)
{
 try{await action();throw new Exception("Expected "+code);}catch(UpdateException ex){Check(ex.Code==code,"Unexpected update failure: "+ex.Code+" (expected "+code+")");}
}
byte[] Package(bool unsafePath=false)
{
 using var output=new MemoryStream();
 using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
 {
  foreach(var name in new[]{UpdatePaths.MainExecutable,UpdatePaths.HelperExecutable})
  {using var entry=zip.CreateEntry(name).Open();entry.Write(File.ReadAllBytes(typeof(UpdateInstaller).Assembly.Location));}
  using(var entry=zip.CreateEntry("payload.txt").Open()){entry.Write("new version"u8);}
  if(unsafePath){using var entry=zip.CreateEntry("../escape.txt").Open();entry.Write("bad"u8);}
 }
 return output.ToArray();
}
var package=Package();var sha=Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
JsonElement Metadata(long? size=null,string? digest=null,string? url=null,bool draft=false,bool prerelease=false,string tag="v1.0.0")=>JsonSerializer.SerializeToElement(new{draft,prerelease,tag_name=tag,assets=new[]{new{name=UpdatePaths.AssetName,size=size??package.Length,digest=digest??("sha256:"+sha),browser_download_url=url??"https://github.com/sidosgr/Claude-2-API/releases/download/"+tag+"/"+UpdatePaths.AssetName}}});
var release=AppUpdateClient.ParseRelease(Metadata(),new Version(0,9,0))!;
Check(release.Version=="1.0.0"&&release.Sha256==sha,"Valid stable release must parse");
Check(AppUpdateClient.ParseRelease(Metadata(),new Version(1,0,0,0))is null,"Version comparison must normalize revision");
Check(AppUpdateClient.ParseRelease(Metadata(draft:true),new Version(0,9,0))is null,"Drafts must be ignored");
Check(AppUpdateClient.ParseRelease(Metadata(prerelease:true),new Version(0,9,0))is null,"Previews must be ignored");
await ExpectFailure(()=>{AppUpdateClient.ParseRelease(Metadata(size:AppUpdateClient.MaximumPackageSize+1),new Version(0,1,0));return Task.CompletedTask;},"invalid_size");
await ExpectFailure(()=>{AppUpdateClient.ParseRelease(Metadata(digest:"unknown"),new Version(0,1,0));return Task.CompletedTask;},"missing_digest");
await ExpectFailure(()=>{AppUpdateClient.ParseRelease(Metadata(url:"https://evil.example/package.zip"),new Version(0,1,0));return Task.CompletedTask;},"invalid_url");
await ExpectFailure(()=>{AppUpdateClient.ParseRelease(Metadata(url:"https://github.com:4443/sidosgr/Claude-2-API/releases/download/v1.0.0/"+UpdatePaths.AssetName),new Version(0,1,0));return Task.CompletedTask;},"invalid_url");
var root=Path.Combine(Path.GetTempPath(),"LocalClaudeBridge-UpdateTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var handler=new FixtureHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(package)});
using var http=new HttpClient(handler);using var client=new AppUpdateClient(http,root);
var cached=await client.DownloadAsync(release);Check(await AppUpdateClient.VerifyAsync(cached,release.Size,release.Sha256),"Downloaded cache must verify");
var requests=handler.Requests;Check(await client.DownloadAsync(release)==cached&&handler.Requests==requests,"Valid cache must be reused");
File.WriteAllText(cached,"tampered");await client.DownloadAsync(release);Check(handler.Requests==requests+1,"Tampered cache must be replaced");
var mismatchRoot=Path.Combine(root,"mismatch");
using(var badHttp=new HttpClient(new FixtureHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(package[..^1])})))
using(var badClient=new AppUpdateClient(badHttp,mismatchRoot))
{
 await ExpectFailure(()=>badClient.DownloadAsync(release),"size_mismatch");
 Check(!Directory.GetFiles(mismatchRoot,"*.partial",SearchOption.AllDirectories).Any(),"Failed download must remove partials");
}
using(var badHttp=new HttpClient(new FixtureHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[package.Length])})))
using(var badClient=new AppUpdateClient(badHttp,Path.Combine(root,"bad-hash")))await ExpectFailure(()=>badClient.DownloadAsync(release),"verification_failed");
using(var redirectHttp=new HttpClient(new FixtureHandler(_=>{var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new Uri("https://evil.example/package.zip");return response;})))
using(var redirectClient=new AppUpdateClient(redirectHttp,Path.Combine(root,"redirect")))await ExpectFailure(()=>redirectClient.DownloadAsync(release),"untrusted_redirect");
var cancelled=new CancellationTokenSource();cancelled.Cancel();
try{await client.DownloadAsync(release with{Version="2.0.0"},cancellation:cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){count++;}
var noReleaseHttp=new HttpClient(new FixtureHandler(_=>new HttpResponseMessage(HttpStatusCode.NotFound)));
using(var noRelease=new AppUpdateClient(noReleaseHttp,root))Check(await noRelease.CheckAsync(new Version(0,1,0))is null,"Missing stable release must not break startup");
try{UpdatePaths.RequireChild(root,Path.Combine(root,"..","outside"));throw new Exception("Unsafe path accepted");}catch(UpdateException){count++;}
var installRoot=Path.Combine(root,"install-case");var updates=UpdatePaths.UpdatesRoot(installRoot);Directory.CreateDirectory(updates);
var packagePath=Path.Combine(updates,"package.zip");File.WriteAllBytes(packagePath,package);
var dataSentinel=Path.Combine(installRoot,"settings.json");File.WriteAllText(dataSentinel,"preserve my settings");
var appFolder=UpdatePaths.InstallRoot(installRoot);Directory.CreateDirectory(appFolder);File.WriteAllText(Path.Combine(appFolder,"old.txt"),"old version");
UpdatePlan Plan(byte[] bytes,string path)=>new(1,Guid.NewGuid().ToString("N"),int.MaxValue,0,path,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),"1.0.0","",true);
var plan=Plan(package,packagePath);bool launchRequested=false;
await new UpdateInstaller(installRoot).ApplyAsync(plan,(exe,operation,token)=>{launchRequested=true;Check(Path.GetDirectoryName(exe)==appFolder&&operation.RestartEndpoint,"Installer must launch the managed target and restore endpoint mode");return Task.FromResult(true);});
Check(launchRequested&&File.Exists(Path.Combine(appFolder,"payload.txt")),"New version must become active");
Check(File.Exists(Path.Combine(updates,"rollback-"+plan.OperationId,"old.txt")),"Previous install must remain as rollback");
Check(File.ReadAllText(dataSentinel)=="preserve my settings","Settings outside the app folder must remain untouched");
var rollback=Plan(package,packagePath);
File.WriteAllText(Path.Combine(appFolder,"keep.txt"),"still working");
await ExpectFailure(()=>new UpdateInstaller(installRoot).ApplyAsync(rollback,(_,_,_)=>Task.FromResult(false)),"startup_failed");
Check(File.Exists(Path.Combine(appFolder,"keep.txt")),"Startup failure must restore previous files");
var badZip=Package(true);var badZipPath=Path.Combine(updates,"unsafe.zip");File.WriteAllBytes(badZipPath,badZip);
await ExpectFailure(()=>new UpdateInstaller(installRoot).ApplyAsync(Plan(badZip,badZipPath),(_,_,_)=>Task.FromResult(true)),"unsafe_package");
Check(!File.Exists(Path.Combine(updates,"escape.txt"))&&File.Exists(Path.Combine(appFolder,"keep.txt")),"ZIP traversal must not alter the installation or escape staging");
File.WriteAllBytes(packagePath,new byte[package.Length]);
await ExpectFailure(()=>new UpdateInstaller(installRoot).ApplyAsync(Plan(package,packagePath),(_,_,_)=>Task.FromResult(true)),"verification_failed");
Check(File.Exists(Path.Combine(appFolder,"keep.txt")),"Reverification failure must preserve existing files");
Console.WriteLine($"PASS: {count} updater assertions (metadata, redirects, verified cache/download, cancellation, staging, data preservation, rollback and traversal protection).");

sealed class FixtureHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
{
 public int Requests{get;private set;}
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
 {cancellationToken.ThrowIfCancellationRequested();Requests++;return Task.FromResult(respond(request));}
}
