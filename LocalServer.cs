using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace ClaudeLocalBridge;

public sealed class LocalServer(BridgeState state, Func<string, string, int, CancellationToken, Task<Completion>> complete) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly SemaphoreSlim generation = new(1, 1);
    public int Port { get; private set; }
    public bool Running => app is not null;

    public async Task StartAsync()
    {
        if (app is not null) return;
        var settings = state.Settings;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, settings.Port);
            options.Limits.MaxRequestBodySize = 256 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        var server = builder.Build();
        server.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
                || context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Headers.ContainsKey("Origin"))
            { await Error(context, 403, "local_clients_only", "This endpoint accepts local non-browser clients only."); return; }
            if (context.Request.Path == "/health") { await next(context); return; }
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !state.Authenticate(auth[7..]))
            { await Error(context, 401, "invalid_api_key", "Supply an active local API key as Authorization: Bearer <key>."); return; }
            await next(context);
        });
        server.MapGet("/health", () => Results.Json(new { status = "running", backend = "claude-web-experimental" }));
        server.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = "claude-web", @object = "model", created = 0, owned_by = "local" } } }));
        if (settings.OpenAi) server.MapPost("/v1/chat/completions", context => Handle(context, true, settings, server.Lifetime.ApplicationStopping));
        server.MapPost("/v1/messages", context => Handle(context, false, settings, server.Lifetime.ApplicationStopping));
        try { await server.StartAsync(); app = server; Port = settings.Port; }
        catch { await server.DisposeAsync(); throw; }
    }

    private async Task Handle(HttpContext context, bool openAi, BridgeSettings settings, CancellationToken stopping)
    {
        var entered = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            if (context.Request.ContentType is null || !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException(415, "json_required", "Use Content-Type: application/json.");
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation.Token);
            var (prompt, stream) = ParseRequest(document.RootElement, openAi);
            entered = await generation.WaitAsync(0, cancellation.Token);
            if (!entered) throw new BridgeException(429, "request_in_progress", "One Claude request is already running. Try again after it finishes.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var result = await complete(prompt, settings.Model, settings.TimeoutSeconds, timeout.Token);
            if (openAi && stream) await WriteOpenAiSse(context, result);
            else if (openAi)
                await context.Response.WriteAsJsonAsync(new { id = "chatcmpl-" + Guid.NewGuid().ToString("N"), @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "claude-web",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = result.Text }, finish_reason = "stop" } } }, cancellation.Token);
            else
                await context.Response.WriteAsJsonAsync(new { id = "msg_" + Guid.NewGuid().ToString("N"), type = "message", role = "assistant", model = "claude-web",
                    content = new[] { new { type = "text", text = result.Text } }, stop_reason = "end_turn", stop_sequence = (string?)null }, cancellation.Token);
        }
        catch (BridgeException ex) { await Error(context, ex.Status, ex.Code, ex.Message); }
        catch (JsonException) { await Error(context, 400, "invalid_json", "The request must contain a valid JSON object."); }
        catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "request_rejected", "The request is too large or malformed. Maximum body size: 256 KiB."); }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested) await Error(context, 504, "request_cancelled", "The request timed out or the endpoint was stopped. It was not retried.");
        }
        catch (Exception) { await Error(context, 502, "bridge_error", "The browser bridge could not complete this request."); }
        finally { if (entered) generation.Release(); }
    }

    public static (string Prompt, bool Stream) ParseRequest(JsonElement root, bool openAi)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new BridgeException(400, "invalid_request", "The JSON body must be an object.");
        var allowed = openAi ? new[] { "model", "messages", "stream" } : new[] { "model", "messages", "stream", "system" };
        foreach (var field in root.EnumerateObject())
            if (!allowed.Contains(field.Name)) throw new BridgeException(400, "unsupported_parameter", "This experimental adapter supports text messages, model, and stream only. Sampling, token budgets, tools, images, and response formats are unavailable.");
        if (root.TryGetProperty("model", out var model) && (model.ValueKind != JsonValueKind.String || model.GetString() != "claude-web"))
            throw new BridgeException(400, "model_not_found", "Use the local model alias claude-web. Set the upstream model in the app.");
        var streaming = false;
        if (root.TryGetProperty("stream", out var stream))
        {
            if (stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException(400, "invalid_stream", "stream must be a boolean.");
            streaming = stream.GetBoolean();
        }
        if (!openAi && streaming) throw new BridgeException(400, "unsupported_stream", "Native Messages streaming is not implemented. Use OpenAI chat completions for buffered SSE output.");
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 1 or > 100)
            throw new BridgeException(400, "invalid_messages", "Provide 1–100 text messages, ending with a user message.");
        var transcript = new List<object>();
        if (root.TryGetProperty("system", out var system)) transcript.Add(new { role = "system", content = ReadText(system) });
        string lastRole = "", lastText = "";
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                role.GetString() is not ("user" or "assistant" or "system" or "developer") || !message.TryGetProperty("content", out var content))
                throw new BridgeException(400, "invalid_message", "Each message needs a supported role and text content.");
            lastRole = role.GetString()!; lastText = ReadText(content);
            transcript.Add(new { role = lastRole, content = lastText });
        }
        if (lastRole != "user" || string.IsNullOrWhiteSpace(lastText)) throw new BridgeException(400, "invalid_messages", "End the conversation with a non-empty user message.");
        var prompt = transcript.Count == 1 ? lastText : "The caller supplies this conversation as JSON. Continue it by answering the final user message.\n" + JsonSerializer.Serialize(transcript);
        if (prompt.Length > 100_000) throw new BridgeException(413, "prompt_too_large", "Prompt exceeds the 100,000 character limit.");
        return (prompt, streaming);
    }
    private static string ReadText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text"
                    || !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new BridgeException(400, "unsupported_content", "Only text content is supported.");
                texts.Add(text.GetString()!);
            }
            return string.Join("\n", texts);
        }
        throw new BridgeException(400, "unsupported_content", "Only text content is supported.");
    }
    private static async Task WriteOpenAiSse(HttpContext context, Completion result)
    {
        context.Response.ContentType = "text/event-stream";
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object Chunk(object delta, string? finish) => new { id, @object = "chat.completion.chunk", created, model = "claude-web", choices = new[] { new { index = 0, delta, finish_reason = finish } } };
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(Chunk(new { role = "assistant", content = result.Text }, null)) + "\n\n", context.RequestAborted);
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(Chunk(new { }, "stop")) + "\n\ndata: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    private static async Task Error(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = new { message, type = "bridge_error", code } });
    }
    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        var closing = app; app = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await closing.StopAsync(timeout.Token); } finally { await closing.DisposeAsync(); }
    }
}
