// What each AI camera graph costs the graphics card, alone and next to a game: every variant StreamLite can pick
// (1080p / 4K capture, standard / High VRAM "hq" / Low VRAM) at 30 fps, its time per frame on an idle card, then with a
// stand-in game keeping the card busy (the 4K graph in tight loops) - the camera's time per frame under contention and
// how much it slows the game (game runs per second with vs without the camera).
// Usage: CamFxCost [seconds per measurement]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Camera;

class CamFxCost
{
    sealed class Graph
    {
        public OnnxSession S; public Array[] In, Out; public long[][] Shapes; public bool Matte; float[][] st, so;
        public Graph(string file, int W, int H, bool lowVram)
        {
            S = new OnnxSession(Path.Combine(OnnxSession.Folder, file), true, 1, fuse: false, lowVram: lowVram);
            Matte = CameraFx.HasMatte(S);
            var RS = CameraFx.StateSizes(S);
            var y = new byte[W * H]; var uv = new byte[W * H / 2]; var rnd = new Random(3);
            for (int i = 0; i < y.Length; i++) y[i] = (byte)(60 + (i % W) * 120 / W + rnd.Next(8));
            for (int i = 0; i < uv.Length; i++) uv[i] = (byte)(128 + rnd.Next(-6, 6));
            st = new float[4][]; so = new float[4][];
            for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; so[i] = new float[st[i].Length]; }
            var p = new float[24]; p[0] = 1;
            Shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 } };
            var lut = new float[256]; for (int i = 0; i < 256; i++) lut[i] = i / 255f;
            In = new Array[] { y, uv, new long[] { 0, 0, H, W }, p, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, lut };
            if (Matte)
            {
                // A blurred background, so the cut-out is used as it would be.
                p[5] = 1;
                Shapes = Shapes.Concat(new[] { new long[] { 540, 960, 3 } }).Concat(RS.Select(r => new long[] { 1, r[0], r[1], r[2] })).ToArray();
                In = In.Concat(new Array[] { new byte[540 * 960 * 3], st[0], st[1], st[2], st[3] }).ToArray();
            }
            // Outputs sized from the model (the face detector's anchor count differs between graphs).
            Out = new Array[S.OutputNames.Length];
            for (int i = 0; i < Out.Length; i++)
            {
                long n = S.OutputShapes[i].Aggregate(1L, (a, b) => a * Math.Max(1, b));
                if (i >= 6) Out[i] = so[i - 6];
                else if (i < 4) Out[i] = new byte[n]; else Out[i] = new float[n];
            }
        }
        public void Run()
        {
            S.Run(In, Shapes, Out);
            if (!Matte) return;
            var t = st; st = so; so = t;
            for (int i = 0; i < 4; i++) { In[7 + i] = st[i]; Out[6 + i] = so[i]; }
        }
    }

    static volatile bool stopGame;
    static long gameRuns;

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 8;
        var variants = new[]
        {
            Tuple.Create("4K capture, High VRAM (hq)", "camfx_2160_hq.onnx", 3840, 2160, false),
            Tuple.Create("4K capture, standard", "camfx_2160.onnx", 3840, 2160, false),
            Tuple.Create("1080p capture, High VRAM (hq)", "camfx_1080_hq.onnx", 1920, 1080, false),
            Tuple.Create("1080p capture, standard", "camfx_1080.onnx", 1920, 1080, false),
            Tuple.Create("1080p capture, Low VRAM", "camfx_1080_low.onnx", 1920, 1080, true),
            Tuple.Create("4K capture, background off", "camfx_2160_nomatte.onnx", 3840, 2160, false),
            Tuple.Create("1080p capture, background off", "camfx_1080_nomatte.onnx", 1920, 1080, false),
            Tuple.Create("1080p, Low VRAM, background off", "camfx_1080_low_nomatte.onnx", 1920, 1080, true),
        };

        // The stand-in game: two loops of the 4K graph, as fast as the card goes.
        double gameAlone = GameRate(seconds, null);
        Console.WriteLine("Stand-in game alone: " + gameAlone.ToString("0.0") + " runs/s");
        foreach (var v in variants)
        {
            if (!File.Exists(Path.Combine(OnnxSession.Folder, v.Item2))) { Console.WriteLine(v.Item1 + ": model missing"); continue; }
            var g = new Graph(v.Item2, v.Item3, v.Item4, v.Item5);
            for (int i = 0; i < 10; i++) g.Run();
            var idle = Paced(g, seconds);
            List<double> loaded = null;
            double withCam = GameRate(seconds, () => loaded = Paced(g, seconds));
            Console.WriteLine(v.Item1.PadRight(32) + " idle card " + Avg(idle).ToString("0.0").PadLeft(5) + " ms (p99 " + P99(idle).ToString("0.0").PadLeft(5) + ")   " +
                "next to the game " + Avg(loaded).ToString("0.0").PadLeft(5) + " ms (p99 " + P99(loaded).ToString("0.0").PadLeft(5) + ", " + Fps(loaded, seconds).ToString("0.0") + " fps)   " +
                "game slowed " + ((1 - withCam / gameAlone) * 100).ToString("0").PadLeft(3) + "%");
            g.S.Dispose();
        }
    }

    /// <summary>The camera at 30 fps (a frame every 33.3 ms; a late frame drops the ones it overran): ms per frame.</summary>
    static List<double> Paced(Graph g, int seconds)
    {
        var ms = new List<double>(); var clock = Stopwatch.StartNew(); double next = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (now < next) { Thread.Sleep(Math.Max(0, (int)(next - now) - 1)); continue; }
            var t = Stopwatch.StartNew(); g.Run(); ms.Add(t.Elapsed.TotalMilliseconds);
            next += 1000.0 / 30; if (next < clock.Elapsed.TotalMilliseconds) next = Math.Ceiling(clock.Elapsed.TotalMilliseconds / (1000.0 / 30)) * (1000.0 / 30);
        }
        return ms;
    }

    /// <summary>Game runs per second over the time, while `during` runs on this thread (null: just wait).</summary>
    static double GameRate(int seconds, Action during)
    {
        stopGame = false; gameRuns = 0;
        var ready = new CountdownEvent(2); var go = new ManualResetEvent(false);
        var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            var g = new Graph("camfx_2160.onnx", 3840, 2160, false);
            for (int i = 0; i < 5; i++) g.Run();
            ready.Signal(); go.WaitOne();
            while (!stopGame) { g.Run(); Interlocked.Increment(ref gameRuns); }
            g.S.Dispose();
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start()); ready.Wait();
        long start = Interlocked.Read(ref gameRuns); var sw = Stopwatch.StartNew(); go.Set();
        if (during != null) during(); else Thread.Sleep(seconds * 1000);
        double rate = (Interlocked.Read(ref gameRuns) - start) / sw.Elapsed.TotalSeconds;
        stopGame = true; threads.ForEach(t => t.Join());
        return rate;
    }

    static double Avg(List<double> l) { return l == null || l.Count == 0 ? 0 : l.Average(); }
    static double P99(List<double> l) { if (l == null || l.Count == 0) return 0; var s = l.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))]; }
    static double Fps(List<double> l, int seconds) { return l == null ? 0 : l.Count / (double)seconds; }
}
