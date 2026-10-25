using ClaudeLocalBridge;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
var folder = Path.Combine(Path.GetTempPath(), "LocalClaudeBridge-Tests-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(folder, "settings.json");
var state = new BridgeState(path);
Check(BridgeState.NormalizeModel("claude-sonnet-5.5") == "claude-sonnet-5-5", "Sonnet display version must use the documented model ID");
var key = state.CreateKey("Test client");
Check(state.Authenticate(key.Secret), "Created key must authenticate");
Check(!state.Authenticate(key.Secret + "wrong"), "Wrong key must fail");
Check(!File.ReadAllText(path).Contains(key.Secret), "Plaintext key must not be saved");
Check(new BridgeState(path).Authenticate(key.Secret), "Key hash must persist");
var rememberedWorkspace = Guid.NewGuid().ToString();
state.Update(state.Settings with { OrganizationId = rememberedWorkspace });
Check(new BridgeState(path).Settings.OrganizationId == rememberedWorkspace, "Workspace preference must persist");
var used = new TcpListener(IPAddress.Loopback, 0); used.Start();
var port = ((IPEndPoint)used.LocalEndpoint).Port; used.Stop();
state.Update(new BridgeSettings(port));
var invocations = 0;
var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var server = new LocalServer(state, async (prompt, model, seconds, cancellation) =>
{
    Interlocked.Increment(ref invocations);
    if (prompt == "block") { blocked.TrySetResult(); await wait.Task.WaitAsync(cancellation); }
    return new Completion(prompt == "Reply with exactly OK." ? "OK" : prompt, model);
});
await server.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
Check((await client.GetAsync("/health")).IsSuccessStatusCode, "Health must work");
Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Models need authentication");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
Check((await client.GetAsync("/v1/models")).IsSuccessStatusCode, "Valid key must work");
using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
{
    request.Headers.Add("Origin", "https://example.com");
    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Browser origins must be rejected");
}
using (var request = new HttpRequestMessage(HttpMethod.Get, "/health"))
{
    request.Headers.Host = "evil.example";
    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Foreign Host must be rejected");
}
object Body(string prompt, bool stream = false) => new { model = "claude-web", messages = new[] { new { role = "user", content = prompt } }, stream };
using (var answer = await client.PostAsJsonAsync("/v1/chat/completions", Body("Reply with exactly OK.")))
{
    using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
    Check(answer.IsSuccessStatusCode && json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "OK", "Chat response translation failed");
    Check(!json.RootElement.TryGetProperty("usage", out _), "Must not invent token counts");
}
using (var answer = await client.PostAsJsonAsync("/v1/chat/completions", Body("hello", true)))
{
    var text = await answer.Content.ReadAsStringAsync();
    Check(answer.Content.Headers.ContentType?.MediaType == "text/event-stream" && text.EndsWith("data: [DONE]\n\n"), "SSE must terminate");
    Check(text.Split("\"finish_reason\":\"stop\"").Length == 2, "SSE must finish once");
}
using (var answer = await client.PostAsJsonAsync("/v1/messages", Body("hello")))
{
    using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
    Check(json.RootElement.GetProperty("content")[0].GetProperty("text").GetString() == "hello", "Native response failed");
}
var before = invocations;
Check((await client.PostAsJsonAsync("/v1/chat/completions", new { model = "claude-web", messages = new[] { new { role = "user", content = "hello" } }, max_tokens = 16 })).StatusCode == HttpStatusCode.BadRequest, "Unsupported token budget must be explicit");
Check((await client.PostAsJsonAsync("/v1/chat/completions", new { messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = "hidden" } } } } })).StatusCode == HttpStatusCode.BadRequest, "Images must not silently drop");
Check(invocations == before, "Invalid requests must not reach Claude");
Check((await client.PostAsJsonAsync("/v1/messages", Body("hello", true))).StatusCode == HttpStatusCode.BadRequest, "Unsupported native streaming must reject");
using (var answer = await client.PostAsync("/v1/messages", new StringContent("not json", System.Text.Encoding.UTF8, "application/json")))
    Check(answer.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON must reject");
using (var answer = await client.PostAsJsonAsync("/v1/messages", Body("x" + new string('x', 300000))))
    Check(answer.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Large body must reject");
var first = client.PostAsJsonAsync("/v1/messages", Body("block"));
await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check((await client.PostAsJsonAsync("/v1/messages", Body("second"))).StatusCode == HttpStatusCode.TooManyRequests, "Concurrent generation must reject");
wait.TrySetResult(); using (var answer = await first) Check(answer.IsSuccessStatusCode, "Original request must finish");
state.Revoke(key.Key.Id);
Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Revoked key must stop working");
await server.DisposeAsync();
state.Update(new BridgeSettings(port, false));
var secondKey = state.CreateKey("Second client");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondKey.Secret);
await server.StartAsync();
Check((await client.PostAsJsonAsync("/v1/chat/completions", Body("hello"))).StatusCode == HttpStatusCode.NotFound, "OpenAI toggle must remove route");
await server.DisposeAsync();
Console.WriteLine($"PASS: {count} integration assertions. Synthetic backend only; no Claude account requests.");
