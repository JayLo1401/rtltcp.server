using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Auth;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Server;

/// <summary>One TCP client, v1 or v2 (decided by HELLO), see docs/protocol-v2.md.</summary>
internal sealed class ClientSession : IStreamOwner
{
    internal const string AuthRequiredText = "Authentication required: the server only accepts authenticated clients";

    /// <summary>How long a v2 client may take to start authentication (tests shorten it).</summary>
    internal static TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private const int IqQueueBuffers = 64;
    private const int V2IqFrameSize = 64 * 1024;

    private readonly Socket _socket;
    private readonly NetworkStream _net;
    private readonly DeviceManager _manager;
    private readonly AccessPolicy _policy;
    private readonly IStreamUserStore _users;
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

    public ClientSession(Socket socket, DeviceManager manager, AccessPolicy policy, IStreamUserStore users,
        string serverVersion, ILogger log, CancellationToken serverToken)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _socket.SendBufferSize = 1 << 20;
        _net = new NetworkStream(socket, ownsSocket: true);
        _manager = manager;
        _policy = policy;
        _users = users;
        _serverVersion = serverVersion;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        RemoteIp = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        RemoteAddress = (RemoteIp.IsIPv4MappedToIPv6 ? RemoteIp.MapToIPv4() : RemoteIp).ToString();
    }

    public IPAddress RemoteIp { get; }
    public string RemoteAddress { get; }
    public long DroppedBuffers => Interlocked.Read(ref _dropped);

    // ---------------------------------------------------------------- lifecycle

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var owns = false;
        try
        {
            var first = await ReadHelloAsync(ct);
            _v2 = V2.IsHello(first);

            Task writer;
            if (_v2)
            {
                if (!await HandshakeV2Async(ct)) return;
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
                if (_policy.RequiresAuth(RemoteIp))
                {
                    _log.LogInformation("v1 client {Client} rejected: authentication required", RemoteAddress);
                    return;
                }
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
        var n = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(V2.HelloTimeout);
        try
        {
            while (n < buf.Length)
            {
                var r = await _socket.ReceiveAsync(buf.AsMemory(n), SocketFlags.None, timeout.Token);
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

    private async Task<bool> HandshakeV2Async(CancellationToken ct)
    {
        var authRequired = _policy.RequiresAuth(RemoteIp);
        var header = new byte[V2.HeaderSize];
        V2.WriteHeader(header, authRequired ? V2HeaderFlags.AuthRequired : V2HeaderFlags.None);
        await _net.WriteAsync(header, ct);

        var serial = _manager.OpenSerial ?? _manager.Config.Serial;
        await _net.WriteAsync(Frame(new ServerInfoMessage("rtltcp.server", _serverVersion,
            new DeviceInfo(_manager.Config.Name, serial),
            authRequired ? ServerInfoMessage.AuthRequired : ServerInfoMessage.AuthNone)), ct);

        if (!authRequired) return true;

        // SCRAM exchange. A client that does not start it (sends something else or nothing within
        // AuthTimeout) is told that authentication is required and disconnected.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AuthTimeout);
        var scram = new ScramServerSession(u => _users.Find(u, serial), _users.ServerSecret);

        V2Message? first;
        try
        {
            first = await ReadJsonAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            first = null;
        }
        if (first is not AuthStartMessage start)
        {
            await SendFatalAsync(ErrorCodes.AuthRequired, AuthRequiredText);
            return false;
        }
        try
        {
            var (nonce, salt, iterations) = scram.Start(start.User, start.Cnonce);
            await _net.WriteAsync(Frame(new AuthChallengeMessage(nonce, salt, iterations)), timeout.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            await SendFatalAsync(ErrorCodes.AuthFailed, "Authentication failed");
            return false;
        }

        var signature = await ReadJsonAsync(timeout.Token) is AuthProofMessage proof ? scram.Finish(proof.Proof) : null;
        if (signature is null)
        {
            _log.LogWarning("{Client}: authentication failed for user {User}", RemoteAddress, start.User);
            await SendFatalAsync(ErrorCodes.AuthFailed, "Authentication failed");
            return false;
        }
        _log.LogInformation("{Client}: authenticated as {User}", RemoteAddress, start.User);
        await _net.WriteAsync(Frame(new AuthOkMessage(signature)), ct);
        return true;
    }

    /// <summary>Reads the next frame; non-JSON frames (e.g. commands sent before authenticating) yield null.</summary>
    private async Task<V2Message?> ReadJsonAsync(CancellationToken ct)
    {
        var (type, payload) = await ReadFrameAsync(ct);
        return type == FrameType.Json ? V2Json.Deserialize(payload) : null;
    }

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
            _socket.Shutdown(SocketShutdown.Send);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var buf = new byte[4096];
            while (await _socket.ReceiveAsync(buf, SocketFlags.None, t.Token) > 0) { }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
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
                        }
                        else
                        {
                            for (var pos = 0; pos < iq.Length; pos += V2IqFrameSize)
                            {
                                var n = Math.Min(V2IqFrameSize, iq.Length - pos);
                                new FrameHeader(FrameType.Iq, n).Write(frameHeader);
                                await _net.WriteAsync(frameHeader, ct);
                                await _net.WriteAsync(iq.Buffer.AsMemory(pos, n), ct);
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
}
