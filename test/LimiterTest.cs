using System;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// The 3D output limiter on its own: loud low notes (dynamics off, +6 dB master so the limiter works hard).
// A limiter that recovers within one cycle of a bass note distorts it; this prints THD per tone.
class LimiterTest
{
    const int Fs = 48000, B = SpatialProcessor.B;

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

    static void Main()
    {
        Console.WriteLine(string.Format("{0,-8} {1,8} {2,12} {3,10}", "tone", "input", "limiter", "THD"));
        foreach (double f in new[] { 50.0, 100.0, 1000.0 })
            foreach (double lvl in new[] { -12.0, -6.0, 0.0 })
            {
                // -12 dB: limiter idle (output -6 dBFS) = the chain's own floor.
                var c = new SpatialConfig { FootstepBoost = 0, LoudTamer = 0, CueSharpen = 0, Room = 0, MasterDb = 6, Limiter = true };
                foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { Enabled = false }; c.DirGainDb[d] = 0; }
                var p = new SpatialProcessor(); p.Configure(c);
                var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
                var l = new float[B]; var r = new float[B];
                int total = Fs * 3; var outL = new float[total];
                double a = Math.Pow(10, lvl / 20);
                for (int pos = 0; pos + B <= total; pos += B)
                {
                    foreach (var s in slots) Array.Clear(s, 0, B);
                    for (int i = 0; i < B; i++) { float v = (float)(a * Math.Sin(2 * Math.PI * f * (pos + i) / Fs)); slots[SpatialProcessor.FL][i] = v; slots[SpatialProcessor.FR][i] = v; }
                    p.Process(slots, SpatialProcessor.Source.Stereo, l, r);
                    Array.Copy(l, 0, outL, pos, B);
                }
                const int N = 65536;
                var re = new double[N]; var im = new double[N];
                for (int i = 0; i < N; i++) re[i] = outL[total - N + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
                Fft(re, im);
                int k0 = (int)Math.Round(f * N / Fs);
                double fund = 0, rest = 0;
                for (int k = 2; k < N / 2; k++) { double pw = re[k] * re[k] + im[k] * im[k]; if (Math.Abs(k - k0) <= 3) fund += pw; else rest += pw; }
                double thd = 10 * Math.Log10(rest / fund);
                double harm = 0;
                for (int h = 2; h <= 10; h++) { int kh = k0 * h; for (int k = kh - 3; k <= kh + 3 && k < N / 2; k++) harm += re[k] * re[k] + im[k] * im[k]; }
                // Strongest non-harmonic component.
                int worst = 0; double wp = 0;
                for (int k = 2; k < N / 2; k++)
                {
                    if (Math.Abs(k - k0) <= 3) continue;
                    bool isH = false; for (int h = 2; h <= 10; h++) if (Math.Abs(k - k0 * h) <= 3) isH = true;
                    if (isH) continue;
                    double pw = re[k] * re[k] + im[k] * im[k]; if (pw > wp) { wp = pw; worst = k; }
                }
                Console.WriteLine(string.Format("{0,-8} {1,6:0} dB {2,9:0.0} dB {3,8:0.00}%  harmonics {4:0.000}%  worst other {5:0} Hz at {6:0} dB",
                    f + " Hz", lvl, p.LimiterDb, 100 * Math.Pow(10, thd / 20), 100 * Math.Sqrt(harm / fund), worst * (double)Fs / N, 10 * Math.Log10(wp / fund)));
            }
    }
}
