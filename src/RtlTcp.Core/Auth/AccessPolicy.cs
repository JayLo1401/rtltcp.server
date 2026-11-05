using System.Net;

namespace RtlTcp.Core.Auth;

/// <summary>Decides per connection whether a stream client must authenticate (docs/protocol-v2.md §6).</summary>
public sealed class AccessPolicy
{
    public static readonly IReadOnlyList<string> DefaultTrustedNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10",
    ];

    private readonly IPNetwork[] _trusted;

    public AccessPolicy(bool authEnabled, IEnumerable<string> trustedNetworks)
    {
        AuthEnabled = authEnabled;
        _trusted = trustedNetworks.Select(IPNetwork.Parse).ToArray();
    }

    public bool AuthEnabled { get; }

    public bool IsTrusted(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes()); // drop %scope for matching
        return _trusted.Any(n => n.Contains(address));
    }

    /// <summary>True if this client needs to authenticate (and therefore cannot use v1).</summary>
    public bool RequiresAuth(IPAddress address) => AuthEnabled && !IsTrusted(address);
}
