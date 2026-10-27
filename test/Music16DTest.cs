// "16D" music (3.5.6): a stereo song through 3D with OrbitSeconds circles the head - its loudness moves from ear to
// ear smoothly (no clicks), once per circle; off = it stays put. Usage: run.ps1 Music16DTest
using System;
using System.Linq;
using StreamLite.Audio;

class Music16DTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    // Left-minus-right level (dB) every 0.25 s over `seconds`, and the largest sample-to-sample jump.
    static double[] Run(double orbit, double seconds, out double maxJump, bool sine = false)
    {
        var sp = new SpatialProcessor();
        var cfg = new SpatialConfig { VirtualizeStereo = true, OrbitSeconds = orbit };
        sp.Configure(cfg);
        int B = SpatialProcessor.B, blocks = (int)(seconds * 48000 / B);
        var slots = Enumerable.Range(0, 8).Select(_ => new float[B]).ToArray();
        var L = new float[B]; var R = new float[B];
        var diffs = new System.Collections.Generic.List<double>();
        double eL = 0, eR = 0; int n = 0; long t = 0; float prevL = 0, prev2 = 0; maxJump = 0;
        var rnd = new Random(1);
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < B; i++, t++)
            {
                float x = sine ? (float)(0.3 * Math.Sin(2 * Math.PI * 200 * t / 48000.0)) : (float)(0.05 * Math.Sin(2 * Math.PI * 440 * t / 48000.0) + 0.2 * (rnd.NextDouble() - 0.5));   // a broadband "song"
                for (int s = 0; s < 8; s++) slots[s][i] = 0;
                slots[0][i] = x; slots[1][i] = x;   // the same song left and right (mono-ish mix)
            }
            sp.Process(slots, SpatialProcessor.Source.Stereo, L, R);
            if (b < 200) { prevL = L[B - 1]; prev2 = L[B - 2]; continue; }   // settle
            for (int i = 0; i < B; i++) { eL += L[i] * L[i]; eR += R[i] * R[i]; maxJump = Math.Max(maxJump, Math.Abs(L[i] - 2 * prevL + prev2)); prev2 = prevL; prevL = L[i]; }
            if (++n == 50) { diffs.Add(10 * Math.Log10((eL + 1e-12) / (eR + 1e-12))); eL = eR = 0; n = 0; }
        }
        return diffs.ToArray();
    }

    static void Main()
    {
        // Build the 0.25° head table first (it's built in the background in the app).
        ContinuousRenderer.Prepare(HrtfSet.Get(HrtfSet.Ku100, null));
        for (int i = 0; i < 100 && !ContinuousRenderer.IsReady(HrtfSet.Get(HrtfSet.Ku100, null)); i++) System.Threading.Thread.Sleep(100);
        double jOn, jOff;
        var on = Run(4, 10, out jOn);
        var off = Run(0, 10, out jOff);
        double cOn, cOff; Run(4, 6, out cOn, true); Run(0, 6, out cOff, true);
        double spanOn = on.Max() - on.Min(), spanOff = off.Max() - off.Min();
        Console.WriteLine("  L-R dB with 16D (4 s circle): " + string.Join(" ", on.Select(d => d.ToString("0"))));
        Check(spanOn > 10, "16D: the sound goes from one ear to the other (" + spanOn.ToString("0.0") + " dB swing)");
        Check(on.Max() > 5 && on.Min() < -5, "both ears get their turn");
        Check(spanOff < 2, "off: it stays put (" + spanOff.ToString("0.0") + " dB)");
        Check(cOn < cOff * 2 + 0.002, "no clicks while it moves (200 Hz tone: sharpest bend " + cOn.ToString("0.0000") + ", standing still " + cOff.ToString("0.0000") + ")");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
