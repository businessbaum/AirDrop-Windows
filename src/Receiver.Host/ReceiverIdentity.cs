using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Receiver.Host;

/// <summary>
/// The TLS identity and the stable service id this receiver advertises.
///
/// The certificate is self-signed. opendrop-rs reports that AirDrop senders accept a
/// self-signed receiver certificate; whether this holds for the iOS build under test is
/// exactly one of the things the first real transfer has to show.
/// </summary>
public sealed class ReceiverIdentity
{
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>12 lowercase hex characters, the shape Apple uses for the instance label.</summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// The SRV target host. Measured on 2026-09-18: real AirDrop peers publish a bare
    /// UUID under .local., not a descriptive name -- the Mac advertised
    /// "758ee1bf-a3f1-446e-8457-89639221c657.local.:8770". We match that shape so the
    /// record differs from a real one in as few ways as possible.
    /// </summary>
    public required string HostUuid { get; init; }

    public static ReceiverIdentity CreateOrLoad(string stateDir, string computerName)
    {
        Directory.CreateDirectory(stateDir);
        var pfxPath = Path.Combine(stateDir, "receiver.pfx");
        var idPath = Path.Combine(stateDir, "service-id.txt");

        var uuidPath = Path.Combine(stateDir, "host-uuid.txt");
        string hostUuid = File.Exists(uuidPath)
            ? File.ReadAllText(uuidPath).Trim()
            : NewUuid(uuidPath);

        string serviceId;
        if (File.Exists(idPath))
        {
            serviceId = File.ReadAllText(idPath).Trim();
        }
        else
        {
            // A stable id means the sender's cache keeps pointing at the same instance
            // across restarts instead of accumulating stale entries.
            serviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            File.WriteAllText(idPath, serviceId);
        }

        X509Certificate2 cert;
        if (File.Exists(pfxPath))
        {
            cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                $"CN={computerName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(computerName);
            req.CertificateExtensions.Add(san.Build());

            using var fresh = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

            // The certificate must be round-tripped through PKCS#12 before Kestrel can
            // serve with it. CreateSelfSigned hands back an ephemeral CNG key that
            // Schannel refuses for server authentication, which surfaces only as a
            // failed TLS handshake with no useful error on the wire.
            var pfx = fresh.Export(X509ContentType.Pfx);
            File.WriteAllBytes(pfxPath, pfx);
            cert = X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        return new ReceiverIdentity
        {
            Certificate = cert,
            ServiceId = serviceId,
            HostUuid = hostUuid,
        };
    }

    private static string NewUuid(string path)
    {
        var uuid = Guid.NewGuid().ToString().ToLowerInvariant();
        File.WriteAllText(path, uuid);
        return uuid;
    }
}
