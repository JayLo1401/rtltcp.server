using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtlTcp.Core.Protocol;

/// <summary>JSON messages of a v2 session (frame type 0x02), docs/protocol-v2.md §4.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ServerInfoMessage), "server_info")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(DevicesMessage), "devices")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
public abstract record V2Message;

/// <param name="Name">Display name of the stick (set in the web GUI).</param>
/// <param name="Port">Stream port of this stick on the server.</param>
/// <param name="Source"><see cref="DeviceSources.Usb"/> or <see cref="DeviceSources.Remote"/>.</param>
/// <param name="Remote">Only for remote sticks: where it comes from.</param>
/// <param name="Location">Where the antenna is (free text); missing = not set.</param>
/// <param name="Antenna">Antenna type (free text); missing = not set.</param>
public sealed record DeviceInfo(string Name, string Serial, int? Port = null, string? Source = null, RemoteOrigin? Remote = null,
    string? Location = null, string? Antenna = null);

/// <summary>Location and antenna of a stick (free text, null = not set).</summary>
public sealed record DeviceDetails(string? Location, string? Antenna)
{
    public static DeviceDetails Of(string? location, string? antenna) =>
        new(string.IsNullOrWhiteSpace(location) ? null : location.Trim(), string.IsNullOrWhiteSpace(antenna) ? null : antenna.Trim());

    public bool IsEmpty => Location is null && Antenna is null;
}

/// <summary>Origin of a stick shared by another site: instance on rtl.over.internet and providing server.</summary>
public sealed record RemoteOrigin(string? Instance, string? Site);

public static class DeviceSources
{
    public const string Usb = "usb";
    public const string Remote = "remote";
}

/// <summary>One stick of the server in a <see cref="DevicesMessage"/>.</summary>
public sealed record DeviceListEntry(int Port, string Name, string Serial, string Source, RemoteOrigin? Remote, string State,
    string? BusyBy, string? Tuner, string? Location = null, string? Antenna = null);

/// <summary>Answer to the info HELLO (docs/protocol-v2.md §2.1): all sticks of the server, then the server closes.</summary>
public sealed record DevicesMessage(IReadOnlyList<DeviceListEntry> Devices) : V2Message;

/// <param name="Auth">Always "none": clients are not authenticated (kept for older clients that read it).</param>
public sealed record ServerInfoMessage(string Server, string Version, DeviceInfo Device, string Auth) : V2Message
{
    public const string AuthNone = "none";
}

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
    public const string Kicked = "KICKED";
    public const string InvalidCommand = "INVALID_COMMAND";
    public const string ProtocolError = "PROTOCOL_ERROR";

    /// <summary>Remote site: the requested stick is not shared with this peer (or does not exist).</summary>
    public const string NotFound = "NOT_FOUND";
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
