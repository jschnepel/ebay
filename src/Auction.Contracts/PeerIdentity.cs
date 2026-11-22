using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Auction.Transport;

/// <summary>Each service has a dedicated certificate and pins the public certificate of its peer.</summary>
internal sealed class PeerIdentity : IDisposable
{
    public PeerIdentity(IConfiguration configuration)
    {
        var identity = configuration["EngineTls:Identity"] ?? throw new InvalidOperationException("EngineTls:Identity is required.");
        PeerPin = configuration["EngineTls:PeerSha256"] ?? throw new InvalidOperationException("EngineTls:PeerSha256 is required.");
        if (PeerPin.Length != 64 || !PeerPin.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("EngineTls:PeerSha256 must be a SHA-256 certificate fingerprint.");
        }

        Certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(identity), null, X509KeyStorageFlags.EphemeralKeySet);
        if (!Certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("The service identity needs its private key.");
        }
    }

    public X509Certificate2 Certificate { get; }

    private string PeerPin { get; }

    public bool Trusts(X509Certificate2? certificate) => Matches(certificate, PeerPin);

    public static bool Matches(X509Certificate2? certificate, string pin) =>
        certificate is not null && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime()
        && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime()
        && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase);

    public HttpClientHandler CreateClientHandler()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, peer, _, _) => Trusts(peer) };
        handler.ClientCertificates.Add(Certificate);
        return handler;
    }

    public void Dispose() => Certificate.Dispose();
}
