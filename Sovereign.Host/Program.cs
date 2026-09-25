using System.Collections.Concurrent;
using System.Text.Json;
using Sovereign.Bedrock;
using Sovereign.Host;
var command=args.FirstOrDefault()??"help";
if(command=="help"){Console.WriteLine("sovereign <serve|token|hash|embed|guard|normalize> [text]\nSet SOVEREIGN_JWT_SECRET (32+ random bytes) before serve/token.");return;}
if(command is "hash" or "embed" or "guard" or "normalize"){Console.WriteLine(JsonSerializer.Serialize(LocalTools.Run(command,string.Join(' ',args.Skip(1)))));return;}
var jwt=new Jwt(Environment.GetEnvironmentVariable("SOVEREIGN_JWT_SECRET")??"");
if(command=="token"){Console.WriteLine(jwt.Issue("local-owner",DateTimeOffset.UtcNow));return;}
if(command!="serve")throw new ArgumentException("Unknown command.");
var builder=WebApplication.CreateBuilder(Array.Empty<string>());builder.WebHost.UseUrls("http://127.0.0.1:5080");builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=1024*1024);
var origin=Environment.GetEnvironmentVariable("SOVEREIGN_ALLOWED_ORIGIN")??"http://127.0.0.1:5173";
builder.Services.AddCors(o=>o.AddDefaultPolicy(p=>p.WithOrigins(origin).WithMethods("GET","POST").WithHeaders("Authorization","Content-Type")));
var app=builder.Build();app.UseCors();
var documents=new ConcurrentDictionary<string,ConcurrentDictionary<int,Document>>();var audit=new ConcurrentQueue<AuditEvent>();int id=0;
var model=Environment.GetEnvironmentVariable("SOVEREIGN_MODEL")??"";var endpoint=new Uri(Environment.GetEnvironmentVariable("SOVEREIGN_MODEL_ENDPOINT")??"http://127.0.0.1:11434/");
using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60),MaxResponseContentBufferSize=4*1024*1024};var backend=new BedrockBackend(http,endpoint,model);
app.Use(async(context,next)=>{
    var auth=context.Request.Headers.Authorization.ToString();
    if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)||!jwt.Validate(auth[7..],DateTimeOffset.UtcNow,out var subject)){context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new{error="A valid, unexpired playground JWT is required."});return;}
    context.Items["subject"]=subject;
    try{await next(context);}
    catch(ArgumentException e){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=e.Message});}
    catch(HttpRequestException){context.Response.StatusCode=502;await context.Response.WriteAsJsonAsync(new{error="Local model endpoint failed. Check its model and server status."});}
    catch(TaskCanceledException)when(!context.RequestAborted.IsCancellationRequested){context.Response.StatusCode=504;await context.Response.WriteAsJsonAsync(new{error="Local model request timed out."});}
    finally{audit.Enqueue(new(subject,context.Request.Path.Value??"",context.Response.StatusCode));while(audit.Count>1000)audit.TryDequeue(out _);}
});
app.MapGet("/api/status",()=>new{service="Sovereign .NET",authentication="HS256 JWT",aws=false,modelConfigured=!string.IsNullOrWhiteSpace(model)});
app.MapGet("/api/models",()=>new{models=string.IsNullOrWhiteSpace(model)?Array.Empty<string>():new[]{model}});
app.MapPost("/api/tools",(ToolRequest r)=>new{value=LocalTools.Run(r.Operation,r.Text)});
app.MapPost("/api/invoke",async(PromptRequest r,CancellationToken cancel)=>{
    LocalTools.Input(r.Prompt);if(LocalTools.Guard(r.Prompt)!=0)return Results.BadRequest(new{error="Secret-label guard blocked this prompt."});
    if(string.IsNullOrWhiteSpace(model))return Results.Json(new{error="Set SOVEREIGN_MODEL to a model installed on your local server."},statusCode:503);
    return Results.Ok(new{text=await backend.GenerateAsync(new[]{("user",r.Prompt)},cancellationToken:cancel)});
});
app.MapPost("/api/documents",(DocumentRequest r,HttpContext c)=>{
    if(string.IsNullOrWhiteSpace(r.Title)||r.Title.Length>256||string.IsNullOrWhiteSpace(r.Text))throw new ArgumentException("Document requires a title (up to 256 characters) and text.");
    var collection=documents.GetOrAdd((string)c.Items["subject"]!,_=>new());lock(collection){if(collection.Count>=100)return Results.BadRequest(new{error="100-document limit reached."});var d=new Document(Interlocked.Increment(ref id),r.Title,r.Text,LocalTools.Embed(r.Text));collection[d.Id]=d;return Results.Ok(new{d.Id,d.Title});}
});
app.MapPost("/api/retrieve",(PromptRequest r,HttpContext c)=>{var q=LocalTools.Embed(r.Prompt);var collection=documents.GetOrAdd((string)c.Items["subject"]!,_=>new());return collection.Values.Select(d=>new{d.Id,d.Title,d.Text,score=LocalTools.Cosine(q,d.Vector)}).OrderByDescending(d=>d.score).ThenBy(d=>d.Id).Take(5);});
app.MapPost("/api/batch",(BatchRequest r)=>{if(r.Texts is null||r.Texts.Length is <1 or >100)throw new ArgumentException("Batch requires 1–100 inputs.");return r.Texts.Select(text=>new{text,value=LocalTools.Run(r.Operation,text)}).ToArray();});
app.MapPost("/api/flows",(FlowRequest r)=>{if(r.Steps is null||r.Steps.Length is <1 or >16)throw new ArgumentException("Flow requires 1–16 steps.");var text=r.Text;var results=new List<object>();foreach(var step in r.Steps){var value=LocalTools.Run(step,text);results.Add(new{step,value});if(step=="guard"&&(uint)value!=0)break;if(step=="normalize")text=(string)value;}return results;});
app.MapGet("/api/audit",(HttpContext c)=>audit.ToArray().Where(e=>e.Subject==(string)c.Items["subject"]!));
await app.RunAsync();
public record ToolRequest(string Operation,string Text);
public record PromptRequest(string Prompt);
public record DocumentRequest(string Title,string Text);
public record Document(int Id,string Title,string Text,uint[] Vector);
public record BatchRequest(string Operation,string[] Texts);
public record FlowRequest(string Text,string[] Steps);
public record AuditEvent(string Subject,string Path,int Status);
