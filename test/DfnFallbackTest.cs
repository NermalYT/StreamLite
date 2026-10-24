// DeepFilterNet on a PC whose graphics card can't run it (simulated with STREAMLITE_NO_GPU=1): the noise remover must
// move to DeepFilterNet on the CPU and say why, not drop to RNNoise. Paced like the live mic.
// Usage: run.ps1 DfnFallbackTest
using System;
using System.IO;
using System.Threading;
using StreamLite;
using StreamLite.Audio;

class DfnFallbackTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        Environment.SetEnvironmentVariable("STREAMLITE_NO_GPU", "1");
        var ns = new NoiseSuppressor { Enabled = true, UseAi = true, Dfn = 2, Strength = 0.7f };
        var rnd = new Random(1);
        var buf = new float[480];
        bool cpu = false;
        for (int k = 0; k < 600 && !cpu; k++)          // up to 6 s
        {
            for (int i = 0; i < 480; i++) buf[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05);
            ns.Process(buf, 480);
            var d = ns.DfnEngine;
            cpu = d != null && !d.Gpu && d.Ready;
            Thread.Sleep(10);
        }
        var e = ns.DfnEngine;
        Check(cpu, "the GPU can't run it -> DeepFilterNet runs on the CPU (" + (e == null ? "no engine" : (e.Gpu ? "GPU" : "CPU") + (e.Ready ? ", ready" : ", not ready") + (e.Error != null ? ", " + e.Error : "")) + ")");
        Check(ns.FallbackNote != null && ns.FallbackNote.Contains("CPU"), "...and says why: " + ns.FallbackNote);
        // Measure that it really removes noise now.
        double ein = 0, eout = 0;
        for (int k = 0; k < 200; k++)
        {
            for (int i = 0; i < 480; i++) { buf[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05); ein += buf[i] * buf[i]; }
            ns.Process(buf, 480);
            if (k >= 100) for (int i = 0; i < 480; i++) eout += buf[i] * buf[i];
            Thread.Sleep(10);
        }
        double dIn = 10 * Math.Log10(ein / (200 * 480)), dOut = 10 * Math.Log10(eout / (100 * 480) + 1e-20);
        Check(dOut < dIn - 30, "steady noise " + dIn.ToString("0") + " -> " + dOut.ToString("0") + " dBFS on the CPU fallback");
        // Picking the engine again gives the graphics card another chance.
        ns.Dfn = 1; ns.Process(buf, 480); ns.Dfn = 2; ns.Process(buf, 480);
        Check(ns.FallbackNote == null, "picking the engine again clears the note");
        Environment.SetEnvironmentVariable("STREAMLITE_NO_GPU", null);
        ns.ReleaseEngine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
