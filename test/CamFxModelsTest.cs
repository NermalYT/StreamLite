// Which camera effects model StreamLite runs (4.1.5): the one without the person cut-out while the background is off
// (a third of the graphics card - test/CamFxCost.cs), the High VRAM cut-out only when asked for; and that the model
// without the cut-out makes exactly the picture the full one makes with the background off (one frame each, on the CPU,
// so it runs without a graphics card too).
using System;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

class CamFxModelsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        foreach (var f in new[] { CameraFx.NoMatteModel(false, false), CameraFx.NoMatteModel(true, false), CameraFx.NoMatteModel(false, true) })
            Check(File.Exists(Path.Combine(OnnxSession.Folder, f)), "installed: " + f);

        var c = new CamFxSettings();
        foreach (var bg in new[] { "off", "blur", "image", "video", "green", "dim" })
        {
            c.Background = bg;
            Check(CameraFx.NeedsMatte(c) == (bg != "off"), "background " + bg + ": " + (bg == "off" ? "no" : "the") + " person cut-out");
        }

        // The model each choice opens (on the CPU: the file picked is the same as on the graphics card, Low VRAM aside).
        using (var s = CameraFx.OpenSession(1920, 1080, false, false, false, false)) Check(!CameraFx.HasMatte(s) && s.InputNames.Length == 6 && s.OutputNames.Length == 6, "1080p, background off: the model without the cut-out (6 inputs, 6 outputs)");
        using (var s = CameraFx.OpenSession(3840, 2160, false, false, true, false)) Check(!CameraFx.HasMatte(s), "4K, High VRAM, background off: no cut-out either (High VRAM only changes the cut-out)");
        using (var s = CameraFx.OpenSession(1920, 1080, false, false, false, true)) Check(CameraFx.HasMatte(s) && s.InputNames.Length == 11, "1080p, background on: the full model");

        // One frame through both: identical NV12 out, the same faces.
        int W = 1920, H = 1080;
        var y = new byte[W * H]; var uv = new byte[W * H / 2]; var rnd = new Random(5);
        for (int i = 0; i < y.Length; i++) { int px = i % W, py = i / W; y[i] = (byte)Math.Min(235, 40 + px * 150 / W + (((px / 64) + (py / 64)) % 2) * 30 + rnd.Next(6)); }
        for (int i = 0; i < uv.Length; i++) uv[i] = (byte)(128 + ((i / 2) % W < W / 2 ? 12 : -10) + rnd.Next(-3, 3));
        var p = new float[24]; p[0] = 0.4f; p[1] = 0.3f; p[2] = 1.05f; p[3] = 0.1f; p[4] = 0.3f; p[8] = 0.5f; p[9] = 0.4f; p[10] = 0.12f; p[11] = 0.2f; p[12] = 1;
        var lut = new float[256]; for (int i = 0; i < 256; i++) lut[i] = i / 255f;
        var crop = new long[] { 0, 0, H, W }; var warp = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        Func<bool, Array[]> run = matte =>
        {
            using (var s = CameraFx.OpenSession(W, H, false, false, false, matte))
            {
                var RS = CameraFx.StateSizes(s);
                var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 } }.ToList();
                var ins = new Array[] { y, uv, crop, p, warp, lut }.ToList();
                var outs = new Array[] { new byte[1920 * 1080], new byte[1920 * 540], new byte[360 * 640 * 4], new byte[90 * 160], new float[4420 * 2], new float[4420 * 4] }.ToList();
                if (matte)
                {
                    shapes.Add(new long[] { 540, 960, 3 }); ins.Add(new byte[540 * 960 * 3]);
                    foreach (var r in RS) { shapes.Add(new long[] { 1, r[0], r[1], r[2] }); ins.Add(new float[r[0] * r[1] * r[2]]); outs.Add(new float[r[0] * r[1] * r[2]]); }
                }
                s.Run(ins.ToArray(), shapes.ToArray(), outs.ToArray());
                return outs.ToArray();
            }
        };
        var full = run(true); var plain = run(false);
        int maxY = 0, maxUv = 0;
        for (int i = 0; i < 1920 * 1080; i++) maxY = Math.Max(maxY, Math.Abs(((byte[])full[0])[i] - ((byte[])plain[0])[i]));
        for (int i = 0; i < 1920 * 540; i++) maxUv = Math.Max(maxUv, Math.Abs(((byte[])full[1])[i] - ((byte[])plain[1])[i]));
        Check(maxY <= 1 && maxUv <= 1, "the same picture with the background off (largest difference: Y " + maxY + ", UV " + maxUv + " of 255)");
        float bestFull = ((float[])full[4]).Where((v, i) => i % 2 == 1).Max(), bestPlain = ((float[])plain[4]).Where((v, i) => i % 2 == 1).Max();
        Check(Math.Abs(bestFull - bestPlain) < 1e-4, "the same face detection (best score " + bestFull.ToString("0.000") + " vs " + bestPlain.ToString("0.000") + ")");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
