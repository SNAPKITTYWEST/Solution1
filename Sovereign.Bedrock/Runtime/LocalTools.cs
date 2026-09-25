using System.Text;
namespace Sovereign.Bedrock;
public static class LocalTools
{
    public static byte[] Input(string text)
    {
        if (text is null) throw new ArgumentException("Text is required.");
        var bytes = Encoding.UTF8.GetBytes(text); if (bytes.Length > 65536) throw new ArgumentException("Input exceeds 65536 UTF-8 bytes."); return bytes;
    }
    public static uint Hash(string text) { uint h = 2166136261; foreach (var b in Input(text)) h = unchecked((h ^ b) * 16777619); return h; }
    public static uint[] Embed(string text)
    {
        uint[] v = new uint[64]; uint h = 2166136261; bool active = false;
        foreach (var raw in Input(text).Append((byte)0))
        {
            var b = raw is >= 65 and <= 90 ? raw + 32 : raw;
            if (b is >= 97 and <= 122 or >= 48 and <= 57) { h = unchecked((h ^ (uint)b) * 16777619); active = true; }
            else if (active) { v[h % 64]++; h = 2166136261; active = false; }
        }
        return v;
    }
    public static string Normalize(string text) { var bytes = Input(text); for (var i = 0; i < bytes.Length; i++) if (bytes[i] is >= 65 and <= 90) bytes[i] += 32; return Encoding.UTF8.GetString(bytes); }
    public static uint Guard(string text) { var s = Normalize(text); return (s.Contains("password", StringComparison.Ordinal) ? 1u : 0u) | (s.Contains("secret", StringComparison.Ordinal) ? 2u : 0u) | (s.Contains("api_key", StringComparison.Ordinal) ? 4u : 0u); }
    public static object Run(string op, string text) => op switch { "hash" => Hash(text), "embed" => Embed(text), "guard" => Guard(text), "normalize" => Normalize(text), _ => throw new ArgumentException("Unknown operation.") };
    public static double Cosine(uint[] a, uint[] b) { if (a.Length != b.Length) throw new ArgumentException("Mismatched dimensions."); double dot=0,aa=0,bb=0; for(var i=0;i<a.Length;i++){dot+=(double)a[i]*b[i];aa+=(double)a[i]*a[i];bb+=(double)b[i]*b[i];} return aa>0&&bb>0?dot/Math.Sqrt(aa*bb):0; }
}
