namespace RtlTcp.Core.Auth;

/// <summary>Stream users managed in the web GUI.</summary>
public interface IStreamUserStore
{
    /// <summary>Credentials of an enabled user who may use the stick with this serial, otherwise null.</summary>
    ScramCredentials? Find(string user, string deviceSerial);

    /// <summary>Per-installation secret (fake salts for unknown users).</summary>
    byte[] ServerSecret { get; }
}
