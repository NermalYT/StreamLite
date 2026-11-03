// The hand-gesture pipeline (palm detector -> landmarks -> pose) on real photos of people's hands: HaGRID's test
// subset (D:\StreamLiteLab\data\hagrid, CC BY-SA, stays on this PC). Each photo goes through HandGestures.Offer as a
// camera frame, with a face box put just above the labelled hand (where a raised hand is next to your face), and the
// pose it reports is counted per sign: peace -> "v", point / one -> "point", palm -> "palm".
// Usage: GestureHagrid [images per sign] [hagrid folder]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using StreamLite.Audio;
using StreamLite.Camera;

class GestureHagrid
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int per = args.Length > 0 ? int.Parse(args[0]) : 200;
        string root = args.Length > 1 ? args[1] : @"D:\StreamLiteLab\data\hagrid";
        string imgRoot = Path.Combine(root, @"images_512_test_subset\HaGRIDv2_dataset_512");
        var signs = new[] { Tuple.Create("peace", "v"), Tuple.Create("point", "point"), Tuple.Create("one", "point"), Tuple.Create("palm", "palm") };
        var g = new HandGestures { Fast = true };
        string seen = null; var got = new AutoResetEvent(false);
        g.Tip += (x, y, pose, t) => { seen = pose; got.Set(); };
        double t0 = 0; int fails = 0;
        foreach (var s in signs)
        {
            // boxes.tsv: sign, image id, x, y, w, h (0..1) of that sign's hand - taken from HaGRID's annotations (18 MB
            // of JSON per sign, too slow for JavaScriptSerializer) by a few lines of Python.
            var counts = new Dictionary<string, int>(); int n = 0;
            foreach (var line in File.ReadLines(Path.Combine(root, "boxes.tsv")).Where(l => l.StartsWith(s.Item1 + "\t")).Take(per))
            {
                var f = line.Split('\t');
                string file = Path.Combine(imgRoot, s.Item1, f[1] + ".jpg");
                if (!File.Exists(file)) continue;
                var b = f.Skip(2).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                byte[] y, uv; int W, H;
                using (var bmp = new Bitmap(file)) ToNv12(bmp, out y, out uv, out W, out H);
                // A face of about the hand's height, just above it: the look's square (5.2 face heights, centred 0.6
                // below the face) then has the hand near its middle, as with a hand raised beside your face.
                double fh = Math.Max(b[2] * W / H, b[3]) * 0.9, fcx = b[0] + b[2] / 2, fcy = b[1] + b[3] / 2 - 0.6 * fh;
                var face = new FaceBox { X0 = fcx - fh * H / W / 2, X1 = fcx + fh * H / W / 2, Y0 = fcy - fh / 2, Y1 = fcy + fh / 2, Score = 1 };
                t0 += 10;   // far apart: every photo is a fresh search, not a follow of the last hand
                seen = null;
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!g.Offer(y, uv, W, H, face, t0) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(1);
                if (g.Error != null) { Console.WriteLine("ERROR " + g.Error); return; }
                if (!got.WaitOne(5000)) { fails++; continue; }
                string pose = seen == "" ? "(no hand / unclear)" : seen;
                int c; counts[pose] = counts.TryGetValue(pose, out c) ? c + 1 : 1; n++;
            }
            int hit; if (!counts.TryGetValue(s.Item2, out hit)) hit = 0;
            Console.WriteLine(s.Item1.PadRight(6) + " -> \"" + s.Item2 + "\" " + hit + " of " + n + " (" + (n > 0 ? 100.0 * hit / n : 0).ToString("0") + "%)   seen: " +
                string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
        }
        if (fails > 0) Console.WriteLine(fails + " looks timed out");
        if (g.Error != null) Console.WriteLine("ERROR " + g.Error);
        g.Dispose();
    }

    static void ToNv12(Bitmap src, out byte[] y, out byte[] uv, out int W, out int H)
    {
        W = src.Width & ~1; H = src.Height & ~1;
        y = new byte[W * H]; uv = new byte[W * H / 2];
        var d = src.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var row = new byte[d.Stride * H];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, row, 0, row.Length);
        src.UnlockBits(d);
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                int o = j * d.Stride + i * 3; int B = row[o], G = row[o + 1], R = row[o + 2];
                y[j * W + i] = (byte)Math.Min(255, Math.Max(0, ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16));
                if ((j & 1) == 0 && (i & 1) == 0)
                {
                    uv[(j / 2) * W + i] = (byte)Math.Min(255, Math.Max(0, ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128));
                    uv[(j / 2) * W + i + 1] = (byte)Math.Min(255, Math.Max(0, ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128));
                }
            }
    }
}
