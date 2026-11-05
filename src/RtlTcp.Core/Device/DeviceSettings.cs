using RtlTcp.Core.Config;
using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Device;

/// <summary>
/// The last value of every v1 command a client sent, so the state can be replayed after a reconnect.
/// </summary>
public sealed class DeviceSettings
{
    /// <summary>Order in which settings are replayed (dependencies first).</summary>
    private static readonly RtlCommand[] ReplayOrder =
    [
        RtlCommand.SetFreqCorrection, RtlCommand.SetRtlXtal, RtlCommand.SetTunerXtal,
        RtlCommand.SetSampleRate, RtlCommand.SetDirectSampling, RtlCommand.SetOffsetTuning,
        RtlCommand.SetFrequency, RtlCommand.SetGainMode, RtlCommand.SetGain, RtlCommand.SetGainByIndex,
        RtlCommand.SetIfGain, RtlCommand.SetAgcMode, RtlCommand.SetTestMode, RtlCommand.SetBiasTee,
    ];

    private readonly Dictionary<RtlCommand, uint> _values = [];

    public static DeviceSettings FromDefaults(DeviceDefaults d)
    {
        var s = new DeviceSettings();
        s.Remember(new Command(RtlCommand.SetFreqCorrection, unchecked((uint)d.Ppm)));
        s.Remember(new Command(RtlCommand.SetSampleRate, d.SampleRate));
        s.Remember(new Command(RtlCommand.SetFrequency, (uint)d.Frequency));
        s.Remember(new Command(RtlCommand.SetGainMode, d.Gain is null ? 0u : 1u));
        if (d.Gain is { } g) s.Remember(new Command(RtlCommand.SetGain, unchecked((uint)g)));
        s.Remember(new Command(RtlCommand.SetBiasTee, d.BiasTee ? 1u : 0u));
        return s;
    }

    public void Remember(Command cmd)
    {
        if (!ReplayOrder.Contains(cmd.Code)) return;
        // Gain by value and gain by index describe the same setting: keep only the latest.
        if (cmd.Code == RtlCommand.SetGain) _values.Remove(RtlCommand.SetGainByIndex);
        if (cmd.Code == RtlCommand.SetGainByIndex) _values.Remove(RtlCommand.SetGain);
        _values[cmd.Code] = cmd.Param;
    }

    public uint? Get(RtlCommand code) => _values.TryGetValue(code, out var v) ? v : null;

    /// <summary>Commands to bring a freshly opened stick into this state.</summary>
    public IEnumerable<Command> Replay()
    {
        var autoGain = _values.GetValueOrDefault(RtlCommand.SetGainMode, 0u) == 0;
        foreach (var code in ReplayOrder)
        {
            if (!_values.TryGetValue(code, out var v)) continue;
            if (autoGain && code is RtlCommand.SetGain or RtlCommand.SetGainByIndex) continue;
            yield return new Command(code, v);
        }
    }
}
