using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

// High VRAM's camera vs Normal's on the same 4K portrait, green background (the cut-out shows plainly): the 1080p
// results, a close-up of the head's outline, how many edge pixels are half person / half green (a soft, smeared outline
// has many), and the time per frame. Pictures stay in test\shots\camhq. Usage: run.ps1 CamHqCompare [frame.nv12]
class CamHqCompare
{
    static Bitmap Rgb(byte[] y, byte[] uv, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var row = new byte[d.Stride];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                double Y = y[j * w + i], U = uv[(j / 2) * w + (i & ~1)] - 128, V = uv[(j / 2) * w + (i & ~1) + 1] - 128;
                int r = (int)(Y + 1.402 * V), g = (int)(Y - 0.344 * U - 0.714 * V), b = (int)(Y + 1.772 * U);
                row[i * 3] = (byte)Math.Max(0, Math.Min(255, b)); row[i * 3 + 1] = (byte)Math.Max(0, Math.Min(255, g)); row[i * 3 + 2] = (byte)Math.Max(0, Math.Min(255, r));
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, d.Scan0 + j * d.Stride, d.Stride);
        }
        bmp.UnlockBits(d);
        return bmp;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string frame = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\camframes\person4k.nv12");
        var raw = File.ReadAllBytes(frame);
        int W = 3840, H = 2160;
        var y = raw.Take(W * H).ToArray(); var uv = raw.Skip(W * H).ToArray();
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\camhq"); Directory.CreateDirectory(dir);
        Rectangle head = Rectangle.Empty;
        foreach (bool hq in new[] { false, true })
        {
            var s = CameraFx.OpenSession(W, H, true, false, hq);
            var RS = CameraFx.StateSizes(s);
            var crop = new long[] { 0, 0, H, W }; var p = new float[24]; var warp = new float[9]; var lut = new float[256];
            var c = new CamFxSettings { Background = "green", FaceTracking = false };
            var tr = new CameraDirector(); var faces = new FaceBox[8]; int nf = 0; var light = new AutoLight();
            var st = new float[4][]; var so = new float[4][];
            for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; so[i] = new float[st[i].Length]; }
            var yOut = new byte[1920 * 1080]; var uvOut = new byte[1920 * 540]; var preview = new byte[360 * 640 * 4];
            var thumb = new byte[90 * 160]; var scores = new float[8840]; var boxes = new float[17680];
            var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
                new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] }, new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] } };
            var green = Backgrounds.Solid(0, 255, 0);
            double total = 0; int n = 0;
            for (int f = 0; f < 25; f++)
            {
                c.FaceTracking = false; c.Zoom = 100; tr.Step(c, faces, nf, W, H, crop, warp);
                light.Build(c, lut); CameraFx.FillParams(c, p, tr);
                var t0 = Stopwatch.StartNew();
                s.Run(new Array[] { y, uv, crop, p, warp, lut, green, st[0], st[1], st[2], st[3] }, shapes, new Array[] { yOut, uvOut, preview, thumb, scores, boxes, so[0], so[1], so[2], so[3] });
                if (f > 4) { total += t0.Elapsed.TotalMilliseconds; n++; }
                var tmp = st; st = so; so = tmp;
                nf = Faces.Decode(scores, boxes, faces); light.Measure(thumb);
            }
            s.Dispose();
            using (var img = Rgb(yOut, uvOut, 1920, 1080))
            {
                img.Save(Path.Combine(dir, (hq ? "high" : "normal") + ".png"));
                // The outline: pixels next to green that are neither person nor green.
                int mixed = 0, edge = 0; var g = new double[1920 * 1080];
                for (int j = 0; j < 1080; j++) for (int i = 0; i < 1920; i++) { var cc = img.GetPixel(i, j); g[j * 1920 + i] = (cc.G - Math.Max(cc.R, cc.B)) / 255.0; }
                for (int j = 2; j < 1078; j += 2) for (int i = 2; i < 1918; i += 2)
                {
                    double v = g[j * 1920 + i];
                    bool nearGreen = g[j * 1920 + i - 2] > 0.8 || g[j * 1920 + i + 2] > 0.8 || g[(j - 2) * 1920 + i] > 0.8 || g[(j + 2) * 1920 + i] > 0.8;
                    bool nearPerson = g[j * 1920 + i - 2] < 0.2 || g[j * 1920 + i + 2] < 0.2 || g[(j - 2) * 1920 + i] < 0.2 || g[(j + 2) * 1920 + i] < 0.2;
                    if (nearGreen && nearPerson) { edge++; if (v > 0.25 && v < 0.75) mixed++; }
                }
                // Close-up of the top of the head (the first rows with person in the middle third).
                if (head == Rectangle.Empty)
                {
                    int top = 0; for (int j = 0; j < 1080 && top == 0; j++) for (int i = 640; i < 1280; i++) if (g[j * 1920 + i] < 0.2) { top = j; break; }
                    head = new Rectangle(660, Math.Max(0, top - 60), 600, 340);
                }
                using (var crop2 = img.Clone(head, img.PixelFormat)) using (var big = new Bitmap(crop2, head.Width * 2, head.Height * 2)) big.Save(Path.Combine(dir, (hq ? "high" : "normal") + "-head.png"));
                Console.WriteLine((hq ? "High VRAM" : "Normal   ") + ": " + (total / n).ToString("0.0") + " ms per frame, outline " + edge + " pixels, " + (100.0 * mixed / Math.Max(1, edge)).ToString("0") + "% of them half person / half green");
            }
        }
        Console.WriteLine("pictures in " + Path.GetFullPath(dir));
    }
}
