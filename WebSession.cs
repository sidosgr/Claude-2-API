using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace ClaudeLocalBridge;

public sealed class WebSession : IDisposable
{
    private readonly WebView2 browser;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = [];
    private readonly string script;
    private readonly ModelCatalog models;
    private string? organizationId;
    private WebPopupHost? popups;
    public event Action? ConnectionChanged;
    public event Action? LoginWindowClosed;
    public event Action? LoginWindowFailed;
    public string? OrganizationId { get => organizationId; set { if (organizationId == value) return; models.Clear(); organizationId = value; ConnectionChanged?.Invoke(); } }
    public bool Ready => OrganizationId is not null;
    public WebSession(WebView2 browser, ModelCatalog? catalog = null)
    {
        this.browser = browser; dispatcher = browser.Dispatcher; models = catalog ?? new();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLocalBridge.WebBridge.js")!;
        using var reader = new StreamReader(stream); script = reader.ReadToEnd();
    }
    public async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(BridgeState.DataFolder, "BrowserProfile"));
        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.WebMessageReceived += Receive;
        browser.CoreWebView2.NavigationStarting += (_, _) =>
        {
            OrganizationId = null;
            foreach (var item in pending.Values) item.TrySetException(new BridgeException(503, "page_changed", "Claude page changed during the request. Check the session and try again."));
            pending.Clear();
        };
        popups=new WebPopupHost(environment,System.Windows.Window.GetWindow(browser));
        popups.PopupClosed+=()=>LoginWindowClosed?.Invoke();
        popups.PopupFailed+=()=>LoginWindowFailed?.Invoke();
        popups.Attach(browser.CoreWebView2);
        browser.CoreWebView2.Navigate("https://claude.ai/new");
    }
    public async Task<IReadOnlyList<Organization>> CheckAsync(CancellationToken cancellation = default)
    {
        OrganizationId = null;
        JsonElement result;
        try { result=await RpcAsync(new { operation = "session" },30,cancellation); }
        catch(BridgeException ex) when(ex.Status==403)
        {
            throw new BridgeException(403,"session_check_forbidden","Claude rejected the session check (HTTP 403). Complete sign-in in the browser, including any verification, then click Connect. No generation request was sent.");
        }
        return result.EnumerateArray().Select(o => new Organization(o.GetProperty("id").GetString()!, o.GetProperty("name").GetString()!))
            .Where(o => Guid.TryParse(o.Id, out _)).ToArray();
    }
    public async Task<IReadOnlyList<WebModel>> DiscoverModelsAsync(CancellationToken cancellation = default)
    {
        if (!Ready) throw new BridgeException(503,"session_not_ready","Verify the session and select a workspace before refreshing models.");
        var result = await RpcAsync(new { operation = "models" }, 10, cancellation);
        models.Replace(result.EnumerateArray().Select(m => new WebModel(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!)));
        return models.Snapshot;
    }
    public void SetDarkMode(bool dark)
    {
        if (browser.CoreWebView2 is not null) browser.CoreWebView2.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(17,21,27) : System.Drawing.Color.FromArgb(244,244,241);
    }
    public Task<Completion> CompleteAsync(string prompt, string model, int timeoutSeconds, CancellationToken cancellation)
        =>CompleteWithThinkingAsync(prompt,model,timeoutSeconds,new(),cancellation);
    public Task<Completion> CompleteWithThinkingAsync(string prompt, string model, int timeoutSeconds, ThinkingOptions thinking, CancellationToken cancellation)
    {
        if (dispatcher.CheckAccess()) return CompleteOnUiAsync(prompt, model, timeoutSeconds, thinking,cancellation);
        return dispatcher.InvokeAsync(() => CompleteOnUiAsync(prompt, model, timeoutSeconds, thinking,cancellation)).Task.Unwrap();
    }
    private async Task<Completion> CompleteOnUiAsync(string prompt, string model, int timeoutSeconds, ThinkingOptions thinking, CancellationToken cancellation)
    {
        if (OrganizationId is null) throw new BridgeException(503, "session_not_ready", "Sign in to Claude and click Check session in the app.");
        var result = await RpcAsync(new { operation = "complete", organization = OrganizationId, prompt, model, thinking_mode=thinking.Mode,effort=thinking.Effort }, timeoutSeconds, cancellation);
        return new Completion(result.GetProperty("text").GetString()!, result.GetProperty("model").GetString()!);
    }
    private async Task<JsonElement> RpcAsync(object input, int timeoutSeconds, CancellationToken cancellation)
    {
        if (browser.CoreWebView2 is null || !Uri.TryCreate(browser.Source?.AbsoluteUri, UriKind.Absolute, out var source) || source.Scheme != "https" || source.Host != "claude.ai")
            throw new BridgeException(503, "wrong_page", "Open claude.ai and finish signing in before checking the session.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var data = JsonSerializer.Serialize(input);
        var wrapper = script + "\n(async()=>{const id='" + id + "';const ctl=new AbortController();" +
            "globalThis.localClaudeBridgeControllers??={};globalThis.localClaudeBridgeControllers[id]=ctl;" +
            "try{const result=await localClaudeBridgeRun(" + data + ",ctl.signal);chrome.webview.postMessage({channel:'local-claude-bridge',id,result});}" +
            "catch(e){chrome.webview.postMessage({channel:'local-claude-bridge',id,error:String(e.message)});}" +
            "finally{delete globalThis.localClaudeBridgeControllers[id];}})();";
        try
        {
            await browser.CoreWebView2.ExecuteScriptAsync(wrapper);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellation);
        }
        catch (TimeoutException) { throw new BridgeException(504, "upstream_timeout", "Claude did not finish before the timeout. The request was not retried."); }
        finally
        {
            pending.Remove(id);
            try { await browser.CoreWebView2.ExecuteScriptAsync("globalThis.localClaudeBridgeControllers?.['" + id + "']?.abort()"); }
            catch (Exception) { /* The page may have closed or navigated. */ }
        }
    }
    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Scheme != "https" || source.Host != "claude.ai") return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson); var root = json.RootElement;
            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "local-claude-bridge" || !root.TryGetProperty("id", out var id)) return;
            if (!pending.TryGetValue(id.GetString() ?? "", out var completion)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var fields = (error.GetString() ?? "browser_error").Split('|', 3);
                var code = fields[0];
                var kind = fields.Length > 1 ? fields[1] : "unknown";
                var reason = fields.Length > 2 ? fields[2] : "unspecified";
                if (!System.Text.RegularExpressions.Regex.IsMatch(kind, @"^[a-z_]{1,64}$")) kind = "unknown";
                if (reason is not ("plan_restriction" or "model_restriction" or "workspace_restriction" or "csrf_validation" or "verification" or "access_restriction")) reason = "unspecified";
                var mapped = code switch
                {
                    "http_401" => new BridgeException(503, "session_expired", "Claude session is signed out or expired. Sign in and check the session again."),
                    "http_403" when kind is "browser_challenge" or "html_response" => new BridgeException(503, "browser_verification", "Claude returned HTTP 403 with a browser verification/HTML response. Inspect the visible Claude page. No automatic retry was made."),
                    "http_403" => new BridgeException(403, "claude_web_forbidden", $"Claude returned HTTP 403. Error type: {kind}; category: {reason}. This generation request was denied."),
                    "browser_verification" => new BridgeException(503, "unexpected_web_response", "Claude returned an unexpected page instead of API data. Inspect the visible browser for verification or a login redirect."),
                    "http_429" => new BridgeException(429, "claude_limit", "Claude returned a usage or rate limit. Wait for the account allowance to reset."),
                    "http_400" => new BridgeException(502, "web_request_rejected", "Claude rejected the web request format or model (HTTP 400). The adapter needs a compatibility update."),
                    "http_404" => new BridgeException(502, "web_route_not_found", "Claude web route or selected model was not found (HTTP 404)."),
                    "incomplete_stream" => new BridgeException(502, "incomplete_stream", "Claude disconnected before the response finished."),
                    "model_picker_missing" => new BridgeException(503,"model_picker_missing","Open a new chat in the visible Claude page, then refresh models. The model-picker control was not found."),
                    "no_available_models" => new BridgeException(503,"model_discovery_failed","The model picker did not expose available entries. No model list was invented; open the picker and refresh again."),
                    _ => new BridgeException(502, "web_request_failed", "Claude web request could not complete. Check the visible browser and model setting.")
                };
                if (code == "http_401") OrganizationId = null;
                completion.TrySetException(mapped);
            }
            else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        popups?.Dispose();
        foreach (var item in pending.Values) item.TrySetCanceled(); pending.Clear(); browser.Dispose();
    }
}
