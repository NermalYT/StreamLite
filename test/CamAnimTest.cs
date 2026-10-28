// 4.1 camera animations and camera hotkeys, offline (no camera): each animation starts and ends exactly where the
// picture was (the flip ends turned round), moves smoothly frame to frame, and the picture warp is exact; the flip's
// last frame matches the upside-down view it hands over to; a contact sheet (test\shots\camanim.png); every camera
// hotkey runs, reports and toggles back, and none of them touches AI tracking unless it's the tracking one.
// Usage: run.ps1 CamAnimTest
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Camera;

class CamAnimTest
{
    sealed class Inline : SynchronizationContext { public override void Post(SendOrPostCallback d, object s) { d(s); } }
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int W = 1920, H = 1080;

    static void Main()
    {
        string shots = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots"));
        Directory.CreateDirectory(shots);
        Curves();
        byte[] y, uv; Frame(out y, out uv);
        Warps(y, uv);
        FlipHandOver();
        Sheet(y, uv, Path.Combine(shots, "camanim.png"));
        NotRunning();
        Hotkeys();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    static void Frame(out byte[] y, out byte[] uv)
    {
        string frame = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\camframes\person4k.nv12"));
        y = new byte[W * H]; uv = new byte[W * H / 2];
        if (File.Exists(frame))
        {
            var raw = File.ReadAllBytes(frame);
            var y4 = new byte[3840 * 2160]; var uv4 = new byte[3840 * 1080];
            Buffer.BlockCopy(raw, 0, y4, 0, y4.Length); Buffer.BlockCopy(raw, y4.Length, uv4, 0, uv4.Length);
            Nv12.Resize(y4, uv4, 3840, 2160, y, uv, W, H);
        }
        else
        {
            Console.WriteLine("  (no test\\camframes\\person4k.nv12: a made-up picture)");
            for (int i = 0; i < y.Length; i++) y[i] = (byte)(16 + (i % W) * 200 / W + (i / W) * 19 / H);
            for (int i = 0; i < uv.Length; i++) uv[i] = (byte)(64 + (i % 256) / 2);
        }
    }

    static void Curves()
    {
        Console.WriteLine("Animations (eased, per frame):");
        foreach (var k in CameraAnimation.Kinds)
        {
            var a = CameraAnimation.At(k, 0); var b = CameraAnimation.At(k, 1);
            bool startId = a.IsIdentity;
            bool endOk = k == "flip" ? b.Angle == 180 && b.Scale == 1 && b.Dx == 0 && b.Dy == 0 : b.IsIdentity;
            // Smooth: at 30 fps no frame jumps, and just after the start / before the end it's still almost where it was.
            double dur = CameraAnimation.Duration(k), step = 1 / (30 * dur);
            double maxAng = 0, maxScale = 0, maxMove = 0, peak = 0;
            var prev = a;
            for (double p = step; p <= 1 + 1e-9; p += step)
            {
                var m = CameraAnimation.At(k, Math.Min(1, p));
                maxAng = Math.Max(maxAng, Math.Abs(m.Angle - prev.Angle)); maxScale = Math.Max(maxScale, Math.Abs(m.Scale - prev.Scale));
                maxMove = Math.Max(maxMove, Math.Max(Math.Abs(m.Dx - prev.Dx), Math.Abs(m.Dy - prev.Dy)));
                peak = Math.Max(peak, Math.Max(Math.Abs(m.Angle) / 90, Math.Max(Math.Abs(m.Scale - 1) * 4, Math.Max(Math.Abs(m.Dx), Math.Abs(m.Dy)) * 20)));
                prev = m;
            }
            var s0 = CameraAnimation.At(k, 0.004); var s1 = CameraAnimation.At(k, 0.996);
            double endAngle = k == "flip" ? 180 : 0, off = ((s1.Angle - endAngle) % 360 + 540) % 360 - 180;
            bool gentle = Math.Abs(s0.Angle) < 1 && Math.Abs(s0.Scale - 1) < 0.02 && Math.Abs(s0.Dx) < 0.005 && Math.Abs(s0.Dy) < 0.005
                && Math.Abs(off) < 1 && Math.Abs(s1.Scale - 1) < 0.02 && Math.Abs(s1.Dx) < 0.005 && Math.Abs(s1.Dy) < 0.005;
            bool smooth = maxAng <= 40 && maxScale <= 0.12 && maxMove <= 0.03;
            Check(startId && endOk && gentle && smooth && peak > 0.15 && dur >= 0.5 && dur <= 1.2,
                CameraAnimation.Label(k) + " (" + dur.ToString("0.0") + " s): starts as is, ends " + (k == "flip" ? "turned 180 degrees" : "exactly where it started") +
                "; at most " + maxAng.ToString("0.0") + " degrees / " + (maxScale * 100).ToString("0.0") + "% zoom / " + (maxMove * 100).ToString("0.0") + "% move a frame");
        }
        Check(CameraAnimation.Duration("spin") >= 0.8 && CameraAnimation.Duration("spin") <= 1.2 && Math.Abs(CameraAnimation.At("spin", 0.5).Angle - 180) < 1e-9, "spin: a full turn in about a second (halfway = 180 degrees)");
    }

    static void Warps(byte[] y0, byte[] uv0)
    {
        Console.WriteLine("The picture warp:");
        var ty = new byte[W * H]; var tuv = new byte[W * H / 2];
        var y = (byte[])y0.Clone(); var uv = (byte[])uv0.Clone();
        CameraAnimation.Warp(y, uv, W, H, CameraAnimation.At("spin", 1), ty, tuv);
        Check(y.SequenceEqual(y0) && uv.SequenceEqual(uv0), "the end of a spin is the picture, untouched");
        CameraAnimation.Warp(y, uv, W, H, CameraAnimation.At("flip", 1), ty, tuv);
        bool exact = true;
        for (int r = 0; r < H && exact; r++) for (int x = 0; x < W; x++) if (y[r * W + x] != y0[(H - 1 - r) * W + (W - 1 - x)]) { exact = false; break; }
        for (int r = 0; r < H / 2 && exact; r++) for (int x = 0; x < W / 2; x++)
                if (uv[r * W + x * 2] != uv0[(H / 2 - 1 - r) * W + (W / 2 - 1 - x) * 2] || uv[r * W + x * 2 + 1] != uv0[(H / 2 - 1 - r) * W + (W / 2 - 1 - x) * 2 + 1]) { exact = false; break; }
        Check(exact, "the end of a flip is the picture turned round, pixel for pixel");
        y = (byte[])y0.Clone(); uv = (byte[])uv0.Clone();
        var sw = Stopwatch.StartNew();
        CameraAnimation.Warp(y, uv, W, H, CameraAnimation.At("spin", 0.5), ty, tuv);
        double ms = sw.Elapsed.TotalMilliseconds;
        // The median of 11 frames, the copies made outside the clock (one slow frame on a busy CI runner isn't the warp).
        var times = new List<double>();
        for (int i = 0; i < 11; i++) { var yy = (byte[])y0.Clone(); var uu = (byte[])uv0.Clone(); sw.Restart(); CameraAnimation.Warp(yy, uu, W, H, CameraAnimation.At("roll", 0.3), ty, tuv); times.Add(sw.Elapsed.TotalMilliseconds); }
        times.Sort(); double avg = times[times.Count / 2];
        // Here: well inside a 30 fps frame. On CI's shared runners: at least keeping up with 30 fps.
        double limit = Environment.GetEnvironmentVariable("CI") == "true" ? 33 : 20;
        int cy = H / 2, cx = W / 2;
        bool corners = y[0] == 16 && y[W - 1] == 16 && y[(H - 1) * W] == 16 && uv[0] == 128 && uv[1] == 128;
        bool centre = Math.Abs(y[cy * W + cx] - y0[(cy - 1) * W + (cx - 1)]) <= 12;
        Check(corners && centre, "half a spin: the picture upside down and smaller, black around it");
        Check(avg < limit, "one animated frame (1080p, CPU): " + avg.ToString("0.0") + " ms median, under " + limit + " (first " + ms.ToString("0.0") + " ms)");
    }

    /// <summary>The flip's last frame (the picture turned round) is exactly what the upside-down view shows next.</summary>
    static void FlipHandOver()
    {
        Console.WriteLine("Flip -> upside down:");
        foreach (int zoom in new[] { 100, 180 })
        {
            var c = new CamFxSettings { Zoom = zoom, ManualX = 0.4, ManualY = 0.55 }; c.Repair();
            var d = new CameraDirector();
            var crop = new long[4]; var warp = new float[9];
            for (int i = 0; i < 5; i++) d.Step(c, new FaceBox[0], 0, 3840, 2160, crop, warp);
            var before = (double[])d.OutToCam.Clone();
            c.UpsideDown = !c.UpsideDown;
            d.Step(c, new FaceBox[0], 0, 3840, 2160, crop, warp);
            var after = d.OutToCam;
            double worst = 0;
            foreach (var p in new[] { new[] { 0.1, 0.1 }, new[] { 0.5, 0.5 }, new[] { 0.9, 0.3 }, new[] { 0.25, 0.8 } })
            {
                double ax, ay, bx, by;
                Homography.Apply(before, 1 - p[0], 1 - p[1], out ax, out ay);
                Homography.Apply(after, p[0], p[1], out bx, out by);
                worst = Math.Max(worst, Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)));
            }
            Check(worst < 0.5, "zoom " + zoom + "%: the next frame after the flip is the same picture (" + worst.ToString("0.000") + " px apart) - turned at once, no glide through the middle");
        }
    }

    static void Sheet(byte[] y0, byte[] uv0, string path)
    {
        // One row per animation, five moments each (start, 1/4, 1/2, 3/4, end), 384 x 216 thumbnails.
        var ps = new[] { 0, 0.25, 0.5, 0.75, 1 };
        int tw = 384, th = 216;
        var ty = new byte[W * H]; var tuv = new byte[W * H / 2];
        using (var bmp = new Bitmap(tw * ps.Length, th * CameraAnimation.Kinds.Length, PixelFormat.Format32bppRgb))
        {
            var px = new byte[bmp.Width * bmp.Height * 4];
            for (int row = 0; row < CameraAnimation.Kinds.Length; row++)
                for (int col = 0; col < ps.Length; col++)
                {
                    var y = (byte[])y0.Clone(); var uv = (byte[])uv0.Clone();
                    CameraAnimation.Warp(y, uv, W, H, CameraAnimation.At(CameraAnimation.Kinds[row], ps[col]), ty, tuv);
                    for (int r = 0; r < th; r++)
                        for (int x = 0; x < tw; x++)
                        {
                            int sx = x * 5, sy = r * 5;
                            double Y = 1.164 * (y[sy * W + sx] - 16), U = uv[(sy / 2) * W + (sx & ~1)] - 128, V = uv[(sy / 2) * W + (sx & ~1) + 1] - 128;
                            int o = ((row * th + r) * bmp.Width + col * tw + x) * 4;
                            px[o] = Cl(Y + 2.112 * U); px[o + 1] = Cl(Y - 0.213 * U - 0.533 * V); px[o + 2] = Cl(Y + 1.793 * V); px[o + 3] = 255;
                        }
                }
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            bmp.UnlockBits(d);
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("  saved " + path);
    }

    static byte Cl(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5); }

    /// <summary>Animations asked for while the camera isn't running (it's never started here: no camera is opened).</summary>
    static void NotRunning()
    {
        Console.WriteLine("Camera not running:");
        var S = new AppSettings(); S.CamFx.Enabled = false; S.CamFx.Repair();   // Enabled off: the camera and the virtual camera are never started
        var fx = new CameraFx(S);
        bool spin = fx.Play("spin"), bogus = fx.Play("moonwalk");
        bool up0 = S.CamFx.UpsideDown;
        bool flip = fx.Play("flip");
        bool up1 = S.CamFx.UpsideDown;
        fx.Play("flip");
        Check(!fx.Running && !spin && !bogus && !flip && up1 == !up0 && S.CamFx.UpsideDown == up0, "nothing plays; a flip just turns the picture round (and back)");
        fx.Dispose();
    }

    static void Hotkeys()
    {
        Console.WriteLine("Camera hotkeys:");
        string dir = Path.Combine(Path.GetTempPath(), "sl_camanimtest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        SynchronizationContext.SetSynchronizationContext(new Inline());
        var core = new AppCore(true);   // settings only: no camera, no devices
        var S = core.S;
        var ids = new[]
        {
            "cam.bg", "cam.track", "cam.privacy", "cam.preset", "cam.view", "cam.zoom", "cam.light", "cam.mirror",
            "cam.framing", "cam.group", "cam.upsidedown", "cam.hdr", "cam.faceexp", "cam.vertical", "cam.autoprivacy", "cam.zone", "cam.4k", "cam.on",
            "cam.gesture.palm", "cam.gesture.v", "cam.gesture.l", "cam.gesture.point", "cam.draw", "cam.draw.clear",
            "cam.anim.flip", "cam.anim.spin", "cam.anim.roll", "cam.anim.punch", "cam.anim.shake",
        };
        var missing = ids.Where(i => HotkeyActions.Find(i) == null || HotkeyActions.Find(i).Group != "Camera").ToList();
        var cam = HotkeyActions.All.Where(a => a.Id.StartsWith("cam.")).ToList();
        bool unique = HotkeyActions.All.Select(a => a.Id).Distinct().Count() == HotkeyActions.All.Count;
        Check(missing.Count == 0 && unique && cam.All(a => a.Group == "Camera"), cam.Count + " camera actions, all in the Camera group, ids unique" + (missing.Count > 0 ? " - missing " + string.Join(", ", missing) : ""));
        var longest = cam.OrderByDescending(a => a.Label.Length).First();
        Check(longest.Label.Length <= 32, "labels short (longest: \"" + longest.Label + "\")");

        var js = new JavaScriptSerializer();
        S.CamFx.FaceTracking = false;
        var toggles = new[] { "cam.gesture.palm", "cam.gesture.v", "cam.gesture.l", "cam.gesture.point", "cam.upsidedown", "cam.hdr", "cam.faceexp", "cam.vertical", "cam.autoprivacy", "cam.group", "cam.4k", "cam.on", "cam.light", "cam.mirror" };
        foreach (var id in toggles)
        {
            var a = HotkeyActions.Find(id);
            string fp1 = js.Serialize(S);
            string n1 = a.Run(core, null, true); string mid = js.Serialize(S); string n2 = a.Run(core, null, true);
            string fp2 = js.Serialize(S);
            Check(n1 != null && n2 != null && n1 != n2 && fp1 == fp2 && mid != fp1 && !S.CamFx.FaceTracking, a.Label + ": \"" + n1 + "\", then \"" + n2 + "\"");
        }
        var palm = HotkeyActions.Find("cam.gesture.palm");
        bool on = S.CamFx.GesturePalm;
        palm.Run(core, null, true);
        Check(S.CamFx.GesturePalm != on && !S.CamFx.FaceTracking, "switching the palm gesture on or off never switches AI tracking");
        palm.Run(core, null, true);

        var fr = HotkeyActions.Find("cam.framing");
        string f1 = fr.Run(core, "next", true), f2 = fr.Run(core, "next", true), f3 = fr.Run(core, "body", true);
        Check(f1 != f2 && S.CamFx.Framing == "body" && f3.StartsWith("Framing: Whole body") && fr.Args(core).Count == 5, "framing: next \"" + f1 + "\", \"" + f2 + "\"; pick \"" + f3 + "\"");
        var zone = HotkeyActions.Find("cam.zone");
        S.CamFx.TrackZone = null; S.CamFx.TrackZoneOn = false;
        string z0 = zone.Run(core, null, true);
        S.CamFx.TrackZone = new[] { 0.2, 0.1, 0.8, 0.9 };
        string z1 = zone.Run(core, null, true), z2 = zone.Run(core, null, true);
        Check(z0.StartsWith("No tracking zone") && z1 == "Tracking zone on" && z2 == "Tracking zone off", "tracking zone: \"" + z0 + "\"; \"" + z1 + "\", \"" + z2 + "\"");
        int hdr = S.CamFx.Hdr; S.CamFx.Hdr = 35;
        var h = HotkeyActions.Find("cam.hdr");
        string h1 = h.Run(core, null, true), h2 = h.Run(core, null, true);
        Check(h1 == "HDR off" && h2 == "HDR on (35%)" && S.CamFx.Hdr == 35, "HDR off, and back at the strength it had: \"" + h1 + "\", \"" + h2 + "\"");
        S.CamFx.Hdr = hdr;
        // Without a camera (settings-only core): the camera-only actions do nothing and say nothing, without failing.
        bool quiet = true;
        foreach (var id in new[] { "cam.draw", "cam.draw.clear", "cam.anim.flip", "cam.anim.spin", "cam.anim.roll", "cam.anim.punch", "cam.anim.shake" })
            try { quiet &= HotkeyActions.Find(id).Run(core, null, true) == null; } catch { quiet = false; }
        Check(quiet && !S.CamFx.FaceTracking, "air drawing and animations without a camera: nothing happens, nothing breaks");
    }
}
