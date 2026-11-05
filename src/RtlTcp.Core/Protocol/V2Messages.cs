using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtlTcp.Core.Protocol;

/// <summary>JSON messages of a v2 session (frame type 0x02), docs/protocol-v2.md §4.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ServerInfoMessage), "server_info")]
[JsonDerivedType(typeof(AuthStartMessage), "auth_start")]
[JsonDerivedType(typeof(AuthChallengeMessage), "auth_challenge")]
[JsonDerivedType(typeof(AuthProofMessage), "auth_proof")]
[JsonDerivedType(typeof(AuthOkMessage), "auth_ok")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
public abstract record V2Message;

public sealed record DeviceInfo(string Name, string Serial);

public sealed record ServerInfoMessage(string Server, string Version, DeviceInfo Device, string Auth) : V2Message
{
    public const string AuthNone = "none";
    public const string AuthRequired = "required";
}

public sealed record AuthStartMessage(string User, string Cnonce) : V2Message;

public sealed record AuthChallengeMessage(string Nonce, string Salt, int Iterations) : V2Message;

public sealed record AuthProofMessage(string Proof) : V2Message;

public sealed record AuthOkMessage(string Signature) : V2Message;

public sealed record TunerInfo(TunerType Type, string Name, int[] Gains);

public sealed record StatusMessage(string State, string Message, TunerInfo? Tuner = null) : V2Message;

public static class DeviceStates
{
    public const string Ready = "ready";
    public const string Starting = "starting";
    public const string DeviceNotReady = "device_not_ready";
    public const string DeviceLost = "device_lost";
    public const string DeviceRestored = "device_restored";
}

public sealed record ErrorMessage(string Code, string Message, bool Fatal, string? Client = null) : V2Message;

public static class ErrorCodes
{
    public const string Busy = "BUSY";
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string AuthFailed = "AUTH_FAILED";
    public const string Kicked = "KICKED";
    public const string InvalidCommand = "INVALID_COMMAND";
    public const string ProtocolError = "PROTOCOL_ERROR";
}

public sealed record PingMessage(long Id) : V2Message;

public sealed record PongMessage(long Id) : V2Message;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    UseStringEnumConverter = false)]
[JsonSerializable(typeof(V2Message))]
internal sealed partial class V2JsonContext : JsonSerializerContext;

public static class V2Json
{
    public static byte[] Serialize(V2Message message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, V2JsonContext.Default.V2Message);

    /// <summary>Parses a JSON frame. Unknown message types and malformed JSON yield <c>null</c> (to be ignored).</summary>
    public static V2Message? Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, V2JsonContext.Default.V2Message);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null; // abstract base without known discriminator
        }
    }
}
