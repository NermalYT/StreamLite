// The hand-gesture models on a saved 4K NV12 frame (CPU): how long a look takes and what they see near each face.
// Usage: GestureProbe <frame.nv12>
using System;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Camera;

class GestureProbe
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int W = 3840, H = 2160;
        var raw = File.ReadAllBytes(args[0]);
        var y = new byte[W * H]; var uv = new byte[W * H / 2];
        Buffer.BlockCopy(raw, 0, y, 0, y.Length); Buffer.BlockCopy(raw, y.Length, uv, 0, uv.Length);
        // The faces, from the camera graph (CPU is fine for one frame).
        var s = CameraFx.OpenSession(W, H, false, false);
        var RS = CameraFx.StateSizes(s);
        var st = Enumerable.Range(0, 4).Select(i => new float[RS[i][0] * RS[i][1] * RS[i][2]]).ToArray();
        var so = Enumerable.Range(0, 4).Select(i => new float[RS[i][0] * RS[i][1] * RS[i][2]]).ToArray();
        var lut = Enumerable.Range(0, 256).Select(i => i / 255f).ToArray();
        var p = new float[24]; p[2] = 1; p[10] = p[11] = 1e-4f;
        var scores = new float[4420 * 2]; var boxes = new float[4420 * 4];
        var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
            new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] }, new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] } };
        s.Run(new Array[] { y, uv, new long[] { 0, 0, H, W }, p, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, lut, new byte[540 * 960 * 3], st[0], st[1], st[2], st[3] }, shapes,
              new Array[] { new byte[1920 * 1080], new byte[1920 * 540], new byte[360 * 640 * 4], new byte[90 * 160], scores, boxes, so[0], so[1], so[2], so[3] });
        s.Dispose();
        var faces = new FaceBox[8]; int n = Faces.Decode(scores, boxes, faces);
        Console.WriteLine(n + " faces");
        var g = new HandGestures();
        string seen = null; g.Gesture += x => seen = x;
        for (int f = 0; f < n; f++)
        {
            float total = 0; int looks = 0;
            for (int k = 0; k < 6; k++)
            {
                while (!g.Offer(y, uv, W, H, faces[f])) Thread.Sleep(5);
                Thread.Sleep(150);
                if (g.LookMs > 0) { total += g.LookMs; looks++; }
            }
            Console.WriteLine("  face " + (f + 1) + " at " + faces[f].Cx.ToString("0.00") + ", " + faces[f].Cy.ToString("0.00") + ": sees \"" + g.Seeing + "\", " + (looks > 0 ? (total / looks).ToString("0.0") : "-") + " ms per look" + (g.Error != null ? " - error " + g.Error : ""));
        }
        Console.WriteLine("gesture fired: " + (seen ?? "none"));
        g.Dispose();
    }
}
