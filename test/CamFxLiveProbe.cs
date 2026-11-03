// The AI camera's real effects loop (CameraFx) on your real webcam, without the virtual camera (so a running
// StreamLite is untouched) and without saving any frame: capture size, fps, GPU time, and this process's video memory
// together with DeepFilterNet. "cpu": effects on the CPU (too slow there: the camera should play without effects).
// "switch" (4.1.5): the loop changing models while it runs - High VRAM, background blur -> background off (the model
// without the cut-out) -> blur again with a game running (the standard cut-out) - fps and GPU time in each phase.
// Usage: CamFxLiveProbe [low|cpu|switch] [seconds]
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

    static void Switch(AppSettings S, CameraFx fx)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;
        S.AiTier = AiTiers.High;
        bool game = false; fx.GameRunning = () => game;
        int frames = 0; fx.PreviewReady += f => Interlocked.Increment(ref frames);
        typeof(CameraFx).GetMethod("StartPipeline", Any).Invoke(fx, null);
        var lastWanted = typeof(CameraFx).GetField("lastWanted", Any);
        var clock = (Stopwatch)typeof(CameraFx).GetField("clock", Any).GetValue(fx);
        foreach (var phase in new[] { Tuple.Create("High VRAM, background blur, no game", "blur", false), Tuple.Create("background off", "off", false),
                                      Tuple.Create("background blur, a game running", "blur", true), Tuple.Create("background off, a game running", "off", true),
                                      Tuple.Create("1080p capture, background off", "off", false) })
        {
            S.CamFx.Background = phase.Item2; game = phase.Item3;
            if (phase.Item1.StartsWith("1080p")) { S.CamFx.Capture4K = false; fx.Restart(); typeof(CameraFx).GetMethod("StartPipeline", Any).Invoke(fx, null); }
            var fpsList = new System.Collections.Generic.List<float>(); var msList = new System.Collections.Generic.List<float>();
            int f0 = frames; var t = Stopwatch.StartNew();
            while (t.Elapsed.TotalSeconds < 8)
            {
                lastWanted.SetValue(fx, clock.ElapsedMilliseconds); Thread.Sleep(250);
                if (t.Elapsed.TotalSeconds > 3 && fx.Fps > 0) { fpsList.Add(fx.Fps); msList.Add(fx.GpuMs); }   // after the model swap settles
                if (fx.Error != null) break;
            }
            Console.WriteLine("  " + phase.Item1.PadRight(38) + (fpsList.Count > 0 ? fpsList.Average().ToString("0.0") : "-") + " fps, " +
                (msList.Count > 0 ? msList.Average().ToString("0.0") : "-") + " ms per frame (" + (frames - f0) + " frames in 8 s, " + fx.CaptureW + "x" + fx.CaptureH + ")" + (fx.Error != null ? "  ERROR " + fx.Error : ""));
        }
    }

    static void Main(string[] args)
    {
        // Its health.log notes go to a folder of its own, not your StreamLite's.
        string data = Path.Combine(Path.GetTempPath(), "streamlite-camprobe"); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool low = args.Contains("low"), cpu = args.Contains("cpu");
        int seconds = args.Select(a => { int v; return int.TryParse(a, out v) ? v : 0; }).FirstOrDefault(v => v > 0);
        if (seconds == 0) seconds = 15;
        var S = new AppSettings();
        S.LowVram = low;
        S.CamFx.Enabled = false;          // no virtual camera: the loop below is started by hand
        S.CamFx.Capture4K = true;         // Low VRAM must still capture 1080p
        S.CamFx.Background = "blur";
        S.CamFx.FaceTracking = true;
        S.CamFx.Gpu = !cpu;

        var dfn = new DeepFilter(true, low);
        while (!dfn.Ready && dfn.Error == null) Thread.Sleep(10);
        var x = new float[480]; var y = new float[480];
        for (int i = 0; i < 100; i++) { dfn.Process(x, y); dfn.WaitIdle(1000); }

        var fx = new CameraFx(S);
        if (args.Contains("switch")) { Switch(S, fx); fx.Dispose(); dfn.Dispose(); return; }
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
            ", " + frames + " frames in " + seconds + " s, " + fps.ToString("0") + " fps, " + ms.ToString("0.0") + " ms per frame on the " + (cpu ? "CPU" : "GPU") + (fx.PlainWhy != null ? " - no effects: " + fx.PlainWhy : "") + (fx.Error != null ? " - error: " + fx.Error : ""));
        Console.WriteLine("DeepFilterNet " + (dfn.LowVram ? "Low VRAM model" : "normal model") + "; video memory of this process (DeepFilterNet + camera), highest: " + peak + " MB");
        fx.Dispose();
        dfn.Dispose();
    }
}
