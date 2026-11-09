using System.Collections.Concurrent;
using System.Net.Sockets;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Tests;

internal sealed class FakeSource : ISdrDeviceSource
{
    public volatile bool Present = true;
    public volatile FakeDevice? Current;
    public int Opens;

    public ISdrDevice? TryOpen(string serial, IReadOnlyCollection<string> exclude)
    {
        if (!Present) return null;
        Interlocked.Increment(ref Opens);
        return Current = new FakeDevice();
    }

    public IReadOnlyList<UsbDeviceInfo> List() => [];
}

internal sealed class FakeDevice : ISdrDevice
{
    public readonly ConcurrentQueue<Command> Executed = new();
    public volatile bool PoweredDown;
    public volatile bool Disposed;
    public FakeStream? Stream;

    public string Serial => "FAKE0001";
    public TunerType TunerType => TunerType.R820T;
    public string TunerName => "R820T";
    public IReadOnlyList<int> Gains => [0, 9, 14];

    public bool Execute(Command command)
    {
        if (command.Code == RtlCommand.SetSampleRate && command.Param < 1000)
            throw new ArgumentOutOfRangeException(nameof(command), "Invalid sample rate");
        Executed.Enqueue(command);
        return true;
    }

    public IBulkStream StartStream(IBulkSink sink) => Stream = new FakeStream(sink);

    public void PowerDown() => PoweredDown = true;

    public void Dispose()
    {
        Stream?.Dispose();
        Disposed = true;
    }
}

/// <summary>Pushes 16 KiB of 0x7F every 5 ms until disposed or <see cref="Unplug"/>.</summary>
internal sealed class FakeStream : IBulkStream
{
    private readonly IBulkSink _sink;
    private volatile bool _running = true;
    private readonly Thread _thread;

    public FakeStream(IBulkSink sink)
    {
        _sink = sink;
        _thread = new Thread(() =>
        {
            var data = Enumerable.Repeat((byte)0x7F, 16384).ToArray();
            while (_running)
            {
                _sink.OnData(data);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public bool IsRunning => _running;

    public void Unplug()
    {
        _running = false;
        _thread.Join();
        _sink.OnError(new UsbException("bulk transfer NoDevice") { DeviceGone = true });
    }

    public void Dispose()
    {
        _running = false;
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}

/// <summary>Minimal test client speaking v1/v2.</summary>
internal sealed class TestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TcpClient _tcp;
    private readonly NetworkStream _net;

    private TestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _net = tcp.GetStream();
    }

    public static async Task<TestClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        return new TestClient(tcp);
    }

    public async Task<byte[]> ReadAsync(int count)
    {
        var buf = new byte[count];
        using var cts = new CancellationTokenSource(Timeout);
        await _net.ReadExactlyAsync(buf, cts.Token);
        return buf;
    }

    /// <summary>True if the server closed the connection (EOF) within the timeout.</summary>
    public async Task<bool> IsClosedAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var buf = new byte[65536];
            while (true)
            {
                if (await _net.ReadAsync(buf, cts.Token) == 0) return true;
            }
        }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    public Task SendAsync(byte[] data) => _net.WriteAsync(data).AsTask();

    public Task SendHelloAsync() => SendAsync(V2.Hello.ToArray());

    public Task SendCommandV1Async(RtlCommand code, uint param) => SendAsync(new Command(code, param).ToArray());

    public async Task SendFrameAsync(FrameType type, byte[] payload)
    {
        var h = new byte[FrameHeader.Size];
        new FrameHeader(type, payload.Length).Write(h);
        await SendAsync([.. h, .. payload]);
    }

    public Task SendJsonAsync(V2Message message) => SendFrameAsync(FrameType.Json, V2Json.Serialize(message));

    public Task SendCommandV2Async(RtlCommand code, uint param) =>
        SendFrameAsync(FrameType.Command, new Command(code, param).ToArray());

    public async Task<(FrameType Type, byte[] Payload)> ReadFrameAsync()
    {
        var header = FrameHeader.Read(await ReadAsync(FrameHeader.Size));
        return (header.Type, await ReadAsync(header.Length));
    }

    /// <summary>Reads frames (skipping IQ) until a JSON message of type T arrives.</summary>
    public async Task<T> ExpectAsync<T>(Func<T, bool>? predicate = null) where T : V2Message
    {
        while (true)
        {
            var (type, payload) = await ReadFrameAsync();
            if (type != FrameType.Json) continue;
            if (V2Json.Deserialize(payload) is T msg && (predicate?.Invoke(msg) ?? true)) return msg;
        }
    }

    public async Task<int> CountIqFramesAsync(int frames)
    {
        var n = 0;
        while (n < frames)
        {
            var (type, payload) = await ReadFrameAsync();
            if (type == FrameType.Iq && payload.Length > 0) n++;
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        await _net.DisposeAsync();
        _tcp.Dispose();
    }
}
