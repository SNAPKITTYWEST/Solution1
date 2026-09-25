using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;

namespace Sovereign.Host;

/// <summary>
/// SAML 2.0 service-provider core: SP-initiated login and Response validation.
/// </summary>
/// <remarks>
/// The signature check is done explicitly rather than through <c>SignedXml.CheckSignature</c>.
/// <c>SignedXml</c> resolves same-document references such as "#_abc" via
/// <c>XmlDocument.GetElementById</c>, which only resolves attributes that a DTD declared as
/// type ID. A real identity provider's Response is not read together with its DTD, so that
/// lookup returns null and verification fails. Resolving the reference here against the
/// already-parsed element removes the DTD dependency, and checking the Reference digest
/// before the SignedInfo signature keeps a tampered DigestValue from being trusted.
/// </remarks>
public sealed class SamlSp
{
    private static readonly XNamespace Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private static readonly XNamespace Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    private static readonly XNamespace Signature = "http://www.w3.org/2000/09/xmldsig#";

    private const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    private const string TransientNameId = "urn:oasis:names:tc:SAML:2.1:nameid-format:transient";

    private readonly IdpMetadata _idp;

    public SamlSp(IdpMetadata idp) => _idp = idp;

    public string EntityId => _idp.SpEntityId;
    public Uri AssertionConsumerService => _idp.Acs;

    /// <summary>SP metadata, advertised to the identity-provider administrator.</summary>
    public string Metadata =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{Escape(EntityId)}">
          <md:SPSSODescriptor protocolSupportEnumeration="{Protocol}">
            <md:NameIDFormat>{TransientNameId}</md:NameIDFormat>
            <md:AssertionConsumerService index="1" isDefault="true" Binding="{PostBinding}" Location="{Escape(AssertionConsumerService.AbsoluteUri)}" />
          </md:SPSSODescriptor>
        </md:EntityDescriptor>
        """;

    /// <summary>Builds a deflated, base64 SAML AuthnRequest for the HTTP-Redirect binding.</summary>
    public string BuildRedirectUrl(string? relayState)
    {
        var request = new XElement(Protocol + "AuthnRequest",
            new XAttribute("ID", "_" + Guid.NewGuid().ToString("N")),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", DateTimeOffset.UtcNow.UtcDateTime.ToString("O")),
            new XAttribute("ProtocolBinding", PostBinding),
            new XAttribute("AssertionConsumerServiceLocation", AssertionConsumerService.AbsoluteUri),
            new XAttribute("Destination", _idp.IdpSsoPost.AbsoluteUri),
            new XElement(Assertion + "Issuer", EntityId),
            new XElement(Protocol + "NameIDPolicy", new XAttribute("Format", TransientNameId), new XAttribute("AllowCreate", "true")));

        var bytes = System.Text.Encoding.UTF8.GetBytes(request.ToString(SaveOptions.DisableFormatting));
        // HTTP-Redirect binding: raw DEFLATE (RFC 1951), base64. ZLibStream would add a zlib
        // header that most identity providers do not strip.
        using var buffer = new System.IO.MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(bytes, 0, bytes.Length);
        var separator = _idp.IdpSsoPost.Query.Contains('?') ? "&" : "?";
        var url = $"{_idp.IdpSsoPost.AbsoluteUri}{separator}SAMLRequest={Convert.ToBase64String(buffer.ToArray())}";
        return relayState is null ? url : $"{url}&RelayState={Uri.EscapeDataString(relayState)}";
    }

    /// <summary>
    /// Validates a base64 SAML Response and returns the authenticated subject plus attributes.
    /// Refuses unsigned Responses, unresolvable or untrusted signatures, wrong audiences and
    /// stale assertions.
    /// </summary>
    public SamlIdentity Validate(string base64Response, string? expectedRelayState)
    {
        if (string.IsNullOrWhiteSpace(base64Response)) throw new SamlException("SAMLResponse is empty.");
        if (base64Response.Length > 512 * 1024) throw new SamlException("SAMLResponse exceeds the 512 KiB limit.");

        XDocument document;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Response);
            using var reader = XmlReader.Create(new System.IO.MemoryStream(bytes), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 512 * 1024,
            });
            document = XDocument.Load(reader);
        }
        catch (Exception e) when (e is FormatException or XmlException)
        {
            throw new SamlException("SAMLResponse is not well-formed XML.");
        }

        var response = document.Root;
        if (response is null || response.Name != Protocol + "Response") throw new SamlException("Expected a SAML Response element.");

        var signature = response.Element(Signature + "Signature");
        if (signature is null) throw new SamlException("SAML Response is not signed. Unsolicited or unsigned assertions are refused.");
        VerifySignature(response, signature, bytes);

        if (response.Attribute("Destination")?.Value is { } destination && destination != AssertionConsumerService.AbsoluteUri)
            throw new SamlException("SAML Response was addressed to a different endpoint.");

        if (!response.Elements(Assertion + "Issuer").Any(i => i.Value == _idp.Issuer))
            throw new SamlException("SAML Response was not issued by the configured identity provider.");

        if (response.Attribute("InResponseTo") is null) throw new SamlException("SP-initiated login requires a correlated InResponseTo.");

        // Only a direct child of the signed Response is honoured. A nested or relocated
        // Assertion is the shape of a signature-wrapping attack, so descendants are ignored.
        var assertions = response.Elements(Assertion + "Assertion").ToArray();
        if (assertions.Length != 1) throw new SamlException("SAML Response must contain exactly one direct Assertion.");
        var assertion = assertions[0];

        var now = DateTimeOffset.UtcNow;
        var conditions = assertion.Element(Assertion + "Conditions");
        if (conditions is not null)
        {
            if (conditions.Attribute("NotBefore") is { } notBefore && Parse(notBefore.Value) > now.AddMinutes(2)) throw new SamlException("SAML Assertion is not yet valid.");
            if (conditions.Attribute("NotOnOrAfter") is { } notOnOrAfter && Parse(notOnOrAfter.Value) < now) throw new SamlException("SAML Assertion has expired.");
            var audiences = conditions.Elements(Assertion + "AudienceRestriction").SelectMany(r => r.Elements(Assertion + "Audience")).Select(a => a.Value).ToArray();
            if (audiences.Length > 0 && !audiences.Contains(EntityId)) throw new SamlException("SAML Assertion is not addressed to this service provider.");
        }

        var subject = assertion.Element(Assertion + "Subject")?.Element(Assertion + "NameID")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new SamlException("SAML Assertion carries no subject.");

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in assertion.Descendants(Assertion + "Attribute"))
        {
            if (attribute.Attribute("Name")?.Value is not { Length: > 0 } name) continue;
            if (attribute.Descendants(Assertion + "AttributeValue").FirstOrDefault() is { } value) attributes[name] = value.Value;
        }

        return new SamlIdentity(subject, attributes, response.Attribute("InResponseTo")!.Value);
    }

    /// <summary>
    /// Verifies the enveloped signature: the Reference must point at the Response element this
    /// Response carries, its DigestValue must match the canonicalized element, and the SignedInfo
    /// signature must verify under a trusted certificate.
    /// </summary>
    private void VerifySignature(XElement response, XElement signature, byte[] received)
    {
        var reference = signature.Descendants(Signature + "Reference").FirstOrDefault()
            ?? throw new SamlException("SAML Response signature has no Reference.");
        var responseId = response.Attribute("ID")?.Value;
        if (responseId is null || reference.Attribute("URI")?.Value != "#" + responseId)
            throw new SamlException("SAML Response signature does not cover this Response element.");

        var signedInfo = signature.Element(Signature + "SignedInfo")
            ?? throw new SamlException("SAML Response signature has no SignedInfo.");

        var digest = Digest(reference.Element(Signature + "DigestMethod")?.Attribute("Algorithm")?.Value);
        var declared = reference.Element(Signature + "DigestValue")?.Value
            ?? throw new SamlException("SAML Response signature has no DigestValue.");

        // Canonicalize from the document exactly as it arrived rather than a re-serialized copy:
        // a round trip can alter quoting, entity escaping or attribute order and silently break
        // the digest. The enveloped-signature transform means the Signature is detached first.
        var pristine = new XmlDocument { PreserveWhitespace = true };
        using (var reader = XmlReader.Create(new System.IO.MemoryStream(received), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            pristine.Load(reader);
        var namespaces = new XmlNamespaceManager(pristine.NameTable);
        namespaces.AddNamespace("ds", Signature.NamespaceName);

        var digestRoot = (XmlElement)pristine.DocumentElement!.CloneNode(true);
        if (digestRoot.SelectSingleNode("ds:Signature", namespaces) is { } detached) digestRoot.RemoveChild(detached);
        var canonical = Canonicalize(digestRoot);
        byte[] declaredDigest;
        try { declaredDigest = Convert.FromBase64String(declared.Trim()); }
        catch (FormatException) { throw new SamlException("SAML Response DigestValue is not valid base64."); }
        if (!CryptographicOperations.FixedTimeEquals(digest.ComputeHash(canonical), declaredDigest))
            throw new SamlException("SAML Response digest does not match its signed content.");

        var algorithm = signedInfo.Element(Signature + "SignatureMethod")?.Attribute("Algorithm")?.Value
            ?? throw new SamlException("SAML Response signature has no SignatureMethod.");
        var signatureValue = Convert.FromBase64String(signature.Element(Signature + "SignatureValue")?.Value ?? throw new SamlException("SAML Response signature has no SignatureValue."));

        // SignedInfo is canonicalized in place, in its original document context, because
        // inclusive C14N renders the namespace declarations it inherits from the ancestors.
        var signedInfoNode = pristine.SelectSingleNode("/samlp:Response/ds:Signature/ds:SignedInfo", Namespaces(pristine)) as XmlElement
            ?? throw new SamlException("SAML Response signature has no SignedInfo.");
        var signedInfoBytes = Canonicalize(signedInfoNode);

        foreach (var certificate in _idp.TrustedCertificates)
        {
            using var key = certificate.GetRSAPublicKey();
            if (key is null) continue;
            if (algorithm == "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256" && key.VerifyData(signedInfoBytes, signatureValue, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return;
            if (algorithm == "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384" && key.VerifyData(signedInfoBytes, signatureValue, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1)) return;
            if (algorithm == "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512" && key.VerifyData(signedInfoBytes, signatureValue, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1)) return;
        }
        throw new SamlException("SAML Response signature is not trusted, or uses an unsupported algorithm.");
    }

    private static XmlNamespaceManager Namespaces(XmlDocument document)
    {
        var manager = new XmlNamespaceManager(document.NameTable);
        manager.AddNamespace("samlp", Protocol.NamespaceName);
        manager.AddNamespace("ds", Signature.NamespaceName);
        return manager;
    }

    private static HashAlgorithm Digest(string? method) => method switch
    {
        "http://www.w3.org/2001/04/xmlenc#sha256" => SHA256.Create(),
        "http://www.w3.org/2001/04/xmldsig-more#sha384" => SHA384.Create(),
        "http://www.w3.org/2001/04/xmlenc#sha512" => SHA512.Create(),
        // SHA-1 is refused deliberately; it is no longer a sound choice for a signature.
        _ => throw new SamlException("SAML Response uses an unsupported digest algorithm."),
    };

    private static byte[] Canonicalize(XmlNode node)
    {
        // C14N consumes a document or a node list. The element is adopted into a fresh document
        // so that it is canonicalized as a root, which is what a Reference to "#id" denotes.
        var document = new XmlDocument { PreserveWhitespace = true };
        document.AppendChild(document.ImportNode(node, true));
        var transform = new System.Security.Cryptography.Xml.XmlDsigC14NTransform();
        transform.LoadInput(document);
        using var output = (Stream)transform.GetOutput(typeof(Stream));
        using var buffer = new MemoryStream();
        output.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}

public sealed record SamlIdentity(string Subject, Dictionary<string, string> Attributes, string InResponseTo);

public sealed class SamlException(string message) : Exception(message);
