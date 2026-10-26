// Low VRAM (needs a DirectML graphics card; CI skips it): DeepFilterNet + the AI camera together stay under 250 MB of
// video memory (also while face tracking changes the crop every frame), the small camera model's picture matches the
// normal one, the noise remover switches models when the setting changes, and with a broken GPU it still falls back to
// DeepFilterNet on the CPU with the full model.
// Usage: run.ps1 LowVramTest
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

class LowVramTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static long VideoMemory()
    {
        GC.Collect(); Thread.Sleep(800);
        long ded = 0;
        var cat = new PerformanceCounterCategory("GPU Process Memory");
        string tag = "pid_" + Process.GetCurrentProcess().Id + "_";
        foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
            using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
        return ded / 1048576;
    }

    const int W = 1920, H = 1080;

    /// <summary>A test picture: soft gradients, a bright "face" disc on a dark "body", fine noise.</summary>
    static void Frame(byte[] y, byte[] uv)
    {
        var rnd = new Random(3);
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                double v = 60 + 80.0 * c / W + 40.0 * r / H;
                double dx = (c - 960) / 160.0, dy = (r - 420) / 200.0;
                if (dx * dx + dy * dy < 1) v = 170;
                else if (r > 620 && Math.Abs(c - 960) < 300 - (r - 620) / 4) v = 45;
                y[r * W + c] = (byte)Math.Max(16, Math.Min(235, v + rnd.Next(-3, 4)));
            }
        for (int i = 0; i < uv.Length; i += 2) { uv[i] = 120; uv[i + 1] = 140; }
    }

    static Array[] Inputs(byte[] y, byte[] uv, float[][] states)
    {
        var p = new float[24]; p[0] = 0.4f; p[1] = 0.3f; p[2] = 1.05f; p[4] = 0.5f; p[5] = 1; p[8] = 0.5f; p[9] = 0.42f; p[10] = 0.12f; p[11] = 0.2f; p[13] = 0.3f;
        var lut = new float[256]; for (int i = 0; i < 256; i++) lut[i] = i / 255f;
        return new Array[] { y, uv, new long[] { 0, 0, H, W }, p, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, lut, new byte[540 * 960 * 3], states[0], states[1], states[2], states[3] };
    }

    static long[][] Shapes()
    {
        var RS = CameraFx.RS;
        return new[]
        {
            new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
            new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] },
            new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] },
        };
    }

    /// <summary>Runs the camera model n frames (states carried over; zoom = a new crop every frame); returns y_out.</summary>
    static byte[] RunCamera(OnnxSession s, byte[] y, byte[] uv, int n, bool zoom)
    {
        var RS = CameraFx.RS;
        var st = new float[4][]; var st2 = new float[4][];
        for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; st2[i] = new float[st[i].Length]; }
        var yOut = new byte[W * H];
        var outs = new Array[] { yOut, new byte[W * H / 2], new byte[360 * 640 * 4], new byte[90 * 160], new float[4420 * 2], new float[4420 * 4], st2[0], st2[1], st2[2], st2[3] };
        var shapes = Shapes();
        var rnd = new Random(5);
        for (int k = 0; k < n; k++)
        {
            var ins = Inputs(y, uv, st);
            if (zoom)
            {
                var crop = (long[])ins[2];
                double z = 1 + 1.5 * rnd.NextDouble();
                crop[2] = (long)(H / z) & ~1L; crop[3] = (long)(W / z) & ~1L;
                crop[0] = (long)((H - crop[2]) * rnd.NextDouble()) & ~1L; crop[1] = (long)((W - crop[3]) * rnd.NextDouble()) & ~1L;
            }
            s.Run(ins, shapes, outs);
            for (int i = 0; i < 4; i++) Array.Copy((float[])outs[6 + i], st[i], st[i].Length);
        }
        return yOut;
    }

    static double Psnr(byte[] a, byte[] b)
    {
        double se = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; se += d * d; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / a.Length));
    }

    static bool WaitFor(NoiseSuppressor ns, Func<DeepFilter, bool> ok, int seconds)
    {
        var buf = new float[480]; var rnd = new Random(1);
        for (int k = 0; k < seconds * 100; k++)
        {
            for (int i = 0; i < 480; i++) buf[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05);
            ns.Process(buf, 480);
            var d = ns.DfnEngine;
            if (d != null && d.Ready && ok(d)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        Check(DeepFilter.LowVramInstalled && CameraFx.LowVramInstalled, "Low VRAM models installed (" + DeepFilter.LowVramFile + ", " + CameraFx.LowVramModel + ")");
        var y = new byte[W * H]; var uv = new byte[W * H / 2];
        Frame(y, uv);

        // 1. Video memory: DeepFilterNet (Low VRAM) + the camera (Low VRAM), face tracking zooming every frame.
        long before = VideoMemory();
        var dfn = new DeepFilter(true, true);
        while (!dfn.Ready && dfn.Error == null) Thread.Sleep(10);
        Check(dfn.Ready && dfn.LowVram, "DeepFilterNet loads with the Low VRAM model on the GPU" + (dfn.Error != null ? " (" + dfn.Error + ")" : ""));
        var x = new float[480]; var o = new float[480];
        for (int i = 0; i < 200; i++) { dfn.Process(x, o); dfn.WaitIdle(1000); }
        var cam = CameraFx.OpenSession(W, H, true, true);
        RunCamera(cam, y, uv, 10, false);
        RunCamera(cam, y, uv, 200, true);
        long used = VideoMemory() - before;
        Check(used < 250, "DeepFilterNet + AI camera after 200 frames of face-tracking zoom: " + used + " MB of video memory (limit 250)");
        dfn.Dispose();

        // 2. The small camera model's picture matches the normal model's.
        byte[] lowOut = RunCamera(cam, y, uv, 8, false);
        cam.Dispose();
        using (var normal = CameraFx.OpenSession(W, H, true, false))
        {
            byte[] normalOut = RunCamera(normal, y, uv, 8, false);
            double psnr = Psnr(normalOut, lowOut);
            Check(psnr > 38, "camera picture, Low VRAM vs normal model: " + psnr.ToString("0.0") + " dB PSNR (> 38)");
        }

        // 3. The noise remover follows the setting: Low VRAM model on, normal model off, and back.
        var ns = new NoiseSuppressor { Enabled = true, UseAi = true, Dfn = 2, Strength = 0.7f, LowVram = true };
        Check(WaitFor(ns, d => d.Gpu && d.LowVram, 15), "Low VRAM on: DeepFilterNet (GPU) runs the small model");
        var first = ns.DfnEngine;
        ns.LowVram = false;
        Check(WaitFor(ns, d => d.Gpu && !d.LowVram, 15) && ns.DfnEngine != first, "Low VRAM off: it reloads with the normal model");
        ns.LowVram = true;
        Check(WaitFor(ns, d => d.Gpu && d.LowVram, 15), "...and back on");
        // Steady noise really goes away with the small model.
        var rnd = new Random(2); var buf = new float[480];
        double ein = 0, eout = 0;
        for (int k = 0; k < 200; k++)
        {
            for (int i = 0; i < 480; i++) { buf[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05); ein += buf[i] * buf[i]; }
            ns.Process(buf, 480);
            if (k >= 100) for (int i = 0; i < 480; i++) eout += buf[i] * buf[i];
            Thread.Sleep(10);
        }
        double dIn = 10 * Math.Log10(ein / (200 * 480)), dOut = 10 * Math.Log10(eout / (100 * 480) + 1e-20);
        Check(dOut < dIn - 30, "steady noise " + dIn.ToString("0") + " -> " + dOut.ToString("0") + " dBFS with the small model");
        // The CPU engine ignores the setting (and isn't reloaded by it).
        ns.Dfn = 1;
        Check(WaitFor(ns, d => !d.Gpu, 15) && !ns.DfnEngine.LowVram, "DeepFilterNet (CPU) always runs the full model");
        var cpu = ns.DfnEngine; ns.LowVram = false; WaitFor(ns, d => true, 1);
        Check(ns.DfnEngine == cpu, "...and switching Low VRAM doesn't reload it");
        ns.ReleaseEngine();

        // 4. A GPU that can't run it: DeepFilterNet on the CPU with the full model, even with Low VRAM on.
        Environment.SetEnvironmentVariable("STREAMLITE_NO_GPU", "1");
        var ns2 = new NoiseSuppressor { Enabled = true, UseAi = true, Dfn = 2, Strength = 0.7f, LowVram = true };
        Check(WaitFor(ns2, d => !d.Gpu, 15) && !ns2.DfnEngine.LowVram && ns2.FallbackNote != null, "broken GPU + Low VRAM -> DeepFilterNet on the CPU, full model (" + ns2.FallbackNote + ")");
        ns2.ReleaseEngine();
        Environment.SetEnvironmentVariable("STREAMLITE_NO_GPU", null);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
