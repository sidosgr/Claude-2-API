using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
namespace ClaudeLocalBridge;

public partial class MainWindow : Window
{
    private BridgeState state = null!;
    private WebSession session = null!;
    private LocalServer server = null!;
    private bool initialized, busy, closing;
    private bool startupTestPending = Environment.GetCommandLineArgs().Contains("--quick-test");
    private readonly List<object> report = [];

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var historyPath = Path.Combine(BridgeState.DataFolder, "web-session-test.json");
            if (File.Exists(historyPath))
            {
                using var history = JsonDocument.Parse(File.ReadAllText(historyPath));
                if (history.RootElement.TryGetProperty("events", out var previous) && previous.ValueKind == JsonValueKind.Array)
                    report.AddRange(previous.EnumerateArray().TakeLast(100).Select(item => (object)item.Clone()));
            }
        }
        catch (Exception) { /* Diagnostic history must not prevent startup. */ }
        Loaded += Initialize;
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            try { if (server is not null) await server.DisposeAsync(); }
            finally { if (session is not null) session.Dispose(); Close(); }
        };
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            state = new BridgeState();
            PortBox.Text = state.Settings.Port.ToString();
            OpenAiBox.IsChecked = state.Settings.OpenAi;
            ModelBox.Text = state.Settings.Model;
            TimeoutBox.Text = state.Settings.TimeoutSeconds.ToString();
            RefreshKeys();
            session = new WebSession(Browser);
            server = new LocalServer(state, session.CompleteAsync);
            await session.InitializeAsync();
            Browser.NavigationCompleted += async (_, args) =>
            {
                if (!startupTestPending || !args.IsSuccess || !initialized || Browser.Source?.Host != "claude.ai" || !Browser.Source.AbsolutePath.StartsWith("/new")) return;
                startupTestPending = false;
                await Guard(RunQuickTest);
            };
            Browser.CoreWebView2.SourceChanged += (_, _) => BrowserLocationText.Text = Browser.Source?.GetLeftPart(UriPartial.Path) ?? "Claude browser";
            initialized = true;
            Log("browser", "Regular Claude web session is ready for you to sign in.");
        }
        catch (WebView2RuntimeNotFoundException) { Log("startup_error", "Microsoft Edge WebView2 Runtime is required. Install the Evergreen runtime from Microsoft, then reopen the app."); }
        catch (Exception ex) { Log("startup_error", "Could not initialize the app: " + ex.GetType().Name + ". Check the local settings file and WebView2 installation."); }
    }
    private void Log(string stage, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(stage, message)); return; }
        StatusText.Text = message;
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} [{stage}] {message}\n"); LogBox.ScrollToEnd();
        report.Add(new { utc = DateTimeOffset.UtcNow, stage, message });
        if (report.Count > 200) report.RemoveRange(0, report.Count - 200);
        try
        {
            Directory.CreateDirectory(BridgeState.DataFolder);
            File.WriteAllText(Path.Combine(BridgeState.DataFolder, "web-session-test.json"), JsonSerializer.Serialize(new { version = "0.1.1", events = report }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private async Task Guard(Func<Task> action)
    {
        if (!initialized) { Log("app", "Browser is still initializing or could not start."); return; }
        if (busy) { Log("app", "Wait for the current app operation to finish."); return; }
        busy = true; CheckButton.IsEnabled = TestButton.IsEnabled = StartButton.IsEnabled = false;
        try { await action(); }
        catch (BridgeException ex) { Log(ex.Code, ex.Message); }
        catch (ArgumentException ex) { Log("settings", ex.Message); }
        catch (Exception ex) { Log("operation_error", "Operation failed: " + ex.GetType().Name + ". If starting the endpoint, check whether the port is already in use."); }
        finally { busy = false; CheckButton.IsEnabled = TestButton.IsEnabled = StartButton.IsEnabled = true; }
    }
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy) return;
        Browser.CoreWebView2.Navigate("https://claude.ai/login");
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await Guard(CheckSession);
    private async Task CheckSession()
    {
        Log("session", "Checking the signed-in Claude web session…");
        OrgBox.ItemsSource = null;
        var organizations = await session.CheckAsync();
        OrgBox.ItemsSource = organizations;
        var remembered = organizations.FirstOrDefault(o => o.Id == state.Settings.OrganizationId);
        if (remembered is not null) OrgBox.SelectedItem = remembered;
        else if (organizations.Count == 1) OrgBox.SelectedIndex = 0;
        Log("session", session.Ready ? "Claude web session verified. Selected workspace is ready; account plan type remains unverified." : organizations.Count == 0 ? "No Claude workspace found. Complete the login in the browser." : "Claude web session verified. Choose the workspace to use.");
    }
    private void Org_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (session is null) return;
        session.OrganizationId = (OrgBox.SelectedItem as Organization)?.Id;
        if (session.OrganizationId is not null)
        {
            try { state.Update(state.Settings with { OrganizationId = session.OrganizationId }); }
            catch (Exception) { Log("settings", "Workspace is selected for this session, but its preference could not be saved."); }
        }
    }
    private async void SignOut_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await server.DisposeAsync(); EndpointText.Text = "Endpoint stopped";
        session.OrganizationId = null; OrgBox.ItemsSource = null;
        state.Update(state.Settings with { OrganizationId = null });
        await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        Browser.CoreWebView2.Navigate("https://claude.ai/login");
        Log("session", "This app’s browser session was cleared.");
    });
    private BridgeSettings ReadSettings()
    {
        if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(TimeoutBox.Text, out var timeout)) throw new ArgumentException("Port and timeout must be whole numbers.");
        var settings = new BridgeSettings(port, OpenAiBox.IsChecked == true, BridgeState.NormalizeModel(ModelBox.Text), timeout, state.Settings.OrganizationId);
        BridgeState.Validate(settings); return settings;
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var settings = ReadSettings();
        await server.DisposeAsync(); EndpointText.Text = "Endpoint stopped";
        state.Update(settings);
        ModelBox.Text = settings.Model;
        Log("settings", "Settings saved. Click Start endpoint to apply them.");
    });
    private async Task StartEndpoint()
    {
        if (server.Running) return;
        var settings = ReadSettings(); state.Update(settings);
        await server.StartAsync(); EndpointText.Text = $"http://127.0.0.1:{server.Port}/v1";
        Log("endpoint", "Local endpoint is running. Claude requests require a verified session and an active local API key.");
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await Guard(StartEndpoint);
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try { await server.DisposeAsync(); EndpointText.Text = "Endpoint stopped"; Log("endpoint", "Endpoint stopped."); }
        catch (Exception) { Log("endpoint", "Endpoint shutdown could not complete cleanly."); }
    }
    private void RefreshKeys() => KeysBox.ItemsSource = state.Keys;
    private async void CreateKey_Click(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var created = state.CreateKey(KeyNameBox.Text); NewKeyBox.Text = created.Secret;
        RefreshKeys(); Log("keys", "Local API key created. Copy it now; only its hash is saved."); return Task.CompletedTask;
    });
    private async void RevokeKey_Click(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (KeysBox.SelectedItem is not LocalKey key) throw new ArgumentException("Select a key to revoke.");
        state.Revoke(key.Id); NewKeyBox.Clear(); RefreshKeys(); Log("keys", "Selected local API key revoked."); return Task.CompletedTask;
    });
    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(NewKeyBox.Text)) return;
        try { Clipboard.SetText(NewKeyBox.Text); Log("keys", "New local key copied to the clipboard."); }
        catch (Exception) { Log("keys", "Clipboard is unavailable. Select and copy the displayed key manually."); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e) => await Guard(RunQuickTest);
    private async Task RunQuickTest()
    {
        if (!session.Ready) await CheckSession();
        if (!session.Ready) throw new BridgeException(503, "session_not_ready", "Finish signing in and select your workspace before running the test.");
        await StartEndpoint();
        var (temporary, secret) = state.CreateKey("Temporary quick test");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(state.Settings.TimeoutSeconds + 10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            Log("test", "Sending one small prompt through the local endpoint to the Claude website…");
            using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{server.Port}/v1/messages", new { model = "claude-web", messages = new[] { new { role = "user", content = "Reply with exactly OK." } } });
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!response.IsSuccessStatusCode)
            {
                var error = result.RootElement.GetProperty("error");
                Log("test_failed", $"HTTP {(int)response.StatusCode}: {error.GetProperty("message").GetString()}"); return;
            }
            var text = result.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            Log("test_passed", text.Trim() == "OK" ? "PASS: local endpoint returned OK from the Claude web session. Free-plan identity and allowance attribution are not independently verified." : "PASS: Claude web session returned a text response through the local endpoint. Reply differed from OK; free allowance remains unverified.");
        }
        finally { state.Revoke(temporary.Id); RefreshKeys(); }
    }
}
