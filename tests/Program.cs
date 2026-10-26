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
BridgeRequest ParseThinking(string fields)
{
    using var document=JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"OK\"}],"+fields+"}");
    return ChatProtocol.Parse(document.RootElement,true,true);
}
Check(ParseThinking("\"reasoning_effort\":\"low\"").Thinking==new ThinkingOptions("extended","low"),"Low effort must reach web controls");
Check(ParseThinking("\"reasoning\":{\"effort\":\"medium\"}").Thinking==new ThinkingOptions("extended","medium"),"Reasoning object must map medium effort");
Check(ParseThinking("\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"high\"}").Thinking==new ThinkingOptions("extended","high"),"Native effort must map to web high");
Check(ParseThinking("\"thinking\":{\"type\":\"disabled\"}").Thinking.Mode=="off","Disabled thinking must remain off");
Check(ParseThinking("\"reasoning_effort\":\"none\"").Thinking.Mode=="off","None effort must disable thinking");
Check(ParseThinking("\"reasoning_effort\":\"max\"").Thinking.Effort=="max","Max effort must pass through unchanged");
Check(ParseThinking("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":2048}").Warnings.Contains("thinking_token_budget_not_enforced"),"Hard thinking budget cannot be promised");
try{ParseThinking("\"thinking\":{\"type\":\"disabled\"},\"reasoning_effort\":\"high\"");throw new Exception("Conflicting thinking accepted");}catch(BridgeException){count++;}
Check(new BridgeState(path).Settings.AutoStartEndpoint&&!new BridgeState(path).Settings.EndpointEnabled&&!new BridgeState(path).Settings.ShowBrowser,"First launch must hide verified browser and wait for first endpoint connection");
Check(BridgeState.NormalizeModel("claude-sonnet-5.5") == "claude-sonnet-5-5", "Sonnet display version must use the documented model ID");
Check(state.Settings.DarkMode && state.Settings.RequireApiKey && state.Settings.ToolCalling,"New settings must default to dark mode, authenticated local access and tool translation");
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
var catalog = new ModelCatalog();
await using var server = new LocalServer(state, async (prompt, model, seconds, cancellation) =>
{
    Interlocked.Increment(ref invocations);
    if (prompt == "block") { blocked.TrySetResult(); await wait.Task.WaitAsync(cancellation); }
    var protocol = System.Text.RegularExpressions.Regex.Match(prompt,"bridge_protocol must equal \\\"([a-f0-9]{32})\\\"");
    if(protocol.Success)
    {
        if(prompt.Contains("RETURN_FINAL_TOOL_TEST"))return new Completion(JsonSerializer.Serialize(new{bridge_protocol=protocol.Groups[1].Value,text="done",tool_calls=Array.Empty<object>()}),model);
        var tool=prompt.Contains("MALFORMED_TOOL_TEST")?"invented_tool":"echo";
        return new Completion(JsonSerializer.Serialize(new{bridge_protocol=protocol.Groups[1].Value,text="",tool_calls=new[]{new{name=tool,arguments=new{value="hello"}}}}),model);
    }
    return new Completion(prompt == "Reply with exactly OK." ? "OK" : prompt, model);
},catalog);
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
    Check(json.RootElement.GetProperty("usage").ValueKind == JsonValueKind.Null, "Must not invent token counts");
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
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="claude-web",messages=new[]{new{role="user",content="hello"}},max_tokens=16,temperature=0.7,store=false,stream_options=new{include_usage=true}}))
{
 Check(answer.IsSuccessStatusCode,"Pi compatibility fields must be accepted");
 Check(answer.Headers.GetValues("X-Local-Claude-Compatibility").Single().Contains("token_budget_as_prompt_hint") && answer.Headers.GetValues("X-Local-Claude-Compatibility").Single().Contains("temperature_not_enforced"),"Compatibility caveats must be explicit");
}
before=invocations;
Check((await client.PostAsJsonAsync("/v1/chat/completions", new { messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = "hidden" } } } } })).StatusCode == HttpStatusCode.BadRequest, "Images must not silently drop");
Check(invocations == before, "Invalid requests must not reach Claude");
using(var answer=await client.PostAsJsonAsync("/v1/messages",Body("hello",true)))
 Check(answer.IsSuccessStatusCode && (await answer.Content.ReadAsStringAsync()).Contains("event: message_stop"),"Buffered native SSE must finish");
using (var answer = await client.PostAsync("/v1/messages", new StringContent("not json", System.Text.Encoding.UTF8, "application/json")))
    Check(answer.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON must reject");
using (var answer = await client.PostAsJsonAsync("/v1/messages", Body("x" + new string('x', 1200000))))
    Check(answer.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Large body must reject");
var first = client.PostAsJsonAsync("/v1/messages", Body("block"));
await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check((await client.PostAsJsonAsync("/v1/messages", Body("second"))).StatusCode == HttpStatusCode.TooManyRequests, "Concurrent generation must reject");
wait.TrySetResult(); using (var answer = await first) Check(answer.IsSuccessStatusCode, "Original request must finish");
catalog.Replace(new[]{new WebModel("claude-sonnet-5-5","Sonnet 5.5"),new WebModel("claude-haiku-5-5","Haiku 5.5")});
using(var answer=await client.GetAsync("/v1/models"))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
 Check(json.RootElement.GetProperty("data").GetArrayLength()==2,"Discovered models must replace the alias-only listing");
}
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="claude-haiku-5-5",messages=new[]{new{role="user",content="hello"}}}))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());Check(json.RootElement.GetProperty("model").GetString()=="claude-haiku-5-5","Requests must route the discovered model");
}
Check((await client.PostAsJsonAsync("/v1/chat/completions",new{model="claude-unknown-9",messages=new[]{new{role="user",content="hi"}}})).StatusCode==HttpStatusCode.NotFound,"Unknown models must reject");
var toolDefinition=new{type="function",function=new{name="echo",description="Echo a value",parameters=new{type="object",properties=new{value=new{type="string"}},required=new[]{"value"}}}};
object ToolBody(string text,bool stream=false)=>new{model="claude-web",messages=new[]{new{role="user",content=text}},tools=new[]{toolDefinition},tool_choice="required",parallel_tool_calls=false,stream,stream_options=new{include_usage=true},max_completion_tokens=2048};
string callId;
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("CALL_TEST_TOOL")))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
 Check(answer.IsSuccessStatusCode && json.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString()=="tool_calls","Tool turn must finish with tool_calls");
 var call=json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];callId=call.GetProperty("id").GetString()!;
 Check(call.GetProperty("function").GetProperty("name").GetString()=="echo" && JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("value").GetString()=="hello","Tool arguments must remain JSON");
}
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("CALL_TEST_TOOL",true)))
{
 var text=await answer.Content.ReadAsStringAsync();Check(text.Contains("\"tool_calls\"")&&text.Contains("\"usage\":null")&&text.EndsWith("data: [DONE]\n\n"),"Tool SSE and include_usage must be SDK compatible");
}
var history=new object[]{new{role="user",content="CALL_TEST_TOOL"},new{role="assistant",content=(string?)null,tool_calls=new[]{new{id=callId,type="function",function=new{name="echo",arguments="{\"value\":\"hello\"}"}}}},new{role="tool",tool_call_id=callId,content="RETURN_FINAL_TOOL_TEST: hello"}};
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="claude-web",messages=history,tools=new[]{toolDefinition}}))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());Check(answer.IsSuccessStatusCode&&json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()=="done","Tool result history must complete another turn");
}
Check((await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("MALFORMED_TOOL_TEST"))).StatusCode==HttpStatusCode.BadGateway,"Invented tools must not be forwarded");
using(var nativeAuth=new HttpRequestMessage(HttpMethod.Get,"/v1/models"))
{
 nativeAuth.Headers.Add("x-api-key",key.Secret);nativeAuth.Headers.Authorization=null;
 // Default request auth is overridden explicitly so only the native key is present.
 client.DefaultRequestHeaders.Authorization=null;
 Check((await client.SendAsync(nativeAuth)).IsSuccessStatusCode,"Native x-api-key must authenticate");
 client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key.Secret);
}
state.Revoke(key.Key.Id);
Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Revoked key must stop working");
await server.DisposeAsync();
state.Update(new BridgeSettings(port, false));
var secondKey = state.CreateKey("Second client");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondKey.Secret);
await server.StartAsync();
Check((await client.PostAsJsonAsync("/v1/chat/completions", Body("hello"))).StatusCode == HttpStatusCode.NotFound, "OpenAI toggle must remove route");
await server.DisposeAsync();
state.Update(new BridgeSettings(port,RequireApiKey:false));
client.DefaultRequestHeaders.Authorization=null;
await server.StartAsync();
Check((await client.GetAsync("/v1/models")).IsSuccessStatusCode,"No-key mode must allow missing credentials");
using(var browser=new HttpRequestMessage(HttpMethod.Get,"/v1/models"))
{
 browser.Headers.Add("Origin","https://example.com");Check((await client.SendAsync(browser)).StatusCode==HttpStatusCode.Forbidden,"No-key mode must preserve browser-origin restrictions");
}
await server.DisposeAsync();
state.Update(state.Settings with{ThinkingLevel="high"});
ThinkingOptions? forwarded=null;
await using(var thinkingServer=new LocalServer(state,(_,_,_,_)=>throw new Exception("Legacy callback used"),catalog,(_,model,_,thinking,_)=>{forwarded=thinking;return Task.FromResult(new Completion("OK",model));}))
{
 await thinkingServer.StartAsync();
 using(var response=await client.PostAsJsonAsync("/v1/chat/completions",Body("OK")))
 {
  Check(response.IsSuccessStatusCode&&forwarded==new ThinkingOptions("extended","high"),"Saved thinking default must reach the web callback");
  Check(response.Headers.GetValues("X-Local-Claude-Thinking").Single()=="mode=extended;effort=high","Effective thinking controls must be observable");
 }
 using(var response=await client.PostAsJsonAsync("/v1/chat/completions",new{messages=new[]{new{role="user",content="OK"}},reasoning_effort="none"}))
  Check(response.IsSuccessStatusCode&&forwarded?.Mode=="off","Explicit API off must override saved high");
 foreach(var level in new[]{"low","medium","high","xhigh","max"})
 {
  using var response=await client.PostAsJsonAsync("/v1/chat/completions",new{messages=new[]{new{role="user",content="OK"}},reasoning_effort=level});
  Check(response.IsSuccessStatusCode&&forwarded==new ThinkingOptions("extended",level),"API thinking level must reach callback unchanged: "+level);
 }
}
Console.WriteLine($"PASS: {count} integration assertions. Synthetic backend only; no Claude account requests.");
