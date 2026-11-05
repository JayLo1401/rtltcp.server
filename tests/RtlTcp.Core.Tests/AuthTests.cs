using System.Net;
using System.Text;
using RtlTcp.Core.Auth;

namespace RtlTcp.Core.Tests;

public class AuthTests
{
    private static readonly byte[] ServerSecret = Encoding.UTF8.GetBytes("test-secret");

    [Fact]
    public void Scram_math_matches_RFC7677_test_vector()
    {
        // RFC 7677 §3: user "user", password "pencil". Only the AuthMessage string differs in rtltcp_v2,
        // so the core math is checked with the RFC's own AuthMessage.
        var salted = Scram.SaltedPassword("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ=="), 4096);
        const string authMessage =
            "n=user,r=rOprNGfwEbeRWgbNEkqO," +
            "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096," +
            "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";

        Assert.Equal("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=",
            Convert.ToBase64String(Scram.ClientProof(salted, authMessage)));
        Assert.Equal("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=",
            Convert.ToBase64String(Scram.ServerSignature(Scram.ServerKey(salted), authMessage)));
    }

    private static (string? Signature, ScramClientSession Client) Login(
        ScramCredentials? stored, string user, string password)
    {
        var server = new ScramServerSession(u => u == "jay" ? stored : null, ServerSecret);
        var client = new ScramClientSession(user, password);
        var (nonce, salt, iterations) = server.Start(client.User, client.Cnonce);
        return (server.Finish(client.Proof(nonce, salt, iterations)), client);
    }

    [Fact]
    public void Login_succeeds_and_client_verifies_server()
    {
        var stored = ScramCredentials.Create("geheim", iterations: 1000);
        var (signature, client) = Login(stored, "jay", "geheim");
        Assert.NotNull(signature);
        Assert.True(client.VerifyServer(signature));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var stored = ScramCredentials.Create("geheim", iterations: 1000);
        Assert.Null(Login(stored, "jay", "falsch").Signature);
    }

    [Fact]
    public void Unknown_user_fails_but_gets_stable_fake_salt()
    {
        Assert.Null(Login(null, "mallory", "x").Signature);

        string Salt() => new ScramServerSession(_ => null, ServerSecret)
            .Start("mallory", Convert.ToBase64String(new byte[18])).Salt;
        Assert.Equal(Salt(), Salt());
    }

    [Fact]
    public void Server_nonce_starts_with_client_nonce()
    {
        var server = new ScramServerSession(_ => null, ServerSecret);
        var cnonce = new byte[18];
        Array.Fill(cnonce, (byte)7);
        var nonce = Convert.FromBase64String(server.Start("jay", Convert.ToBase64String(cnonce)).Nonce);
        Assert.Equal(36, nonce.Length);
        Assert.Equal(cnonce, nonce[..18]);
    }

    [Theory]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::ffff:192.168.1.20", false)]
    [InlineData("fe80::1%12", false)]
    [InlineData("fd00::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2001:db8::1", true)]
    public void Trusted_networks_skip_auth(string ip, bool requiresAuth)
    {
        var policy = new AccessPolicy(authEnabled: true, AccessPolicy.DefaultTrustedNetworks);
        Assert.Equal(requiresAuth, policy.RequiresAuth(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Auth_disabled_never_requires_auth()
    {
        var policy = new AccessPolicy(authEnabled: false, []);
        Assert.False(policy.RequiresAuth(IPAddress.Parse("8.8.8.8")));
    }
}
