using System.Net;
using System.Net.Quic;
using Microsoft.Extensions.Logging.Abstractions;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Remote;
using RtlTcp.Core.Server;

#pragma warning disable CA1416 // QUIC: tests return early where it is not supported

namespace RtlTcp.Core.Tests;

/// <summary>
/// Site 1 (fake stick, <see cref="ShareHost"/>) and site 2 (<see cref="RemoteDeviceSource"/> behind a normal
/// stream port) in one process, connected over real QUIC on loopback.
/// </summary>
public sealed class RemoteTests : IAsyncLifetime
{
    private const string Share = "PROV0001";

    private static readonly PeerIdentity Site1 = PeerIdentity.CreateEphemeral("site1");
    private static readonly PeerIdentity Site2 = PeerIdentity.CreateEphemeral("site2");

    private readonly FakeSource _stick = new();
    private readonly List<RemotePeerConfig> _peers =
        [new RemotePeerConfig { Name = "Standort 2", Fingerprint = Site2.Fingerprint, Devices = [Share] }];

    private DeviceManager _provider = null!;
    private ShareHost _host = null!;
    private StreamServer? _localServer;          // site 1: local clients of the same stick
    private DeviceManager? _consumer;
    private StreamServer? _consumerServer;

    private static bool Supported => QuicConnection.IsSupported && QuicListener.IsSupported;

    public async Task InitializeAsync()
    {
        if (!Supported) return;
        RemoteDeviceSource.OpenTimeout = TimeSpan.FromSeconds(3);
        _provider = new DeviceManager(new DeviceConfig
        {
            Serial = Share, Name = "Standort1 Stick 1", Bind = "127.0.0.1", Port = 0, IdleTimeoutSeconds = 1,
        }, _stick, NullLogger.Instance);
        _host = new ShareHost(new IPEndPoint(IPAddress.Loopback, 0), Site1, () => _peers,
            s => s == Share ? _provider : null, "test", NullLogger.Instance);
        await _host.StartAsync();
    }

    private int StartConsumer(string? fingerprint = null, string share = Share, PeerIdentity? identity = null)
    {
        var remote = new RemoteDeviceConfig
        {
            Host = "127.0.0.1", Port = _host.LocalEndPoint!.Port, Fingerprint = fingerprint ?? Site1.Fingerprint, Share = share,
        };
        _consumer = new DeviceManager(new DeviceConfig
        {
            Name = "Remote", Bind = "127.0.0.1", Port = 0, IdleTimeoutSeconds = 0, V1WaitForDeviceSeconds = 5, Remote = remote,
            Defaults = new DeviceDefaults { Frequency = 92_600_000, SampleRate = 2_400_000, Gain = 144 },
        }, new RemoteDeviceSource(remote, identity ?? Site2, NullLogger.Instance), NullLogger.Instance);
        _consumerServer = new StreamServer(_consumer, "test", NullLogger.Instance);
        _consumerServer.Start();
        return _consumerServer.LocalEndPoint!.Port;
    }

    public async Task DisposeAsync()
    {
        if (!Supported) return;
        if (_consumerServer is not null) await _consumerServer.DisposeAsync();
        if (_consumer is not null) await _consumer.DisposeAsync();
        if (_localServer is not null) await _localServer.DisposeAsync();
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 8000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) Assert.Fail("Condition not reached in time");
            await Task.Delay(20);
        }
    }

    private static async Task<TestClient> ConnectV2Async(int port)
    {
        var c = await TestClient.ConnectAsync(port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        return c;
    }

    [Fact]
    public async Task Remote_stick_streams_to_a_local_v2_client_and_commands_reach_the_provider()
    {
        if (!Supported) return;
        var port = StartConsumer();
        await using var c = await ConnectV2Async(port);
        var ready = await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        Assert.Equal(TunerType.R820T, ready.Tuner!.Type);
        Assert.Equal([0, 9, 14], ready.Tuner.Gains);
        Assert.Equal(3, await c.CountIqFramesAsync(3));

        // defaults of site 2 were sent to site 1, and the client's own commands as well
        var stick = _stick.Current!;
        await Eventually(() => stick.Executed.Contains(new Command(RtlCommand.SetFrequency, 92_600_000)));
        await c.SendCommandV2Async(RtlCommand.SetFrequency, 100_000_000);
        await Eventually(() => stick.Executed.Contains(new Command(RtlCommand.SetFrequency, 100_000_000)));

        Assert.StartsWith("Standort 2 (127.0.0.1)", _provider.Owner!.RemoteAddress);
    }

    [Fact]
    public async Task Remote_stick_serves_v1_clients_with_the_remote_tuner_header()
    {
        if (!Supported) return;
        var port = StartConsumer();
        await using var c = await TestClient.ConnectAsync(port);
        var header = await c.ReadAsync(V1.HeaderSize);
        Assert.Equal("RTL0"u8.ToArray(), header[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 5, 0, 0, 0, 3 }, header[4..]);
        Assert.All(await c.ReadAsync(32768), b => Assert.Equal(0x7F, b));
    }

    [Fact]
    public async Task Connection_is_closed_when_the_last_local_client_leaves()
    {
        if (!Supported) return;
        var port = StartConsumer();
        await using (var c = await TestClient.ConnectAsync(port))
            await c.ReadAsync(V1.HeaderSize);
        await Eventually(() => _provider.Owner is null);
        await Eventually(() => _consumer!.State == DeviceState.Idle);
    }

    [Fact]
    public async Task Invalid_sample_rate_is_rejected_locally()
    {
        if (!Supported) return;
        var port = StartConsumer();
        await using var c = await ConnectV2Async(port);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        await c.SendCommandV2Async(RtlCommand.SetSampleRate, 5);
        Assert.Equal(ErrorCodes.InvalidCommand, (await c.ExpectAsync<ErrorMessage>()).Code);
    }

    [Fact]
    public async Task Wrong_provider_fingerprint_is_reported_as_not_ready()
    {
        if (!Supported) return;
        var port = StartConsumer(fingerprint: Site2.Fingerprint);
        await using var c = await ConnectV2Async(port);
        var s = await c.ExpectAsync<StatusMessage>(m => m.State != DeviceStates.Starting);
        Assert.Equal(DeviceStates.DeviceNotReady, s.State);
        Assert.Contains("fingerprint", s.Message);
        Assert.Null(_provider.Owner);
    }

    [Fact]
    public async Task Unknown_peer_certificate_is_refused_by_the_provider()
    {
        if (!Supported) return;
        using var stranger = PeerIdentity.CreateEphemeral("stranger");
        var port = StartConsumer(identity: stranger);
        await using var c = await ConnectV2Async(port);
        var s = await c.ExpectAsync<StatusMessage>(m => m.State != DeviceStates.Starting);
        Assert.Equal(DeviceStates.DeviceNotReady, s.State);
        Assert.Null(_provider.Owner);
        Assert.Equal(0, _stick.Opens);
    }

    [Fact]
    public async Task Stick_not_shared_with_this_peer_is_reported()
    {
        if (!Supported) return;
        var port = StartConsumer(share: "OTHER");
        await using var c = await ConnectV2Async(port);
        var s = await c.ExpectAsync<StatusMessage>(m => m.State != DeviceStates.Starting);
        Assert.Equal(DeviceStates.DeviceNotReady, s.State);
        Assert.Equal("Remote site: Stick OTHER is not shared with this server", s.Message);
    }

    [Fact]
    public async Task Busy_at_the_provider_names_the_owner()
    {
        if (!Supported) return;
        _localServer = new StreamServer(_provider, "test", NullLogger.Instance);
        _localServer.Start();
        await using var local = await TestClient.ConnectAsync(_localServer.LocalEndPoint!.Port);
        await local.ReadAsync(V1.HeaderSize);

        var port = StartConsumer();
        await using var c = await ConnectV2Async(port);
        var s = await c.ExpectAsync<StatusMessage>(m => m.State != DeviceStates.Starting);
        Assert.Equal(DeviceStates.DeviceNotReady, s.State);
        Assert.Equal("Remote stick in use by 127.0.0.1", s.Message);
    }

    [Fact]
    public async Task Lost_connection_is_reestablished_and_settings_are_replayed()
    {
        if (!Supported) return;
        var port = StartConsumer();
        await using var c = await ConnectV2Async(port);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        await c.SendCommandV2Async(RtlCommand.SetFrequency, 145_500_000);
        var first = _stick.Current!;
        await Eventually(() => first.Executed.Contains(new Command(RtlCommand.SetFrequency, 145_500_000)));

        _provider.KickOwner(); // drops the remote session like a broken internet connection
        var lost = await c.ExpectAsync<StatusMessage>(s => s.State != DeviceStates.Ready);
        Assert.Equal(DeviceStates.DeviceLost, lost.State);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.DeviceRestored);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);

        await Eventually(() => _stick.Current!.Executed.Contains(new Command(RtlCommand.SetFrequency, 145_500_000))
                               && _provider.Owner is not null);
        Assert.Equal(1, await c.CountIqFramesAsync(1));
    }
}
