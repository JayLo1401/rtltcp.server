using RtlTcp.Core.Protocol;
using RtlTcp.Core.Tuners;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Rtl2832;

/// <summary>
/// One opened receiver: RTL2832U demodulator + tuner. Not thread-safe; the caller serializes access.
/// Settings mirror the v1 commands so a client's state can be re-applied after a reconnect.
/// </summary>
public sealed class RtlSdrDevice : IDisposable
{
    private readonly Rtl2832 _demod;
    private readonly ITuner? _tuner;

    private RtlSdrDevice(Rtl2832 demod, ITuner? tuner, TunerType tunerType)
    {
        _demod = demod;
        _tuner = tuner;
        TunerType = tunerType;
    }

    public TunerType TunerType { get; }
    public string TunerName => _tuner?.Name ?? TunerType.ToString();
    public IReadOnlyList<int> Gains => _tuner?.Gains ?? [];

    public ulong CenterFrequency { get; private set; }
    public uint SampleRate => _demod.SampleRate;
    public int DirectSampling { get; private set; }
    public bool TunerLocked { get; private set; }

    public static RtlSdrDevice Open(IUsbDeviceHandle usb)
    {
        var demod = new Rtl2832(usb);
        try
        {
            demod.InitBaseband();
            var type = demod.ProbeTuner();
            ITuner? tuner = type switch
            {
                TunerType.R820T or TunerType.R828D => new R82xx(demod, type),
                _ => null, // other tuners: later; device still streams (direct sampling works)
            };
            var dev = new RtlSdrDevice(demod, tuner, type);
            dev.InitTuner();
            return dev;
        }
        catch
        {
            demod.Dispose();
            throw;
        }
    }

    private void InitTuner()
    {
        if (_tuner is null) return;
        WithTuner(t => t.Init(_demod.CorrectedXtal));
        _demod.SetIfFrequency(_tuner.IfFrequency);
        _demod.SetSpectrumInversion(_tuner.SpectrumInverted);
        if (_tuner.IfFrequency != 0)
        {
            _demod.DemodWrite(1, 0xB1, 0x1A); // low-IF: zero-IF mode off
            _demod.DemodWrite(0, 0x08, 0x4D); // only I-ADC input
        }
    }

    private T WithTuner<T>(Func<ITuner, T> action)
    {
        _demod.SetI2cRepeater(true);
        try { return action(_tuner!); }
        finally { _demod.SetI2cRepeater(false); }
    }

    private void WithTuner(Action<ITuner> action) => WithTuner(t => { action(t); return 0; });

    // ---------------------------------------------------------------- v1-equivalent settings

    /// <summary>Executes a v1 command; false if not supported by this hardware.</summary>
    public bool Execute(Command cmd)
    {
        switch (cmd.Code)
        {
            case RtlCommand.SetFrequency: SetCenterFrequency(cmd.Param); return true;
            case RtlCommand.SetSampleRate: SetSampleRate(cmd.Param); return true;
            case RtlCommand.SetGainMode: SetAutoGain(cmd.Param == 0); return true;
            case RtlCommand.SetGain: SetGain(cmd.SignedParam); return true;
            case RtlCommand.SetFreqCorrection: SetFreqCorrection(cmd.SignedParam); return true;
            case RtlCommand.SetTestMode: SetTestMode(cmd.Param != 0); return true;
            case RtlCommand.SetAgcMode: SetAgc(cmd.Param != 0); return true;
            case RtlCommand.SetDirectSampling:
                if (cmd.Param > 2) throw new ArgumentOutOfRangeException(nameof(cmd), "Direct sampling: 0, 1 or 2");
                SetDirectSampling((int)cmd.Param);
                return true;
            case RtlCommand.SetGainByIndex: SetGainByIndex((int)cmd.Param); return true;
            case RtlCommand.SetBiasTee: SetBiasTee(cmd.Param != 0); return true;
            case RtlCommand.SetOffsetTuning:   // only meaningful for zero-IF tuners (E4000); R82xx uses low IF
            case RtlCommand.SetIfGain:         // only E4000
            case RtlCommand.SetRtlXtal:
            case RtlCommand.SetTunerXtal:
            default:
                return false;
        }
    }

    public void SetCenterFrequency(ulong hz)
    {
        if (DirectSampling != 0)
            _demod.SetIfFrequency(hz);
        else if (_tuner is not null)
            TunerLocked = WithTuner(t => t.SetFrequency(hz));
        CenterFrequency = hz;
    }

    /// <summary>Safe while streaming (no FIFO reset here; resetting the endpoint during transfers stalls the pipe).</summary>
    public double SetSampleRate(uint rate) => _demod.SetSampleRate(rate);

    public void SetAutoGain(bool auto)
    {
        if (_tuner is null) return;
        if (auto) WithTuner(t => t.SetAutoGain());
        else WithTuner(t => t.SetManualGain(Gains.Count > 0 ? Gains[^1] : 0));
    }

    public int SetGain(int tenthDb) => _tuner is null ? 0 : WithTuner(t => t.SetManualGain(tenthDb));

    public int SetGainByIndex(int index) =>
        Gains.Count == 0 ? 0 : SetGain(Gains[Math.Clamp(index, 0, Gains.Count - 1)]);

    public void SetFreqCorrection(int ppm)
    {
        _demod.SetFreqCorrection(ppm);
        if (_tuner is null) return;
        WithTuner(t => t.SetXtal(_demod.CorrectedXtal));
        _demod.SetIfFrequency(_tuner.IfFrequency);
        if (CenterFrequency != 0) SetCenterFrequency(CenterFrequency);
    }

    public void SetAgc(bool on) => _demod.SetAgc(on);

    public void SetTestMode(bool on) => _demod.SetTestMode(on);

    public void SetBiasTee(bool on) => _demod.SetBiasTee(on);

    public void SetDirectSampling(int mode)
    {
        _demod.SetDirectSampling(mode);
        DirectSampling = mode;
        if (mode == 0) InitTuner();
        if (CenterFrequency != 0) SetCenterFrequency(CenterFrequency);
    }

    public IBulkStream StartStream(IBulkSink sink)
    {
        _demod.ResetBuffer();
        return _demod.StartStream(sink);
    }

    /// <summary>Puts tuner and demodulator into standby (used before closing on idle).</summary>
    public void PowerDown()
    {
        try
        {
            if (_tuner is not null) WithTuner(t => t.Standby());
            _demod.PowerDown();
        }
        catch (UsbException) { /* device may already be gone */ }
    }

    public void Dispose() => _demod.Dispose();
}
