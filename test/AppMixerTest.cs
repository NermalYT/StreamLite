using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;

// Direct to headphones' own mixer, offline: apps arrive from the side output in Windows' 10 ms per-app packets
// (uneven, on the side output's clock, 300 ppm off) and are read in the headphones' 3 ms exclusive steps. Checks: an
// app comes out as it is (level, clean through the drift correction, no gaps), a pause keeps its place, two apps mix,
// a family of processes is captured once (not doubled), and nothing is allocated while running.
class AppMixerTest
{
    const int Fs = 48000, Period = 144, Secs = 20;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static double Db(double x) { return 10 * Math.Log10(x + 1e-30); }

    /// <summary>Plays the apps' sounds (stereo, interleaved) through a mixer; returns the left channel out.</summary>
    static float[] Run(float[][] apps, AppMixer.Tap[] taps, out long alloc, out int outN)
    {
        int total = Secs * Fs;
        var mix = new AppMixer();
        double now = 0;
        foreach (var t in taps) { t.Ring.Now = () => now; mix.Add(t); }
        double sideMs = 10.0 / 1.0003;             // the side output's clock runs 300 ppm fast
        var next = new double[apps.Length]; var fed = new long[apps.Length];
        for (int k = 0; k < apps.Length; k++) next[k] = 3.7 * k;
        var outp = new float[Period * 2]; var pkt = new float[480 * 2];
        var got = new float[total]; int n = 0;
        long a0 = 0, a1 = 0;
        int steps = (int)((Secs - 1) * 1000 / 3.0);
        for (int s = 0; s < steps; s++)
        {
            now = s * 3.0;
            for (int k = 0; k < apps.Length; k++)
                // Per-app packets due by now, every 5th one 4 ms late (Windows hands them out unevenly).
                while (next[k] + (fed[k] / 480 % 5 == 2 ? 4 : 0) <= now && fed[k] + 480 <= total)
                {
                    Array.Copy(apps[k], fed[k] * 2, pkt, 0, 960); mix.Feed(taps[k], pkt, 480, false);
                    fed[k] += 480; next[k] += sideMs;
                }
            if (s == 2000) a0 = GC.GetAllocatedBytesForCurrentThread();
            Array.Clear(outp, 0, outp.Length);
            mix.AddTo(outp, Period);
            if (s == 5000) a1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Period; i++) got[n++] = outp[2 * i];
        }
        alloc = a1 - a0; outN = n;
        return got;
    }

    static void Main()
    {
        int total = Secs * Fs;
        var rnd = new Random(3);
        // Voice chat: bursts of speech-like sound over a music bed; quiet from 8 to 10 s (silent packets keep coming).
        var voice = new float[total * 2]; var tone = new float[total * 2];
        double lp = 0;
        for (int i = 0; i < total; i++)
        {
            lp = 0.95 * lp + 0.05 * (rnd.NextDouble() * 2 - 1);
            bool talk = (i / 9600) % 2 == 0 && !(i >= 8 * Fs && i < 10 * Fs);
            double m = 0.1 * Math.Sin(2 * Math.PI * 440 * i / Fs) + 0.05 * Math.Sin(2 * Math.PI * 660 * i / Fs);
            voice[2 * i] = voice[2 * i + 1] = (float)((talk ? lp * 1.5 : 0) + (i >= 8 * Fs && i < 10 * Fs ? 0 : m));
            tone[2 * i] = tone[2 * i + 1] = (float)(0.08 * Math.Sin(2 * Math.PI * 1000 * i / Fs));
        }
        long alloc; int gotN;
        var a = new AppMixer.Tap(1, "discord");
        var got = Run(new[] { voice }, new[] { a }, out alloc, out gotN);
        long alloc1 = alloc;
        var want = new float[total];
        for (int i = 0; i < total; i++) want[i] = voice[2 * i];
        int bestLag = 0; double bestC = double.MinValue;
        for (int lag = 0; lag < 4800; lag++)
        {
            double c = 0; for (int i = 2 * Fs; i < 2 * Fs + 4800; i++) { double d = got[i] - want[i - lag]; c -= d * d; }
            if (c > bestC) { bestC = c; bestLag = lag; }
        }
        // Drift correction slides the delay slowly (that's its job): the best (fractional) delay per 20 ms window (it nudges its speed by up to 0.3%).
        double eo = 0, ew = 0, err = 0, lagNow = bestLag;
        Func<double, float> W = x => { int i0 = (int)Math.Floor(x); float f = (float)(x - i0); return want[i0] + (want[i0 + 1] - want[i0]) * f; };
        for (int w0 = 2 * Fs; w0 + 960 <= 18 * Fs && w0 + 960 <= gotN; w0 += 960)
        {
            double bestE = double.MaxValue, bl = lagNow;
            for (double lag = lagNow - 8; lag <= lagNow + 8; lag += 0.05)
            {
                double e = 0; for (int i = w0; i < w0 + 960; i += 2) { double d = got[i] - W(i - lag); e += d * d; }
                if (e < bestE) { bestE = e; bl = lag; }
            }
            lagNow = bl;
            for (int i = w0; i < w0 + 960; i++) { double w = W(i - lagNow); eo += got[i] * (double)got[i]; ew += w * w; double d = got[i] - w; err += d * d; }
        }
        Console.WriteLine("  voice chat through the mixer: " + (bestLag * 1000.0 / Fs).ToString("0.0") + " ms buffered, level " + (Db(eo) - Db(ew)).ToString("+0.00;-0.00") + " dB, gaps " + a.Ring.Underruns);
        Check(Math.Abs(Db(eo) - Db(ew)) < 0.2, "an app comes out at its own level");
        // Clean: a pure 1 kHz tone through the mixer gains no high-frequency energy (clicks are broadband). The
        // triple difference turns a 1 kHz tone down by 53 dB and leaves clicks.
        var tg = Run(new[] { tone }, new[] { new AppMixer.Tap(3, "tone") }, out alloc, out gotN);
        Func<float[], int, int, int, double> hf = (x, stride, from, to) => { double e = 0, s = 0; for (int i = from; i < to; i++) { double d = x[i * stride] - 3 * x[(i - 1) * stride] + 3 * x[(i - 2) * stride] - x[(i - 3) * stride]; e += d * d; s += x[i * stride] * (double)x[i * stride]; } return Db(e) - Db(s); };
        double hfIn = hf(tone, 2, 2 * Fs, 18 * Fs), hfOut = hf(tg, 1, 2 * Fs, 18 * Fs);
        Console.WriteLine("  1 kHz tone: high frequencies " + hfIn.ToString("0") + " dB in, " + hfOut.ToString("0") + " dB out");
        Check(hfOut < hfIn + 3, "clean - the side output's clock drift is corrected without clicks");
        Check(a.Ring.Underruns <= 1, "no gaps after the start, uneven packets and a 2 s pause included");
        Check(bestLag * 1000.0 / Fs < 25, "small buffer (" + (bestLag * 1000.0 / Fs).ToString("0.0") + " ms on top of Windows' per-app delay)");
        Check(alloc1 == 0 && alloc == 0, "no allocations while running (" + alloc1 + " / " + alloc + " bytes)");
        // Two apps at once: both in the mix.
        var b1 = new AppMixer.Tap(1, "discord"); var b2 = new AppMixer.Tap(2, "spotify");
        var both = Run(new[] { voice, tone }, new[] { b1, b2 }, out alloc, out gotN);
        double e1 = 0, e2 = 0, eb = 0;
        for (int i = 11 * Fs; i < 18 * Fs; i++) { e1 += got[i] * (double)got[i]; eb += both[i] * (double)both[i]; e2 += tone[2 * i] * (double)tone[2 * i]; }
        Console.WriteLine("  two apps: " + Db(eb / (7.0 * Fs)).ToString("0.0") + " dB (alone " + Db(e1 / (7.0 * Fs)).ToString("0.0") + " + " + Db(e2 / (7.0 * Fs)).ToString("0.0") + " dB)");
        Check(Math.Abs(Db(eb) - Db(e1 + e2)) < 0.3 && b1.Ring.Underruns + b2.Ring.Underruns <= 2, "two apps mix (levels add, no gaps)");
        // Process families: Discord (100) with its voice process (101) and a helper of that (102); Chrome (200) with
        // its audio process (201); Spotify (300) on its own.
        var parent = new Dictionary<int, int> { { 101, 100 }, { 102, 101 }, { 201, 200 }, { 100, 4 }, { 200, 4 }, { 300, 4 } };
        var roots = AppMixer.Roots(new[] { 101, 102, 201, 300 }, parent);
        Check(roots.SetEquals(new[] { 101, 201, 300 }), "each family of processes is captured once (" + string.Join(", ", roots.OrderBy(x => x)) + ")");
        Check(AppMixer.Roots(new[] { 100, 101 }, parent).SetEquals(new[] { 100 }), "an app and its child process: only the app (its capture includes the child)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
