using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Sovereign.Bedrock;
using Sovereign.Host;
var command=args.FirstOrDefault()??"help";
if(command=="help"){Console.WriteLine("sovereign <serve|token|passwd|hash|embed|guard|normalize> [text]\nSet SOVEREIGN_JWT_SECRET (32+ random bytes) before serve/token.\npasswd prints a PBKDF2 hash for SOVEREIGN_ADMIN_PASSWORD_HASH; the password itself is never stored.");return;}
if(command is "hash" or "embed" or "guard" or "normalize"){Console.WriteLine(JsonSerializer.Serialize(LocalTools.Run(command,string.Join(' ',args.Skip(1)))));return;}
if(command=="passwd"){var value=string.Join(' ',args.Skip(1));if(value.Length==0)throw new ArgumentException("passwd requires the password to hash.");Console.WriteLine(Password.Hash(value));return;}
var jwt=new Jwt(Environment.GetEnvironmentVariable("SOVEREIGN_JWT_SECRET")??"");
if(command=="token"){Console.WriteLine(jwt.Issue("local-owner",DateTimeOffset.UtcNow));return;}
if(command!="serve")throw new ArgumentException("Unknown command.");
var builder=WebApplication.CreateBuilder(Array.Empty<string>());
// Loopback by default. A public SAML assertion consumer requires a routable listener,
// so the operator can opt into a wider bind explicitly.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("SOVEREIGN_BIND")??"http://127.0.0.1:5080");builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=1024*1024);
// Behind a TLS-terminating proxy the connection Kestrel sees is plain HTTP, so Request.IsHttps
// would be false and the session cookie would be issued without Secure. Trusting the forwarded
// headers restores it. This is safe only while the service stays bound to loopback, because
// those headers are then set by the local proxy and not by an outside caller.
builder.Services.Configure<ForwardedHeadersOptions>(o=>{
    o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();o.KnownProxies.Clear();
});
var origin=Environment.GetEnvironmentVariable("SOVEREIGN_ALLOWED_ORIGIN")??"http://127.0.0.1:5173";
builder.Services.AddCors(o=>o.AddDefaultPolicy(p=>p.WithOrigins(origin).WithMethods("GET","POST").WithHeaders("Authorization","Content-Type")));var app=builder.Build();app.UseForwardedHeaders();app.UseCors();
var documents=new ConcurrentDictionary<string,ConcurrentDictionary<int,Document>>();var audit=new ConcurrentQueue<AuditEvent>();int id=0;
var model=Environment.GetEnvironmentVariable("SOVEREIGN_MODEL")??"";var endpoint=new Uri(Environment.GetEnvironmentVariable("SOVEREIGN_MODEL_ENDPOINT")??"http://127.0.0.1:11434/");
using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60),MaxResponseContentBufferSize=4*1024*1024};var backend=new BedrockBackend(http,endpoint,model);
var idp=IdpMetadata.FromEnvironment();var samlProblem=idp.ConfigurationProblem();var saml=new SamlSp(idp);
var pending=new ConcurrentDictionary<string,DateTimeOffset>();
// The IdP posts the assertion back with no Authorization header, so the SAML endpoints sit
// outside the bearer middleware. They authenticate by signature instead.
app.Use(async(context,next)=>{
    // The SAML endpoints and the sign-in endpoints run before any bearer check: the identity
    // provider posts back without an Authorization header, and /auth/login is how one is obtained.
    if(context.Request.Path.StartsWithSegments("/saml")||context.Request.Path.StartsWithSegments("/auth")||context.Request.Path.StartsWithSegments("/login")){await next(context);return;}
    // A browser cannot attach an Authorization header to a page navigation, so a successful
    // sign-in also sets an HttpOnly session cookie. The token is the same short-lived JWT.
    var auth=context.Request.Headers.Authorization.ToString();
    var token=auth.StartsWith("Bearer ",StringComparison.Ordinal)?auth[7..]:context.Request.Cookies["sovereign_session"];
    if(string.IsNullOrEmpty(token)||!jwt.Validate(token,DateTimeOffset.UtcNow,out var subject)){
        // An unauthenticated request for the playground is sent to sign-in rather than
        // being answered with a bare 401, so a browser lands somewhere it can use.
        if(!context.Request.Path.StartsWithSegments("/api")){context.Response.Redirect("/login");return;}
        context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new{error="A valid, unexpired playground JWT is required."});return;
    }
    context.Items["subject"]=subject;
    try{await next(context);}
    catch(ArgumentException e){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=e.Message});}
    catch(HttpRequestException){context.Response.StatusCode=502;await context.Response.WriteAsJsonAsync(new{error="Local model endpoint failed. Check its model and server status."});}
    catch(TaskCanceledException)when(!context.RequestAborted.IsCancellationRequested){context.Response.StatusCode=504;await context.Response.WriteAsJsonAsync(new{error="Local model request timed out."});}
    finally{audit.Enqueue(new(subject,context.Request.Path.Value??"",context.Response.StatusCode));while(audit.Count>1000)audit.TryDequeue(out _);}
});
// --- SAML service provider -------------------------------------------------
// RelayState is a caller-controlled return URL, so it is bound to a one-time nonce
// that the browser supplies when the flow starts rather than trusted on its own.
var allowedReturn=Environment.GetEnvironmentVariable("SOVEREIGN_SAML_RETURN_ORIGIN")??"";

// --- Local password sign-in -------------------------------------------------
// Enabled only when SOVEREIGN_ADMIN_PASSWORD_HASH holds a PBKDF2 digest. Repeated
// failures from one client are throttled so the digest cannot be ground down cheaply.
var adminHash=Environment.GetEnvironmentVariable("SOVEREIGN_ADMIN_PASSWORD_HASH");
var adminUser=Environment.GetEnvironmentVariable("SOVEREIGN_ADMIN_USER")??"admin";
var failures=new ConcurrentDictionary<string,(int Count,DateTimeOffset Until)>();
app.MapGet("/auth/status",()=>new{passwordEnabled=!string.IsNullOrWhiteSpace(adminHash),samlEnabled=samlProblem is null});
app.MapPost("/auth/login",(HttpContext c)=>{
    if(string.IsNullOrWhiteSpace(adminHash))return Results.Problem("Password sign-in is not configured. Set SOVEREIGN_ADMIN_PASSWORD_HASH.",statusCode:503);
    var key=c.Connection.RemoteIpAddress?.ToString()??"unknown";
    if(failures.TryGetValue(key,out var state)){
        if(state.Until>DateTimeOffset.UtcNow)return Results.Json(new{error="Too many failed attempts. Try again shortly."},statusCode:429);
        if(state.Until<=DateTimeOffset.UtcNow)failures.TryRemove(key,out _);
    }
    var form=c.Request.HasFormContentType?c.Request.ReadFormAsync().GetAwaiter().GetResult():null;
    var user=form?["username"].ToString()??"";var secret=form?["password"].ToString()??"";
    if(!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(user),System.Text.Encoding.UTF8.GetBytes(adminUser))||!Password.Verify(adminHash,secret)){
        var now=DateTimeOffset.UtcNow;
        failures[key]=failures.TryGetValue(key,out var prior)&&prior.Until>now.AddMinutes(-15)?(prior.Count+1,prior.Count+1>=5?now.AddMinutes(5):prior.Until):(1,now.AddMinutes(5));
        return Results.Json(new{error="Incorrect username or password."},statusCode:401);
    }
    failures.TryRemove(key,out _);
    var issued=jwt.Issue(adminUser,DateTimeOffset.UtcNow);
    // HttpOnly keeps the token out of reach of page scripts; SameSite=Strict stops it being
    // sent on cross-site requests, which is what a CSRF'd sign-in would rely on.
    c.Response.Cookies.Append("sovereign_session",issued,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=c.Request.IsHttps,Path="/",MaxAge=TimeSpan.FromMinutes(15)});
    return Results.Ok(new{token=issued,expiresInSeconds=900});
});
app.MapGet("/saml/metadata",()=>samlProblem is not null?Results.Problem("SAML is not configured.",statusCode:503):Results.Text(saml.Metadata,"application/xml"));
app.MapGet("/saml/status",()=>new{enabled=samlProblem is null,problem=samlProblem,entityId=saml.EntityId,acs=saml.AssertionConsumerService.AbsoluteUri,idp=idp.Issuer,subjectAttribute=Environment.GetEnvironmentVariable("SOVEREIGN_SAML_SUBJECT_ATTRIBUTE")??"email"});
app.MapGet("/saml/login",(string? relay,HttpContext c)=>{
    if(samlProblem is not null)return Results.Problem(samlProblem,statusCode:503);
    var nonce=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    pending[nonce]=DateTimeOffset.UtcNow;
    foreach(var stale in pending.Where(p=>p.Value<DateTimeOffset.UtcNow.AddMinutes(-10)).Select(p=>p.Key).ToArray())pending.TryRemove(stale,out _);
    c.Response.Headers.CacheControl="no-store";
    return Results.Redirect(saml.BuildRedirectUrl(nonce));
});
app.MapPost("/saml/acs",async(HttpContext c)=>{
    if(samlProblem is not null)return Results.Problem(samlProblem,statusCode:503);
    var form=await c.Request.ReadFormAsync();var relay=form["RelayState"].ToString();
    if(!pending.TryGetValue(relay,out var started)){return Results.BadRequest(new{error="Unknown or expired login attempt. Start again from the sign-in page."});}
    pending.TryRemove(relay,out _);
    if(DateTimeOffset.UtcNow-started>TimeSpan.FromMinutes(10))return Results.BadRequest(new{error="This login attempt expired. Start again from the sign-in page."});
    SamlIdentity identity;
    try{identity=saml.Validate(form["SAMLResponse"].ToString(),relay);}
    catch(SamlException e){return Results.BadRequest(new{error=e.Message});}
    var attribute=Environment.GetEnvironmentVariable("SOVEREIGN_SAML_SUBJECT_ATTRIBUTE");
    var subject=attribute is { Length:>0 } key&&identity.Attributes.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:identity.Subject;
    // Issue the token once and bind the return location to the configured origin.
    var raw=jwt.Issue(subject,DateTimeOffset.UtcNow);
    c.Response.Cookies.Append("sovereign_session",raw,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=c.Request.IsHttps,Path="/",MaxAge=TimeSpan.FromMinutes(15)});
    // When the operator has pinned a return origin, hand the browser back to the sign-in page
    // there so it can pick up the token. With no origin configured, stay on this host.
    if(allowedReturn is { Length:>0 } configured)return Results.Redirect($"{configured.TrimEnd('/')}/login#token={Uri.EscapeDataString(raw)}");
    return Results.Redirect("/login#token="+Uri.EscapeDataString(raw));
});
app.MapGet("/api/status",()=>new{service="Sovereign .NET",authentication="HS256 JWT",samlEnabled=samlProblem is null,aws=false,modelConfigured=!string.IsNullOrWhiteSpace(model)});
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
// Clears the session cookie. The token itself stays valid until it expires, so this ends
// the browser session rather than revoking the credential.
app.MapPost("/auth/logout",(HttpContext c)=>{c.Response.Cookies.Delete("sovereign_session",new CookieOptions{Path="/"});return Results.Ok(new{signedOut=true});});

// Serve the built playground from this host so the app is only reachable once a session
// exists. SOVEREIGN_WEB_ROOT points at the Vite build output; without it the host is
// API-only and the caller keeps using the separately deployed front end.
var webRoot=Environment.GetEnvironmentVariable("SOVEREIGN_WEB_ROOT");
if(!string.IsNullOrWhiteSpace(webRoot)&&Directory.Exists(webRoot)){
    var provider=new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions{FileProvider=provider});
    app.UseStaticFiles(new StaticFileOptions{FileProvider=provider,OnPrepareResponse=ctx=>ctx.Context.Response.Headers.CacheControl="no-store"});
    // Unknown paths fall back to the single-page app rather than 404.
    app.MapFallbackToFile("index.html",new Microsoft.AspNetCore.Builder.StaticFileOptions{FileProvider=provider});
}
await app.RunAsync();
public record ToolRequest(string Operation,string Text);
public record PromptRequest(string Prompt);
public record DocumentRequest(string Title,string Text);
public record Document(int Id,string Title,string Text,uint[] Vector);
public record BatchRequest(string Operation,string[] Texts);
public record FlowRequest(string Text,string[] Steps);
public record AuditEvent(string Subject,string Path,int Status);
