// How much delay the mic's effects chain adds with each noise engine, measured: voiced bursts with sudden starts go
// through the whole chain (offline, the voice test's copy, after the engine has loaded) and each burst's start is found
// in the output (the earliest of 8: a remover easing in can only make a start look later). Prints each engine's delay and checks NoiseSuppressor.DelayMs reports it.
// Usage: run.ps1 ChainDelayTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class ChainDelayTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int Fs = 48000;

    /// <summary>Where the signal first reaches 20% of its loudest 1 ms RMS between from and to.</summary>
    static int Onset(float[] a, int from, int to)
    {
        const int B = 48;
        double peak = 0;
        var rms = new double[(to - from) / B];
        for (int k = 0; k < rms.Length; k++) { double s = 0; for (int i = 0; i < B; i++) { double v = a[from + k * B + i]; s += v * v; } rms[k] = Math.Sqrt(s / B); peak = Math.Max(peak, rms[k]); }
        for (int k = 0; k < rms.Length; k++)
            if (rms[k] >= 0.2 * peak)
            {
                // refine to the sample: first sample in this ms (or the one before) above 20% of the peak amplitude
                int start = from + Math.Max(0, k - 1) * B;
                double thr = 0.2 * peak * Math.Sqrt(2);
                for (int i = start; i < from + (k + 1) * B; i++) if (Math.Abs(a[i]) >= thr) return i;
                return from + k * B;
            }
        return -1;
    }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        const int Bursts = 8, Period = Fs * 8 / 10, Len = Fs / 5;   // 200 ms vowel every 800 ms
        int total = Fs + Bursts * Period;
        var x = new float[total];
        var rnd = new Random(4);
        var starts = new List<int>();
        for (int b = 0; b < Bursts; b++)
        {
            int s0 = Fs + b * Period + rnd.Next(0, 480);
            starts.Add(s0);
            double f0 = 120 + 15 * b;
            for (int i = 0; i < Len; i++)
            {
                double t = (double)i / Fs, v = 0;
                for (int k = 1; k < 12; k++) v += Math.Sin(2 * Math.PI * k * f0 * t + 0.7 * k) / k;
                x[s0 + i] = (float)(0.2 * v * Math.Min(1, i / 24.0));   // 0.5 ms fade-in: a sudden but click-free start
            }
        }
        for (int i = 0; i < total; i++) x[i] += (float)(0.0005 * (rnd.NextDouble() * 2 - 1));

        foreach (bool realtime in new[] { false, true })
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai, NoiseEngines.DfnCpu })
        {
            var S = new AppSettings { NoiseRemoval = true, NsEngine = engine, NsRealtime = realtime, Gate = false, Compressor = false, DeEsser = false, AutoLevel = false, StudioVoice = !realtime };   // realtime: the lowest-delay path on its own
            S.MicEq.Enabled = false;
            var chain = new VoiceChain();
            chain.Configure(S);
            chain.Suppressor.Synchronous = true;
            var block = new float[144];
            // DeepFilterNet loads in the background (RNNoise stands in meanwhile): measure only once it's running.
            for (int k = 0; k < 6000 && chain.Suppressor.Dfn != 0; k++)
            {
                for (int i = 0; i < 144; i++) block[i] = (float)(0.0005 * (rnd.NextDouble() * 2 - 1));
                chain.Process(block, 144, false);
                var d = chain.Suppressor.DfnEngine;
                if (d != null && (d.Ready || d.Error != null)) break;
                System.Threading.Thread.Sleep(5);
            }
            var y = new float[total];
            for (int pos = 0; pos + 144 <= total; pos += 144)
            {
                Array.Copy(x, pos, block, 0, 144);
                chain.Process(block, 144, false);
                Array.Copy(block, 0, y, pos, 144);
            }
            var delays = starts.Select(s => { int o = Onset(y, s - 480, s + Len); return o < 0 ? double.NaN : (o - Onset(x, s - 480, s + Len)) * 1000.0 / Fs; }).Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
            double ms = delays.Count > 0 ? delays[0] : double.NaN;   // earliest: a remover easing in can only make a start look later
            float reported = chain.DelayMs;
            var dfn = chain.Suppressor.DfnEngine;
            Console.WriteLine("  " + (engine + (realtime ? " realtime" : "")).PadRight(17) + " chain delay " + ms.ToString("0.0") + " ms (bursts " + string.Join(", ", delays.Select(d => d.ToString("0.0"))) + "), reported " + reported.ToString("0.0") + " ms" + (dfn != null ? ", " + dfn.Late + " late frames" : ""));
            Check(Math.Abs(ms - reported) <= 1.5, engine + (realtime ? " realtime" : "") + ": the latency readout matches the measured delay");
            if (realtime) Check(ms <= 3.5, engine + " realtime: the whole chain adds " + ms.ToString("0.0") + " ms");
            chain.Suppressor.ReleaseEngine();
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
