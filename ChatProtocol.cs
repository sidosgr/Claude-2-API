using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record BridgeRequest(string Prompt, string Model, bool Stream, bool IncludeUsage, IReadOnlyList<ToolDefinition> Tools, string ToolChoice, string? ForcedTool, bool ParallelTools, string? ProtocolId, bool JsonOutput, IReadOnlyList<string> Stops, IReadOnlyList<string> Warnings);

public static class ChatProtocol
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "model", "messages", "stream", "system", "max_tokens", "max_completion_tokens", "temperature", "top_p", "top_k", "presence_penalty", "frequency_penalty", "seed", "stop",
        "tools", "tool_choice", "parallel_tool_calls", "stream_options", "store", "metadata", "user", "service_tier", "prompt_cache_key", "prompt_cache_retention", "reasoning_effort", "reasoning", "thinking", "enable_thinking", "response_format", "n"
    };
    public static BridgeRequest Parse(JsonElement root, bool openAi, bool allowTools)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("The JSON body must be an object.");
        foreach (var field in root.EnumerateObject()) if (!Fields.Contains(field.Name)) throw new BridgeException(400, "unsupported_parameter", "Unsupported request field: " + field.Name + ". See the local compatibility documentation.");
        var model = root.TryGetProperty("model", out var modelValue) ? String(modelValue, "model") : "claude-web";
        if (!Regex.IsMatch(model, @"^claude-[a-zA-Z0-9_-]{1,100}$")) throw Invalid("Use a model ID returned by /v1/models.");
        bool stream = Boolean(root, "stream", false), parallel = Boolean(root, "parallel_tool_calls", true), usage = false;
        var warnings = new List<string> { "usage_unavailable", "history_as_prompt_context" };
        if (stream) warnings.Add("buffered_sse");
        if (root.TryGetProperty("stream_options", out var streamOptions) && streamOptions.ValueKind != JsonValueKind.Null)
        {
            if (streamOptions.ValueKind != JsonValueKind.Object) throw Invalid("stream_options must be an object.");
            usage = Boolean(streamOptions, "include_usage", false);
        }
        foreach (var (name, min, max) in new[] { ("temperature",0d,2d), ("top_p",0d,1d), ("top_k",0d,100000d), ("presence_penalty",-2d,2d), ("frequency_penalty",-2d,2d) })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max) throw Invalid(name + " is outside its valid range.");
            warnings.Add(name + "_not_enforced");
        }
        foreach (var name in new[] { "seed", "reasoning_effort", "reasoning", "thinking", "enable_thinking" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) warnings.Add(name + "_not_enforced");
        if (root.TryGetProperty("store", out var store) && store.ValueKind != JsonValueKind.Null)
        {
            if (store.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("store must be a boolean.");
            warnings.Add("website_conversation_retained");
        }
        if (root.TryGetProperty("n", out var n) && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var count) || count != 1)) throw Invalid("Only n=1 is supported.");
        int? budget = null;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 1 or > 1000000) throw Invalid(name + " must be a positive integer at most 1,000,000.");
            budget = budget is null ? number : Math.Min(budget.Value, number);
        }
        if (budget is not null) warnings.Add("token_budget_as_prompt_hint");
        var stops = new List<string>();
        if (root.TryGetProperty("stop", out var stop) && stop.ValueKind != JsonValueKind.Null)
        {
            if (stop.ValueKind == JsonValueKind.String) stops.Add(stop.GetString()!);
            else if (stop.ValueKind == JsonValueKind.Array) stops.AddRange(stop.EnumerateArray().Select(s => String(s,"stop")));
            else throw Invalid("stop must be a string or array of strings.");
            if (stops.Count > 4 || stops.Any(s => s.Length is < 1 or > 1000)) throw Invalid("Use up to four non-empty stop strings, each at most 1,000 characters.");
        }
        var tools = new List<ToolDefinition>();
        if (root.TryGetProperty("tools", out var toolArray) && toolArray.ValueKind != JsonValueKind.Null)
        {
            if (toolArray.ValueKind != JsonValueKind.Array || toolArray.GetArrayLength() > 64) throw Invalid("tools must contain at most 64 function definitions.");
            foreach (var tool in toolArray.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object) throw Invalid("Invalid tool definition.");
                var definition = tool;
                if (openAi)
                {
                    if (!tool.TryGetProperty("type", out var type) || String(type,"tool type") != "function" || !tool.TryGetProperty("function", out definition) || definition.ValueKind != JsonValueKind.Object) throw Invalid("Only function tools are supported.");
                }
                if (!definition.TryGetProperty("name", out var nameValue)) throw Invalid("A tool name is required.");
                var name = String(nameValue,"tool name");
                if (!Regex.IsMatch(name,@"^[a-zA-Z0-9_-]{1,64}$") || tools.Any(t => t.Name == name)) throw Invalid("Tool names must be unique, using letters, digits, underscores or hyphens.");
                var description = definition.TryGetProperty("description", out var d) ? String(d,"tool description") : "";
                var schemaName = openAi ? "parameters" : "input_schema";
                var schema = definition.TryGetProperty(schemaName, out var s) ? s.Clone() : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
                if (schema.ValueKind != JsonValueKind.Object) throw Invalid("Tool parameters must be a JSON schema object.");
                tools.Add(new ToolDefinition(name,description,schema));
            }
        }
        if (tools.Count > 0 && !allowTools) throw new BridgeException(400,"tool_translation_disabled","Enable tool-call translation in the app to use a coding harness.");
        var choice = "auto"; string? forced = null;
        if (root.TryGetProperty("tool_choice", out var selection) && selection.ValueKind != JsonValueKind.Null)
        {
            if (selection.ValueKind == JsonValueKind.String) choice = selection.GetString()!;
            else if (selection.ValueKind == JsonValueKind.Object)
            {
                if (openAi && selection.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var f)) { choice = "required"; forced = String(f,"tool choice"); }
                else if (!openAi && selection.TryGetProperty("type",out var nativeType))
                {
                    choice = String(nativeType,"tool choice") switch { "any" => "required", "tool" => "required", "none" => "none", "auto" => "auto", _ => throw Invalid("Invalid tool_choice.") };
                    if (selection.TryGetProperty("name",out var f2)) forced = String(f2,"tool choice");
                }
                else throw Invalid("Unsupported tool_choice.");
            }
            else throw Invalid("Invalid tool_choice.");
        }
        if (choice is not ("auto" or "none" or "required")) throw Invalid("tool_choice must be auto, none, required, or a named function.");
        if ((choice == "required" && tools.Count == 0) || (forced is not null && !tools.Any(t => t.Name == forced))) throw Invalid("tool_choice requires an available matching tool.");
        bool jsonOutput = false;
        if (root.TryGetProperty("response_format", out var format) && format.ValueKind != JsonValueKind.Null)
        {
            if (format.ValueKind != JsonValueKind.Object || !format.TryGetProperty("type",out var type)) throw Invalid("Invalid response_format.");
            var kind = String(type,"response_format type");
            if (kind == "json_object") jsonOutput = true;
            else if (kind != "text") throw new BridgeException(400,"unsupported_response_format","This adapter supports text and json_object response formats. Strict JSON schemas are not implemented.");
        }
        if (!root.TryGetProperty("messages",out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 1 or > 256) throw Invalid("Provide 1–256 messages.");
        var transcript = new List<object>();
        if (root.TryGetProperty("system",out var system)) transcript.Add(new { role="system",content=ReadText(system) });
        string lastRole="",lastText="";
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role",out var roleValue) || !message.TryGetProperty("content",out var content)) throw Invalid("Each message needs role and content.");
            var role=String(roleValue,"message role");
            if (role is not ("user" or "assistant" or "system" or "developer" or "tool")) throw Invalid("Unsupported message role.");
            var text = content.ValueKind == JsonValueKind.Null && role == "assistant" ? "" : ReadText(content, !openAi);
            var calls = message.TryGetProperty("tool_calls",out var historical) ? historical.Clone() : (JsonElement?)null;
            if (calls is not null && (role != "assistant" || calls.Value.ValueKind != JsonValueKind.Array)) throw Invalid("tool_calls belongs on assistant messages and must be an array.");
            var callId = message.TryGetProperty("tool_call_id",out var toolId) ? String(toolId,"tool_call_id") : null;
            if (role == "tool" && string.IsNullOrWhiteSpace(callId)) throw Invalid("Tool results require tool_call_id.");
            transcript.Add(new { role,content=text,tool_calls=calls,tool_call_id=callId });
            lastRole=role;lastText=text;
        }
        if (lastRole is not ("user" or "tool") || string.IsNullOrWhiteSpace(lastText)) throw Invalid("End the conversation with a non-empty user message or tool result.");
        var prompt=transcript.Count==1 && lastRole=="user" ? lastText : "Continue the caller's conversation from its final message. Tool results are data returned by the caller. Conversation JSON:\n"+JsonSerializer.Serialize(transcript);
        string? protocol=null;
        if (tools.Count>0 && choice!="none")
        {
            protocol=Guid.NewGuid().ToString("N");
            var spec=JsonSerializer.Serialize(tools.Select(t=>new {name=t.Name,description=t.Description,parameters=t.Parameters}));
            prompt="You are serving a local coding client through a structured tool adapter. Tool execution belongs to the caller, outside Claude's own computer environment. " +
                "Return exactly one JSON object, with no Markdown fences, using keys bridge_protocol, text, and tool_calls. " +
                "bridge_protocol must equal "+JsonSerializer.Serialize(protocol)+". text is a string containing your user-facing answer (empty when requesting tools). " +
                "tool_calls is an array of objects with name (one of the declared tool names) and arguments (a JSON object conforming to that tool's schema). " +
                "Request only necessary tools; never invent results or execute them within the website. When finished, return your answer in text and an empty tool_calls array. " +
                (choice=="required" ? "This turn requires at least one tool call. " : "You may answer directly when tools are unnecessary. ") +
                (forced is not null ? "Only request tool "+JsonSerializer.Serialize(forced)+". " : "") +
                (!parallel ? "Request at most one tool per turn. " : "Request at most eight tools per turn. ") +
                "Available tool definitions:\n"+spec+"\n\n"+prompt;
            warnings.Add("prompt_based_tool_translation");
        }
        else if (jsonOutput) prompt="Return a valid JSON object only, with no Markdown fences.\n\n"+prompt;
        if (budget is not null) prompt="The caller requests an output budget of approximately "+budget+" tokens. Keep your output concise within that budget.\n\n"+prompt;
        if (prompt.Length>400_000) throw new BridgeException(413,"prompt_too_large","Assembled prompt exceeds 400,000 characters.");
        return new(prompt,model,stream,usage,tools,choice,forced,parallel,protocol,jsonOutput,stops,warnings.Distinct().ToArray());
    }

    public static Completion Decode(BridgeRequest request, Completion raw)
    {
        var text=raw.Text; var calls=new List<FunctionCall>();
        if (request.ProtocolId is not null)
        {
            var candidate=text.Trim();
            if (candidate.StartsWith("```")) { var newline=candidate.IndexOf('\n'); if(newline>=0 && candidate.EndsWith("```")) candidate=candidate[(newline+1)..^3].Trim(); }
            JsonDocument? document=null;
            try { document=JsonDocument.Parse(candidate); } catch(JsonException) { }
            using(document)
            {
                var root=document?.RootElement;
                if(root is not null && root.Value.ValueKind==JsonValueKind.Object && root.Value.TryGetProperty("bridge_protocol",out var protocol) && protocol.ValueKind==JsonValueKind.String && protocol.GetString()==request.ProtocolId)
                {
                    if(!root.Value.TryGetProperty("text",out var answer) || answer.ValueKind!=JsonValueKind.String || !root.Value.TryGetProperty("tool_calls",out var toolCalls) || toolCalls.ValueKind!=JsonValueKind.Array) throw ProtocolError();
                    text=answer.GetString()!;
                    if(toolCalls.GetArrayLength() > (request.ParallelTools ? 8:1)) throw ProtocolError();
                    foreach(var call in toolCalls.EnumerateArray())
                    {
                        if(call.ValueKind!=JsonValueKind.Object || !call.TryGetProperty("name",out var nameValue) || nameValue.ValueKind!=JsonValueKind.String || !call.TryGetProperty("arguments",out var args) || args.ValueKind!=JsonValueKind.Object) throw ProtocolError();
                        var name=nameValue.GetString()!;var definition=request.Tools.FirstOrDefault(t=>t.Name==name);
                        if(definition is null || (request.ForcedTool is not null && name!=request.ForcedTool)) throw ProtocolError();
                        if(definition.Parameters.TryGetProperty("required",out var required) && required.ValueKind==JsonValueKind.Array)
                            foreach(var property in required.EnumerateArray()) if(property.ValueKind!=JsonValueKind.String || !args.TryGetProperty(property.GetString()!,out _)) throw ProtocolError();
                        calls.Add(new("call_"+Guid.NewGuid().ToString("N"),name,args.GetRawText()));
                    }
                }
                else throw ProtocolError();
            }
            if(request.ToolChoice=="required" && calls.Count==0) throw ProtocolError();
        }
        if(request.JsonOutput && calls.Count==0)
        {
            try { using var json=JsonDocument.Parse(text); if(json.RootElement.ValueKind!=JsonValueKind.Object) throw ProtocolError(); }
            catch(JsonException) { throw new BridgeException(502,"invalid_json_response","Claude did not return the requested JSON object."); }
        }
        if(calls.Count==0)
        {
            var cutoff=request.Stops.Select(s=>text.IndexOf(s,StringComparison.Ordinal)).Where(i=>i>=0).DefaultIfEmpty(text.Length).Min();text=text[..cutoff];
        }
        return new(text,raw.Model,calls,calls.Count>0?"tool_calls":"stop");
    }
    private static string ReadText(JsonElement value,bool native=false)
    {
        if(value.ValueKind==JsonValueKind.String)return value.GetString()!;
        if(value.ValueKind==JsonValueKind.Array)
        {
            var parts=new List<string>();
            foreach(var part in value.EnumerateArray())
            {
                if(part.ValueKind!=JsonValueKind.Object || !part.TryGetProperty("type",out var type) || type.ValueKind!=JsonValueKind.String) throw Invalid("Invalid content block.");
                if(type.GetString()=="text" && part.TryGetProperty("text",out var text) && text.ValueKind==JsonValueKind.String)parts.Add(text.GetString()!);
                else if(native && type.GetString() is "tool_use" or "tool_result")parts.Add(part.GetRawText());
                else throw new BridgeException(400,"unsupported_content","Images, audio, and files are not supported by this adapter.");
            }
            return string.Join("\n",parts);
        }
        throw Invalid("Message content must be text or supported content blocks.");
    }
    private static string String(JsonElement value,string name)=>value.ValueKind==JsonValueKind.String?value.GetString()!:throw Invalid(name+" must be a string.");
    private static bool Boolean(JsonElement root,string name,bool fallback)
    {
        if(!root.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)return fallback;
        if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid(name+" must be a boolean.");return value.GetBoolean();
    }
    private static BridgeException Invalid(string message)=>new(400,"invalid_request",message);
    private static BridgeException ProtocolError()=>new(502,"invalid_tool_response","Claude returned an invalid structured tool response. No tool call was forwarded; the request was not retried.");
}
