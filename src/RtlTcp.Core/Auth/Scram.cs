using System.Security.Cryptography;
using System.Text;

namespace RtlTcp.Core.Auth;

/// <summary>What the server stores per stream user. Not password-equivalent.</summary>
public sealed record ScramCredentials(byte[] Salt, int Iterations, byte[] StoredKey, byte[] ServerKey)
{
    public const int DefaultIterations = 100_000;

    public static ScramCredentials Create(string password, int iterations = DefaultIterations, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var salted = Scram.SaltedPassword(password, salt, iterations);
        return new ScramCredentials(salt, iterations,
            SHA256.HashData(Scram.ClientKey(salted)), Scram.ServerKey(salted));
    }
}

/// <summary>SCRAM-SHA-256 primitives (RFC 5802 / RFC 7677 math), docs/protocol-v2.md §4.2.</summary>
public static class Scram
{
    public static byte[] SaltedPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] ClientKey(byte[] saltedPassword) => HMACSHA256.HashData(saltedPassword, "Client Key"u8);

    public static byte[] ServerKey(byte[] saltedPassword) => HMACSHA256.HashData(saltedPassword, "Server Key"u8);

    /// <summary>AuthMessage as defined by rtltcp_v2 (all binary values as base64 strings).</summary>
    public static string AuthMessage(string user, string cnonce, string nonce, string salt, int iterations) =>
        $"{user},{cnonce},{nonce},{salt},{iterations}";

    public static byte[] ClientProof(byte[] saltedPassword, string authMessage)
    {
        var clientKey = ClientKey(saltedPassword);
        var signature = HMACSHA256.HashData(SHA256.HashData(clientKey), Encoding.UTF8.GetBytes(authMessage));
        return Xor(clientKey, signature);
    }

    public static byte[] ServerSignature(byte[] serverKey, string authMessage) =>
        HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(authMessage));

    public static bool VerifyClientProof(byte[] storedKey, string authMessage, byte[] proof)
    {
        if (proof.Length != storedKey.Length) return false;
        var signature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(authMessage));
        var clientKey = Xor(proof, signature);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(clientKey), storedKey);
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var r = new byte[a.Length];
        for (var i = 0; i < r.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }
}

/// <summary>Server side of one authentication exchange.</summary>
public sealed class ScramServerSession
{
    private readonly Func<string, ScramCredentials?> _lookup;
    private readonly byte[] _serverSecret;
    private string? _user;
    private string? _authMessage;
    private ScramCredentials? _credentials;

    /// <param name="lookup">Returns credentials of an enabled, permitted user, otherwise null.</param>
    /// <param name="serverSecret">Per-installation secret used to derive fake salts for unknown users.</param>
    public ScramServerSession(Func<string, ScramCredentials?> lookup, byte[] serverSecret)
    {
        _lookup = lookup;
        _serverSecret = serverSecret;
    }

    public string? User => _user;

    public (string Nonce, string Salt, int Iterations) Start(string user, string cnonce)
    {
        if (_user is not null) throw new InvalidOperationException("Already started");
        if (string.IsNullOrEmpty(user) || user.Contains(',')) throw new ArgumentException("Invalid user name");
        var cnonceBytes = Convert.FromBase64String(cnonce);
        if (cnonceBytes.Length < 16) throw new ArgumentException("Client nonce too short");

        _user = user;
        _credentials = _lookup(user);
        // Unknown user: deterministic fake salt, so the answer looks the same as for real users.
        var salt = _credentials?.Salt ?? HMACSHA256.HashData(_serverSecret, Encoding.UTF8.GetBytes("salt:" + user))[..16];
        var iterations = _credentials?.Iterations ?? ScramCredentials.DefaultIterations;

        var nonce = Convert.ToBase64String([.. cnonceBytes, .. RandomNumberGenerator.GetBytes(18)]);
        var saltB64 = Convert.ToBase64String(salt);
        _authMessage = Scram.AuthMessage(user, cnonce, nonce, saltB64, iterations);
        return (nonce, saltB64, iterations);
    }

    /// <summary>Returns the server signature (base64) on success, null on failure.</summary>
    public string? Finish(string proof)
    {
        if (_authMessage is null) throw new InvalidOperationException("Not started");
        byte[] proofBytes;
        try { proofBytes = Convert.FromBase64String(proof); }
        catch (FormatException) { return null; }

        if (_credentials is null || !Scram.VerifyClientProof(_credentials.StoredKey, _authMessage, proofBytes))
            return null;
        return Convert.ToBase64String(Scram.ServerSignature(_credentials.ServerKey, _authMessage));
    }
}

/// <summary>Client side of one authentication exchange (used by tests and C# clients).</summary>
public sealed class ScramClientSession(string user, string password)
{
    private byte[]? _salted;
    private string? _authMessage;

    public string User { get; } = user;
    public string Cnonce { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    public string Proof(string nonce, string salt, int iterations)
    {
        if (!Convert.FromBase64String(nonce).AsSpan().StartsWith(Convert.FromBase64String(Cnonce)))
            throw new InvalidOperationException("Server nonce does not contain client nonce");
        _salted = Scram.SaltedPassword(password, Convert.FromBase64String(salt), iterations);
        _authMessage = Scram.AuthMessage(User, Cnonce, nonce, salt, iterations);
        return Convert.ToBase64String(Scram.ClientProof(_salted, _authMessage));
    }

    public bool VerifyServer(string signature)
    {
        if (_salted is null || _authMessage is null) throw new InvalidOperationException("Proof not computed");
        var expected = Scram.ServerSignature(Scram.ServerKey(_salted), _authMessage);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(signature));
    }
}
