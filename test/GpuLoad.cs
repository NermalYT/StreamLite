// Keeps the graphics card busy (the 4K camera graph in a tight loop, several at once) for some seconds - a stand-in
// for a game, to test DeepFilterNet on the GPU under contention. Usage: GpuLoad [seconds] [sessions]
using System;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Camera;

class GpuLoad
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 30, n = args.Length > 1 ? int.Parse(args[1]) : 3;
        var until = DateTime.Now.AddSeconds(seconds); int runs = 0;
        var threads = Enumerable.Range(0, n).Select(_ => new Thread(() =>
        {
            int W = 3840, H = 2160; var RS = CameraFx.RS;
            using (var s = new OnnxSession(Path.Combine(OnnxSession.Folder, "camfx_2160.onnx"), true, 1, fuse: false))
            {
                var y = new byte[W * H]; var uv = new byte[W * H / 2]; var st = new float[4][]; var so = new float[4][];
                for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; so[i] = new float[st[i].Length]; }
                var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
                    new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] }, new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] } };
                var outs = new Array[] { new byte[1920 * 1080], new byte[1920 * 540], new byte[360 * 640 * 4], new byte[90 * 160], new float[8840 * 2], new float[8840 * 4], so[0], so[1], so[2], so[3] };
                var ins = new Array[] { y, uv, new long[] { 0, 0, H, W }, new float[24], new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, new float[256], new byte[540 * 960 * 3], st[0], st[1], st[2], st[3] };
                while (DateTime.Now < until) { s.Run(ins, shapes, outs); Interlocked.Increment(ref runs); }
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Console.WriteLine("GPU load: " + runs + " graph runs in " + seconds + " s");
    }
}
