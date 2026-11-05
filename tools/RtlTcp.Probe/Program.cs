// Hardware probe for development.
//   RtlTcp.Probe rate [sampleRate] [seconds]            → measures bulk throughput
//   RtlTcp.Probe fm <freqHz> [seconds] [gain|auto] [out.wav] → records broadcast FM into a WAV file
using System.Diagnostics;
using System.Numerics;
using RtlTcp.Core.Rtl2832;
using RtlTcp.Core.Usb;
using RtlTcp.Usb.LibUsb;

var mode = args.Length > 0 ? args[0] : "rate";

using var backend = new LibUsbBackend();
var devices = backend.Enumerate(KnownDevices.IsSupported);
if (devices.Count == 0)
{
    Console.WriteLine("No RTL2832U device found.");
    return 1;
}
foreach (var d in devices)
    Console.WriteLine($"{d.VendorId:x4}:{d.ProductId:x4} @ {d.Location}  {KnownDevices.Name(d.VendorId, d.ProductId)}  " +
                      $"[{d.Manufacturer} / {d.Product} / SN {d.Serial ?? "?"}]");

Rtl2832.I2cRecoveryLog = Console.WriteLine;
var sw = Stopwatch.StartNew();
using var sdr = RtlSdrDevice.Open(backend.Open(devices[0]));
Console.WriteLine($"Tuner: {sdr.TunerName}, init {sw.ElapsedMilliseconds} ms, {sdr.Gains.Count} gains: " +
                  string.Join(" ", sdr.Gains.Select(g => (g / 10.0).ToString("0.0"))));

return mode switch
{
    "rate" => Rate(sdr, args),
    "fm" => Fm(sdr, args),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: rate [sampleRate] [seconds] | fm <freqHz> [seconds] [gain|auto] [out.wav]");
    return 2;
}

static int Rate(RtlSdrDevice sdr, string[] args)
{
    var rate = args.Length > 1 ? uint.Parse(args[1]) : 2_400_000u;
    var seconds = args.Length > 2 ? int.Parse(args[2]) : 5;
    sdr.SetCenterFrequency(100_000_000);
    var actual = sdr.SetSampleRate(rate);
    var sink = new CountingSink();
    var sw = Stopwatch.StartNew();
    using (var bulk = sdr.StartStream(sink))
    {
        for (var i = 1; i <= seconds && bulk.IsRunning; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"{i,2}s  {Interlocked.Read(ref sink.Bytes) / sw.Elapsed.TotalSeconds / 2 / 1e6:F3} MS/s " +
                              $"(expected {actual / 1e6:F3})");
        }
    }
    sdr.PowerDown();
    if (sink.Error is not null) Console.WriteLine($"Stream error: {sink.Error.Message}");
    return 0;
}

static int Fm(RtlSdrDevice sdr, string[] args)
{
    if (args.Length < 2) return Usage();
    var station = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    var seconds = args.Length > 2 ? int.Parse(args[2]) : 5;
    var gainArg = args.Length > 3 ? args[3] : "auto";
    var outFile = args.Length > 4 ? args[4] : "fm.wav";

    const uint fs = 1_200_000;
    const double offset = 300_000;       // tune beside the station to keep it away from DC
    sdr.SetSampleRate(fs);
    sdr.SetCenterFrequency((ulong)(station - offset));
    if (gainArg == "auto") sdr.SetAutoGain(true);
    else Console.WriteLine($"Manual gain: {sdr.SetGain(int.Parse(gainArg)) / 10.0:0.0} dB");
    Console.WriteLine($"PLL locked: {sdr.TunerLocked}, center {sdr.CenterFrequency} Hz");

    var sink = new CaptureSink((int)(fs * 2 * seconds));
    using (sdr.StartStream(sink))
        while (!sink.Full && sink.Error is null) Thread.Sleep(50);
    sdr.PowerDown();
    if (sink.Error is not null) { Console.WriteLine($"Stream error: {sink.Error.Message}"); return 1; }

    var iq = sink.Samples();
    Console.WriteLine($"Captured {iq.Length} samples, clipped: {sink.ClipRatio:P2}");
    Console.WriteLine("Spectrum (power by offset from station):");
    foreach (var probe in new[] { -400_000, -200_000, -100_000, 0, 100_000, 200_000 })
        Console.WriteLine($"  {probe / 1000,5} kHz: {Dsp.Power(iq, fs, offset + probe):F1} dB");

    var audio = Dsp.DemodFm(iq, fs, offset);
    Wav.Write(outFile, audio, 48_000);
    Console.WriteLine($"Wrote {Path.GetFullPath(outFile)} ({audio.Length / 48_000.0:F1} s), " +
                      $"audio RMS {Math.Sqrt(audio.Average(a => a * a)):F3}");
    return 0;
}

sealed class CountingSink : IBulkSink
{
    public long Bytes;
    public UsbException? Error;
    public void OnData(ReadOnlySpan<byte> data) => Interlocked.Add(ref Bytes, data.Length);
    public void OnError(UsbException error) => Error = error;
}

sealed class CaptureSink(int bytes) : IBulkSink
{
    private readonly byte[] _buf = new byte[bytes];
    private int _pos;
    private long _clipped;
    public volatile UsbException? Error;
    public bool Full => Volatile.Read(ref _pos) >= _buf.Length;
    public double ClipRatio => (double)_clipped / _buf.Length;

    public void OnData(ReadOnlySpan<byte> data)
    {
        var n = Math.Min(data.Length, _buf.Length - _pos);
        data[..n].CopyTo(_buf.AsSpan(_pos));
        foreach (var b in data[..n]) if (b is 0 or 255) _clipped++;
        Volatile.Write(ref _pos, _pos + n);
    }

    public void OnError(UsbException error) => Error = error;

    public Complex[] Samples()
    {
        var s = new Complex[_buf.Length / 2];
        for (var i = 0; i < s.Length; i++)
            s[i] = new Complex((_buf[2 * i] - 127.5) / 127.5, (_buf[2 * i + 1] - 127.5) / 127.5);
        return s;
    }
}

static class Dsp
{
    /// <summary>Windowed-sinc low-pass FIR.</summary>
    public static double[] LowPass(int taps, double cutoff, double fs)
    {
        var h = new double[taps];
        var m = (taps - 1) / 2.0;
        for (var i = 0; i < taps; i++)
        {
            var x = i - m;
            var sinc = x == 0 ? 2 * cutoff / fs : Math.Sin(2 * Math.PI * cutoff / fs * x) / (Math.PI * x);
            h[i] = sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1)));
        }
        var sum = h.Sum();
        return h.Select(v => v / sum).ToArray();
    }

    private static Complex[] Shift(Complex[] x, double fs, double freq)
    {
        var y = new Complex[x.Length];
        var w = -2 * Math.PI * freq / fs;
        for (var n = 0; n < x.Length; n++) y[n] = x[n] * Complex.FromPolarCoordinates(1, w * n);
        return y;
    }

    private static Complex[] FilterDecimate(Complex[] x, double[] h, int factor)
    {
        var y = new Complex[(x.Length - h.Length) / factor];
        for (var k = 0; k < y.Length; k++)
        {
            Complex acc = 0;
            var o = k * factor;
            for (var j = 0; j < h.Length; j++) acc += x[o + j] * h[j];
            y[k] = acc;
        }
        return y;
    }

    /// <summary>Average power (dB) in a 50 kHz channel around <paramref name="freq"/>.</summary>
    public static double Power(Complex[] x, double fs, double freq)
    {
        var part = x.AsSpan(0, Math.Min(x.Length, 240_000)).ToArray();
        var y = FilterDecimate(Shift(part, fs, freq), LowPass(101, 25_000, fs), 10);
        return 10 * Math.Log10(y.Average(c => c.Magnitude * c.Magnitude) + 1e-20);
    }

    public static float[] DemodFm(Complex[] x, double fs, double offset)
    {
        // channel: ±100 kHz, 1.2 MS/s → 240 kS/s
        var ch = FilterDecimate(Shift(x, fs, offset), LowPass(63, 100_000, fs), 5);
        var fs2 = fs / 5;

        // polar discriminator
        var demod = new double[ch.Length - 1];
        for (var n = 1; n < ch.Length; n++)
            demod[n - 1] = (ch[n] * Complex.Conjugate(ch[n - 1])).Phase;

        // de-emphasis 50 µs (Europe)
        var a = Math.Exp(-1 / (fs2 * 50e-6));
        double state = 0;
        for (var n = 0; n < demod.Length; n++) demod[n] = state = (1 - a) * demod[n] + a * state;

        // audio low-pass 15 kHz, 240 kS/s → 48 kS/s
        var h = LowPass(101, 15_000, fs2);
        var audio = new float[(demod.Length - h.Length) / 5];
        for (var k = 0; k < audio.Length; k++)
        {
            double acc = 0;
            for (var j = 0; j < h.Length; j++) acc += demod[k * 5 + j] * h[j];
            audio[k] = (float)acc;
        }
        var peak = audio.Max(Math.Abs);
        if (peak > 0) for (var i = 0; i < audio.Length; i++) audio[i] = audio[i] / peak * 0.8f;
        return audio;
    }
}

static class Wav
{
    public static void Write(string path, float[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
    }
}
