// What footstep tracking really sends to each ear, with nothing played: per footstep the time and level difference
// between the ears, for footsteps standing still (only the recordings change) and for a walker.
// Usage: run.ps1 TrackAudioProbe
using System;
using System.Linq;
using StreamLite.Audio;

class TrackAudioProbe
{
    static double Rms(float[] x) { double e = 0; foreach (var v in x) e += v * v; return Math.Sqrt(e / Math.Max(1, x.Length)); }
    static double Db(double a) { return 20 * Math.Log10(a + 1e-9); }

    /// <summary>Time difference (ms, + = right ear first) from the cross-correlation below 1.5 kHz's worth of smoothing.</summary>
    static double Itd(float[] l, float[] r)
    {
        int max = 48, n = Math.Min(l.Length, r.Length); double best = double.MinValue; int at = 0;
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < n - Math.Max(0, lag); i += 1) s += l[i + lag] * r[i];
            if (s > best) { best = s; at = lag; }
        }
        return at / 48.0;
    }

    static double Centroid(float[] x)
    {
        // Zero crossings per second as a cheap brightness measure.
        int z = 0; for (int i = 1; i < x.Length; i++) if ((x[i] >= 0) != (x[i - 1] >= 0)) z++;
        return z * 48000.0 / x.Length / 2;
    }

    static void Main()
    {
        var head = new Sphere3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75, HeadSize = 100 };
        var rend = new StepRenderer();
        float[] l, r;
        rend.RenderAt(head, Sphere3D.Forward(0, 0) * Sphere3D.Radius, 0, 0, 0, out l, out r);
        Console.WriteLine("CS2's own recordings in use: " + StepRenderer.GameSteps);

        foreach (var az in new[] { 0.0, 60.0, 180.0 })
        {
            Console.WriteLine("Standing still at " + az + " degrees, view ahead, 34 footsteps:");
            var itd = new double[34]; var ild = new double[34]; var lvl = new double[34]; var bright = new double[34];
            for (int s = 0; s < 34; s++)
            {
                rend.RenderAt(head, Sphere3D.Forward(az, 0) * Sphere3D.Radius, 0, 0, s, out l, out r);
                itd[s] = Itd(l, r); ild[s] = Db(Rms(r)) - Db(Rms(l)); lvl[s] = Db(Math.Sqrt((Rms(l) * Rms(l) + Rms(r) * Rms(r)) / 2)); bright[s] = Centroid(l);
            }
            Console.WriteLine("  ear time difference " + itd.Min().ToString("0.00") + " to " + itd.Max().ToString("0.00") + " ms; ear level difference " + ild.Min().ToString("+0.0;-0.0") + " to " + ild.Max().ToString("+0.0;-0.0") + " dB; loudness " + lvl.Min().ToString("0.0") + " to " + lvl.Max().ToString("0.0") + " dB; brightness " + bright.Min().ToString("0") + " to " + bright.Max().ToString("0") + " Hz");
        }

        // One recording, moved in small steps: what the head responses themselves do. The time difference is taken below
        // about 1.2 kHz (a broadband peak can flip between cycles), as hearing uses it.
        Func<float[], float[]> smooth = x => { var y = new float[x.Length]; double a = 0, b = 0; const double c = 0.15; for (int i = 0; i < x.Length; i++) { a += c * (x[i] - a); b += c * (a - b); y[i] = (float)b; } return y; };
        foreach (var el0 in new[] { 0.0, 40.0 })
        {
            Console.WriteLine("Sweep at height " + el0 + ", same recording: az, broadband time, low-frequency time, level dB");
            for (double az = 20; az <= 80; az += 5)
            {
                rend.RenderAt(head, Sphere3D.Forward(az, el0) * Sphere3D.Radius, 0, 0, 0, out l, out r);
                Console.WriteLine("  " + az.ToString("00") + "  " + Itd(l, r).ToString("+0.00;-0.00") + "  " + Itd(smooth(l), smooth(r)).ToString("+0.00;-0.00") + "  " + (Db(Rms(r)) - Db(Rms(l))).ToString("+00.0;-00.0"));
            }
        }

        foreach (bool level in new[] { true, false })
        {
            Console.WriteLine((level ? "Ear-level" : "Everywhere") + " walker, view ahead (first 30 footsteps): az, el, ear time ms, ear level dB");
            var w = new Sphere3D.Walker(new Random(7), level);
            for (int s = 0; s < 30; s++)
            {
                var p = w.Step();
                rend.RenderAt(head, p, 0, 0, s, out l, out r);
                Console.WriteLine("  " + s.ToString("00") + "  az " + w.Az.ToString("000") + "  el " + w.El.ToString("+00;-00") + "  time " + Itd(l, r).ToString("+0.00;-0.00") + "  level " + (Db(Rms(r)) - Db(Rms(l))).ToString("+00.0;-00.0"));
            }
        }
    }
}
