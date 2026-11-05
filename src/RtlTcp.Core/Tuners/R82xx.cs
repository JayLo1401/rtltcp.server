using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Tuners;

/// <summary>
/// Rafael Micro R820T / R820T2 / R828D tuner.
/// Register values are the chip's documented programming data; the driver logic is an independent implementation.
/// </summary>
public sealed class R82xx : ITuner
{
    public const byte AddressR820T = 0x34;
    public const byte AddressR828D = 0x74;

    private const int FirstReg = 0x05;
    private const int RegCount = 0x20;
    private const int MaxI2cChunk = 7; // RTL2832 I²C: 8 bytes per message incl. register address
    private const ulong VcoMin = 1_770_000_000;
    private const ulong VcoMax = VcoMin * 2;
    private const double IfHz = 3_570_000;

    /// <summary>Power-on defaults for registers 0x05–0x1F.</summary>
    private static readonly byte[] InitRegs =
    [
        0x83, 0x32, 0x75,                   // 05–07
        0xC0, 0x40, 0xD6, 0x6C,             // 08–0B
        0xF5, 0x63, 0x75, 0x68,             // 0C–0F
        0x6C, 0x83, 0x80, 0x00,             // 10–13
        0x0F, 0x00, 0xC0, 0x30,             // 14–17
        0x48, 0xCC, 0x60, 0x00,             // 18–1B
        0x54, 0xAE, 0x4A, 0xC0,             // 1C–1F
    ];

    /// <summary>Gain increments of the LNA (index 0–15) and mixer (0–15) in tenths of dB.</summary>
    private static readonly int[] LnaSteps = [0, 9, 13, 40, 38, 13, 31, 22, 26, 31, 26, 14, 19, 5, 35, 13];
    private static readonly int[] MixerSteps = [0, 5, 10, 10, 19, 9, 10, 25, 17, 10, 8, 16, 13, 6, 3, -8];

    /// <summary>RF input band setup: (lower edge MHz, open drain, RF mux/polyphase, tracking filter cap).</summary>
    private static readonly (int Mhz, byte OpenDrain, byte RfMux, byte TfCap)[] Bands =
    [
        (0, 0x08, 0x02, 0xDF), (50, 0x08, 0x02, 0xBE), (55, 0x08, 0x02, 0x8B), (60, 0x08, 0x02, 0x7B),
        (65, 0x08, 0x02, 0x69), (70, 0x08, 0x02, 0x58), (75, 0x00, 0x02, 0x44), (80, 0x00, 0x02, 0x44),
        (90, 0x00, 0x02, 0x34), (100, 0x00, 0x02, 0x34), (110, 0x00, 0x02, 0x24), (120, 0x00, 0x02, 0x24),
        (140, 0x00, 0x02, 0x14), (180, 0x00, 0x02, 0x13), (220, 0x00, 0x02, 0x13), (250, 0x00, 0x02, 0x11),
        (280, 0x00, 0x02, 0x00), (310, 0x00, 0x41, 0x00), (450, 0x00, 0x41, 0x00), (588, 0x00, 0x40, 0x00),
        (650, 0x00, 0x40, 0x00),
    ];

    private readonly II2cBus _bus;
    private readonly byte _addr;
    private readonly bool _isR828D;
    private readonly byte[] _regs = new byte[RegCount];
    private readonly int[] _gains;
    private double _xtal;
    private byte _filterCalCode;

    public R82xx(II2cBus bus, TunerType type)
    {
        if (type is not (TunerType.R820T or TunerType.R828D)) throw new ArgumentException("Not an R82xx tuner", nameof(type));
        _bus = bus;
        Type = type;
        _isR828D = type == TunerType.R828D;
        _addr = _isR828D ? AddressR828D : AddressR820T;
        _gains = ComputeGainTable();
    }

    public TunerType Type { get; }
    public string Name => _isR828D ? "R828D" : "R820T";
    public IReadOnlyList<int> Gains => _gains;
    public double IfFrequency => IfHz;
    public bool SpectrumInverted => true;
    public bool HasLock { get; private set; }

    /// <summary>All reachable totals when LNA and mixer are raised alternately (that is how manual gain is applied).</summary>
    private static int[] ComputeGainTable()
    {
        var list = new List<int> { 0 };
        int total = 0, lna = 0, mix = 0;
        while (lna < 15 || mix < 15)
        {
            if (lna < 15) list.Add(total += LnaSteps[++lna]);
            if (mix < 15) list.Add(total += MixerSteps[++mix]);
        }
        return [.. list.Distinct().Order()];
    }

    // ---------------------------------------------------------------- register I/O with shadow copy

    private void Write(int reg, ReadOnlySpan<byte> values)
    {
        values.CopyTo(_regs.AsSpan(reg));
        Span<byte> msg = stackalloc byte[MaxI2cChunk + 1];
        for (var pos = 0; pos < values.Length; pos += MaxI2cChunk)
        {
            var n = Math.Min(MaxI2cChunk, values.Length - pos);
            msg[0] = (byte)(reg + pos);
            values.Slice(pos, n).CopyTo(msg[1..]);
            I2cWriteWithRetry(msg[..(n + 1)]);
        }
    }

    /// <summary>Number of I²C writes that needed a retry (diagnostics).</summary>
    public int I2cRetries { get; private set; }

    /// <summary>
    /// After a pause on the bus (e.g. the 250 ms AGC discharge wait) the first I²C write reliably stalls,
    /// although the repeater is still enabled. Any demod register access clears it, so recover and retry.
    /// </summary>
    private void I2cWriteWithRetry(ReadOnlySpan<byte> msg)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _bus.Write(_addr, msg);
                return;
            }
            catch (Usb.UsbException e) when (!e.DeviceGone && attempt < 3)
            {
                I2cRetries++;
                Thread.Sleep(2);
                _bus.Recover();
            }
        }
    }

    private void Write(int reg, byte value) => Write(reg, [value]);

    private void WriteMask(int reg, byte value, byte mask) =>
        Write(reg, (byte)((_regs[reg] & ~mask) | (value & mask)));

    /// <summary>Reads registers 0..n-1. The chip returns every byte bit-reversed.</summary>
    private byte[] Read(int count)
    {
        _bus.Write(_addr, [0x00]);
        var buf = new byte[count];
        _bus.Read(_addr, buf);
        for (var i = 0; i < buf.Length; i++) buf[i] = ReverseBits(buf[i]);
        return buf;
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    // ---------------------------------------------------------------- init

    public void Init(double xtal)
    {
        _xtal = xtal;
        Write(FirstReg, InitRegs);
        InitDigitalMode();
        InitAgcAndInput();
    }

    public void SetXtal(double xtal) => _xtal = xtal;

    /// <summary>Filter calibration and channel filter setup for SDR use with a fixed 3.57 MHz IF.</summary>
    private void InitDigitalMode()
    {
        const byte hpCorner = 0x6B, filterGain = 0x10, imageRej = 0x00, filterQ = 0x10;
        const byte extEnable = 0x60, loopThrough = 0x00, loopAtt = 0x00, extWidest = 0x00, polyCurrent = 0x60;
        const ulong calLo = 56_000_000;

        WriteMask(0x0C, 0x00, 0x0F);
        WriteMask(0x13, 49, 0x3F);        // version tag expected by the chip
        WriteMask(0x1D, 0x00, 0x38);
        Thread.Sleep(1);

        _filterCalCode = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            WriteMask(0x0B, hpCorner, 0x60);
            WriteMask(0x0F, 0x04, 0x04);  // calibration clock on
            WriteMask(0x10, 0x00, 0x03);  // xtal cap 0 pF
            if (!SetPll(calLo)) throw new TunerException("PLL did not lock during filter calibration");
            WriteMask(0x0B, 0x10, 0x10);  // start calibration
            Thread.Sleep(1);
            WriteMask(0x0B, 0x00, 0x10);
            WriteMask(0x0F, 0x00, 0x04);  // calibration clock off
            _filterCalCode = (byte)(Read(5)[4] & 0x0F);
            if (_filterCalCode is not (0 or 0x0F)) break;
        }
        if (_filterCalCode == 0x0F) _filterCalCode = 0;

        WriteMask(0x0A, (byte)(filterQ | _filterCalCode), 0x1F);
        WriteMask(0x0B, hpCorner, 0xEF);
        WriteMask(0x07, imageRej, 0x80);
        WriteMask(0x06, filterGain, 0x30);
        WriteMask(0x1E, extEnable, 0x60);
        WriteMask(0x05, loopThrough, 0x80);
        WriteMask(0x1F, loopAtt, 0x80);
        WriteMask(0x0F, extWidest, 0x80);
        WriteMask(0x19, polyCurrent, 0x60);
    }

    private void InitAgcAndInput()
    {
        const byte lnaTop = 0xE5, mixerTop = 0x24, lnaVth = 0x53, mixerVth = 0x75;
        const byte cable1In = 0x00, cable2In = 0x00, cpCurrent = 0x38, divBufCurrent = 0x30, filterCurrent = 0x40;
        const byte lnaDischarge = 14;

        WriteMask(0x1D, lnaTop, 0xC7);
        WriteMask(0x1C, mixerTop, 0xF8);
        Write(0x0D, lnaVth);
        Write(0x0E, mixerVth);
        WriteMask(0x05, cable1In, 0x60);
        WriteMask(0x06, cable2In, 0x08);
        WriteMask(0x11, cpCurrent, 0x38);
        WriteMask(0x17, divBufCurrent, 0x30);
        WriteMask(0x0A, filterCurrent, 0x60);

        // LNA discharge sequence
        WriteMask(0x1D, 0x00, 0x38);      // LNA top lowest
        WriteMask(0x1C, 0x00, 0x04);      // normal mode
        WriteMask(0x06, 0x00, 0x40);      // pre-detect off
        WriteMask(0x1A, 0x30, 0x30);      // AGC clock 250 Hz
        Thread.Sleep(250);
        WriteMask(0x1D, 0x18, 0x38);      // LNA top 3
        WriteMask(0x1C, mixerTop, 0x04);  // discharge mode
        WriteMask(0x1E, lnaDischarge, 0x1F);
        WriteMask(0x1A, 0x20, 0x30);      // AGC clock 60 Hz
    }

    // ---------------------------------------------------------------- tuning

    public bool SetFrequency(ulong hz)
    {
        var lo = hz + (ulong)IfHz;
        SelectBand(lo);
        HasLock = SetPll(lo);
        if (_isR828D) WriteMask(0x05, (byte)(hz > 345_000_000 ? 0x00 : 0x60), 0x60); // R828D: cable-1 input below 345 MHz
        return HasLock;
    }

    private void SelectBand(ulong loHz)
    {
        var mhz = (int)(loHz / 1_000_000);
        var band = Bands[0];
        foreach (var b in Bands)
            if (mhz >= b.Mhz) band = b;

        WriteMask(0x17, band.OpenDrain, 0x08);
        WriteMask(0x1A, band.RfMux, 0xC3);
        Write(0x1B, band.TfCap);
        WriteMask(0x10, 0x00, 0x0B);      // xtal cap 0 pF, high drive
        WriteMask(0x08, 0x00, 0x3F);
        WriteMask(0x09, 0x00, 0x3F);
    }

    /// <summary>
    /// Programs the fractional-N PLL: VCO = LO × mixDiv must be within 1.77–3.54 GHz,
    /// VCO / (2 × ref) = N + SDM/65536 with N = 4·Ni + Si + 13.
    /// </summary>
    private bool SetPll(ulong loHz)
    {
        WriteMask(0x10, 0x00, 0x10);      // reference divider off
        WriteMask(0x1A, 0x00, 0x0C);      // PLL autotune 128 kHz
        WriteMask(0x12, 0x80, 0xE0);      // VCO current

        uint mixDiv = 2;
        var divNum = 0;
        while (mixDiv <= 64)
        {
            var vco = loHz * mixDiv;
            if (vco is >= VcoMin and < VcoMax) break;
            mixDiv <<= 1;
            divNum++;
        }
        if (mixDiv > 64) throw new TunerException($"Frequency {loHz} Hz out of range");

        // The VCO band selected by the chip shifts the usable divider by one step.
        var vcoPowerRef = _isR828D ? 1 : 2;
        var fineTune = (Read(5)[4] & 0x30) >> 4;
        if (fineTune > vcoPowerRef) divNum--;
        else if (fineTune < vcoPowerRef) divNum++;
        WriteMask(0x10, (byte)(divNum << 5), 0xE0);

        var n = (double)loHz * mixDiv / (2 * _xtal);
        var nInt = (int)Math.Floor(n);
        var sdm = (int)Math.Round((n - nInt) * 65536);
        if (sdm == 65536) { nInt++; sdm = 0; }
        if (nInt < 13 || nInt > 128 / vcoPowerRef - 1) throw new TunerException($"PLL N={nInt} out of range");

        var ni = (nInt - 13) / 4;
        var si = nInt - 13 - 4 * ni;
        Write(0x14, (byte)(ni | (si << 6)));
        WriteMask(0x12, (byte)(sdm == 0 ? 0x08 : 0x00), 0x08);   // SDM off for integer N
        Write(0x15, [(byte)(sdm & 0xFF), (byte)(sdm >> 8)]);   // 0x15 low, 0x16 high

        var locked = false;
        for (var i = 0; i < 2 && !locked; i++)
        {
            Thread.Sleep(1);
            locked = (Read(3)[2] & 0x40) != 0;
            if (!locked && i == 0) WriteMask(0x12, 0x60, 0xE0);   // retry with higher VCO current
        }
        if (locked) WriteMask(0x1A, 0x08, 0x08);                // PLL autotune 8 kHz
        return locked;
    }

    // ---------------------------------------------------------------- gain

    public void SetAutoGain()
    {
        WriteMask(0x05, 0x00, 0x10);      // LNA AGC on
        WriteMask(0x07, 0x10, 0x10);      // mixer AGC on
        WriteMask(0x0C, 0x0B, 0x9F);      // fixed VGA ≈ 26.5 dB
    }

    public int SetManualGain(int tenthDb)
    {
        WriteMask(0x05, 0x10, 0x10);      // LNA AGC off
        WriteMask(0x07, 0x00, 0x10);      // mixer AGC off
        WriteMask(0x0C, 0x08, 0x9F);      // fixed VGA ≈ 16.3 dB

        int total = 0, lna = 0, mix = 0;
        while ((lna < 15 || mix < 15) && total < tenthDb)
        {
            if (lna < 15) total += LnaSteps[++lna];
            if (total >= tenthDb) break;
            if (mix < 15) total += MixerSteps[++mix];
        }
        WriteMask(0x05, (byte)lna, 0x0F);
        WriteMask(0x07, (byte)mix, 0x0F);
        return total;
    }

    // ---------------------------------------------------------------- power

    public void Standby()
    {
        Write(0x06, 0xB1);
        Write(0x05, 0x03);
        Write(0x07, 0x3A);
        Write(0x08, 0x40);
        Write(0x09, 0xC0);
        Write(0x0A, 0x36);
        Write(0x0C, 0x35);
        Write(0x0F, 0x68);
        Write(0x11, 0x03);
        Write(0x17, 0xF4);
        Write(0x19, 0x0C);
    }
}

public sealed class TunerException(string message) : Exception(message);
