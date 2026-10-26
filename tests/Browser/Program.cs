using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ClaudeLocalBridge;
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app=new Application();var window=new Window {Title="Local Claude Bridge popup regression test",Width=600,Height=480};
        var browser=new WebView2();window.Content=browser;int exitCode=1;
        window.Loaded+=async(_,_)=>
        {
            WebPopupHost? host=null;
            try
            {
                var profileStore=new ProfileStore(Path.Combine(Path.GetTempPath(),"BridgePopupTest-"+Guid.NewGuid().ToString("N"),"profiles.json"));
                var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:profileStore.BrowserFolder("default"));
                await browser.EnsureCoreWebView2Async(environment);
                host=new WebPopupHost(environment,window);host.Attach(browser.CoreWebView2);
                var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.PopupClosed+=()=>closed.TrySetResult();
                host.PopupFailed+=()=>received.TrySetException(new Exception("Popup initialization failed"));
                browser.CoreWebView2.WebMessageReceived+=(_,message)=>{if(message.TryGetWebMessageAsString()=="opener-ok")received.TrySetResult();};
                browser.CoreWebView2.NavigateToString("""
                    <html><body id="parent">Synthetic opener test<script>
                    window.addEventListener('message',e=>{
                      if(e.data==='popup-result'&&document.body.id==='parent')chrome.webview.postMessage('opener-ok');
                    });
                    setTimeout(()=>{
                      const child=window.open('about:blank','bridge-sign-in-test');
                      child.document.write('<html><body><script>window.opener.postMessage("popup-result","*");window.close();<\/script></body></html>');
                    },200);
                    </script></body></html>
                    """);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if(host.OpenCount!=0)throw new Exception("Closed popup was retained");
                var marker=await browser.CoreWebView2.ExecuteScriptAsync("document.body.id");
                if(marker!="\"parent\"")throw new Exception("Popup replaced the opener document");
                Console.WriteLine("PASS: real WebView2 popup preserves opener, postMessage, parent document, window.close and cleanup. Synthetic profile; no external login.");
                var grid=new System.Windows.Controls.Grid();window.Content=grid;grid.Children.Add(browser);
                var alternate=profileStore.Add("Alternate");var second=new WebView2{Visibility=Visibility.Hidden};grid.Children.Add(second);
                try
                {
                    var otherEnvironment=await CoreWebView2Environment.CreateAsync(userDataFolder:profileStore.BrowserFolder(alternate.Id));
                    await second.EnsureCoreWebView2Async(otherEnvironment);
                    async Task Fixture(WebView2 view,CoreWebView2Environment env)
                    {
                        view.CoreWebView2.AddWebResourceRequestedFilter("https://bridge-profile.test/*",CoreWebView2WebResourceContext.Document);
                        view.CoreWebView2.WebResourceRequested+=(_,request)=>request.Response=env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html><body>Profile isolation fixture</body></html>")),200,"OK","Content-Type: text/html");
                        var loaded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        view.NavigationCompleted+=(_,args)=>{if(args.IsSuccess)loaded.TrySetResult();};
                        view.CoreWebView2.Navigate("https://bridge-profile.test/");await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    await Fixture(browser,environment);
                    await browser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('profile-marker','primary');document.cookie='profile-marker=primary; path=/; Secure; SameSite=Lax'");
                    await Fixture(second,otherEnvironment);
                    var other=await second.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('profile-marker')===null&&!document.cookie.includes('profile-marker')");
                    var primary=await browser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('profile-marker')==='primary'&&document.cookie.includes('profile-marker=primary')");
                    if(other!="true"||primary!="true")throw new Exception("Browser sessions are not isolated");
                    Console.WriteLine("PASS: named browser folders isolate cookies and local storage in actual WebView2, including a hidden background view. Synthetic fixture only.");
                }
                finally{second.Dispose();}
                exitCode=0;
            }
            catch(Exception ex){Console.Error.WriteLine("FAIL: "+ex);}
            finally{host?.Dispose();browser.Dispose();window.Close();}
        };
        app.Run(window);return exitCode;
    }
}
