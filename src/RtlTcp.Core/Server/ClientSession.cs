using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Server;

/// <summary>Transport of one client: a TCP socket or a QUIC stream from a remote site.</summary>
internal sealed class ClientConnection(Stream stream, IPAddress remoteIp, string remoteAddress, Action shutdownSend,
    bool waitForHello = true, int localPort = 0, bool remoteSite = false)
{
    /// <summary>The peer is another rtltcp.server (QUIC from a remote site).</summary>
    public bool RemoteSite { get; } = remoteSite;

    /// <summary>TCP port the client connected to (0 = not a TCP connection).</summary>
    public int LocalPort { get; } = localPort;

    public Stream Stream { get; } = stream;
    public IPAddress RemoteIp { get; } = remoteIp;
    public string RemoteAddress { get; } = remoteAddress;

    /// <summary>Half-close: no more data from us (TCP FIN / QUIC end of stream).</summary>
    public Action ShutdownSend { get; } = shutdownSend;

    /// <summary>
    /// True: wait at most <see cref="V2.HelloTimeout"/> for HELLO, then fall back to v1 (TCP).
    /// False: the peer always speaks v2 (QUIC); a cancelled read would abort the QUIC stream.
    /// </summary>
    public bool WaitForHello { get; } = waitForHello;

    public static ClientConnection FromSocket(Socket socket)
    {
        socket.NoDelay = true;
        socket.SendBufferSize = 1 << 20;
        var ip = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        return new ClientConnection(new NetworkStream(socket, ownsSocket: true), ip,
            (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(), () => socket.Shutdown(SocketShutdown.Send),
            localPort: ((IPEndPoint)socket.LocalEndPoint!).Port);
    }
}

/// <summary>One client, v1 or v2 (decided by HELLO), see docs/protocol-v2.md.</summary>
internal sealed class ClientSession : IStreamOwner
{
    private const int IqQueueBuffers = 64;
    private const int V2IqFrameSize = 64 * 1024;

    private readonly ClientConnection _conn;
    private readonly Stream _net;
    private readonly DeviceManager _manager;
    private readonly string _serverVersion;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<(byte[] Buffer, int Length)> _iq =
        Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(IqQueueBuffers) { SingleReader = true });
    private readonly Channel<(byte[] Data, bool CloseAfter)> _control =
        Channel.CreateUnbounded<(byte[], bool)>(new() { SingleReader = true });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _v2;
    private DeviceState? _lastState;
    private string? _lastMessage;
    private long _dropped;

    private readonly Func<IReadOnlyList<DeviceManager>> _allDevices;

    /// <param name="allDevices">All sticks of this server, for the info HELLO; null = only this one.</param>
    public ClientSession(ClientConnection conn, DeviceManager manager, string serverVersion, ILogger log,
        CancellationToken serverToken, Func<IReadOnlyList<DeviceManager>>? allDevices = null)
    {
        _conn = conn;
        _net = conn.Stream;
        _manager = manager;
        _allDevices = allDevices ?? (() => [manager]);
        _serverVersion = serverVersion;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        RemoteIp = conn.RemoteIp;
        RemoteAddress = conn.RemoteAddress;
    }

    public IPAddress RemoteIp { get; }
    public string RemoteAddress { get; }
    public long DroppedBuffers => Interlocked.Read(ref _dropped);
    public long BytesSent => Interlocked.Read(ref _sent);
    public bool IsRemoteSite => _conn.RemoteSite;

    private long _sent;

    // ---------------------------------------------------------------- lifecycle

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var owns = false;
        try
        {
            var first = await ReadHelloAsync(ct);
            if (V2.IsInfo(first))
            {
                await SendDeviceListAsync(ct);
                return;
            }
            _v2 = V2.IsHello(first);

            Task writer;
            if (_v2)
            {
                await HandshakeV2Async(ct);
                if (!_manager.TryAcquire(this, out var current))
                {
                    await SendFatalAsync(ErrorCodes.Busy, $"Server already in use by {current?.RemoteAddress}", current?.RemoteAddress);
                    return;
                }
                owns = true;
                writer = WriterAsync(ct);
                await ReadV2Async(ct);
            }
            else
            {
                if (!_manager.TryAcquire(this, out var current))
                {
                    _log.LogInformation("v1 client {Client} rejected: busy ({Owner})", RemoteAddress, current?.RemoteAddress);
                    return;
                }
                owns = true;
                if (!await WaitReadyV1Async(ct)) return;
                var tuner = _manager.Tuner!;
                var header = new byte[V1.HeaderSize];
                V1.WriteHeader(header, tuner.Type, (uint)tuner.Gains.Length);
                await _net.WriteAsync(header, ct);
                writer = WriterAsync(ct);
                await ReadV1Async(first, ct);
            }
            await _cts.CancelAsync();
            await writer;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException)
        {
            // client went away or server shutdown
        }
        catch (ProtocolException ex)
        {
            _log.LogInformation("{Client}: protocol error: {Error}", RemoteAddress, ex.Message);
            if (_v2) await TrySendAsync(Frame(new ErrorMessage(ErrorCodes.ProtocolError, ex.Message, true)));
        }
        finally
        {
            if (owns) _manager.Release(this);
            await _cts.CancelAsync();
            while (_iq.Reader.TryRead(out var b)) ArrayPool<byte>.Shared.Return(b.Buffer);
            if (_dropped > 0) _log.LogInformation("{Client}: {Dropped} IQ buffers dropped (client too slow)", RemoteAddress, _dropped);
            await _net.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>Reads up to 5 bytes within the HELLO timeout. Fewer bytes = v1 client that sent nothing (yet).</summary>
    private async Task<byte[]> ReadHelloAsync(CancellationToken ct)
    {
        var buf = new byte[Command.Size];
        if (!_conn.WaitForHello)
        {
            await _net.ReadExactlyAsync(buf, ct);
            return buf;
        }
        var n = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(V2.HelloTimeout);
        try
        {
            while (n < buf.Length)
            {
                var r = await _net.ReadAsync(buf.AsMemory(n), timeout.Token);
                if (r == 0) throw new EndOfStreamException();
                n += r;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return buf[..n];
    }

    private async Task<bool> WaitReadyV1Async(CancellationToken ct)
    {
        if (_manager.State == DeviceState.Ready && _manager.Tuner is not null) return true;
        var wait = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _manager.Config.V1WaitForDeviceSeconds)), ct);
        await Task.WhenAny(_ready.Task, wait);
        if (_ready.Task.IsCompleted && _manager.Tuner is not null) return true;
        _log.LogInformation("v1 client {Client} closed: device not ready", RemoteAddress);
        return false;
    }

    // ---------------------------------------------------------------- v1

    private async Task ReadV1Async(byte[] prefix, CancellationToken ct)
    {
        var buf = new byte[Command.Size];
        prefix.CopyTo(buf, 0);
        var have = prefix.Length;
        while (!ct.IsCancellationRequested)
        {
            if (have < Command.Size)
            {
                await _net.ReadExactlyAsync(buf.AsMemory(have), ct);
            }
            _manager.Send(this, Command.Read(buf));
            have = 0;
        }
    }

    // ---------------------------------------------------------------- v2

    /// <summary>
    /// Header + server_info. There is no client authentication (the internet connection is handled by
    /// rtl.over.internet); header flags stay 0 and "auth" stays "none" for older clients.
    /// </summary>
    private async Task HandshakeV2Async(CancellationToken ct)
    {
        var header = new byte[V2.HeaderSize];
        V2.WriteHeader(header, V2HeaderFlags.None);
        await _net.WriteAsync(header, ct);

        await _net.WriteAsync(Frame(new ServerInfoMessage("rtltcp.server", _serverVersion, DeviceInfoOf(_manager),
            ServerInfoMessage.AuthNone)), ct);
    }

    private DeviceInfo DeviceInfoOf(DeviceManager m)
    {
        var details = m.Details;
        return new(m.Config.Name, m.OpenSerial ?? m.Config.Serial, m == _manager && _conn.LocalPort > 0 ? _conn.LocalPort : m.Config.Port,
            m.Config.Remote is null ? DeviceSources.Usb : DeviceSources.Remote, m.Origin(), details.Location, details.Antenna);
    }
    /// <summary>Info HELLO: header, server_info, all sticks, close. Never takes a stick.</summary>
    private async Task SendDeviceListAsync(CancellationToken ct)
    {
        _v2 = true;
        await HandshakeV2Async(ct);
        var list = _allDevices().Select(m =>
        {
            var info = DeviceInfoOf(m);
            var state = m.State switch
            {
                DeviceState.Ready => DeviceStates.Ready,
                DeviceState.Starting => DeviceStates.Starting,
                DeviceState.NotReady => DeviceStates.DeviceNotReady,
                DeviceState.Lost => DeviceStates.DeviceLost,
                _ => "idle",
            };
            return new DeviceListEntry(info.Port ?? m.Config.Port, info.Name, info.Serial, info.Source!, info.Remote, state,
                m.Owner?.RemoteAddress, m.Tuner?.Name, info.Location, info.Antenna);
        }).ToList();
        await _net.WriteAsync(Frame(new DevicesMessage(list)), ct);
        _log.LogDebug("{Client}: device list sent ({Count} stick(s))", RemoteAddress, list.Count);
        await GracefulCloseAsync();
    }

    public string Protocol => _v2 ? "rtl_tcp Jay extended" : "rtl_tcp";

    private async Task<(FrameType Type, byte[] Payload)> ReadFrameAsync(CancellationToken ct)
    {
        var h = new byte[FrameHeader.Size];
        await _net.ReadExactlyAsync(h, ct);
        var header = FrameHeader.Read(h);
        if (header.Length > 64 * 1024) throw new ProtocolException("Client frame too large");
        var payload = new byte[header.Length];
        await _net.ReadExactlyAsync(payload, ct);
        return (header.Type, payload);
    }

    private async Task ReadV2Async(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, payload) = await ReadFrameAsync(ct);
            switch (type)
            {
                case FrameType.Command when payload.Length == Command.Size:
                    _manager.Send(this, Command.Read(payload));
                    break;
                case FrameType.Command:
                    throw new ProtocolException("Command frame must have 5 bytes");
                case FrameType.Json when V2Json.Deserialize(payload) is PingMessage ping:
                    Enqueue(new PongMessage(ping.Id));
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- output

    private static byte[] Frame(V2Message message)
    {
        var json = V2Json.Serialize(message);
        var frame = new byte[FrameHeader.Size + json.Length];
        new FrameHeader(FrameType.Json, json.Length).Write(frame);
        json.CopyTo(frame, FrameHeader.Size);
        return frame;
    }

    private void Enqueue(V2Message message, bool closeAfter = false)
    {
        if (_v2) _control.Writer.TryWrite((Frame(message), closeAfter));
        else if (closeAfter) _cts.Cancel();
    }

    private async Task SendFatalAsync(string code, string message, string? client = null)
    {
        _log.LogInformation("{Client}: {Code} {Message}", RemoteAddress, code, message);
        await TrySendAsync(Frame(new ErrorMessage(code, message, true, client)));
        await GracefulCloseAsync();
    }

    /// <summary>
    /// Sends FIN and reads (discards) what the client still sends, up to 1 s. Closing a socket with unread
    /// incoming data makes the OS send RST, and the client may then never see the error frame we just sent.
    /// </summary>
    private async Task GracefulCloseAsync()
    {
        try
        {
            _conn.ShutdownSend();
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var buf = new byte[4096];
            while (await _net.ReadAsync(buf, t.Token) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                       or InvalidOperationException) { }
    }

    private async Task TrySendAsync(byte[] data)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _net.WriteAsync(data, t.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>Single writer: control messages first, then IQ.</summary>
    private async Task WriterAsync(CancellationToken ct)
    {
        var frameHeader = new byte[FrameHeader.Size];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (_control.Reader.TryRead(out var c))
                {
                    await _net.WriteAsync(c.Data, ct);
                    if (c.CloseAfter)
                    {
                        await GracefulCloseAsync();
                        await _cts.CancelAsync();
                        return;
                    }
                }

                if (_iq.Reader.TryRead(out var iq))
                {
                    try
                    {
                        if (!_v2)
                        {
                            await _net.WriteAsync(iq.Buffer.AsMemory(0, iq.Length), ct);
                            Interlocked.Add(ref _sent, iq.Length);
                        }
                        else
                        {
                            for (var pos = 0; pos < iq.Length; pos += V2IqFrameSize)
                            {
                                var n = Math.Min(V2IqFrameSize, iq.Length - pos);
                                new FrameHeader(FrameType.Iq, n).Write(frameHeader);
                                await _net.WriteAsync(frameHeader, ct);
                                await _net.WriteAsync(iq.Buffer.AsMemory(pos, n), ct);
                                Interlocked.Add(ref _sent, FrameHeader.Size + n);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(iq.Buffer);
                    }
                    continue;
                }

                await Task.WhenAny(
                    _control.Reader.WaitToReadAsync(ct).AsTask(),
                    _iq.Reader.WaitToReadAsync(ct).AsTask());
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await _cts.CancelAsync(); // stop the reader too
        }
    }

    // ---------------------------------------------------------------- IStreamOwner (called by DeviceManager)

    public void OnIq(ReadOnlySpan<byte> data)
    {
        var buf = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buf);
        if (!_iq.Writer.TryWrite((buf, data.Length)))
        {
            ArrayPool<byte>.Shared.Return(buf);
            Interlocked.Increment(ref _dropped);
        }
    }

    public void OnStateChanged(DeviceState state, string message, bool restored)
    {
        if (state == DeviceState.Ready) _ready.TrySetResult();
        if (!restored && state == _lastState && message == _lastMessage) return;
        _lastState = state;
        _lastMessage = message;

        if (restored) Enqueue(new StatusMessage(DeviceStates.DeviceRestored, "USB device restored"));
        Enqueue(state switch
        {
            DeviceState.Ready => new StatusMessage(DeviceStates.Ready, message,
                _manager.Tuner is { } t ? new TunerInfo(t.Type, t.Name, t.Gains) : null),
            DeviceState.NotReady => new StatusMessage(DeviceStates.DeviceNotReady, message),
            DeviceState.Lost => new StatusMessage(DeviceStates.DeviceLost, message),
            _ => new StatusMessage(DeviceStates.Starting, message),
        });
    }

    public void OnCommandRejected(Command command, string reason) =>
        Enqueue(new ErrorMessage(ErrorCodes.InvalidCommand, $"{command.Code}: {reason}", false));

    public void OnKicked()
    {
        _log.LogInformation("{Client}: disconnected by administrator", RemoteAddress);
        Enqueue(new ErrorMessage(ErrorCodes.Kicked, "Disconnected by administrator", true), closeAfter: true);
    }

    public void OnPreempted(string by)
    {
        _log.LogInformation("{Client}: disconnected, local client {By} has priority", RemoteAddress, by);
        Enqueue(new ErrorMessage(ErrorCodes.Preempted, $"Local client {by} has priority", true, by), closeAfter: true);
    }
}
