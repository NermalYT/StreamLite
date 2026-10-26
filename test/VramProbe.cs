// Graphics memory of every AI model StreamLite can run on the GPU, alone and all together, from Windows' "GPU Process
// Memory" counters (dedicated = video memory on the card, shared = system RAM the GPU maps). Usage: VramProbe [low|hq]
//   hq = the High VRAM tier (the camera's cut-out worked out at full HD).
//   low = the Low VRAM settings (what the app uses with "Low VRAM" on); passes when all AI together stays under
//   250 MB of video memory, also after a few hundred frames of face-tracking zoom (crop sizes change every frame).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

class VramProbe
{
    static void Gpu(out long ded, out long sh)
    {
        ded = 0; sh = 0;
        var cat = new PerformanceCounterCategory("GPU Process Memory");
        string tag = "pid_" + Process.GetCurrentProcess().Id + "_";
        foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
        {
            using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
            using (var c = new PerformanceCounter("GPU Process Memory", "Shared Usage", inst, true)) sh += c.RawValue;
        }
    }

    static long Report(string what, out long shared)
    {
        GC.Collect(); Thread.Sleep(800);
        long d; Gpu(out d, out shared);
        Console.WriteLine("  " + what.PadRight(50) + "video memory " + (d / 1048576).ToString().PadLeft(4) + " MB   shared " + (shared / 1048576).ToString().PadLeft(4) + " MB");
        return d;
    }

    static OnnxSession session;
    static Array[] ins, outs;
    static long[][] shapes;

    static bool hq;

    static void Camera(int w, int h, bool low)
    {
        session = CameraFx.OpenSession(w, h, true, low, hq);
        var RS = CameraFx.StateSizes(session);
        shapes = new[]
        {
            new long[] { h, w }, new long[] { h / 2, w }, new long[] { 4 }, new long[] { 16 }, new long[] { 256 }, new long[] { 540, 960, 3 },
            new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] },
            new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] },
        };
        ins = new Array[10]; outs = new Array[10];
        ins[0] = new byte[w * h]; ins[1] = new byte[w * h / 2]; ins[2] = new long[] { 0, 0, h, w };
        var p = new float[16]; p[0] = 0.4f; p[1] = 0.3f; p[2] = 1.05f; p[4] = 0.5f; p[5] = 1; p[8] = 0.5f; p[9] = 0.42f; p[10] = 0.12f; p[11] = 0.2f; p[13] = 0.3f;
        ins[3] = p;
        var lut = new float[256]; for (int i = 0; i < 256; i++) lut[i] = i / 255f;
        ins[4] = lut; ins[5] = new byte[540 * 960 * 3];
        for (int i = 0; i < 4; i++) { ins[6 + i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; outs[6 + i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; }
        outs[0] = new byte[1920 * 1080]; outs[1] = new byte[1920 * 1080 / 2]; outs[2] = new byte[360 * 640 * 4];
        outs[3] = new byte[90 * 160]; outs[4] = new float[4420 * 2]; outs[5] = new float[4420 * 4];
        for (int k = 0; k < 10; k++) session.Run(ins, shapes, outs);
    }

    /// <summary>Face tracking: a different crop (zoom 100-250 %, anywhere in the frame) every frame.</summary>
    static void Zoom(int w, int h, int frames)
    {
        var rnd = new Random(7);
        var crop = (long[])ins[2];
        for (int k = 0; k < frames; k++)
        {
            double z = 1 + 1.5 * rnd.NextDouble();
            long ch = (long)(h / z) & ~1L, cw = (long)(w / z) & ~1L;
            crop[0] = (long)((h - ch) * rnd.NextDouble()) & ~1L; crop[1] = (long)((w - cw) * rnd.NextDouble()) & ~1L;
            crop[2] = ch; crop[3] = cw;
            session.Run(ins, shapes, outs);
        }
        crop[0] = 0; crop[1] = 0; crop[2] = h; crop[3] = w;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool low = args.Contains("low"); hq = args.Contains("hq");
        Console.WriteLine(low ? "Low VRAM settings" : hq ? "High VRAM settings" : "Normal settings");
        long sh;
        long base0 = Report("nothing loaded", out sh);
        var dfn = new DeepFilter(true, low);
        while (!dfn.Ready && dfn.Error == null) Thread.Sleep(10);
        if (dfn.Error != null) Console.WriteLine("  DeepFilterNet: " + dfn.Error);
        if (low && !dfn.LowVram) Console.WriteLine("  DeepFilterNet: Low VRAM model missing - running the normal one");
        var x = new float[480]; var yy = new float[480];
        for (int i = 0; i < 300; i++) { dfn.Process(x, yy); dfn.WaitIdle(1000); }
        Report("+ DeepFilterNet 3 (noise removal)", out sh);
        Camera(1920, 1080, low);
        Report("+ AI camera, 1080p capture", out sh);
        var t = Stopwatch.StartNew();
        Zoom(1920, 1080, 300);
        double ms = t.Elapsed.TotalMilliseconds / 300;
        long all = Report("  after 300 frames of face-tracking zoom", out sh);
        Console.WriteLine("  (camera " + ms.ToString("0.0") + " ms per frame)");
        session.Dispose();
        if (!low)
        {
            Camera(3840, 2160, false);
            Zoom(3840, 2160, 100);
            Report("+ AI camera, 4K capture (instead)", out sh);
            session.Dispose();
        }
        dfn.Dispose();
        long sh2; Report("everything unloaded", out sh2);
        long total = all - base0;
        Console.WriteLine("all AI features at once (1080p camera): " + (total / 1048576) + " MB of video memory (+ " + (sh / 1048576) + " MB of shared system memory)");
        if (low) Console.WriteLine(total < 250L * 1048576 ? "ALL PASS (under 250 MB of video memory)" : "FAILED (over 250 MB of video memory)");
    }
}
