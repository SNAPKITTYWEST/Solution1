using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sovereign.Bedrock;
using Sovereign.Host;
int count=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);count++;}
var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);var jwt=new Jwt(new string('x',32));var token=jwt.Issue("alice",now);
Check(jwt.Validate(token,now,out var subject)&&subject=="alice","JWT valid signature and subject");
Check(!jwt.Validate(token,now.AddSeconds(900),out _),"JWT expiry boundary");Check(!jwt.Validate(token,now.AddSeconds(-1),out _),"JWT future nbf");
Check(!new Jwt(new string('y',32)).Validate(token,now,out _),"JWT wrong key");Check(!jwt.Validate(token[..^5]+"AAAAA",now,out _),"JWT modified signature");
Check(!jwt.Validate("eyJhbGciOiJub25lIn0.e30.",now,out _),"JWT none algorithm rejected");Check(!jwt.Validate("a.b.c",now,out _),"JWT malformed encoding");
Check(LocalTools.Hash("hello")==0x4f9f2cab,"FNV known vector");Check(LocalTools.Embed("cat cat dog").SequenceEqual(LocalTools.Embed("DOG cat cat")),"Embedding invariance");
Check(LocalTools.Embed("cat cat dog").Sum(x=>x)==3,"Embedding token count");Check(LocalTools.Guard("PASSWORD secret api_key")==7,"Guard bitmask");Check(LocalTools.Normalize("ABC 世界")=="abc 世界","UTF8 preserved");
try{LocalTools.Input(new string('a',65537));Check(false,"Input bound");}catch(ArgumentException){Check(true,"Input bound");}
try{_=new BedrockProvider(new HttpClient(),new Uri("https://example.com"));Check(false,"Remote endpoint rejection");}catch(ArgumentException){Check(true,"Remote endpoint rejection");}
using var client=new HttpClient(new FakeHandler());var backend=new BedrockBackend(client,new Uri("http://127.0.0.1:11434/"),"test-model");
Check(await backend.GenerateAsync(new[]{("user","hello")})=="local response","Local model transport");
var provider=new BedrockProvider(client,new Uri("http://127.0.0.1:11434/"));var chunks=new List<JsonNode>();await foreach(var chunk in provider.InvokeModelStreamAsync("test-model",new JsonArray()))chunks.Add(chunk);
Check(chunks.Count==1&&chunks[0]["choices"]![0]!["delta"]!["content"]!.GetValue<string>()=="hi","SSE stream transport");Console.WriteLine($"{count} checks passed.");
sealed class FakeHandler:HttpMessageHandler{
protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){
var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;if(request.RequestUri!.AbsolutePath!="/v1/chat/completions")throw new Exception("Wrong endpoint");bool stream=body["stream"]!.GetValue<bool>();
return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(stream?"data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n":"{\"choices\":[{\"message\":{\"content\":\"local response\"}}]}",Encoding.UTF8,stream?"text/event-stream":"application/json")};}}
