using System.Security.Cryptography;
using System.Text;

namespace Sovereign.Host;

/// <summary>
/// Optional local-password authentication, alongside or instead of SAML.
/// The stored value is a salted PBKDF2-HMAC-SHA256 hash, never a plaintext password.
/// Format: pbkdf2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;
/// </summary>
public static class Password
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Computes the stored representation of a password for the operator's environment.</summary>
    public static string Hash(string password, int iterations = Iterations)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password must not be empty.");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a candidate password against the stored hash in constant time with respect to
    /// the digest comparison. Returns false for any malformed or absent stored value.
    /// </summary>
    public static bool Verify(string? stored, string candidate)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrEmpty(candidate)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations is < 10_000 or > 2_000_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(candidate), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
