using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Config;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Device;

public enum DeviceState
{
    /// <summary>Stick closed (no client, idle timeout passed).</summary>
    Idle,
    /// <summary>Stick is being opened.</summary>
    Starting,
    /// <summary>Stick open; streaming while a client is connected.</summary>
    Ready,
    /// <summary>No stick with the configured serial found.</summary>
    NotReady,
    /// <summary>Stick disappeared while in use; reconnecting.</summary>
    Lost,
}

/// <summary>The client currently owning a stick.</summary>
public interface IStreamOwner
{
    string RemoteAddress { get; }

    /// <summary>"rtl_tcp" (v1) or "rtl_tcp Jay extended" (v2), for the status page.</summary>
    string Protocol => "";

    /// <summary>Bytes really written to the client's connection (IQ incl. framing), for the data rate display.</summary>
    long BytesSent => 0;

    /// <summary>IQ buffers thrown away because the connection was too slow.</summary>
    long DroppedBuffers => 0;

    /// <summary>The client is another rtltcp.server (stick shared over the internet).</summary>
    bool IsRemoteSite => false;

    /// <summary>Called on the USB thread; must copy and return quickly.</summary>
    void OnIq(ReadOnlySpan<byte> data);

    /// <param name="restored">True if the stick came back after <see cref="DeviceState.Lost"/>.</param>
    void OnStateChanged(DeviceState state, string message, bool restored);

    void OnCommandRejected(Command command, string reason);

    void OnKicked();

    /// <summary>A local client took the stick over (local clients have priority over remote sites).</summary>
    void OnPreempted(string by) => OnKicked();
}

public sealed record TunerSnapshot(TunerType Type, string Name, int[] Gains);

/// <summary>Current tuning as last requested by the client (or the defaults). Gain null = auto.</summary>
public sealed record TuningSnapshot(ulong? Frequency, uint? SampleRate, int? Gain, bool BiasTee);

/// <summary>
/// Owns one stick: open/close, idle shutdown, reconnect, ownership ("first come, first served").
/// All state changes run sequentially on one loop; public methods only post signals.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private abstract record Signal;
    private sealed record OwnerChanged(IStreamOwner? Owner) : Signal;
    private sealed record Execute(IStreamOwner Owner, Command Command) : Signal;
    private sealed record StreamFailed(int Generation, UsbException Error) : Signal;
    private sealed record Retry : Signal;
    private sealed record IdleExpired(int Generation) : Signal;
    private sealed record Kick : Signal;
    private sealed record CheckStall : Signal;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A USB stick streaming to a client that delivers no data for this long is reset and reopened
    /// (a hung stick otherwise stays in "streaming" forever until it is replugged).
    /// </summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly ISdrDeviceSource _source;
    private readonly Func<IReadOnlyCollection<string>> _claimedSerials;
    private readonly ILogger _log;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _ownerLock = new();
    private readonly Task _loop;

    private IStreamOwner? _owner;        // guarded by _ownerLock, read lock-free by the USB thread
    private ISdrDevice? _device;
    private IBulkStream? _stream;
    private int _streamGeneration;
    private long _lastDataTicks;         // Environment.TickCount64 of the last bulk data (or stream start)
    private int _idleGeneration;
    private bool _retryScheduled;
    private DeviceSettings _settings;

    public DeviceManager(DeviceConfig config, ISdrDeviceSource source, ILogger log,
        Func<IReadOnlyCollection<string>>? claimedSerials = null)
    {
        Config = config;
        _source = source;
        _log = log;
        _claimedSerials = claimedSerials ?? (() => []);
        _settings = DeviceSettings.FromDefaults(config.Defaults);
        UpdateTuning();
        _loop = Task.Run(RunAsync);
        if (AlwaysOn) Post(new Retry()); // open right away
        if (!_source.IsRemote) _ = WatchStallsAsync();
    }

    private async Task WatchStallsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token)) Post(new CheckStall());
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Keep the stick open without clients (idle timeout 0). Never for remote sticks.</summary>
    private bool AlwaysOn => Config.IdleTimeoutSeconds <= 0 && !_source.IsRemote;

    private string NotReadyText => _source.IsRemote ? "Remote stick not ready" : "USB device not ready";

    public DeviceConfig Config { get; }

    /// <summary>For remote sticks: instance and providing site (looked up when asked, names can change).</summary>
    public Func<Protocol.RemoteOrigin?> Origin { get; set; } = () => null;

    /// <summary>For remote sticks: location/antenna as announced by the provider (rtl.over.internet), if known.</summary>
    public Func<DeviceDetails?> AnnouncedDetails { get; set; } = () => null;

    /// <summary>What the provider reported in the last direct connection (kept after the connection ends).</summary>
    private DeviceDetails? _remoteDetails;

    /// <summary>
    /// Location and antenna shown to clients. USB stick: this server's settings. Remote stick: the provider's values
    /// (last connection, else rtl.over.internet), this server's settings only if the provider sends none.
    /// </summary>
    public DeviceDetails Details
    {
        get
        {
            var own = DeviceDetails.Of(Config.Location, Config.Antenna);
            if (Config.Remote is null) return own;
            var remote = Volatile.Read(ref _remoteDetails) ?? AnnouncedDetails();
            return remote is null || remote.IsEmpty ? own : remote;
        }
    }

    public DeviceState State { get; private set; } = DeviceState.Idle;
    public string StateMessage { get; private set; } = "Device idle";

    /// <summary>Serial of the stick currently open (also when the config serial is empty).</summary>
    public string? OpenSerial { get; private set; }

    public TunerSnapshot? Tuner { get; private set; }
    public IStreamOwner? Owner => Volatile.Read(ref _owner);

    /// <summary>When the current owner connected.</summary>
    public DateTimeOffset? OwnerSince { get; private set; }

    /// <summary>IQ bytes handed to clients since start (for rate display).</summary>
    public long BytesStreamed => Interlocked.Read(ref _bytes);

    public TuningSnapshot Tuning { get; private set; } = new(null, null, null, false);

    private long _bytes;

    private void UpdateTuning()
    {
        var gainMode = _settings.Get(RtlCommand.SetGainMode) ?? 0;
        int? gain = gainMode == 0 ? null
            : _settings.Get(RtlCommand.SetGain) is { } g ? unchecked((int)g)
            : _settings.Get(RtlCommand.SetGainByIndex) is { } i && Tuner is { Gains.Length: > 0 } t ? t.Gains[Math.Min((int)i, t.Gains.Length - 1)]
            : null;
        Tuning = new TuningSnapshot(_settings.Get(RtlCommand.SetFrequency), _settings.Get(RtlCommand.SetSampleRate), gain,
            _settings.Get(RtlCommand.SetBiasTee) is 1);
    }

    // ---------------------------------------------------------------- public API (thread-safe)

    /// <summary>
    /// Takes ownership. Returns false (and the current owner) if another client owns the stick.
    /// Local clients have priority: a local client takes the stick from a remote site (rtl.over.internet),
    /// which is then disconnected.
    /// </summary>
    public bool TryAcquire(IStreamOwner owner, out IStreamOwner? current)
    {
        IStreamOwner? preempted = null;
        lock (_ownerLock)
        {
            current = _owner;
            if (current is not null)
            {
                if (!current.IsRemoteSite || owner.IsRemoteSite) return false;
                preempted = current;
            }
            Volatile.Write(ref _owner, owner);
            OwnerSince = DateTimeOffset.Now;
        }
        if (preempted is not null)
        {
            // the old session's Release is a no-op now (it no longer owns the stick); the stream keeps running
            _log.LogInformation("{Device}: local client {Client} has priority, disconnecting remote site {Remote}",
                Config.Name, owner.RemoteAddress, preempted.RemoteAddress);
            preempted.OnPreempted(owner.RemoteAddress);
        }
        _log.LogInformation("{Device}: client {Client} connected", Config.Name, owner.RemoteAddress);
        Post(new OwnerChanged(owner));
        return true;
    }

    public void Release(IStreamOwner owner)
    {
        lock (_ownerLock)
        {
            if (_owner != owner) return;
            Volatile.Write(ref _owner, null);
            OwnerSince = null;
        }
        _log.LogInformation("{Device}: client {Client} disconnected", Config.Name, owner.RemoteAddress);
        Post(new OwnerChanged(null));
    }

    public void Send(IStreamOwner owner, Command command) => Post(new Execute(owner, command));

    /// <summary>Disconnects the current owner (admin action).</summary>
    public void KickOwner() => Post(new Kick());

    private void Post(Signal s) => _signals.Writer.TryWrite(s);

    private async Task PostLater(TimeSpan delay, Signal s)
    {
        try
        {
            await Task.Delay(delay, _cts.Token);
            Post(s);
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- loop

    private async Task RunAsync()
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_cts.Token))
            {
                try { Handle(signal); }
                catch (Exception ex) { _log.LogError(ex, "{Device}: error handling {Signal}", Config.Name, signal); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            CloseDevice(powerDown: true);
        }
    }

    private void Handle(Signal signal)
    {
        switch (signal)
        {
            case OwnerChanged { Owner: { } owner }:
                _idleGeneration++;
                _settings = DeviceSettings.FromDefaults(Config.Defaults);
                UpdateTuning();
                if (_device is null) TryOpen();
                else
                {
                    Replay();
                    StartStream();
                }
                owner.OnStateChanged(State, StateMessage, restored: false);
                break;

            case OwnerChanged { Owner: null }:
                StopStream();
                if (_device is null)
                {
                    if (!AlwaysOn) SetState(DeviceState.Idle, "Device idle");
                }
                else if (_source.IsRemote)
                {
                    _log.LogInformation("{Device}: last client left, closing connection to remote stick", Config.Name);
                    CloseDevice(powerDown: false);
                    SetState(DeviceState.Idle, "Device idle");
                }
                else if (!AlwaysOn)
                {
                    var gen = ++_idleGeneration;
                    _ = PostLater(TimeSpan.FromSeconds(Config.IdleTimeoutSeconds), new IdleExpired(gen));
                }
                break;

            case IdleExpired e when e.Generation == _idleGeneration && Owner is null:
                _log.LogInformation("{Device}: idle timeout, closing stick", Config.Name);
                CloseDevice(powerDown: true);
                SetState(DeviceState.Idle, "Device idle");
                break;

            case Execute x when x.Owner == Owner:
                ExecuteCommand(x.Owner, x.Command);
                break;

            case StreamFailed f when f.Generation == _streamGeneration:
                _log.LogWarning("{Device}: stick lost ({Error})", Config.Name, f.Error.Message);
                CloseDevice(powerDown: false);
                SetState(DeviceState.Lost, _source.IsRemote
                    ? "Connection to remote stick lost, reconnecting" : "USB device lost, reconnecting");
                ScheduleRetry();
                break;

            case Retry:
                _retryScheduled = false;
                if (_device is null && (Owner is not null || AlwaysOn)) TryOpen();
                break;

            case Kick when Owner is { } kicked:
                kicked.OnKicked();
                break;

            case CheckStall when _stream is not null && Owner is not null
                                 && Environment.TickCount64 - Volatile.Read(ref _lastDataTicks) > StallTimeout.TotalMilliseconds:
                ResetStalledDevice();
                break;
        }
    }

    /// <summary>Stick open and streaming but no data arrives: USB reset (software replug), then reopen.</summary>
    private void ResetStalledDevice()
    {
        _log.LogWarning("{Device}: no data from stick for {Seconds:0} s, resetting USB device",
            Config.Name, StallTimeout.TotalSeconds);
        StopStream();
        try { _device?.Reset(); }
        catch (Exception ex) { _log.LogDebug("{Device}: USB reset failed: {Error}", Config.Name, ex.Message); }
        CloseDevice(powerDown: false);
        SetState(DeviceState.Lost, "USB device not delivering data, resetting");
        ScheduleRetry();
    }

    // ---------------------------------------------------------------- device handling (loop only)

    private void TryOpen()
    {
        var wasLost = State == DeviceState.Lost;
        if (State is DeviceState.Idle)
            SetState(DeviceState.Starting, _source.IsRemote ? "Connecting to remote stick" : "Opening USB device");

        ISdrDevice? dev = null;
        try
        {
            dev = _source.TryOpen(Config.Serial, _claimedSerials());
        }
        catch (Exception ex)
        {
            _log.LogWarning("{Device}: cannot open stick: {Error}", Config.Name, ex.Message);
        }

        if (dev is null)
        {
            if (!wasLost) SetState(DeviceState.NotReady, _source.LastError ?? NotReadyText);
            ScheduleRetry();
            return;
        }

        _device = dev;
        OpenSerial = dev.Serial;
        if (dev.Details is { } details) Volatile.Write(ref _remoteDetails, details);
        Tuner = new TunerSnapshot(dev.TunerType, dev.TunerName, [.. dev.Gains]);
        _log.LogInformation("{Device}: opened stick {Serial} ({Tuner})", Config.Name, dev.Serial, dev.TunerName);
        Replay();
        if (Owner is not null) StartStream();
        SetState(DeviceState.Ready, "Device ready", restored: wasLost);
    }

    private void ScheduleRetry()
    {
        if (_retryScheduled) return;
        _retryScheduled = true;
        _ = PostLater(RetryInterval, new Retry());
    }

    private void Replay()
    {
        foreach (var cmd in _settings.Replay())
        {
            try { _device!.Execute(cmd); }
            catch (ArgumentOutOfRangeException ex) { _log.LogDebug("replay {Cmd} rejected: {Msg}", cmd, ex.Message); }
        }
    }

    private void ExecuteCommand(IStreamOwner owner, Command cmd)
    {
        try
        {
            if (_device is not null && !_device.Execute(cmd))
            {
                _log.LogDebug("{Device}: unsupported command {Cmd}", Config.Name, cmd);
                return;
            }
            _settings.Remember(cmd);
            UpdateTuning();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            owner.OnCommandRejected(cmd, ex.Message.Split(Environment.NewLine)[0]);
        }
        catch (UsbException ex) when (ex.DeviceGone)
        {
            _settings.Remember(cmd); // applied on reconnect
            Post(new StreamFailed(_streamGeneration, ex));
        }
    }

    private void StartStream()
    {
        if (_device is null || _stream is not null) return;
        var gen = ++_streamGeneration;
        Volatile.Write(ref _lastDataTicks, Environment.TickCount64);
        try
        {
            _stream = _device.StartStream(new Sink(this, gen));
        }
        catch (UsbException ex)
        {
            Post(new StreamFailed(gen, ex));
        }
    }

    private void StopStream()
    {
        _streamGeneration++;
        var s = _stream;
        _stream = null;
        s?.Dispose();
    }

    private void CloseDevice(bool powerDown)
    {
        StopStream();
        var dev = _device;
        _device = null;
        OpenSerial = null;
        if (dev is null) return;
        try
        {
            if (powerDown) dev.PowerDown();
        }
        finally
        {
            dev.Dispose();
        }
    }

    private void SetState(DeviceState state, string message, bool restored = false)
    {
        if (state == State && message == StateMessage && !restored) return;
        State = state;
        StateMessage = message;
        Owner?.OnStateChanged(state, message, restored);
    }

    private sealed class Sink(DeviceManager manager, int generation) : IBulkSink
    {
        public void OnData(ReadOnlySpan<byte> data)
        {
            if (generation != Volatile.Read(ref manager._streamGeneration)) return;
            Volatile.Write(ref manager._lastDataTicks, Environment.TickCount64);
            if (Volatile.Read(ref manager._owner) is not { } owner) return;
            owner.OnIq(data);
            Interlocked.Add(ref manager._bytes, data.Length);
        }

        public void OnError(UsbException error) => manager.Post(new StreamFailed(generation, error));
    }

    public async ValueTask DisposeAsync()
    {
        _signals.Writer.TryComplete();
        await _cts.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
