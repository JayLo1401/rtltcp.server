using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Tuners;

/// <summary>RF tuner behind the RTL2832U. All calls happen with the I²C repeater handled by the caller.</summary>
public interface ITuner
{
    TunerType Type { get; }

    string Name { get; }

    /// <summary>Selectable gains in tenths of dB, ascending (as reported to v1 clients).</summary>
    IReadOnlyList<int> Gains { get; }

    /// <summary>IF the tuner delivers to the demodulator (0 = zero-IF).</summary>
    double IfFrequency { get; }

    /// <summary>True if the tuner output spectrum is inverted relative to RF.</summary>
    bool SpectrumInverted { get; }

    /// <param name="xtal">Tuner reference frequency in Hz, already corrected by ppm.</param>
    void Init(double xtal);

    /// <summary>Updates the reference frequency (ppm change) without re-initializing.</summary>
    void SetXtal(double xtal);

    /// <summary>Tunes to <paramref name="hz"/> (RF frequency, the IF offset is added internally).</summary>
    /// <returns>False if the PLL did not lock.</returns>
    bool SetFrequency(ulong hz);

    void SetAutoGain();

    /// <summary>Sets the nearest available manual gain; returns the gain actually set (tenths of dB).</summary>
    int SetManualGain(int tenthDb);

    /// <summary>Puts the tuner into its lowest-power state.</summary>
    void Standby();
}
