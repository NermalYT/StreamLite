// Studio voice's Hilbert pair and single-sideband shift: the two outputs 90 degrees apart across the band, and a tone
// shifted up lands above (not below). Usage: SsbProbe
using System;
using StreamLite.Audio;

class SsbProbe
{
    static double Goertzel(double[] x, double f, int from)
    {
        double w = 2 * Math.PI * f / 48000, re = 0, im = 0;
        for (int i = from; i < x.Length; i++) { re += x[i] * Math.Cos(w * i); im -= x[i] * Math.Sin(w * i); }
        return 20 * Math.Log10(Math.Sqrt(re * re + im * im) / (x.Length - from) * 2 + 1e-12);
    }

    static void Main()
    {
        int fails = 0, n = 48000;
        foreach (double f in new[] { 100.0, 1000, 4000, 8000, 12000, 18000, 22000 })
        {
            var a = new double[16]; var b = new double[16]; double prev = 0;
            var I = new double[n]; var Q = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = Math.Cos(2 * Math.PI * f * i / 48000);
                I[i] = prev; prev = StudioVoice.Allpass(StudioVoice.HilA, a, x);
                Q[i] = StudioVoice.Allpass(StudioVoice.HilB, b, x);
            }
            // Phase of Q relative to I.
            double sIQ = 0, sII = 0, sQQ = 0, sIQh = 0;
            for (int i = 4800; i < n; i++) { sIQ += I[i] * Q[i]; sII += I[i] * I[i]; sQQ += Q[i] * Q[i]; }
            // Q against I advanced by a quarter period: the sign says lead or lag.
            double cosd = sIQ / Math.Sqrt(sII * sQQ);
            double deg = Math.Acos(Math.Max(-1, Math.Min(1, cosd))) * 180 / Math.PI;
            // Shift up by 3 kHz: y = I cos + Q sin.
            var y = new double[n]; double sh = 3000;
            for (int i = 0; i < n; i++) { double p = 2 * Math.PI * sh * i / 48000; y[i] = I[i] * Math.Cos(p) + Q[i] * Math.Sin(p); }
            double up = f + sh < 24000 ? Goertzel(y, f + sh, 4800) : double.NaN, down = Goertzel(y, Math.Abs(f - sh), 4800);
            Console.WriteLine(f.ToString("0").PadLeft(6) + " Hz: phase " + deg.ToString("0.00") + " deg, gains " + (10 * Math.Log10(sII / sQQ)).ToString("0.00") + " dB apart, shifted: up " + up.ToString("0.0") + " dB, image " + down.ToString("0.0") + " dB");
            if (f >= 100 && f + sh < 24000 && !(up - down > 30)) fails++;
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
