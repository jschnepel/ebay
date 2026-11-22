using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Auction.IntegrationTests.EngineService;

/// <summary>Independent, ephemeral credentials for the two workloads in this test run.</summary>
internal sealed record TestIdentity(string Identity, string Pin)
{
    public static TestIdentity Api { get; } = Create("api", "1.3.6.1.5.5.7.3.2");

    public static TestIdentity Engine { get; } = Create("engine", "1.3.6.1.5.5.7.3.1");

    private static TestIdentity Create(string name, string usage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=auction-{name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return new TestIdentity(Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)), certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }
}
