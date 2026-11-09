using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RtlTcp.Core.Remote;

/// <summary>
/// This server's own certificate for the direct (QUIC) connection between sites. Self-signed and created on
/// first use; the other side checks it by its SHA-256 fingerprint, never by a CA.
/// </summary>
public sealed class PeerIdentity : IDisposable
{
    private PeerIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = RemoteProtocol.FingerprintOf(certificate);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate, upper-case hex in pairs ("AB:CD:…").</summary>
    public string Fingerprint { get; }

    /// <summary>Loads the PKCS#12 file at <paramref name="path"/> or creates it (owner-only permissions on Unix).</summary>
    public static PeerIdentity LoadOrCreate(string path, string subjectName)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, CreatePkcs12(subjectName));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
        }
        return new PeerIdentity(X509CertificateLoader.LoadPkcs12FromFile(path, null));
    }

    /// <summary>In-memory identity (tests).</summary>
    public static PeerIdentity CreateEphemeral(string subjectName) =>
        new(X509CertificateLoader.LoadPkcs12(CreatePkcs12(subjectName), null));

    private static byte[] CreatePkcs12(string subjectName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(subjectName);
        var req = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false)); // server + client auth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(RemoteProtocol.TargetHost);
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        return cert.Export(X509ContentType.Pkcs12);
    }

    public void Dispose() => Certificate.Dispose();
}
