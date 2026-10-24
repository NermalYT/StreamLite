using System;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// Tone coloration of the 3D virtual speakers: pink noise in one 7.1 channel at a time, both ears' combined power per
// third-octave band relative to the input. The average over all speakers is the "diffuse-field" coloration that a
// virtualizer should keep flat, or every game sounds tilted.
class HrtfColorTest
{
    const int Fs = 48000, B = SpatialProcessor.B;
    static readonly double[] Third = { 50, 100, 200, 400, 800, 1600, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; } }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    static double[] Bands(float[] x)
    {
        const int N = 8192; var pw = new double[N / 2 + 1]; int fr = 0;
        var re = new double[N]; var im = new double[N];
        for (int s = 0; s + N <= x.Length; s += N / 2)
        {
            for (int i = 0; i < N; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
            Fft(re, im); for (int k = 0; k <= N / 2; k++) pw[k] += re[k] * re[k] + im[k] * im[k]; fr++;
        }
        return Third.Select(f => { double lo = f / Math.Pow(2, 1 / 6.0), hi = f * Math.Pow(2, 1 / 6.0), s2 = 0; for (int k = (int)(lo * N / Fs) + 1; k <= (int)(hi * N / Fs); k++) s2 += pw[k]; return 10 * Math.Log10(s2 / fr + 1e-30); }).ToArray();
    }

    static void Main(string[] args)
    {
        string hrtf = args.Length > 0 ? args[0] : HrtfSet.Kemar;
        var rnd = new Random(4);
        int total = Fs * 6;
        var noise = new float[total]; for (int i = 0; i < total; i++) noise[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.1);
        var inB = Bands(noise.Skip(Fs).ToArray());
        int[] slotsUsed = { SpatialProcessor.FL, SpatialProcessor.FR, SpatialProcessor.FC, SpatialProcessor.SL, SpatialProcessor.SR, SpatialProcessor.BL, SpatialProcessor.BR };
        var sum = new double[Third.Length];
        Console.WriteLine("HRTF " + hrtf + ", room 0. dB per band (both ears' power vs input), white noise:");
        Console.WriteLine("            " + string.Join("", Third.Select(f => (f >= 1000 ? (f / 1000) + "k" : f.ToString()).PadLeft(6))));
        foreach (int slot in slotsUsed)
        {
            var c = new SpatialConfig { HrtfKind = hrtf, Room = 0, FootstepBoost = 0, LoudTamer = 0, CueSharpen = 0, Limiter = false };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { Enabled = false }; c.DirGainDb[d] = 0; }
            var p = new SpatialProcessor(); p.Configure(c);
            var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B]; var oL = new float[total]; var oR = new float[total];
            for (int pos = 0; pos + B <= total; pos += B)
            {
                foreach (var s in slots) Array.Clear(s, 0, B);
                Array.Copy(noise, pos, slots[slot], 0, B);
                p.Process(slots, SpatialProcessor.Source.Surround71, l, r);
                Array.Copy(l, 0, oL, pos, B); Array.Copy(r, 0, oR, pos, B);
            }
            var bl = Bands(oL.Skip(Fs).ToArray()); var br = Bands(oR.Skip(Fs).ToArray());
            var resp = Enumerable.Range(0, Third.Length).Select(b => 10 * Math.Log10((Math.Pow(10, bl[b] / 10) + Math.Pow(10, br[b] / 10)) / 2) - inB[b]).ToArray();
            for (int b = 0; b < Third.Length; b++) sum[b] += Math.Pow(10, resp[b] / 10);
            Console.WriteLine(SpatialProcessor.SlotNames[slot].PadRight(12) + string.Join("", resp.Select(v => v.ToString("+0.0;-0.0").PadLeft(6))));
        }
        var avg = sum.Select(s => 10 * Math.Log10(s / slotsUsed.Length)).ToArray();
        double mid = avg[4];   // 800 Hz
        Console.WriteLine("average     " + string.Join("", avg.Select(v => (v - mid).ToString("+0.0;-0.0").PadLeft(6))) + "   (re 800 Hz)");
    }
}
