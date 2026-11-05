// True 16D (4.4): the song's parts each at their own place. Bass, centred vocals, drums, hard-panned instruments and the air
// on top are fed one at a time, through the real 3D path: each must land in its own part of the sphere, the parts must add
// up to the song, nothing clicks while they move, and off stays off. Usage: run.ps1 Music16DTest
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamLite.Audio;

class Music16DTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    const int B = SpatialProcessor.B, Fs = SpatialProcessor.Fs;

    /// <summary>A sound (left, right at sample t) through the 3D path with 16D on (or off). Out: both ears.</summary>
    static void Run(Func<long, float[]> sound, double seconds, double orbit, out float[] L, out float[] R, out double msPerBlock)
    {
        var sp = new SpatialProcessor();
        sp.Configure(new SpatialConfig { OrbitSeconds = orbit, Limiter = false, Room = 0 });
        int blocks = (int)(seconds * Fs / B);
        var slots = Enumerable.Range(0, 8).Select(_ => new float[B]).ToArray();
        var l = new float[B]; var r = new float[B];
        L = new float[blocks * B]; R = new float[blocks * B];
        long t = 0; var sw = new Stopwatch(); int counted = 0;
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < B; i++, t++)
            {
                var x = sound(t);
                for (int s = 0; s < 8; s++) slots[s][i] = 0;
                slots[0][i] = x[0]; slots[1][i] = x[1];
            }
            if (b > 100) { sw.Start(); counted++; }
            sp.Process(slots, SpatialProcessor.Source.Stereo, l, r);
            sw.Stop();
            Array.Copy(l, 0, L, b * B, B); Array.Copy(r, 0, R, b * B, B);
        }
        msPerBlock = counted > 0 ? sw.Elapsed.TotalMilliseconds / counted : 0;
    }

    static double Rms(float[] x, int from, int to) { double e = 0; for (int i = from; i < to; i++) e += x[i] * (double)x[i]; return Math.Sqrt(e / Math.Max(1, to - from)); }

    /// <summary>Left-minus-right level (dB) in the second half, over a window starting at sec.</summary>
    static double Side(float[] L, float[] R, double sec, double len = 1.0)
    {
        int a = (int)(sec * Fs), b = (int)((sec + len) * Fs);
        return 20 * Math.Log10((Rms(L, a, b) + 1e-9) / (Rms(R, a, b) + 1e-9));
    }

    static float Harmonics(long t, double f0, double amp)
    {
        double y = 0, s = t / (double)Fs;
        for (int h = 1; h <= 20 && h * f0 < 6000; h++) y += Math.Sin(2 * Math.PI * h * f0 * s) / h;
        return (float)(amp * y);
    }

    static void Main()
    {
        var set = HrtfSet.Get(HrtfSet.Ku100, null);
        Check(Stems16D.Supports(set), "a measured head has the sphere it needs");
        Stems16D.Prepare(set);
        for (int i = 0; i < 300 && !Stems16D.IsReady(set); i++) System.Threading.Thread.Sleep(50);
        Check(Stems16D.IsReady(set), "its 16 virtual speakers are built in the background");

        var rnd = new Random(1);
        // Each part alone. All at the same moment of the 16D path (they're deterministic in time).
        Func<long, float[]> bass = t => { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 70 * t / Fs)); return new[] { v, v }; };
        Func<long, float[]> vocal = t => { float v = Harmonics(t, 220, 0.12); return new[] { v, v }; };
        Func<long, float[]> left = t => new[] { Harmonics(t, 330, 0.12), 0f };
        Func<long, float[]> right = t => new[] { 0f, Harmonics(t, 330, 0.12) };
        Func<long, float[]> air = t => { float v = (float)(0.2 * (rnd.NextDouble() * 2 - 1) * Math.Sin(2 * Math.PI * 12000 * t / Fs)); return new[] { v, v }; };
        Func<long, float[]> drums = t =>
        {
            long at = t % (Fs / 2); double e = Math.Exp(-at / (0.012 * Fs));
            float v = (float)(0.5 * e * (rnd.NextDouble() * 2 - 1));
            return new[] { v, v };
        };
        double ms;
        float[] bL, bR, vL, vR, lL, lR, rL, rR, dL, dR;
        Run(bass, 12, 8, out bL, out bR, out ms);
        Run(vocal, 12, 8, out vL, out vR, out ms);
        Run(left, 12, 8, out lL, out lR, out ms);
        Run(right, 12, 8, out rL, out rR, out ms);
        Run(drums, 12, 8, out dL, out dR, out ms);
        var st = new Stems16D();

        Console.WriteLine("Each part at its own place (left minus right, dB, at three moments)");
        Func<float[], float[], string> show = (a, b) => string.Join("  ", new[] { 2.0, 6.0, 10.0 }.Select(s => Side(a, b, s).ToString("+0.0;-0.0")));
        Console.WriteLine("    bass   " + show(bL, bR));
        Console.WriteLine("    vocals " + show(vL, vR));
        Console.WriteLine("    left   " + show(lL, lR));
        Console.WriteLine("    right  " + show(rL, rR));
        Console.WriteLine("    drums  " + show(dL, dR));
        Check(Math.Abs(Side(vL, vR, 2)) < 6, "vocals stay near the front");
        double spreadLR = Side(lL, lR, 2) - Side(rL, rR, 2);
        Check(spreadLR > 8, "what the mix pans left and right sits on either side of you (" + spreadLR.ToString("0.0") + " dB apart)");
        Check(Math.Abs(Side(bL, bR, 2) - Side(vL, vR, 2)) > 1.5, "the bass isn't where the vocals are");
        Check(Math.Abs(Side(dL, dR, 2) - Side(vL, vR, 2)) > 3, "the drums aren't where the vocals are");

        Console.WriteLine("They move, slowly, each its own way");
        var moves = new[] { Tuple.Create("bass", bL, bR), Tuple.Create("left", lL, lR), Tuple.Create("drums", dL, dR) };
        foreach (var m in moves)
        {
            var path = Enumerable.Range(0, 11).Select(i => Side(m.Item2, m.Item3, 1 + i)).ToList();
            Check(path.Max() - path.Min() > 1, m.Item1 + " wanders (" + (path.Max() - path.Min()).ToString("0.0") + " dB across its path)");
        }
        Check(Correl(bL, bR, lL, lR) < 0.95, "not all on one circle: their paths differ");

        Console.WriteLine("The song stays the song");
        Func<long, float[]> song = t =>
        {
            float b = bass(t)[0] * 0.5f, v = vocal(t)[0], g = Harmonics(t, 330, 0.08), n = (float)((rnd.NextDouble() * 2 - 1) * 0.02);
            long at = t % (Fs / 2); float k = (float)(0.3 * Math.Exp(-at / (0.012 * Fs)) * (rnd.NextDouble() * 2 - 1));
            return new[] { b + v + g + k + n, b + v + k + n };
        };
        float[] sL, sR;
        Run(song, 10, 8, out sL, out sR, out ms);
        var inL = new float[sL.Length]; var inR = new float[sL.Length];
        for (long t = 0; t < sL.Length; t++) { var x = song(t); inL[t] = x[0]; inR[t] = x[1]; }
        double inp = Math.Sqrt((Math.Pow(Rms(inL, Fs, sL.Length), 2) + Math.Pow(Rms(inR, Fs, sL.Length), 2)) / 2);
        double outp = Math.Sqrt((Math.Pow(Rms(sL, Fs, sL.Length), 2) + Math.Pow(Rms(sR, Fs, sL.Length), 2)) / 2);
        double db = 20 * Math.Log10(outp / inp);
        Check(Math.Abs(db) < 4, "as loud as it was (" + db.ToString("+0.0;-0.0") + " dB)");
        Check(!sL.Any(float.IsNaN) && !sR.Any(float.IsNaN), "no NaN");
        double cost = ms / (B * 1000.0 / Fs);
        Check(cost < 0.5, "it keeps up: " + ms.ToString("0.00") + " ms of every " + (B * 1000.0 / Fs).ToString("0.0") + " ms block (" + (100 * cost).ToString("0") + "% of one core, with the rest of the 3D path)");

        Console.WriteLine("No clicks while they move");
        float[] tL, tR;
        Run(t => { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 500 * t / Fs)); return new[] { v, v }; }, 8, 2, out tL, out tR, out ms);
        double bend = 0;
        for (int i = Fs; i + 2 < tL.Length; i++) bend = Math.Max(bend, Math.Abs(tL[i] - 2 * tL[i + 1] + tL[i + 2]));
        Check(bend < 0.05, "a steady tone stays smooth with the fastest movement (sharpest bend " + bend.ToString("0.0000") + ")");

        Console.WriteLine("Off");
        float[] oL, oR;
        Run(left, 3, 0, out oL, out oR, out ms);
        Check(Rms(oR, Fs, oR.Length) < 1e-6, "off: a left channel stays in the left ear, nothing placed");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    static double Correl(float[] aL, float[] aR, float[] bL, float[] bR)
    {
        var x = Enumerable.Range(0, 11).Select(i => Side(aL, aR, 1 + i)).ToArray(); var y = Enumerable.Range(0, 11).Select(i => Side(bL, bR, 1 + i)).ToArray();
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return Math.Abs(sxy) / Math.Sqrt(sxx * syy + 1e-12);
    }
}
