using System;
using System.Diagnostics;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// How fast StreamLite's sound work runs on this runtime (.NET Framework on Windows, Mono on Linux): the mic chain with
// each CPU noise remover and the 7.1 -> 360 3D renderer, as fast as they go. Prints ms per block and how many times
// faster than real time. Usage: run.ps1 MonoBench [noop]  /  mono MonoBench.exe
class MonoBench
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "noop") return;
        Console.WriteLine("runtime: " + (Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET Framework") + " " + Environment.Version + ", " + Environment.ProcessorCount + " threads");
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai })
        {
            const int P = 480; const int seconds = 20;
            var chain = new VoiceChain(); chain.Configure(new AppSettings { NsEngine = engine });
            var rnd = new Random(1); var buf = new float[P];
            for (int i = 0; i < 100; i++) { Fill(buf, rnd); chain.Process(buf, P, false); }
            int n = seconds * 48000 / P; var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) { Fill(buf, rnd); chain.Process(buf, P, false); }
            sw.Stop();
            Report("mic chain (" + engine + ")", sw.Elapsed.TotalMilliseconds / n, P * 1000.0 / 48000);
        }
        {
            const int B = SpatialProcessor.B; const int seconds = 20;
            var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
            var cfg = g.ToConfig();
            var sp = new SpatialProcessor(); sp.Configure(cfg);
            var set = HrtfSet.Get(cfg.HrtfKind, cfg.CustomPath);
            for (int w = 0; w < 1500 && !ContinuousRenderer.IsReady(set); w++) System.Threading.Thread.Sleep(10);
            var rnd = new Random(2);
            var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B];
            for (int i = 0; i < 300; i++) { foreach (var s in slots) Fill(s, rnd); sp.Process(slots, SpatialProcessor.Source.Surround71, l, r); }
            int n = seconds * 48000 / B; var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) { foreach (var s in slots) Fill(s, rnd); sp.Process(slots, SpatialProcessor.Source.Surround71, l, r); }
            sw.Stop();
            Report("3D 7.1 -> 360", sw.Elapsed.TotalMilliseconds / n, B * 1000.0 / 48000);
        }
    }

    static void Fill(float[] b, Random rnd) { for (int i = 0; i < b.Length; i++) b[i] = (float)(rnd.NextDouble() * 0.2 - 0.1); }

    static void Report(string what, double msPerBlock, double blockMs)
    {
        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,-28} {1,7:0.000} ms per {2:0.0} ms block = {3,6:0.0}x real time", what, msPerBlock, blockMs, blockMs / msPerBlock));
    }
}
