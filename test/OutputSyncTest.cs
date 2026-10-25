// Output sync, offline: clicks heard by a (noisy) microphone at a known delay are timed to the millisecond, a too-quiet
// output is reported instead of guessed, outputs are lined up to the slowest, and the delay line holds a stream back
// exactly. Usage: run.ps1 OutputSyncTest
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamLite.Audio;

class OutputSyncTest
{
    const int Fs = 48000, Pk = 144;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>A mic stream in 3 ms packets with clicks arriving delayMs after each click time; returns the click times.</summary>
    static List<long> Simulate(OutputSyncMeter m, double delayMs, double level, int seed)
    {
        var rnd = new Random(seed);
        double tick = Stopwatch.Frequency / 1000.0;
        long t0 = 1000000000;
        int seconds = 4;
        var x = new float[seconds * Fs];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(0.002 * (rnd.NextDouble() * 2 - 1) + 0.01 * Math.Sin(2 * Math.PI * 100 * i / Fs));   // hiss + hum
        var clicks = new List<long>();
        for (int c = 0; c < 5; c++)
        {
            double at = 1000 + c * 500;                       // ms after t0
            clicks.Add(t0 + (long)(at * tick));
            int start = (int)((at + delayMs) * Fs / 1000);
            // What a mic hears from headphones held against it: the click, a little ringing.
            for (int i = 0; i < 480 && start + i < x.Length; i++) x[start + i] += (float)(level * (rnd.NextDouble() * 2 - 1) * Math.Exp(-i / 60.0));
        }
        for (int p = 0; p + Pk <= x.Length; p += Pk)
        {
            var pkt = new float[Pk]; Array.Copy(x, p, pkt, 0, Pk);
            m.Feed(pkt, Pk, t0 + (long)((p + Pk - 1) * 1000.0 / Fs * tick));   // stamped when its last sample arrived
        }
        return clicks;
    }

    static void Main()
    {
        foreach (var d in new[] { 38.0, 192.5 })
        {
            var m = new OutputSyncMeter();
            var r = m.Measure(Simulate(m, d, 0.2, (int)d));
            Check(r.DelayMs.HasValue && Math.Abs(r.DelayMs.Value - d) < 1.0 && r.Heard == 5 && r.Problem == null,
                "a click " + d + " ms late is timed at " + (r.DelayMs.HasValue ? r.DelayMs.Value.ToString("0.0") : "-") + " ms (" + r.Heard + "/5 heard)");
        }
        var quiet = new OutputSyncMeter();
        var q = quiet.Measure(Simulate(quiet, 50, 0.002, 3));
        Check(!q.DelayMs.HasValue && q.Problem != null, "an output the mic can't hear is reported, not guessed (" + q.Problem + ")");

        var a = OutputSyncMeter.Align(new double?[] { 40, 210, null, 95 });
        Check(a.SequenceEqual(new[] { 170, 0, 0, 115 }), "line up: every output waits for the slowest (" + string.Join(", ", a) + " ms)");

        var dl = new DelayLine();
        var inter = new float[2 * 9600]; inter[0] = 1; inter[1] = 0.5f;
        var outp = new List<float>();
        for (int k = 0; k < 4; k++) { var blk = new float[2 * 2400]; Array.Copy(inter, k * 4800, blk, 0, 4800); dl.Process(blk, 2400, 4800); outp.AddRange(blk); }
        int at = outp.FindIndex(v => v == 1f);
        Check(at == 2 * 4800 && outp[at + 1] == 0.5f, "the delay line holds a stream back exactly 100 ms (" + (at / 2) + " frames)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
