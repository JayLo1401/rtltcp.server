using System.Buffers.Binary;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Rtl2832;

/// <summary>
/// Realtek RTL2832U demodulator in SDR mode: register access, baseband init, sample rate, I²C to the tuner.
/// Written from the chip's documented vendor-request interface; no code taken from other drivers.
/// </summary>
public sealed class Rtl2832 : IDisposable, Tuners.II2cBus
{
    void Tuners.II2cBus.Write(byte address, ReadOnlySpan<byte> data) => I2cWrite(address, data);

    void Tuners.II2cBus.Read(byte address, Span<byte> data) => I2cRead(address, data);

    void Tuners.II2cBus.Recover()
    {
        var before = DemodRead(1, 0x01);
        SetI2cRepeater(true);
        I2cRecoveryLog?.Invoke($"I2C recover: demod 1/0x01 was 0x{before:X2}");
    }

    /// <summary>Diagnostics hook for I²C recoveries.</summary>
    public static Action<string>? I2cRecoveryLog { get; set; }

    public const uint DefaultXtal = 28_800_000;
    public const byte BulkEndpoint = 0x81;

    // Register blocks addressed through wIndex (block << 8).
    private enum Block : byte { Demod = 0, Usb = 1, Sys = 2, Tuner = 3, Rom = 4, Ir = 5, I2c = 6 }

    private const ushort UsbSysctl = 0x2000;
    private const ushort UsbEpaCtl = 0x2148;
    private const ushort UsbEpaMaxPkt = 0x2158;
    private const ushort SysDemodCtl = 0x3000;
    private const ushort SysGpo = 0x3001;
    private const ushort SysGpoEnable = 0x3003;
    private const ushort SysGpd = 0x3004;
    private const ushort SysDemodCtl1 = 0x300B;

    private const byte WriteFlag = 0x10;

    private readonly IUsbDeviceHandle _usb;

    public Rtl2832(IUsbDeviceHandle usb) => _usb = usb;

    public uint Xtal { get; private set; } = DefaultXtal;
    public int PpmCorrection { get; private set; }
    public uint SampleRate { get; private set; }

    /// <summary>Crystal frequency corrected by the ppm value.</summary>
    public double CorrectedXtal => Xtal * (1.0 + PpmCorrection / 1e6);

    // ---------------------------------------------------------------- raw register access

    private void WriteReg(Block block, ushort addr, uint value, int len)
    {
        Span<byte> buf = stackalloc byte[2];
        if (len == 1) buf[0] = (byte)value;
        else BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value);
        _usb.ControlOut(0, addr, (ushort)(((byte)block << 8) | WriteFlag), buf[..len]);
    }

    private uint ReadReg(Block block, ushort addr, int len)
    {
        Span<byte> buf = stackalloc byte[2];
        _usb.ControlIn(0, addr, (ushort)((byte)block << 8), buf[..len]);
        return len == 1 ? buf[0] : BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    /// <summary>Writes a demodulator register (page 0–4). Every write is followed by a dummy read as sync.</summary>
    public void DemodWrite(byte page, byte addr, uint value, int len = 1)
    {
        Span<byte> buf = stackalloc byte[2];
        if (len == 1) buf[0] = (byte)value;
        else BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value);
        _usb.ControlOut(0, (ushort)((addr << 8) | 0x20), (ushort)(WriteFlag | page), buf[..len]);
        DemodRead(0x0A, 0x01);
    }

    public uint DemodRead(byte page, byte addr, int len = 1)
    {
        Span<byte> buf = stackalloc byte[2];
        _usb.ControlIn(0, (ushort)((addr << 8) | 0x20), page, buf[..len]);
        return len == 1 ? buf[0] : BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    // ---------------------------------------------------------------- I²C (tuner)

    public void SetI2cRepeater(bool on) => DemodWrite(1, 0x01, on ? 0x18u : 0x10u);

    public void I2cWrite(byte i2cAddr, ReadOnlySpan<byte> data) =>
        _usb.ControlOut(0, i2cAddr, ((byte)Block.I2c << 8) | WriteFlag, data);

    public void I2cRead(byte i2cAddr, Span<byte> data) =>
        _usb.ControlIn(0, i2cAddr, (byte)Block.I2c << 8, data);

    public void I2cWriteReg(byte i2cAddr, byte reg, byte value) => I2cWrite(i2cAddr, [reg, value]);

    /// <summary>Reads one register by first writing its address (works for most tuners).</summary>
    public byte I2cReadReg(byte i2cAddr, byte reg)
    {
        I2cWrite(i2cAddr, [reg]);
        Span<byte> b = stackalloc byte[1];
        I2cRead(i2cAddr, b);
        return b[0];
    }

    // ---------------------------------------------------------------- GPIO (bias tee etc.)

    public void SetGpioOutput(int gpio)
    {
        var bit = 1u << gpio;
        WriteReg(Block.Sys, SysGpd, ReadReg(Block.Sys, SysGpd, 1) & ~bit, 1);
        WriteReg(Block.Sys, SysGpoEnable, ReadReg(Block.Sys, SysGpoEnable, 1) | bit, 1);
    }

    public void SetGpio(int gpio, bool on)
    {
        var bit = 1u << gpio;
        var v = ReadReg(Block.Sys, SysGpo, 1);
        WriteReg(Block.Sys, SysGpo, on ? v | bit : v & ~bit, 1);
    }

    public void SetBiasTee(bool on, int gpio = 0)
    {
        SetGpioOutput(gpio);
        SetGpio(gpio, on);
    }

    // ---------------------------------------------------------------- init / power

    /// <summary>
    /// RTL2832 default 16-tap FIR low-pass (symmetric, 8 × 8-bit + 8 × 12-bit coefficients),
    /// as documented for the chip's power-on default.
    /// </summary>
    private static readonly short[] DefaultFir = [-54, -36, -41, -40, -32, -14, 14, 53, 101, 156, 215, 273, 327, 372, 404, 421];

    public void InitBaseband()
    {
        // USB: init endpoint A for bulk-in, max packet 512
        WriteReg(Block.Usb, UsbSysctl, 0x09, 1);
        WriteReg(Block.Usb, UsbEpaMaxPkt, 0x0002, 2);
        WriteReg(Block.Usb, UsbEpaCtl, 0x1002, 2);

        // Power on demodulator and ADCs
        WriteReg(Block.Sys, SysDemodCtl1, 0x22, 1);
        WriteReg(Block.Sys, SysDemodCtl, 0xE8, 1);

        SoftReset();

        DemodWrite(1, 0x15, 0x00);        // no spectrum inversion
        DemodWrite(1, 0x16, 0x0000, 2);   // no adjacent channel rejection
        for (byte i = 0; i < 6; i++)      // IF frequency = 0 (DDC off)
            DemodWrite(1, (byte)(0x16 + i), 0x00);

        WriteFir(DefaultFir);

        DemodWrite(0, 0x19, 0x05);        // SDR mode, digital AGC off
        DemodWrite(1, 0x93, 0xF0);        // FSM state-holding registers
        DemodWrite(1, 0x94, 0x0F);
        DemodWrite(1, 0x11, 0x00);        // DAGC off
        DemodWrite(1, 0x04, 0x00);        // RF/IF AGC loop off
        DemodWrite(0, 0x61, 0x60);        // PID filter off
        DemodWrite(0, 0x06, 0x80);        // default ADC I/Q datapath
        DemodWrite(1, 0xB1, 0x1B);        // zero-IF, DC cancellation, IQ estimation/compensation
        DemodWrite(0, 0x0D, 0x83);        // TP_CK0 clock output off
    }

    public void PowerDown()
    {
        DemodWrite(1, 0x01, 0x20);        // hold demod in reset
        WriteReg(Block.Sys, SysDemodCtl, 0x20, 1); // ADCs and PLL off
    }

    public void SoftReset()
    {
        DemodWrite(1, 0x01, 0x14);
        DemodWrite(1, 0x01, 0x10);
    }

    private void WriteFir(short[] fir)
    {
        Span<byte> b = stackalloc byte[20];
        for (var i = 0; i < 8; i++) b[i] = (byte)fir[i];
        for (var i = 0; i < 8; i += 2)
        {
            int v0 = fir[8 + i] & 0xFFF, v1 = fir[9 + i] & 0xFFF;
            var o = 8 + i * 3 / 2;
            b[o] = (byte)(v0 >> 4);
            b[o + 1] = (byte)(((v0 & 0x0F) << 4) | (v1 >> 8));
            b[o + 2] = (byte)v1;
        }
        for (var i = 0; i < 20; i++) DemodWrite(1, (byte)(0x1C + i), b[i]);
    }

    // ---------------------------------------------------------------- sample rate

    public static bool IsValidSampleRate(uint rate) =>
        rate is > 225_000 and <= 300_000 or > 900_000 and <= 3_200_000;

    /// <summary>Sets the resampler; returns the exact rate that is achieved.</summary>
    public double SetSampleRate(uint rate)
    {
        if (!IsValidSampleRate(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "Valid: 225001–300000 or 900001–3200000 S/s");

        // Resampling ratio, fixed point with 22 fractional bits; the two lowest bits must be zero.
        var ratio = (uint)((ulong)Xtal * (1UL << 22) / rate) & 0x0FFFFFFC;
        DemodWrite(1, 0x9F, ratio >> 16, 2);
        DemodWrite(1, 0xA1, ratio & 0xFFFF, 2);
        ApplySampleFreqCorrection();
        SoftReset();
        SampleRate = rate;
        return Xtal * (double)(1UL << 22) / ratio;
    }

    public void SetFreqCorrection(int ppm)
    {
        PpmCorrection = ppm;
        ApplySampleFreqCorrection();
    }

    private void ApplySampleFreqCorrection()
    {
        // 14-bit signed correction of the sampling frequency, in units of 2^-24 (≈ 0.06 ppm)
        var offs = (short)(-PpmCorrection * (1 << 24) / 1_000_000);
        DemodWrite(1, 0x3F, (uint)(offs & 0xFF));
        DemodWrite(1, 0x3E, (uint)((offs >> 8) & 0x3F));
    }

    // ---------------------------------------------------------------- misc demod features

    public void SetTestMode(bool on) => DemodWrite(0, 0x19, on ? 0x03u : 0x05u);

    public void SetAgc(bool on) => DemodWrite(0, 0x19, on ? 0x25u : 0x05u);

    /// <summary>
    /// 0 = off (quadrature via tuner), 1 = I-ADC, 2 = Q-ADC direct sampling.
    /// The tuner driver must restore its own IF/zero-IF settings after switching back to 0.
    /// </summary>
    public void SetDirectSampling(int mode)
    {
        if (mode == 0)
        {
            DemodWrite(0, 0x08, 0xCD);    // both ADCs
            DemodWrite(0, 0x06, 0x80);
        }
        else
        {
            DemodWrite(1, 0xB1, 0x1A);    // zero-IF off, keep DC/IQ settings
            DemodWrite(1, 0x15, 0x00);    // no spectrum inversion
            DemodWrite(0, 0x08, 0x4D);    // single ADC input
            DemodWrite(0, 0x06, mode == 2 ? 0x90u : 0x80u);
        }
    }

    /// <summary>Sets the IF frequency of the internal DDC (for tuners with low IF such as R820T).</summary>
    public void SetIfFrequency(double ifHz)
    {
        var v = -(int)(ifHz * (1 << 22) / CorrectedXtal) & 0x3FFFFF;
        DemodWrite(1, 0x19, (uint)(v >> 16) & 0x3F);
        DemodWrite(1, 0x1A, (uint)(v >> 8) & 0xFF);
        DemodWrite(1, 0x1B, (uint)v & 0xFF);
    }

    public void SetSpectrumInversion(bool on) => DemodWrite(1, 0x15, on ? 0x01u : 0x00u);

    /// <summary>Starts streaming raw u8 I/Q. 256 KiB × 15 buffers ≈ 0.8 s of headroom at 2.4 MS/s.</summary>
    public IBulkStream StartStream(IBulkSink sink, int bufferSize = 256 * 1024, int bufferCount = 15) =>
        _usb.StartBulkIn(BulkEndpoint, bufferSize, bufferCount, sink);

    public void ResetBuffer()
    {
        WriteReg(Block.Usb, UsbEpaCtl, 0x1002, 2);
        WriteReg(Block.Usb, UsbEpaCtl, 0x0000, 2);
    }

    // ---------------------------------------------------------------- tuner detection

    public TunerType ProbeTuner()
    {
        SetI2cRepeater(true);
        try
        {
            if (TryReadId(0xC8, 0x02) == 0x40) return TunerType.E4000;
            if (TryReadId(0xC6, 0x00) == 0xA3) return TunerType.Fc0013;
            if (TryReadId(0x34, 0x00) == 0x69) return TunerType.R820T;
            if (TryReadId(0x74, 0x00) == 0x69) return TunerType.R828D;
            if (TryReadId(0xC6, 0x00) == 0xA1) return TunerType.Fc0012;
            if ((TryReadId(0xAC, 0x01) & 0x7F) == 0x56) return TunerType.Fc2580;
            return TunerType.Unknown;
        }
        finally
        {
            SetI2cRepeater(false);
        }
    }

    private int TryReadId(byte addr, byte reg)
    {
        try { return I2cReadReg(addr, reg); }
        catch (UsbException e) when (!e.DeviceGone) { return -1; }
    }

    public void Dispose() => _usb.Dispose();
}
