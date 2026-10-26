// The AI camera's tracking and framing on your real webcam (no virtual camera, so a running StreamLite is untouched):
// every second what the director does, how fast the camera runs and what the hand-gesture models see, through a
// sequence of modes (tracking, group, framings, level, upside down, HDR, 9:16, whiteboard, desk view, privacy).
// "shots": also saves a preview per mode to test\shots\camlive (stays on this PC).
// Usage: CamTrackLive [seconds per mode] [shots]
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Camera;

class CamTrackLive
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        int per = args.Select(a => { int v; return int.TryParse(a, out v) ? v : 0; }).FirstOrDefault(v => v > 0);
        if (per == 0) per = 6;
        bool shots = args.Contains("shots");
        string dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\camlive"));
        if (shots) Directory.CreateDirectory(dir);
        var S = new AppSettings();
        S.CamFx.Enabled = false;          // no virtual camera: the loop is started by hand
        S.CamFx.Capture4K = true; S.CamFx.Gestures = true; S.CamFx.FaceTracking = true; S.CamFx.Framing = "half";
        S.CamFx.Repair();
        var fx = new CameraFx(S);
        byte[] last = null;
        fx.PreviewReady += f => { last = f; };
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CameraFx).GetMethod("StartPipeline", Any).Invoke(fx, null);
        var lastWanted = typeof(CameraFx).GetField("lastWanted", Any);
        var clock = (Stopwatch)typeof(CameraFx).GetField("clock", Any).GetValue(fx);
        var gField = typeof(CameraFx).GetField("gestures", Any);
        var c = S.CamFx;
        var steps = new Action[]
        {
            () => { c.FaceTracking = true; c.Framing = "half"; },
            () => { c.Framing = "head"; },
            () => { c.Framing = "body"; },
            () => { c.Framing = "half"; c.TrackMode = "group"; },
            () => { c.TrackMode = "single"; c.Level = 8; },
            () => { c.Level = 0; c.UpsideDown = true; },
            () => { c.UpsideDown = false; c.Hdr = 80; },
            () => { c.Hdr = 0; c.Vertical = true; },
            () => { c.Vertical = false; c.ViewMode = "whiteboard"; },
            () => { c.ViewMode = "desk"; },
            () => { c.ViewMode = "normal"; fx.Privacy = true; },
            () => { fx.Privacy = false; c.FaceTracking = false; c.Zoom = 200; c.ManualX = 0.4; c.ManualY = 0.45; },
        };
        string[] names = { "tracking, half body", "head and shoulders", "whole body", "group", "level +8", "upside down", "HDR 80%", "vertical 9:16", "whiteboard", "desk view", "privacy", "manual 2x" };
        double look = 0; int looks = 0;
        for (int k = 0; k < steps.Length; k++)
        {
            steps[k]();
            Console.WriteLine("== " + names[k]);
            for (int s = 0; s < per; s++)
            {
                for (int i = 0; i < 4; i++) { lastWanted.SetValue(fx, clock.ElapsedMilliseconds); Thread.Sleep(250); }
                var g = gField.GetValue(fx) as HandGestures;
                if (g != null && g.LookMs > 0) { look += g.LookMs; looks++; }
                Console.WriteLine(string.Format("  {0,2} fps {1,5:0.0} ms  faces {2}  {3}  hands: {4} ({5:0} ms){6}", fx.Fps.ToString("0"), fx.GpuMs, fx.Director.OutputFaces.Length,
                    fx.Director.State, g == null ? "off" : g.Seeing == "" ? "-" : g.Seeing, g == null ? 0 : g.LookMs, fx.Error != null ? "  ERROR " + fx.Error : ""));
                if (fx.Error != null) break;
            }
            if (shots && last != null) Save(last, Path.Combine(dir, k.ToString("00") + "-" + names[k].Replace(' ', '_').Replace(':', '-').Replace('+', 'p').Replace('%', 'p').Replace(",", "") + ".png"));
            if (fx.Error != null) break;
        }
        Console.WriteLine("camera " + fx.CameraName + " " + fx.CaptureW + "x" + fx.CaptureH + "; hand models: " + (looks > 0 ? (look / looks).ToString("0.0") + " ms per look" : "never ran (no face?)"));
        fx.Dispose();
    }

    static void Save(byte[] bgra, string path)
    {
        using (var b = new Bitmap(640, 360, PixelFormat.Format32bppRgb))
        {
            var d = b.LockBits(new Rectangle(0, 0, 640, 360), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(bgra, 0, d.Scan0, 640 * 360 * 4); b.UnlockBits(d);
            b.Save(path, ImageFormat.Png);
        }
    }
}
