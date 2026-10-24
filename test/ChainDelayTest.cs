// How much delay the mic's effects chain adds with each noise engine, measured: a speech-like signal goes through the
// whole chain (offline, the voice test's copy) and the output is lined up with the input by cross-correlation.
// Prints each engine's delay and checks NoiseSuppressor.DelayMs reports it. Usage: run.ps1 ChainDelayTest
using System;
using System.IO;
using StreamLite;
using StreamLite.Audio;

class ChainDelayTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>Delay in samples that best lines y up with x (0..maxLag).</summary>
    static int Lag(float[] x, float[] y, int from, int len, int maxLag)
    {
        int best = 0; double bestC = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double c = 0, ex = 0, ey = 0;
            for (int i = from; i < from + len; i++) { c += x[i] * y[i + lag]; ex += x[i] * x[i]; ey += y[i + lag] * y[i + lag]; }
            c /= Math.Sqrt(ex * ey + 1e-20);
            if (c > bestC) { bestC = c; best = lag; }
        }
        return best;
    }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        const int Fs = 48000;
        int total = Fs * 6;
        var x = new float[total];
        var rnd = new Random(4);
        // Voiced bursts with harmonics and a little noise: something every engine keeps.
        for (int i = 0; i < total; i++)
        {
            double t = (double)i / Fs, f0 = 130 + 30 * Math.Sin(2 * Math.PI * 0.9 * t);
            double env = Math.Max(0, Math.Sin(2 * Math.PI * 2.1 * t));
            double v = 0; for (int k = 1; k < 10; k++) v += Math.Sin(2 * Math.PI * k * f0 * t + k) / k;
            x[i] = (float)(0.15 * env * v + 0.002 * (rnd.NextDouble() * 2 - 1));
        }
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai, NoiseEngines.DfnCpu })
        {
            var S = new AppSettings { NoiseRemoval = true, NsEngine = engine, Gate = false, Compressor = false, DeEsser = false, AutoLevel = false };
            S.MicEq.Enabled = false;
            var chain = new VoiceChain();
            chain.Configure(S);
            chain.Suppressor.Synchronous = true;
            var y = new float[total];
            var block = new float[144];
            for (int pos = 0; pos + 144 <= total; pos += 144)
            {
                Array.Copy(x, pos, block, 0, 144);
                chain.Process(block, 144, false);
                Array.Copy(block, 0, y, pos, 144);
            }
            int lag = Lag(x, y, Fs * 3, Fs, Fs / 10);
            float ms = lag * 1000f / Fs, reported = chain.Suppressor.DelayMs;
            Console.WriteLine("  " + engine.PadRight(8) + " chain delay " + ms.ToString("0.0") + " ms (" + lag + " samples), reported " + reported.ToString("0.0") + " ms");
            Check(Math.Abs(ms - reported) <= 1.0, engine + ": the latency readout matches the measured delay");
            chain.Suppressor.ReleaseEngine();
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
