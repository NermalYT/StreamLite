using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StreamLite.Camera;
using StreamLite.Interop;

// Lists Media Foundation cameras and reads one for a few seconds. Usage: CamReadTest [name-part] [w h fps] [seconds]
class CamReadTest
{
    static void Main(string[] args)
    {
        foreach (var c in MF.Cameras()) Console.WriteLine("camera: " + c.Item1 + "  " + c.Item2);
        string part = args.Length > 0 ? args[0] : "NexiGo";
        int w = args.Length > 3 ? int.Parse(args[1]) : 1920, h = args.Length > 3 ? int.Parse(args[2]) : 1080; double fps = args.Length > 3 ? double.Parse(args[3]) : 30;
        int secs = args.Length > 4 ? int.Parse(args[4]) : 3;
        var cam = MF.Cameras().First(c => c.Item1.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        var r = new CameraReader();
        double meanY = 0; long last = 0; double maxGap = 0;
        r.FrameArrived += (y, uv, t) => { long s = 0; for (int i = 0; i < y.Length; i += 97) s += y[i]; meanY = s / (y.Length / 97.0); if (last != 0) maxGap = Math.Max(maxGap, (t - last) / 10000.0); last = t; };
        var sw = Stopwatch.StartNew();
        if (!r.Open(cam.Item2, cam.Item1, w, h, fps)) { Console.WriteLine("open failed: " + r.Error); return; }
        Console.WriteLine("opened " + r.Width + "x" + r.Height + " @" + r.Fps.ToString("0.#") + " " + r.Format + " in " + sw.ElapsedMilliseconds + " ms");
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime; sw.Restart();
        Thread.Sleep(secs * 1000);
        int n = r.Frames; double el = sw.Elapsed.TotalSeconds;
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds / el * 100;
        Console.WriteLine("frames " + n + " in " + el.ToString("0.0") + " s = " + (n / el).ToString("0.0") + " fps, max gap " + maxGap.ToString("0") + " ms, mean luma " + meanY.ToString("0") + ", CPU " + cpu.ToString("0") + "% of one core" + (r.Error != null ? ", error " + r.Error : ""));
        if (args.Length > 5)
        {
            byte[] ly = null, luv = null;
            r.FrameArrived += (yy, uu, t) => { if (ly == null) { ly = (byte[])yy.Clone(); luv = (byte[])uu.Clone(); } };
            for (int i = 0; i < 100 && ly == null; i++) Thread.Sleep(20);
            if (ly != null) { System.IO.File.WriteAllBytes(args[5], ly.Concat(luv).ToArray()); Console.WriteLine("saved " + args[5] + " " + r.Width + "x" + r.Height); }
        }
        r.Dispose();
        Console.WriteLine("closed");
    }
}
