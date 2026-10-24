// The AI camera's real effects loop (CameraFx) on your real webcam, without the virtual camera (so a running
// StreamLite is untouched) and without saving any frame: capture size, fps, GPU time, and this process's video memory
// together with DeepFilterNet. Usage: CamFxLiveProbe [low] [seconds]
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

class CamFxLiveProbe
{
    static long VideoMemory()
    {
        long ded = 0;
        var cat = new PerformanceCounterCategory("GPU Process Memory");
        string tag = "pid_" + Process.GetCurrentProcess().Id + "_";
        foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
            using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
        return ded / 1048576;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool low = args.Contains("low");
        int seconds = args.Select(a => { int v; return int.TryParse(a, out v) ? v : 0; }).FirstOrDefault(v => v > 0);
        if (seconds == 0) seconds = 15;
        var S = new AppSettings();
        S.LowVram = low;
        S.CamFx.Enabled = false;          // no virtual camera: the loop below is started by hand
        S.CamFx.Capture4K = true;         // Low VRAM must still capture 1080p
        S.CamFx.Background = "blur";
        S.CamFx.FaceTracking = true;

        var dfn = new DeepFilter(true, low);
        while (!dfn.Ready && dfn.Error == null) Thread.Sleep(10);
        var x = new float[480]; var y = new float[480];
        for (int i = 0; i < 100; i++) { dfn.Process(x, y); dfn.WaitIdle(1000); }

        var fx = new CameraFx(S);
        int frames = 0;
        fx.PreviewReady += f => Interlocked.Increment(ref frames);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CameraFx).GetMethod("StartPipeline", Any).Invoke(fx, null);
        // The camera's monitor stops a pipeline nobody wants after 4 s; keep telling it we do.
        var lastWanted = typeof(CameraFx).GetField("lastWanted", Any);
        var clock = (Stopwatch)typeof(CameraFx).GetField("clock", Any).GetValue(fx);
        long peak = 0; int w = 0, h = 0; float fps = 0, ms = 0;
        var t = Stopwatch.StartNew();
        while (t.Elapsed.TotalSeconds < seconds)
        {
            for (int i = 0; i < 4; i++) { lastWanted.SetValue(fx, clock.ElapsedMilliseconds); Thread.Sleep(250); }
            peak = Math.Max(peak, VideoMemory());
            w = fx.CaptureW; h = fx.CaptureH; fps = fx.Fps; ms = fx.GpuMs;
            if (fx.Error != null) break;
        }
        Console.WriteLine((low ? "Low VRAM" : "Normal") + ": camera " + fx.CameraName + " captured " + w + "x" + h +
            ", " + frames + " frames in " + seconds + " s, " + fps.ToString("0") + " fps, " + ms.ToString("0.0") + " ms per frame on the GPU" + (fx.Error != null ? " - error: " + fx.Error : ""));
        Console.WriteLine("DeepFilterNet " + (dfn.LowVram ? "Low VRAM model" : "normal model") + "; video memory of this process (DeepFilterNet + camera), highest: " + peak + " MB");
        fx.Dispose();
        dfn.Dispose();
    }
}
