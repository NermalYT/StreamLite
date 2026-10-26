// include: DnsmosRun.cs
using System;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// Studio voice's plosive catcher, block by block, on the recorded voice with pops added: the low and voice-band
// energies, the floor, and the gain - to tune the detector. Usage: run.ps1 PopProbe
class PopProbe
{
    static void Main()
    {
        const int Fs = 48000;
        string live = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\live"));
        var x = DnsmosRun.ReadWav(Directory.GetFiles(live, "mic-*-mono48.wav").OrderByDescending(File.GetLastWriteTime).First());
        double rms = Math.Sqrt(x.Average(v => (double)v * v)); for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * 0.08 / rms);
        var pops = new System.Collections.Generic.List<int>();
        for (int at = Fs; at + Fs / 10 < x.Length; at += 2 * Fs)
        {
            pops.Add(at);
            for (int i = 0; i < Fs * 6 / 100; i++) { double t = i / (double)Fs; x[at + i] += (float)(0.6 * Math.Sin(2 * Math.PI * 60 * t) * Math.Exp(-t / 0.02) * (1 - Math.Exp(-t / 0.002))); }
        }
        foreach (float st in new[] { 0f, 0.75f })
        {
            var sv = new StudioVoice { Strength = st };
            var y = (float[])x.Clone();
            for (int at = 0; at + 480 <= y.Length; at += 480) { var blk = new float[480]; Array.Copy(y, at, blk, 0, 480); sv.Process(blk, 480); Array.Copy(blk, 0, y, at, 480); }
            Func<float[], int, double> lowE = (sig, a0) => { var q1 = Biquad.LowPass(150, Fs); var q2 = Biquad.LowPass(150, Fs); double e = 0; for (int i = Math.Max(0, a0 - 4800); i < a0 + Fs / 15 && i < sig.Length; i++) { double v = q2.Process(q1.Process(sig[i])); if (i >= a0) e += v * v; } return e; };
            Console.WriteLine("strength " + st + ": pops " + sv.Pops + ", rumble down " + string.Join(" ", pops.Select(p => (10 * Math.Log10(lowE(x, p) / (lowE(y, p + StudioVoice.N) + 1e-20))).ToString("0"))) + " dB");
        }
        var lp1 = Biquad.LowPass(200, Fs); var lp2 = Biquad.LowPass(200, Fs); var bp1 = Biquad.HighPass(300, Fs); var bp2 = Biquad.LowPass(3000, Fs);
        for (int at = 0; at + 480 <= x.Length && at < 5 * Fs; at += 480)
        {
            double lf = 0, mf = 0;
            for (int i = at; i < at + 480; i++) { float l = lp2.Process(lp1.Process(x[i])); lf += l * (double)l; float m = bp2.Process(bp1.Process(x[i])); mf += m * (double)m; }
            bool pop = pops.Any(p => at + 480 > p && at < p + Fs / 15);
            Console.WriteLine((at / 48) + " ms  lf " + (10 * Math.Log10(lf / 480 + 1e-20)).ToString("0.0") + "  mf " + (10 * Math.Log10(mf / 480 + 1e-20)).ToString("0.0") + (pop ? "  POP" : ""));
        }
    }
}
