using System;
using System.Linq;
using StreamLite.Audio;

// Front vs behind in every head: the head responses StreamLite plays for Personalize 3D's front and behind footsteps -
// level per band (both ears), and left/right at the sides. Real heads shadow sound from behind above ~4 kHz (the
// pinna), so behind should be duller than front; the sides should be louder in their own ear.
// Usage: run.ps1 FrontBackProbe
class FrontBackProbe
{
    static double Band(float[] h, double lo, double hi)
    {
        const int N = 4096; var fft = new Fft(N); var re = new float[N]; var im = new float[N];
        Array.Copy(h, re, Math.Min(N, h.Length)); fft.Transform(re, im, false);
        double e = 0;
        for (int k = 1; k < N / 2; k++) { double f = k * 48000.0 / N; if (f >= lo && f < hi) e += re[k] * re[k] + im[k] * im[k]; }
        return 10 * Math.Log10(e + 1e-20);
    }

    static void Main()
    {
        int duller = 0, n = 0, sides = 0;
        foreach (var head in HrtfSet.Heads())
        {
            var set = HrtfSet.Get(head, null);
            Func<double, double, double, double> both = (az, lo, hi) => { float[] l, r; set.BuildPrecise(az, 0, 0, out l, out r); return 10 * Math.Log10(Math.Pow(10, Band(l, lo, hi) / 10) + Math.Pow(10, Band(r, lo, hi) / 10)); };
            double f1 = both(0, 1000, 4000) - both(180, 1000, 4000), f2 = both(0, 4000, 8000) - both(180, 4000, 8000), f3 = both(0, 8000, 16000) - both(180, 8000, 16000);
            float[] l90, r90; set.BuildPrecise(90, 0, 0, out l90, out r90);
            bool leftOk = Band(l90, 500, 8000) > Band(r90, 500, 8000) + 6;
            Console.WriteLine(string.Format("{0,-22} front-behind dB: 1-4k {1,5:0.0}  4-8k {2,5:0.0}  8-16k {3,5:0.0}   az 90 louder in left ear: {4}", HrtfSet.DisplayName(head), f1, f2, f3, leftOk));
            n++; if (f2 + f3 > 0) duller++; if (leftOk) sides++;
        }
        Console.WriteLine("behind duller than front (4-16 kHz) in " + duller + " of " + n + " heads; sides right in " + sides + " of " + n);
    }
}
