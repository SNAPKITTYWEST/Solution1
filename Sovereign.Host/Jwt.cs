using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Sovereign.Host;
/// <summary>Restricted HS256 JWT profile. Signing keys never leave the host.</summary>
public sealed class Jwt
{
    private readonly byte[] _key;
    public Jwt(string key) { _key=Encoding.UTF8.GetBytes(key); if(_key.Length<32) throw new ArgumentException("SOVEREIGN_JWT_SECRET must contain at least 32 bytes; use a random secret."); }
    private static string Encode(byte[] data)=>Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] Decode(string text)
    {
        if(text.Length==0||text.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))) throw new FormatException();
        return Convert.FromBase64String(text.Replace('-','+').Replace('_','/')+new string('=',(4-text.Length%4)%4));
    }
    public string Issue(string subject,DateTimeOffset now,int seconds=900)
    {
        if(string.IsNullOrWhiteSpace(subject)||seconds is <1 or >900) throw new ArgumentException("Invalid token subject or lifetime.");
        var header=Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload=Encode(JsonSerializer.SerializeToUtf8Bytes(new {iss="sovereign",aud="sovereign-api",sub=subject,iat=now.ToUnixTimeSeconds(),nbf=now.ToUnixTimeSeconds(),exp=now.ToUnixTimeSeconds()+seconds,jti=Guid.NewGuid().ToString("N"),scope="playground"}));
        var input=header+"."+payload;return input+"."+Encode(HMACSHA256.HashData(_key,Encoding.ASCII.GetBytes(input)));
    }
    public bool Validate(string token,DateTimeOffset now,out string subject)
    {
        subject="";
        try{
            if(token.Length>4096)return false;var parts=token.Split('.');if(parts.Length!=3)return false;
            using var h=JsonDocument.Parse(Decode(parts[0]));var header=h.RootElement;
            if(header.EnumerateObject().Count()!=2||header.GetProperty("alg").GetString()!="HS256"||header.GetProperty("typ").GetString()!="JWT")return false;
            var expected=HMACSHA256.HashData(_key,Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]));
            if(!CryptographicOperations.FixedTimeEquals(expected,Decode(parts[2])))return false;
            using var p=JsonDocument.Parse(Decode(parts[1]));var c=p.RootElement;var names=c.EnumerateObject().Select(x=>x.Name).ToArray();if(names.Distinct().Count()!=names.Length)return false;
            var time=now.ToUnixTimeSeconds();var exp=c.GetProperty("exp").GetInt64();var nbf=c.GetProperty("nbf").GetInt64();var iat=c.GetProperty("iat").GetInt64();
            if(c.GetProperty("iss").GetString()!="sovereign"||c.GetProperty("aud").GetString()!="sovereign-api"||c.GetProperty("scope").GetString()!="playground"||exp<=time||nbf>time||iat<0||iat>time||exp<=iat||checked(exp-iat)>900||nbf<iat)return false;
            subject=c.GetProperty("sub").GetString()??"";return !string.IsNullOrWhiteSpace(subject);
        }catch(Exception e)when(e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException){return false;}
    }
}
