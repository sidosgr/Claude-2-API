using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace ClaudeLocalBridge;

public sealed class LocalServer(BridgeState state, Func<string,string,int,CancellationToken,Task<Completion>> complete, ModelCatalog? catalog = null, Func<string,string,int,ThinkingOptions,CancellationToken,Task<Completion>>? completeThinking = null) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly ModelCatalog models=catalog??new();
    private readonly SemaphoreSlim generation=new(1,1);
    public int Port { get; private set; }
    public bool Running=>app is not null;
    public async Task StartAsync()
    {
        if(app is not null)return;
        var settings=state.Settings;
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],EnvironmentName="Production"});
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options=>
        {
            options.Listen(IPAddress.Loopback,settings.Port);
            options.Limits.MaxRequestBodySize=1024*1024;
            options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);
        });
        var server=builder.Build();
        server.Use(async(context,next)=>
        {
            context.Response.Headers["Cache-Control"]="no-store";
            context.Response.Headers["X-Content-Type-Options"]="nosniff";
            if(context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) || context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Headers.ContainsKey("Origin"))
            {await Error(context,403,"local_clients_only","This endpoint accepts local non-browser clients only.");return;}
            if(context.Request.Path=="/health"){await next(context);return;}
            if(settings.RequireApiKey)
            {
                string? key=null;
                if(context.Request.Headers.ContainsKey("Authorization"))
                {
                    if(AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(),out var auth) && auth.Scheme.Equals("Bearer",StringComparison.OrdinalIgnoreCase))key=auth.Parameter?.Trim();
                }
                else if(context.Request.Headers.TryGetValue("x-api-key",out var nativeKey) && nativeKey.Count==1)key=nativeKey.ToString().Trim();
                if(key is null || !state.Authenticate(key)){await Error(context,401,"invalid_api_key","Supply an active local key as Authorization: Bearer <key> or x-api-key. Key authentication can be disabled in the app's endpoint settings.");return;}
            }
            await next(context);
        });
        server.MapGet("/health",()=>Results.Json(new{status="running",backend="claude-web-experimental",authentication_required=settings.RequireApiKey}));
        server.MapGet("/v1/models",()=>
        {
            var available=models.Snapshot;
            if(available.Count==0)available=[new("claude-web","Configured default — refresh models in app")];
            return Results.Json(new{@object="list",data=available.Select(m=>new{id=m.Id,name=m.Name,@object="model",created=0,owned_by="claude-web",source="web_picker"})});
        });
        if(settings.OpenAi)server.MapPost("/v1/chat/completions",context=>Handle(context,true,settings,server.Lifetime.ApplicationStopping));
        server.MapPost("/v1/messages",context=>Handle(context,false,settings,server.Lifetime.ApplicationStopping));
        try{await server.StartAsync();app=server;Port=settings.Port;}catch{await server.DisposeAsync();throw;}
    }
    private async Task Handle(HttpContext context,bool openAi,BridgeSettings settings,CancellationToken stopping)
    {
        var entered=false;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,stopping);
        try
        {
            if(context.Request.ContentType is null || !context.Request.ContentType.StartsWith("application/json",StringComparison.OrdinalIgnoreCase))throw new BridgeException(415,"json_required","Use Content-Type: application/json.");
            using var document=await JsonDocument.ParseAsync(context.Request.Body,cancellationToken:cancellation.Token);
            var request=ChatProtocol.Parse(document.RootElement,openAi,settings.ToolCalling);
            var model=models.Resolve(request.Model,settings.Model);
            var thinking=request.HasThinking?request.Thinking:ThinkingOptions.FromLevel(settings.ThinkingLevel);
            context.Response.Headers["X-Local-Claude-Thinking"]="mode="+thinking.Mode+";effort="+thinking.Effort;
            var warnings=request.Warnings.AsEnumerable();
            if(!request.HasThinking&&thinking.Mode!="off")warnings=warnings.Concat(new[]{"experimental_web_thinking_passthrough","thinking_output_not_returned"});
            context.Response.Headers["X-Local-Claude-Compatibility"]=string.Join(",",warnings.Distinct());
            entered=await generation.WaitAsync(0,cancellation.Token);
            if(!entered)throw new BridgeException(429,"request_in_progress","One Claude request is already running. Try again after it finishes.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var raw=completeThinking is null ? await complete(request.Prompt,model,settings.TimeoutSeconds,timeout.Token) : await completeThinking(request.Prompt,model,settings.TimeoutSeconds,thinking,timeout.Token);
            var result=ChatProtocol.Decode(request,raw);
            if(openAi && request.Stream)await WriteOpenAiSse(context,request,result);
            else if(openAi)
            {
                var message=new Dictionary<string,object?>{{"role","assistant"},{"content",string.IsNullOrEmpty(result.Text)&&result.ToolCalls?.Count>0?null:result.Text}};
                if(result.ToolCalls?.Count>0)message["tool_calls"]=result.ToolCalls.Select(c=>new{id=c.Id,type="function",function=new{name=c.Name,arguments=c.Arguments}});
                await context.Response.WriteAsJsonAsync(new{id="chatcmpl-"+Guid.NewGuid().ToString("N"),@object="chat.completion",created=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),model=result.Model,
                    choices=new[]{new{index=0,message,finish_reason=result.FinishReason}},usage=(object?)null},cancellation.Token);
            }
            else if(request.Stream)await WriteNativeSse(context,result);
            else await context.Response.WriteAsJsonAsync(NativeMessage(result),cancellation.Token);
        }
        catch(BridgeException ex){await Error(context,ex.Status,ex.Code,ex.Message);}
        catch(JsonException){await Error(context,400,"invalid_json","The request must contain valid JSON.");}
        catch(BadHttpRequestException ex){await Error(context,ex.StatusCode,"request_rejected","The request is too large or malformed. Maximum body size: 1 MiB.");}
        catch(OperationCanceledException){if(!context.RequestAborted.IsCancellationRequested)await Error(context,504,"request_cancelled","The request timed out or the endpoint was stopped. It was not retried.");}
        catch(Exception){await Error(context,502,"bridge_error","The browser bridge could not complete this request.");}
        finally{if(entered)generation.Release();}
    }
    private static List<object> NativeContent(Completion result)
    {
        var content=new List<object>();
        if(!string.IsNullOrEmpty(result.Text))content.Add(new{type="text",text=result.Text});
        foreach(var call in result.ToolCalls??[])
        {using var args=JsonDocument.Parse(call.Arguments);content.Add(new{type="tool_use",id=call.Id,name=call.Name,input=args.RootElement.Clone()});}
        return content;
    }
    private static object NativeMessage(Completion result)=>new{id="msg_"+Guid.NewGuid().ToString("N"),type="message",role="assistant",model=result.Model,content=NativeContent(result),stop_reason=result.ToolCalls?.Count>0?"tool_use":"end_turn",stop_sequence=(string?)null,usage=(object?)null};
    private static async Task WriteOpenAiSse(HttpContext context,BridgeRequest request,Completion result)
    {
        context.Response.ContentType="text/event-stream";
        var id="chatcmpl-"+Guid.NewGuid().ToString("N");var created=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object Chunk(object delta,string? finish)=>new{id,@object="chat.completion.chunk",created,model=result.Model,choices=new[]{new{index=0,delta,finish_reason=finish}}};
        async Task Send(object chunk)=>await context.Response.WriteAsync("data: "+JsonSerializer.Serialize(chunk)+"\n\n",context.RequestAborted);
        await Send(Chunk(new{role="assistant",content=result.Text},null));
        for(var i=0;i<(result.ToolCalls?.Count??0);i++)
        {var call=result.ToolCalls![i];await Send(Chunk(new{tool_calls=new[]{new{index=i,id=call.Id,type="function",function=new{name=call.Name,arguments=call.Arguments}}}},null));}
        await Send(Chunk(new{},result.FinishReason));
        if(request.IncludeUsage)await Send(new{id,@object="chat.completion.chunk",created,model=result.Model,choices=Array.Empty<object>(),usage=(object?)null});
        await context.Response.WriteAsync("data: [DONE]\n\n",context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    private static async Task WriteNativeSse(HttpContext context,Completion result)
    {
        context.Response.ContentType="text/event-stream";
        async Task Send(string name,object data)=>await context.Response.WriteAsync("event: "+name+"\ndata: "+JsonSerializer.Serialize(data)+"\n\n",context.RequestAborted);
        var id="msg_"+Guid.NewGuid().ToString("N");
        await Send("message_start",new{type="message_start",message=new{id,type="message",role="assistant",model=result.Model,content=Array.Empty<object>(),stop_reason=(string?)null,usage=(object?)null}});
        int index=0;
        if(!string.IsNullOrEmpty(result.Text))
        {
            await Send("content_block_start",new{type="content_block_start",index,content_block=new{type="text",text=""}});
            await Send("content_block_delta",new{type="content_block_delta",index,delta=new{type="text_delta",text=result.Text}});
            await Send("content_block_stop",new{type="content_block_stop",index});index++;
        }
        foreach(var call in result.ToolCalls??[])
        {
            await Send("content_block_start",new{type="content_block_start",index,content_block=new{type="tool_use",id=call.Id,name=call.Name,input=new{}}});
            await Send("content_block_delta",new{type="content_block_delta",index,delta=new{type="input_json_delta",partial_json=call.Arguments}});
            await Send("content_block_stop",new{type="content_block_stop",index});index++;
        }
        await Send("message_delta",new{type="message_delta",delta=new{stop_reason=result.ToolCalls?.Count>0?"tool_use":"end_turn",stop_sequence=(string?)null},usage=(object?)null});
        await Send("message_stop",new{type="message_stop"});await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    private static async Task Error(HttpContext context,int status,string code,string message)
    {
        if(context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)return;
        context.Response.StatusCode=status;await context.Response.WriteAsJsonAsync(new{error=new{message,type="bridge_error",code}});
    }
    public async ValueTask DisposeAsync()
    {
        if(app is null)return;var closing=app;app=null;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try{await closing.StopAsync(timeout.Token);}finally{await closing.DisposeAsync();}
    }
}
