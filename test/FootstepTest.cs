using System;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// The footsteps 3D audio and Personalize 3D play: how bright and sharp they are (what makes a step easy to place) -
// share of energy above 4 kHz and 10 kHz, spectral centroid, how fast the attack is, crest factor, length - and that
// every step differs. Optional: "wav <folder>" writes them for listening.
// Usage: run.ps1 FootstepTest [wav folder]
class FootstepTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        var bank = FootstepSynth.Bank(10, 23);
        const int N = 16384, Fs = 48000;
        var fft = new Fft(N);
        double hi4 = 0, hi10 = 0, cen = 0, lo = 0, crest = 0, attack = 0, len = 0;
        foreach (var s in bank)
        {
            var re = new float[N]; var im = new float[N];
            Array.Copy(s, re, Math.Min(N, s.Length));
            fft.Transform(re, im, false);
            double tot = 0, a4 = 0, a10 = 0, c = 0, l = 0;
            for (int k = 1; k < N / 2; k++)
            {
                double p = re[k] * re[k] + im[k] * im[k], f = k * (double)Fs / N;
                tot += p; c += p * f; if (f > 4000) a4 += p; if (f > 10000) a10 += p; if (f < 300) l += p;
            }
            hi4 += a4 / tot; hi10 += a10 / tot; cen += c / tot; lo += l / tot;
            double pk = s.Max(v => Math.Abs(v)), rms = Math.Sqrt(s.Take(Fs / 10).Average(v => (double)v * v));
            crest += 20 * Math.Log10(pk / rms);
            int at = Array.FindIndex(s, v => Math.Abs(v) >= 0.5 * pk);
            attack += at * 1000.0 / Fs;
            int last = Array.FindLastIndex(s, v => Math.Abs(v) > pk * 0.003);
            len += last * 1000.0 / Fs;
        }
        int n = bank.Length;
        Console.WriteLine("footsteps: energy above 4 kHz " + (100 * hi4 / n).ToString("0.0") + "%, above 10 kHz " + (100 * hi10 / n).ToString("0.0") + "%, below 300 Hz " + (100 * lo / n).ToString("0.0") +
            "%, centroid " + (cen / n).ToString("0") + " Hz, crest " + (crest / n).ToString("0.0") + " dB, half-peak after " + (attack / n).ToString("0.0") + " ms, length " + (len / n).ToString("0") + " ms");
        Check(hi4 / n > 0.15, "bright: over 15% of the energy above 4 kHz (where ears tell front, back, up and down)");
        Check(lo / n < 0.5, "not a boom: under half the energy below 300 Hz");
        Check(1 - lo / n - hi4 / n > 0.25, "a knock you can hear: over 25% between 300 Hz and 4 kHz");
        Check(hi10 / n > 0.02, "air: over 2% above 10 kHz");
        Check(attack / n < 3, "sharp: half the peak within 3 ms of the start");
        Check(bank.All(s => s.Max(v => Math.Abs(v)) < 0.95), "no clipping");
        double diff = 0; for (int i = 1; i < n; i++) diff += bank[i].Zip(bank[i - 1], (a, b) => Math.Abs(a - b)).Sum();
        Check(diff > 1, "every step differs");
        if (args.Length > 0)
        {
            Directory.CreateDirectory(args[0]);
            var all = new float[Fs * 6];
            for (int i = 0; i < 12; i++) { var s = bank[i % n]; Array.Copy(s, 0, all, i * Fs / 2, Math.Min(s.Length, all.Length - i * Fs / 2)); }
            WriteWav(Path.Combine(args[0], "footsteps.wav"), all, Fs);
            Console.WriteLine("Wrote " + Path.Combine(args[0], "footsteps.wav"));
        }
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static void WriteWav(string path, float[] x, int fs)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + x.Length * 2); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(fs); w.Write(fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
        }
    }
}
