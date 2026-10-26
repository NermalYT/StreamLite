using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

// The camera effect graph from C#, on a saved 4K NV12 frame: every AI model loaded at once (DeepFilterNet on the GPU
// for the mic + the camera graph), speed, video memory and RAM, face tracking convergence, and preview PNGs per mode.
// Usage: CamFxTest <frame.nv12> <outdir> [cpu]
class CamFxTest
{
    [DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Mem(string what)
    {
        GC.Collect();
        var p = Process.GetCurrentProcess(); p.Refresh();
        long ded = 0, sh = 0;
        var cat = new PerformanceCounterCategory("GPU Process Memory");
        foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith("pid_" + p.Id + "_")))
        {
            using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
            using (var c = new PerformanceCounter("GPU Process Memory", "Shared Usage", inst, true)) sh += c.RawValue;
        }
        lastVram = ded / 1048576.0; lastWs = p.WorkingSet64 / 1048576.0;
        Console.WriteLine("  " + what.PadRight(44) + "VRAM " + lastVram.ToString("0").PadLeft(4) + " MB dedicated + " + (sh / 1048576.0).ToString("0") + " MB shared; RAM working set " + lastWs.ToString("0") + " MB");
    }
    static double lastVram, lastWs;

    static void SavePreview(byte[] bgra, string path)
    {
        using (var bmp = new Bitmap(640, 360, PixelFormat.Format32bppRgb))
        {
            var d = bmp.LockBits(new Rectangle(0, 0, 640, 360), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(bgra, 0, d.Scan0, bgra.Length);
            bmp.UnlockBits(d);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool gpu = !(args.Length > 2 && args[2] == "cpu");
        string outDir = args[1]; Directory.CreateDirectory(outDir);
        var raw = File.ReadAllBytes(args[0]);
        int W = 3840, H = 2160;
        var y = raw.Take(W * H).ToArray(); var uv = raw.Skip(W * H).ToArray();
        Mem("start");

        var dfn = new DeepFilter(true);
        while (!dfn.Ready && dfn.Error == null) System.Threading.Thread.Sleep(10);
        var fx = new float[480]; var fo = new float[480];
        for (int i = 0; i < 50; i++) { dfn.Process(fx, fo); dfn.WaitIdle(500); }
        Mem("DeepFilterNet (mic) on the GPU");

        var sw = Stopwatch.StartNew();
        var s = new OnnxSession(Path.Combine(OnnxSession.Folder, "camfx_2160.onnx"), gpu, Environment.ProcessorCount / 2, fuse: false);
        Console.WriteLine("  camera graph loaded in " + sw.ElapsedMilliseconds + " ms");
        var RS = CameraFx.RS;
        var crop = new long[4]; var p = new float[24]; var warp = new float[9]; var lut = new float[256];
        var st = new float[4][]; var so = new float[4][];
        for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; so[i] = new float[st[i].Length]; }
        var yOut = new byte[1920 * 1080]; var uvOut = new byte[1920 * 540]; var preview = new byte[360 * 640 * 4];
        var thumb = new byte[90 * 160]; var scores = new float[8840]; var boxes = new float[17680];
        var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
            new long[] { 1, 16, 135, 240 }, new long[] { 1, 20, 68, 120 }, new long[] { 1, 40, 34, 60 }, new long[] { 1, 64, 17, 30 } };
        var c = new CamFxSettings { FaceTracking = true, Framing = "custom", Zoom = 170, TrackSpeed = 100, SkinSmoothing = 50, Background = "blur" };
        var tr = new CameraDirector(); var faces = new FaceBox[8]; int nf = 0; var light = new AutoLight();
        var modes = new[] { "blur", "green", "image", "dim", "off" };
        var pic = Backgrounds.Solid(30, 60, 140);
        double total = 0; int n = 0;
        foreach (var mode in modes)
        {
            c.Background = mode;
            for (int f = 0; f < (mode == "blur" ? 120 : 20); f++)
            {
                tr.Step(c, faces, nf, W, H, crop, warp);
                light.Build(c, lut);
                CameraFx.FillParams(c, p, tr);
                byte[] bg = mode == "green" ? Backgrounds.Solid(0, 255, 0) : mode == "image" ? pic : new byte[540 * 960 * 3];
                var t0 = Stopwatch.StartNew();
                s.Run(new Array[] { y, uv, crop, p, warp, lut, bg, st[0], st[1], st[2], st[3] }, shapes,
                      new Array[] { yOut, uvOut, preview, thumb, scores, boxes, so[0], so[1], so[2], so[3] });
                if (f > 3) { total += t0.Elapsed.TotalMilliseconds; n++; }
                var tmp = st; st = so; so = tmp;
                nf = Faces.Decode(scores, boxes, faces);
                light.Measure(thumb);
            }
            SavePreview(preview, Path.Combine(outDir, "fx_" + mode + ".png"));
            if (mode == "blur")
            {
                Check(nf > 0, "face found: centre " + faces[0].Cx.ToString("0.00") + ", " + faces[0].Cy.ToString("0.00") + " size " + faces[0].W.ToString("0.00") + "x" + faces[0].H.ToString("0.00"));
                double cw = crop[3], cx = crop[1] + cw / 2;
                Check(Math.Abs(cw - W / 1.7) < W * 0.03 && Math.Abs(cx / W - faces[0].Cx) < 0.12, "framing zoomed to 170% around the face: crop " + string.Join(",", crop));
                Mem("+ camera graph (4K in, all effects)");
            }
        }
        double avg = total / n;
        Console.WriteLine("  camera graph: " + avg.ToString("0.0") + " ms per frame on the " + (gpu ? "GPU" : "CPU"));
        Check(avg < 25, "fast enough for 30 fps with headroom");
        Check(lastVram < 800, "all AI models together under 0.8 GB of video memory (" + lastVram.ToString("0") + " MB)");
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        for (int f = 0; f < 30; f++)
        {
            tr.Step(c, faces, nf, W, H, crop, warp); CameraFx.FillParams(c, p, tr);
            s.Run(new Array[] { y, uv, crop, p, warp, lut, new byte[540 * 960 * 3], st[0], st[1], st[2], st[3] }, shapes,
                  new Array[] { yOut, uvOut, preview, thumb, scores, boxes, so[0], so[1], so[2], so[3] });
            var tmp = st; st = so; so = tmp;
        }
        Mem("running, after the start-up trim");
        dfn.Dispose(); s.Dispose();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
