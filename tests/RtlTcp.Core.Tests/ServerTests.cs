using Microsoft.Extensions.Logging.Abstractions;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Server;

namespace RtlTcp.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly FakeSource _source = new();
    private DeviceManager _manager = null!;
    private StreamServer _server = null!;

    private readonly DeviceConfig _config = new()
    {
        Name = "Test", Location = "Wien Dach", Antenna = " ", Bind = "127.0.0.1", Port = 0, IdleTimeoutSeconds = 1, V1WaitForDeviceSeconds = 2,
        Defaults = new DeviceDefaults { Frequency = 92_600_000, SampleRate = 2_400_000, Gain = 144 },
    };

    private int Port => _server.LocalEndPoint!.Port;

    public Task InitializeAsync()
    {
        _manager = new DeviceManager(_config, _source, NullLogger.Instance);
        _server = new StreamServer(_manager, "test", NullLogger.Instance);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _manager.DisposeAsync();
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) Assert.Fail("Condition not reached in time");
            await Task.Delay(20);
        }
    }

    // ---------------------------------------------------------------- v1

    [Fact]
    public async Task V1_client_gets_RTL0_header_IQ_and_commands_reach_the_device()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        var header = await c.ReadAsync(V1.HeaderSize);
        Assert.Equal("RTL0"u8.ToArray(), header[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 5, 0, 0, 0, 3 }, header[4..]);   // R820T, 3 gains

        var iq = await c.ReadAsync(32768);
        Assert.All(iq, b => Assert.Equal(0x7F, b));

        await c.SendCommandV1Async(RtlCommand.SetFrequency, 100_000_000);
        await Eventually(() => _source.Current!.Executed.Contains(new Command(RtlCommand.SetFrequency, 100_000_000)));

        // defaults from config were applied when the client took over
        Assert.Contains(new Command(RtlCommand.SetFrequency, 92_600_000), _source.Current!.Executed);
        Assert.Contains(new Command(RtlCommand.SetGain, 144), _source.Current!.Executed);
    }

    [Fact]
    public async Task Second_v1_client_is_closed_while_busy()
    {
        await using var first = await TestClient.ConnectAsync(Port);
        await first.ReadAsync(V1.HeaderSize);
        await using var second = await TestClient.ConnectAsync(Port);
        Assert.True(await second.IsClosedAsync());
    }

    [Fact]
    public async Task V1_client_waits_for_device_and_is_closed_if_it_never_appears()
    {
        _source.Present = false;
        await using var c = await TestClient.ConnectAsync(Port);
        Assert.True(await c.IsClosedAsync());
        await Eventually(() => _manager.State == DeviceState.Idle);
    }

    // ---------------------------------------------------------------- v2

    [Fact]
    public async Task V2_client_gets_RTL2_server_info_status_and_IQ_frames()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        var (version, flags) = V2.ReadHeader(await c.ReadAsync(V2.HeaderSize));
        Assert.Equal(V2.ProtocolVersion, version);
        Assert.Equal(V2HeaderFlags.None, flags);

        var info = await c.ExpectAsync<ServerInfoMessage>();
        Assert.Equal("none", info.Auth);
        Assert.Equal("Test", info.Device.Name);

        var ready = await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        Assert.Equal(TunerType.R820T, ready.Tuner!.Type);
        Assert.Equal([0, 9, 14], ready.Tuner.Gains);

        Assert.Equal(3, await c.CountIqFramesAsync(3));

        await c.SendCommandV2Async(RtlCommand.SetFrequency, 100_000_000);
        await Eventually(() => _source.Current!.Executed.Contains(new Command(RtlCommand.SetFrequency, 100_000_000)));
    }

    [Fact]
    public async Task Second_v2_client_gets_BUSY_with_owner_ip()
    {
        await using var first = await TestClient.ConnectAsync(Port);
        await first.ReadAsync(V1.HeaderSize);

        await using var second = await TestClient.ConnectAsync(Port);
        await second.SendHelloAsync();
        await second.ReadAsync(V2.HeaderSize);
        var error = await second.ExpectAsync<ErrorMessage>();
        Assert.Equal(ErrorCodes.Busy, error.Code);
        Assert.Equal("Server already in use by 127.0.0.1", error.Message);
        Assert.True(error.Fatal);
        Assert.True(await second.IsClosedAsync());
    }

    [Fact]
    public async Task BUSY_reaches_a_client_that_already_sent_commands()
    {
        // The client sends its start commands right after HELLO without waiting; the server must still deliver
        // the BUSY frame (no RST because of unread data).
        await using var first = await TestClient.ConnectAsync(Port);
        await first.ReadAsync(V1.HeaderSize);

        await using var second = await TestClient.ConnectAsync(Port);
        await second.SendHelloAsync();
        for (var i = 0; i < 50; i++) await second.SendCommandV2Async(RtlCommand.SetFrequency, 100_000_000);
        await second.ReadAsync(V2.HeaderSize);
        Assert.Equal(ErrorCodes.Busy, (await second.ExpectAsync<ErrorMessage>()).Code);
    }

    [Fact]
    public async Task V2_invalid_command_is_reported_but_not_fatal()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        await c.SendCommandV2Async(RtlCommand.SetSampleRate, 5);
        var error = await c.ExpectAsync<ErrorMessage>();
        Assert.Equal(ErrorCodes.InvalidCommand, error.Code);
        Assert.False(error.Fatal);
        Assert.Equal(1, await c.CountIqFramesAsync(1));
    }

    [Fact]
    public async Task V2_reports_not_ready_and_then_ready_when_stick_is_plugged_in()
    {
        _source.Present = false;
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        var notReady = await c.ExpectAsync<StatusMessage>(s => s.State != DeviceStates.Starting);
        Assert.Equal(DeviceStates.DeviceNotReady, notReady.State);
        Assert.Equal("USB device not ready", notReady.Message);

        _source.Present = true;
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        Assert.Equal(1, await c.CountIqFramesAsync(1));
    }

    [Fact]
    public async Task Unplugged_stick_is_reopened_and_client_settings_are_replayed()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        await c.SendCommandV2Async(RtlCommand.SetFrequency, 145_500_000);
        var first = _source.Current!;
        await Eventually(() => first.Executed.Contains(new Command(RtlCommand.SetFrequency, 145_500_000)));

        _source.Present = false;
        first.Stream!.Unplug();
        var lost = await c.ExpectAsync<StatusMessage>(s => s.State != DeviceStates.Ready);
        Assert.Equal(DeviceStates.DeviceLost, lost.State);
        Assert.True(first.Disposed);

        _source.Present = true;
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.DeviceRestored);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        Assert.NotSame(first, _source.Current);
        Assert.Contains(new Command(RtlCommand.SetFrequency, 145_500_000), _source.Current!.Executed);
        Assert.Equal(1, await c.CountIqFramesAsync(1));
    }

    [Fact]
    public async Task Stick_that_stops_delivering_data_is_reset_and_reopened()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        await c.SendCommandV2Async(RtlCommand.SetFrequency, 145_500_000);
        var first = _source.Current!;
        await Eventually(() => first.Executed.Contains(new Command(RtlCommand.SetFrequency, 145_500_000)));

        DeviceManager.StallTimeout = TimeSpan.FromSeconds(2); // TestClient reads time out after 5 s
        try
        {
            first.Stream!.Freeze();
            await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.DeviceRestored);
        }
        finally
        {
            DeviceManager.StallTimeout = TimeSpan.FromSeconds(5);
        }
        Assert.True(first.WasReset);
        Assert.True(first.Disposed);
        Assert.NotSame(first, _source.Current);
        Assert.Contains(new Command(RtlCommand.SetFrequency, 145_500_000), _source.Current!.Executed);
        Assert.Equal(1, await c.CountIqFramesAsync(1));
    }

    [Fact]
    public async Task Stick_is_closed_after_idle_timeout_and_reopened_by_next_client()
    {
        await using (var c = await TestClient.ConnectAsync(Port))
            await c.ReadAsync(V1.HeaderSize);
        var dev = _source.Current!;
        await Eventually(() => dev.Disposed, 4000);
        Assert.True(dev.PoweredDown);
        Assert.Equal(DeviceState.Idle, _manager.State);

        await using var again = await TestClient.ConnectAsync(Port);
        await again.ReadAsync(V1.HeaderSize);
        Assert.Equal(2, _source.Opens);
    }

    [Fact]
    public async Task Kicked_v2_client_gets_KICKED_and_stick_becomes_free()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        await c.ExpectAsync<StatusMessage>(s => s.State == DeviceStates.Ready);
        _manager.KickOwner();
        Assert.Equal(ErrorCodes.Kicked, (await c.ExpectAsync<ErrorMessage>()).Code);
        await Eventually(() => _manager.Owner is null);
    }

    [Fact]
    public async Task Server_info_names_port_and_source_of_the_stick()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendHelloAsync();
        await c.ReadAsync(V2.HeaderSize);
        var info = await c.ExpectAsync<ServerInfoMessage>();
        Assert.Equal("Test", info.Device.Name);
        Assert.Equal(Port, info.Device.Port);
        Assert.Equal(DeviceSources.Usb, info.Device.Source);
        Assert.Null(info.Device.Remote);
        Assert.Equal("Wien Dach", info.Device.Location);
        Assert.Null(info.Device.Antenna); // blank = not set, field omitted
        Assert.Equal("none", info.Auth);
    }

    [Fact]
    public async Task Info_hello_lists_the_sticks_and_does_not_take_one()
    {
        // someone owns the stick: the list still comes, with busy_by
        await using var owner = await TestClient.ConnectAsync(Port);
        await owner.ReadAsync(V1.HeaderSize);

        await using var c = await TestClient.ConnectAsync(Port);
        await c.SendAsync(V2.HelloInfo.ToArray());
        V2.ReadHeader(await c.ReadAsync(V2.HeaderSize));
        await c.ExpectAsync<ServerInfoMessage>();
        var list = await c.ExpectAsync<DevicesMessage>();
        var d = Assert.Single(list.Devices);
        Assert.Equal(Port, d.Port);
        Assert.Equal("Test", d.Name);
        Assert.Equal(DeviceSources.Usb, d.Source);
        Assert.Equal(DeviceStates.Ready, d.State);
        Assert.Equal("127.0.0.1", d.BusyBy);
        Assert.True(await c.IsClosedAsync());
        Assert.Equal("127.0.0.1", _manager.Owner!.RemoteAddress); // owner unchanged
    }

    [Fact]
    public void Devices_message_uses_snake_case_and_omits_empty_fields()
    {
        var json = System.Text.Encoding.UTF8.GetString(V2Json.Serialize(new DevicesMessage([
            new DeviceListEntry(1235, "Standort1 Stick 1", "0001", DeviceSources.Remote, new RemoteOrigin("Max", "Standort1"),
                DeviceStates.Ready, null, "R828D")])));
        Assert.Equal("""{"type":"devices","devices":[{"port":1235,"name":"Standort1 Stick 1","serial":"0001","source":"remote","remote":{"instance":"Max","site":"Standort1"},"state":"ready","tuner":"R828D"}]}""",
            json);
    }

    [Fact]
    public async Task V1_client_from_any_address_is_accepted_without_login()
    {
        await using var c = await TestClient.ConnectAsync(Port);
        Assert.Equal("RTL0"u8.ToArray(), (await c.ReadAsync(V1.HeaderSize))[..4]);
    }
}
