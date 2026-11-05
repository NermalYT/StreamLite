// What the hand models see in a picture (a real hand, on this PC only: pictures of people stay out of the repo).
// Usage: PointProbe <picture.png> <faceCx 0..1> <faceCy 0..1> <faceH 0..1>
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Camera;

class PointProbe
{
    [STAThread]
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int W = 1920, H = 1080;
        var y = new byte[W * H]; var uv = new byte[W * H / 2];
        using (var src = new Bitmap(args[0]))
        using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Black); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, W, H); }
            var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var row = new byte[d.Stride * H];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, row, 0, row.Length);
            bmp.UnlockBits(d);
            for (int j = 0; j < H; j++) for (int i = 0; i < W; i++)
            {
                int o = j * d.Stride + i * 3; int b = row[o], g = row[o + 1], r = row[o + 2];
                y[j * W + i] = (byte)Math.Max(16, Math.Min(235, 16 + 0.183 * r + 0.614 * g + 0.062 * b));
                if ((j & 1) == 0 && (i & 1) == 0)
                {
                    int c = (j / 2) * W + i;
                    uv[c] = (byte)Math.Max(16, Math.Min(240, 128 - 0.101 * r - 0.339 * g + 0.439 * b));
                    uv[c + 1] = (byte)Math.Max(16, Math.Min(240, 128 + 0.439 * r - 0.399 * g - 0.040 * b));
                }
            }
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double cx = double.Parse(args[1], inv), cy = double.Parse(args[2], inv), fh = double.Parse(args[3], inv);
        var face = new FaceBox { X0 = cx - fh * 9 / 32, X1 = cx + fh * 9 / 32, Y0 = cy - fh / 2, Y1 = cy + fh / 2, Score = 1 };
        var h = new HandGestures { KeepLooks = true };
        for (int i = 0; i < 40 && h.LastWorld == null && h.Error == null; i++)
        {
            h.Offer(y, uv, W, H, face, i * 0.1);
            Thread.Sleep(250);
        }
        if (h.Error != null) Console.WriteLine("error: " + h.Error);
        if (h.LastWorld == null) { Console.WriteLine("no hand found"); h.Dispose(); return; }
        var w = h.LastWorld; var lm = h.LastLandmarks;
        Console.WriteLine("score " + h.LastScore.ToString("0.00") + ", from the picture alone: \"" + HandGestures.Classify(lm) + "\", with depth: \"" + HandGestures.Classify(lm, w) + "\"");
        foreach (var f in new[] { new[] { 5, 0 }, new[] { 9, 1 }, new[] { 13, 2 }, new[] { 17, 3 } })
            Console.WriteLine("  " + new[] { "index", "middle", "ring", "little" }[f[1]] + " straightness " + HandGestures.Straightness(w, f[0]).ToString("0.00"));
        Console.WriteLine("  index tip on the picture: " + (lm[24] / 224).ToString("0.00") + ", " + (lm[25] / 224).ToString("0.00") + " (of its crop)");
        h.Dispose();
    }
}
