using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RtlTcp.Core.Remote;

/// <summary>
/// Direct connection between two rtltcp.server sites (docs/remote-sharing.md):
/// QUIC with ALPN <see cref="Alpn"/>, both sides present self-signed certificates checked by fingerprint.
/// Each bidirectional QUIC stream carries one stick: a share preamble, then an ordinary
/// rtl_tcp Jay extended (v2) session (docs/protocol-v2.md), started with HELLO.
/// </summary>
public static class RemoteProtocol
{
    public static readonly SslApplicationProtocol Alpn = new("rtltcp-jay/1");

    /// <summary>SNI / certificate name; identity is the fingerprint, not the name.</summary>
    public const string TargetHost = "rtltcp.server";

    /// <summary>QUIC application error codes.</summary>
    public const long ErrorNone = 0;
    public const long ErrorStreamAborted = 1;

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Keeps the NAT mapping open (docs: about every 15 s).</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public const int MaxShareIdLength = 255;

    /// <summary>QUIC client (user side). <paramref name="local"/>: the punched port, null = any.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public static System.Net.Quic.QuicClientConnectionOptions ClientOptions(System.Net.EndPoint remote, System.Net.IPEndPoint? local,
        PeerIdentity identity, string expectedFingerprint) => new()
    {
        RemoteEndPoint = remote,
        LocalEndPoint = local,
        DefaultStreamErrorCode = ErrorStreamAborted,
        DefaultCloseErrorCode = ErrorNone,
        IdleTimeout = IdleTimeout,
        KeepAliveInterval = KeepAlive,
        MaxInboundBidirectionalStreams = 0,
        MaxInboundUnidirectionalStreams = 0,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [Alpn],
            TargetHost = TargetHost,
            ClientCertificates = [identity.Certificate],
            RemoteCertificateValidationCallback = (_, cert, _, _) => FingerprintMatches(cert, expectedFingerprint),
        },
    };

    public static string FingerprintOf(X509Certificate certificate) =>
        string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Chunk(2).Select(c => new string(c)));

    /// <summary>Upper-case hex without separators, for comparing fingerprints entered in any notation.</summary>
    public static string NormalizeFingerprint(string fingerprint) =>
        new([.. fingerprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);

    public static bool FingerprintMatches(X509Certificate? certificate, string expected) =>
        certificate is not null && NormalizeFingerprint(expected) is { Length: 64 } e &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(NormalizeFingerprint(FingerprintOf(certificate))), Encoding.ASCII.GetBytes(e));

    /// <summary>Stream preamble: u8 length | UTF-8 share id (the stick's serial at the provider).</summary>
    public static byte[] SharePreamble(string share)
    {
        var id = Encoding.UTF8.GetBytes(share);
        if (id.Length is 0 or > MaxShareIdLength) throw new ArgumentException("Share id must have 1–255 bytes", nameof(share));
        return [(byte)id.Length, .. id];
    }

    public static async Task<string> ReadSharePreambleAsync(Stream stream, CancellationToken ct)
    {
        var len = new byte[1];
        await stream.ReadExactlyAsync(len, ct);
        if (len[0] == 0) throw new Protocol.ProtocolException("Empty share id");
        var id = new byte[len[0]];
        await stream.ReadExactlyAsync(id, ct);
        return Encoding.UTF8.GetString(id);
    }
}
