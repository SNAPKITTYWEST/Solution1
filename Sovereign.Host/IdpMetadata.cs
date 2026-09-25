using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Sovereign.Host;

/// <summary>
/// Identity-provider configuration for the SAML service provider.
/// Every value comes from the operator's environment. Nothing is hardcoded and
/// nothing is defaulted: an unconfigured IdP disables SAML rather than trusting
/// any identity provider.
/// </summary>
public sealed class IdpMetadata
{
    public required string Issuer { get; init; }
    public required Uri IdpSsoPost { get; init; }
    public required X509Certificate2[] TrustedCertificates { get; init; }

    /// <summary>Entity ID this service provider publishes in its metadata.</summary>
    public required string SpEntityId { get; init; }

    /// <summary>Publicly reachable assertion consumer service the IdP posts back to.</summary>
    public required Uri Acs { get; init; }

    public bool Configured => !string.IsNullOrWhiteSpace(Issuer) && IdpSsoPost is not null && TrustedCertificates.Length > 0;

    /// <summary>
    /// Builds the configuration from environment variables:
    /// SOVEREIGN_SAML_IDP_ENTITYID  - the IdP entityID, matched against the Response Issuer.
    /// SOVEREIGN_SAML_IDP_SSO_POST   - absolute URL of the IdP HTTP-POST single-sign-on endpoint.
    /// SOVEREIGN_SAML_IDP_CERT       - path to a PEM signing certificate, or a ';' separated list.
    /// SOVEREIGN_SAML_SP_ENTITYID    - this service provider's entity ID.
    /// SOVEREIGN_SAML_ACS            - the public HTTPS assertion consumer URL.
    /// </summary>
    public static IdpMetadata FromEnvironment()
    {
        var entity = Environment.GetEnvironmentVariable("SOVEREIGN_SAML_IDP_ENTITYID")?.Trim() ?? "";
        var sso = Environment.GetEnvironmentVariable("SOVEREIGN_SAML_IDP_SSO_POST")?.Trim() ?? "";
        var spEntity = Environment.GetEnvironmentVariable("SOVEREIGN_SAML_SP_ENTITYID")?.Trim() ?? "";
        var acs = Environment.GetEnvironmentVariable("SOVEREIGN_SAML_ACS")?.Trim() ?? "";
        var certs = LoadCertificates(Environment.GetEnvironmentVariable("SOVEREIGN_SAML_IDP_CERT"));
        return new IdpMetadata
        {
            Issuer = entity,
            IdpSsoPost = Absolute(sso),
            TrustedCertificates = certs,
            SpEntityId = spEntity,
            Acs = Absolute(acs),
        };
    }

    private static Uri Absolute(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : new Uri("about:blank");

    /// <summary>SAML is usable only when both the SP and the IdP are fully described.</summary>
    public string? ConfigurationProblem()
    {
        if (string.IsNullOrWhiteSpace(SpEntityId)) return "SOVEREIGN_SAML_SP_ENTITYID is not set.";
        if (Acs.Scheme != "https") return "SOVEREIGN_SAML_ACS must be an absolute https URL reachable by the identity provider.";
        if (string.IsNullOrWhiteSpace(Issuer)) return "SOVEREIGN_SAML_IDP_ENTITYID is not set.";
        if (IdpSsoPost.Scheme != "https") return "SOVEREIGN_SAML_IDP_SSO_POST must be an absolute https URL.";
        if (TrustedCertificates.Length == 0) return "SOVEREIGN_SAML_IDP_CERT does not contain a usable signing certificate.";
        return null;
    }

    private static X509Certificate2[] LoadCertificates(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<X509Certificate2>();
        var loaded = new List<X509Certificate2>();
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Reject inline private keys outright: a signing certificate carries no secret material.
            if (entry.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("SOVEREIGN_SAML_IDP_CERT must reference a public signing certificate, not a private key.");
            var pem = File.ReadAllText(entry);
            const string header = "-----BEGIN CERTIFICATE-----";
            const string footer = "-----END CERTIFICATE-----";
            var start = pem.IndexOf(header, StringComparison.Ordinal);
            var end = pem.IndexOf(footer, StringComparison.Ordinal);
            if (start < 0 || end < 0) throw new ArgumentException($"No PEM certificate block found in {entry}.");
            var body = new string(pem.Substring(start + header.Length, end - start - header.Length).Where(c => !char.IsWhiteSpace(c)).ToArray());
            loaded.Add(new X509Certificate2(Convert.FromBase64String(body)));
        }
        return loaded.ToArray();
    }
}
