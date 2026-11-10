using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Rtl2832;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Remote;

/// <summary>
/// User side (Standort 2): a stick at another site, reached directly over QUIC. To the
/// <see cref="DeviceManager"/> it looks like a USB stick: losing the connection is reported like an
/// unplugged stick, so the manager reconnects and replays the client's settings.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class RemoteDeviceSource(IRemoteConnector connector, ILogger log) : ISdrDeviceSource
{
    /// <summary>Manual test mode: fixed address and fingerprint from the device config.</summary>
    public RemoteDeviceSource(RemoteDeviceConfig config, PeerIdentity identity, ILogger log)
        : this(new DirectConnector(config, identity), log)
    {
    }

    /// <summary>Extra time to wait for the remote stick after the connection is up (tests shorten it).</summary>
    internal static TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private int _failures;
    private long _nextAttempt;

    public bool IsRemote => true;
    public string? LastError { get; private set; }

    public IReadOnlyList<UsbDeviceInfo> List() => [];

    /// <summary>Called on the DeviceManager loop; blocks up to <see cref="OpenTimeout"/>.</summary>
    public ISdrDevice? TryOpen(string serial, IReadOnlyCollection<string> exclude)
    {
        if (Environment.TickCount64 < _nextAttempt) return null; // back off, keep LastError
        try
        {
            using var cts = new CancellationTokenSource(OpenTimeout + connector.ConnectTimeout);
            var device = RemoteDevice.OpenAsync(connector, log, cts.Token).GetAwaiter().GetResult();
            _failures = 0;
            LastError = null;
            return device;
        }
        catch (Exception ex)
        {
            LastError = ex switch
            {
                RemoteOpenException r => r.Message,
                OperationCanceledException => "Remote site not responding",
                QuicException q => $"Remote site not reachable ({q.Message})",
                System.Security.Authentication.AuthenticationException =>
                    "Remote site rejected: certificate fingerprint does not match (on one of the two sides)",
                _ => $"Remote site not reachable ({ex.Message})",
            };
            var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(_failures, 5))));
            _failures++;
            _nextAttempt = Environment.TickCount64 + (long)delay.TotalMilliseconds;
            log.LogInformation("Remote stick {Remote}: {Error}", connector.Describe, LastError);
            return null;
        }
    }
}

/// <summary>The remote side answered but the stick cannot be used (busy, not shared, not ready …).</summary>
public sealed class RemoteOpenException(string message) : Exception(message);

/// <summary>Establishes the QUIC connection to the site that shares a stick.</summary>
public interface IRemoteConnector
{
    /// <summary>Stick id (serial) at the provider, sent in the stream preamble.</summary>
    string Share { get; }

    /// <summary>For log messages.</summary>
    string Describe { get; }

    /// <summary>How long <see cref="ConnectAsync"/> may take.</summary>
    TimeSpan ConnectTimeout { get; }

    /// <exception cref="RemoteOpenException">The setup failed for a reason worth showing to the user.</exception>
    Task<RemoteLink> ConnectAsync(CancellationToken ct);
}

/// <summary>A connection to the providing site; disposing it closes the connection and ends the session.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class RemoteLink(QuicConnection connection, Func<ValueTask>? onClosed = null) : IAsyncDisposable
{
    private int _disposed;

    public QuicConnection Connection { get; } = connection;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Connection.CloseAsync(RemoteProtocol.ErrorNone);
        }
        catch (Exception ex) when (ex is QuicException or ObjectDisposedException or OperationCanceledException) { }
        await Connection.DisposeAsync();
        if (onClosed is not null) await onClosed();
    }
}

/// <summary>Test mode: provider address, port and fingerprint entered by hand.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class DirectConnector(RemoteDeviceConfig config, PeerIdentity identity) : IRemoteConnector
{
    public string Share => config.Share;
    public string Describe => $"{config.Share} at {config.Host}:{config.Port}";
    public TimeSpan ConnectTimeout => TimeSpan.Zero;

    public async Task<RemoteLink> ConnectAsync(CancellationToken ct)
    {
        EndPoint remote = IPAddress.TryParse(config.Host.Trim('[', ']'), out var ip)
            ? new IPEndPoint(ip, config.Port) : new DnsEndPoint(config.Host, config.Port);
        return new RemoteLink(await QuicConnection.ConnectAsync(
            RemoteProtocol.ClientOptions(remote, null, identity, config.Fingerprint), ct));
    }
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class RemoteDevice : ISdrDevice
{
    private readonly RemoteLink _link;
    private readonly QuicStream _stream;
    private readonly ILogger _log;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private IBulkSink? _sink;
    private volatile string? _deadReason;
    private int _disposed;

    private RemoteDevice(RemoteLink link, QuicStream stream, string serial, TunerInfo tuner, string name, ILogger log)
    {
        _link = link;
        _stream = stream;
        Serial = serial;
        TunerType = tuner.Type;
        TunerName = tuner.Name;
        Gains = tuner.Gains;
        _name = name;
        _log = log;
        _ = ReadLoopAsync();
        _ = WriteLoopAsync();
    }

    public string Serial { get; }
    public TunerType TunerType { get; }
    public string TunerName { get; }
    public IReadOnlyList<int> Gains { get; }

    /// <summary>Location and antenna from the provider's server_info.</summary>
    public DeviceDetails? Details { get; private init; }

    // ---------------------------------------------------------------- open

    public static async Task<RemoteDevice> OpenAsync(IRemoteConnector connector, ILogger log, CancellationToken ct)
    {
        var link = await connector.ConnectAsync(ct);
        var conn = link.Connection;
        var share = connector.Share;
        QuicStream? stream = null;
        try
        {
            stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            byte[] start = [.. RemoteProtocol.SharePreamble(share), .. V2.Hello.ToArray()];
            await stream.WriteAsync(start, ct);

            var header = new byte[V2.HeaderSize];
            await stream.ReadExactlyAsync(header, ct);
            V2.ReadHeader(header);

            var name = share;
            var serial = share;
            DeviceDetails? details = null;
            string? lastStatus = null;
            while (true)
            {
                V2Message? msg;
                try
                {
                    var (type, payload) = await ReadFrameAsync(stream, ct);
                    if (type != FrameType.Json) continue;
                    msg = V2Json.Deserialize(payload);
                }
                catch (OperationCanceledException) when (lastStatus is not null)
                {
                    throw new RemoteOpenException($"Remote stick: {lastStatus}");
                }
                catch (EndOfStreamException)
                {
                    throw new RemoteOpenException("Remote site closed the connection");
                }

                switch (msg)
                {
                    case ServerInfoMessage info:
                        name = info.Device.Name;
                        details = DeviceDetails.Of(info.Device.Location, info.Device.Antenna);
                        if (info.Device.Serial.Length > 0) serial = info.Device.Serial;
                        break;
                    case StatusMessage { State: DeviceStates.Ready, Tuner: { } tuner }:
                        log.LogInformation("Remote stick {Name} ({Remote}) ready", name, connector.Describe);
                        return new RemoteDevice(link, stream, serial, tuner, name, log) { Details = details };
                    case StatusMessage { State: DeviceStates.DeviceNotReady or DeviceStates.DeviceLost } s:
                        // no stick there right now: report at once, the source retries with backoff
                        throw new RemoteOpenException($"Remote stick: {s.Message}");
                    case StatusMessage s:
                        lastStatus = s.Message;
                        break;
                    case ErrorMessage { Code: ErrorCodes.Busy } e:
                        throw new RemoteOpenException(e.Client is { } c ? $"Remote stick in use by {c}" : $"Remote stick busy: {e.Message}");
                    case ErrorMessage { Code: ErrorCodes.NotFound } e:
                        throw new RemoteOpenException($"Remote site: {e.Message}");
                    case ErrorMessage { Fatal: true } e:
                        throw new RemoteOpenException($"Remote site: {e.Code} {e.Message}");
                }
            }
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            await link.DisposeAsync();
            throw;
        }
    }

    private static async Task<(FrameType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var h = new byte[FrameHeader.Size];
        await stream.ReadExactlyAsync(h, ct);
        var header = FrameHeader.Read(h);
        var payload = new byte[header.Length];
        await stream.ReadExactlyAsync(payload, ct);
        return (header.Type, payload);
    }

    // ---------------------------------------------------------------- running

    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        var h = new byte[FrameHeader.Size];
        var buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
        string reason;
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(h, ct);
                var header = FrameHeader.Read(h);
                if (header.Length > buf.Length)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = ArrayPool<byte>.Shared.Rent(header.Length);
                }
                await _stream.ReadExactlyAsync(buf.AsMemory(0, header.Length), ct);
                switch (header.Type)
                {
                    case FrameType.Iq:
                        Volatile.Read(ref _sink)?.OnData(buf.AsSpan(0, header.Length));
                        break;
                    case FrameType.Json:
                        OnMessage(V2Json.Deserialize(buf.AsSpan(0, header.Length)));
                        break;
                }
            }
        }
        catch (EndOfStreamException) { reason = "remote site closed the stream"; }
        catch (OperationCanceledException) { return; } // disposed
        catch (Exception ex) { reason = ex.Message; }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        Fail(_deadReason ?? reason);
    }

    private void OnMessage(V2Message? msg)
    {
        switch (msg)
        {
            case StatusMessage s:
                // The connection stays; IQ pauses while the remote stick is not ready and resumes afterwards.
                _log.LogInformation("Remote stick {Name}: {State} {Message}", _name, s.State, s.Message);
                break;
            case ErrorMessage { Fatal: true } e:
                _deadReason = $"{e.Code} {e.Message}";
                break;
            case ErrorMessage e:
                _log.LogInformation("Remote stick {Name}: {Code} {Message}", _name, e.Code, e.Message);
                break;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(_cts.Token))
                await _stream.WriteAsync(frame, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _deadReason ??= reason;
        _log.LogWarning("Remote stick {Name}: connection lost ({Reason})", _name, reason);
        Volatile.Read(ref _sink)?.OnError(new UsbException($"Connection to remote stick lost: {reason}") { DeviceGone = true });
    }

    public bool Execute(Command command)
    {
        if (_deadReason is { } reason) throw new UsbException($"Connection to remote stick lost: {reason}") { DeviceGone = true };
        // Checked here so the client gets INVALID_COMMAND right away, as with a local stick.
        if (command.Code == RtlCommand.SetSampleRate && !Rtl2832.Rtl2832.IsValidSampleRate(command.Param))
            throw new ArgumentOutOfRangeException(nameof(command), command.Param, "Valid: 225001–300000 or 900001–3200000 S/s");
        var frame = new byte[FrameHeader.Size + Command.Size];
        new FrameHeader(FrameType.Command, Command.Size).Write(frame);
        command.Write(frame.AsSpan(FrameHeader.Size));
        _out.Writer.TryWrite(frame);
        return true;
    }

    public IBulkStream StartStream(IBulkSink sink)
    {
        if (_deadReason is { } reason) throw new UsbException($"Connection to remote stick lost: {reason}") { DeviceGone = true };
        Volatile.Write(ref _sink, sink);
        return new StreamHandle(this, sink);
    }

    /// <summary>Nothing to do: the provider powers its stick down after its own idle timeout.</summary>
    public void PowerDown() { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _sink, null);
        _cts.Cancel();
        _out.Writer.TryComplete();
        _ = Task.Run(async () =>
        {
            try
            {
                await _stream.DisposeAsync();
                await _link.DisposeAsync();
            }
            catch (Exception ex) when (ex is QuicException or ObjectDisposedException or OperationCanceledException) { }
            finally
            {
                _cts.Dispose();
            }
        });
    }

    private sealed class StreamHandle(RemoteDevice device, IBulkSink sink) : IBulkStream
    {
        public bool IsRunning => device._deadReason is null && Volatile.Read(ref device._sink) == sink;

        public void Dispose() => Interlocked.CompareExchange(ref device._sink, null, sink);
    }
}
