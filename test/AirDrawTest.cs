// 4.1 gestures and air drawing, offline (no camera): hand poses from synthetic landmarks (point / V / palm / L / fist),
// holding and re-firing signs, each gesture's own switch, gestures never turning tracking on, the fingertip's
// smoothing, lines joined up between sparse looks, a "Hi!" written onto a saved camera frame (test\shots\airdraw.png),
// lines right under the fingertip with mirror / upside down, fading, clearing, the settings migration, and how fast
// the hand models follow a hand on the CPU (needs the ai folder). Usage: run.ps1 AirDrawTest
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

class AirDrawTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int W = 1920, H = 1080;

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string shots = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots"));
        Directory.CreateDirectory(shots);
        Poses();
        Deciding();
        Routing();
        Smoothing();
        Joining();
        Hi(shots);
        Placement();
        FadeAndClear();
        Migration();
        Following();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    // ------------------------------------------------------------------ synthetic hands

    /// <summary>21 landmarks (x, y, z) of a hand: fingers index..little "1" out / "0" curled, thumb out / in / up,
    /// spread "v" = index and middle apart; turned by deg around the wrist, scaled, moved.</summary>
    static float[] Hand(string fingers, string thumb, string spread, double deg = 0, double scale = 1, double ox = 0, double oy = 0)
    {
        var p = new float[63];
        Action<int, double, double> s = (i, x, y) => { p[i * 3] = (float)x; p[i * 3 + 1] = (float)y; };
        s(0, 100, 200);
        double[][] mcp = { new[] { 85.0, 130 }, new[] { 100.0, 125 }, new[] { 115.0, 130 }, new[] { 128.0, 140 } };
        for (int k = 0; k < 4; k++)
        {
            int b = 5 + k * 4; double mx = mcp[k][0], my = mcp[k][1];
            s(b, mx, my);
            bool up = fingers[k] == '1';
            double lean = spread == "v" && k == 0 ? -0.35 : spread == "v" && k == 1 ? 0.2 : 0;
            if (up) { s(b + 1, mx + lean * 25, my - 25); s(b + 2, mx + lean * 45, my - 45); s(b + 3, mx + lean * 62, my - 62); }
            else { s(b + 1, mx, my - 20); s(b + 2, mx + 4, my - 4); s(b + 3, mx + 3, my + 10); }
        }
        if (thumb == "out") { s(1, 78, 185); s(2, 62, 168); s(3, 40, 165); s(4, 20, 165); }
        else if (thumb == "up") { s(1, 80, 185); s(2, 75, 165); s(3, 72, 140); s(4, 70, 120); }
        else { s(1, 80, 185); s(2, 72, 170); s(3, 78, 158); s(4, 88, 150); }
        double a = deg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        for (int i = 0; i < 21; i++)
        {
            double x = p[i * 3] - 100, y = p[i * 3 + 1] - 200;
            p[i * 3] = (float)(ox + 100 + (x * ca - y * sa) * scale); p[i * 3 + 1] = (float)(oy + 200 + (x * sa + y * ca) * scale);
        }
        return p;
    }

    static void Poses()
    {
        Console.WriteLine("Hand poses (synthetic landmarks):");
        var cases = new[]
        {
            new[] { "1000", "in", "", "point" }, new[] { "1000", "up", "", "point" }, new[] { "1100", "in", "v", "v" },
            new[] { "1111", "out", "", "palm" }, new[] { "1000", "out", "", "l" }, new[] { "0000", "in", "", "fist" }, new[] { "0000", "out", "", "fist" },
        };
        int n = 0, right = 0; string wrong = "";
        foreach (var k in cases)
            foreach (double deg in new[] { 0.0, 35, -60, 150 })
                foreach (double sc in new[] { 0.4, 1, 2.5 })
                {
                    string got = HandGestures.Classify(Hand(k[0], k[1], k[2], deg, sc, 300, 200));
                    n++; if (got == k[3]) right++; else wrong += " " + k[3] + "@" + deg + "->" + got;
                }
        Check(right == n, "point, V, palm, L and fist told apart at any turn and size (" + right + "/" + n + ")" + wrong);
        Check(HandGestures.Classify(Hand("1100", "in", "")) == "" && HandGestures.Classify(Hand("1111", "in", "")) == "",
            "two fingers held together, or four out with the thumb tucked: no pose");
    }

    // ------------------------------------------------------------------ holding a sign

    static void Deciding()
    {
        Console.WriteLine("Holding a sign:");
        var g = new HandGestures();   // Decide is called directly here; its worker just waits
        var fired = new List<string>(); double zoom = 1;
        g.Gesture += s => fired.Add(s); g.Zoom += f => zoom *= f;
        double t = 0, dt = 0.125;
        Action<string, double> show = (pose, secs) => { for (double e = 0; e < secs - 1e-9; e += dt) { g.Decide(pose, 0.5, 0.2, t); t += dt; } };
        show("palm", 0.25);
        Check(fired.Count == 0, "a palm shown for a quarter of a second: nothing yet");
        show("palm", 0.4);
        Check(fired.Count == 1 && fired[0] == "palm", "held for " + HandGestures.HoldSeconds + " s: fires once");
        show("palm", 1.5); show("", 0.125); show("palm", 1.0); show("", 0.25); show("palm", 1.0);
        Check(fired.Count == 1, "held on, with missed looks in between: still once (no flip-flopping tracking)");
        show("", 1.0); show("palm", 0.6);
        Check(fired.Count == 2, "shown again after a second away: fires again");
        show("point", 3); show("fist", 3); show("", 0.5);
        Check(fired.Count == 2, "pointing and a fist never fire anything");
        dt = 0.04;   // air drawing: 25 looks a second
        show("v", 0.32); show("point", 1);
        Check(fired.Count == 2, "at 25 looks a second a V passing for 0.3 s doesn't count");
        show("v", 0.6);
        Check(fired.Count == 3 && fired[2] == "v", "a V held: fires");
        dt = 0.125;
        show("", 1);
        for (int i = 0; i < 12; i++) { g.Decide("l", 0.6 - i * 0.03, 0.2, t); t += dt; }
        Check(zoom > 1.4, "an L held and moved up zooms in (" + zoom.ToString("0.00") + "x)");
        g.Dispose();
    }

    // ------------------------------------------------------------------ each sign its own switch, tracking left alone

    static void Routing()
    {
        Console.WriteLine("Each gesture's own switch:");
        var c = new CamFxSettings(); c.Repair(); c.Zoom = 100;
        var d = new CameraDirector(); var ink = new AirDrawing();
        var crop = new long[4]; var warp = new float[9]; var none = new FaceBox[0];
        Action step = () => d.Step(c, none, 0, W, H, crop, warp);
        step();
        bool ok = true; string why = "";
        foreach (var sign in new[] { "palm", "v", "l", "point", "fist" })
            foreach (bool on in new[] { false, true })
                foreach (string vdoes in new[] { "whiteboard", "draw" })
                {
                    c.GesturePalm = c.GestureV = c.GestureL = c.GesturePoint = on; c.VAction = vdoes;
                    bool before = c.FaceTracking;
                    CameraFx.RouteGesture(c, sign, d, ink);
                    if (sign == "l") CameraFx.RouteZoom(c, 1.5, d);
                    step();
                    bool changed = c.FaceTracking != before, want = sign == "palm" && on;
                    if (changed != want) { ok = false; why += " " + sign + (on ? "/on" : "/off"); }
                    c.FaceTracking = false; c.ViewMode = "normal"; c.Zoom = 100; ink.SetOn(false); step();
                }
        Check(ok, "only the open palm, with its own switch on, changes AI tracking - V, L, pointing and a fist never do" + why);

        c.GesturePalm = c.GestureV = c.GestureL = c.GesturePoint = true; c.VAction = "whiteboard";
        c.GestureV = false; CameraFx.RouteGesture(c, "v", d, ink); step();
        bool vOff = c.ViewMode == "normal" && !ink.On;
        c.GestureV = true; CameraFx.RouteGesture(c, "v", d, ink); step();
        bool vBoard = c.ViewMode == "whiteboard" && !ink.On;
        CameraFx.RouteGesture(c, "v", d, ink); step();
        c.VAction = "draw"; CameraFx.RouteGesture(c, "v", d, ink); step();
        bool vDraw = c.ViewMode == "normal" && ink.On;
        c.VAction = "whiteboard"; CameraFx.RouteGesture(c, "v", d, ink); step();
        bool vStops = !ink.On && c.ViewMode == "normal";
        Check(vOff && vBoard && vDraw && vStops, "V: nothing while its switch is off; whiteboard, or air drawing (V does); a V always stops air drawing");
        c.GestureL = false; CameraFx.RouteZoom(c, 2, d); step();
        bool lOff = c.Zoom == 100;
        c.GestureL = true; CameraFx.RouteZoom(c, 2, d); step();
        Check(lOff && c.Zoom == 200 && !c.FaceTracking, "L: no zoom while its switch is off; on: the view zooms (" + c.Zoom + "%), tracking stays off");
        c.Zoom = 100; step();
        c.GesturePalm = false; CameraFx.RouteGesture(c, "palm", d, ink); step();
        bool palmOff = !c.FaceTracking;
        c.GesturePalm = true; CameraFx.RouteGesture(c, "palm", d, ink); step();
        Check(palmOff && c.FaceTracking, "palm: nothing while its switch is off; shown with it on, tracking turns on");
        c.FaceTracking = false; step();

        ink.SetOn(true);
        double tt = 0;
        c.GesturePoint = false;
        for (int i = 0; i < 6; i++) { CameraFx.FeedTip(c, d, ink, 0.3 + i * 0.01, 0.3, "point", tt); tt += 0.04; }
        bool pointOff = ink.Strokes == 0;
        c.GesturePoint = true;
        for (int i = 0; i < 6; i++) { CameraFx.FeedTip(c, d, ink, 0.3 + i * 0.01, 0.3, "v", tt); tt += 0.04; }
        bool vNoInk = ink.Strokes == 0;
        for (int i = 0; i < 6; i++) { CameraFx.FeedTip(c, d, ink, 0.3 + i * 0.01, 0.3, "point", tt); tt += 0.04; }
        Check(pointOff && vNoInk && ink.Strokes == 1, "pointing draws only while Point: draw is on; another pose never draws");
        ink.SetOn(false); ink.Clear();

        // Switching gestures on (settings) never touches tracking.
        var s = AppSettings.Defaults();
        s.CamFx.FaceTracking = false; s.CamFx.GesturePalm = s.CamFx.GestureV = s.CamFx.GestureL = s.CamFx.GesturePoint = true; s.CamFx.Repair();
        Check(!s.CamFx.FaceTracking && !AppSettings.Defaults().CamFx.FaceTracking, "gestures on by default and when switched on - AI tracking stays off");
        var faces = new[] { new FaceBox { X0 = 0.1, Y0 = 0.1, X1 = 0.15, Y1 = 0.18 }, new FaceBox { X0 = 0.5, Y0 = 0.2, X1 = 0.62, Y1 = 0.4 } };
        c.FaceTracking = false;
        int off = CameraFx.GestureFace(c, faces, 2, 0);
        c.FaceTracking = true;
        int on2 = CameraFx.GestureFace(c, faces, 2, 0);
        c.FaceTracking = false;
        Check(off == 1 && on2 == 0, "hands count beside the biggest face with tracking off, beside the followed one with it on");
    }

    // ------------------------------------------------------------------ smoothing and joining

    static void Smoothing()
    {
        Console.WriteLine("Fingertip smoothing (One Euro):");
        var rnd = new Random(7);
        Func<double> noise = () => { double u = 1 - rnd.NextDouble(), v = rnd.NextDouble(); return 3 * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); };
        var f = new OneEuro2D(1.5, 0.012, 1.0);
        double rawE = 0, outE = 0; int n = 0;
        for (int i = 0; i < 150; i++)
        {
            double x = 500 + noise(), y = 400 + noise(), ox, oy;
            f.Filter(x, y, i * 0.04, out ox, out oy);
            if (i >= 25) { rawE += (x - 500) * (x - 500) + (y - 400) * (y - 400); outE += (ox - 500) * (ox - 500) + (oy - 400) * (oy - 400); n++; }
        }
        double still = Math.Sqrt(outE / n), stillRaw = Math.Sqrt(rawE / n);
        f.Reset(); rawE = outE = 0; n = 0; double lag = 0;
        for (int i = 0; i < 150; i++)
        {
            double tx = 200 + 400 * i * 0.04, x = tx + noise(), y = 400 + noise(), ox, oy;   // 400 px a second to the right
            f.Filter(x, y, i * 0.04, out ox, out oy);
            if (i >= 25) { rawE += (y - 400) * (y - 400); outE += (oy - 400) * (oy - 400); lag += tx - ox; n++; }
        }
        double moving = Math.Sqrt(outE / n), movingRaw = Math.Sqrt(rawE / n); lag /= n;
        Check(still < stillRaw * 0.5, "a still fingertip: jitter " + stillRaw.ToString("0.0") + " px -> " + still.ToString("0.0") + " px");
        Check(moving < movingRaw * 0.85 && lag < 25, "moving at 400 px/s: " + movingRaw.ToString("0.0") + " -> " + moving.ToString("0.0") + " px off the line, " + lag.ToString("0") + " px behind");
    }

    static byte[] Grey(out byte[] uv) { var y = Enumerable.Repeat((byte)100, W * H).ToArray(); uv = Enumerable.Repeat((byte)128, W * H / 2).ToArray(); return y; }

    static void Joining()
    {
        Console.WriteLine("Lines joined between looks:");
        var c = new CamFxSettings(); c.Repair();
        int pY, pU, pV; AirDrawing.ToYuv(c.PenColor, out pY, out pU, out pV);
        // A circle seen only 8 times a turn (45 degrees apart: straight pieces between the looks would cut 23 px inside
        // it, more than the pen's half width). Unsmoothed here, to see the joining on its own.
        var ink = new AirDrawing { Smoothing = false }; ink.SetOn(true);
        double t = 0, R = 300;
        for (int i = 0; i <= 10; i++) { double a = i * Math.PI / 4; ink.Feed((960 + R * Math.Cos(a)) / W, (540 + R * Math.Sin(a)) / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t);
        byte[] uv; var y = Grey(out uv);
        ink.Render(y, uv, c, t);
        int on = 0, all = 0;
        for (double a = Math.PI / 4; a <= Math.PI / 4 * 10 + 1e-9; a += Math.PI / 720)   // from the pen going down (the second look) to the last
        {
            int x = (int)(960 + R * Math.Cos(a)), yy = (int)(540 + R * Math.Sin(a));
            all++; if (Math.Abs(y[yy * W + x] - pY) <= 3) on++;
        }
        Check(on == all, "a circle seen only every 45 degrees comes out round and unbroken (" + on + "/" + all + " points of it under the line)");
        int across = 0;
        for (int r = -30; r <= 30; r++) if (Math.Abs(y[540 * W + (int)(960 + R) + r] - pY) <= 3) across++;
        Check(Math.Abs(across - c.PenSize) <= 3, "the line is " + across + " px thick (pen " + c.PenSize + " px)");
        // Smoothed, written at a natural speed (a turn a second, 25 looks): still round, close to the path.
        ink = new AirDrawing(); ink.SetOn(true); ink.Feed(0.5, 0.5, false, t);
        var rnd = new Random(3); t += 1;
        for (int i = 0; i <= 30; i++) { double a = i * 2 * Math.PI / 25; ink.Feed((960 + 200 * Math.Cos(a) + rnd.NextDouble() * 4 - 2) / W, (540 + 200 * Math.Sin(a) + rnd.NextDouble() * 4 - 2) / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t);
        y = Grey(out uv); ink.Render(y, uv, c, t);
        on = 0; all = 0;
        for (double a = 0; a < 2 * Math.PI; a += Math.PI / 360)
        {
            int x = (int)(960 + 200 * Math.Cos(a)), yy = (int)(540 + 200 * Math.Sin(a));
            all++; if (Math.Abs(y[yy * W + x] - pY) <= 3) on++;
        }
        Check(on == all, "a jittery \"O\" written in a second comes out round, under the finger's path all the way (" + on + "/" + all + ")");
        SavePng(y, uv, Path.Combine(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots")), "airdraw_o.png"));
        // Round ends: a dot (pen 18 px) covers 8.5 px straight out, not 10.6 px out diagonally.
        c.PenSize = 18;
        ink.Clear();
        t += 1;   // the hand comes back somewhere else a second later
        for (int i = 0; i < 4; i++) { ink.Feed(400.0 / W, 300.0 / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t);
        y = Grey(out uv); ink.Render(y, uv, c, t);
        bool round = Math.Abs(y[308 * W + 400] - pY) <= 3 && Math.Abs(y[305 * W + 405] - pY) <= 3 && Math.Abs(y[294 * W + 394] - pY) <= 3 && y[307 * W + 407] == 100 && y[292 * W + 392] == 100;
        Check(round, "a dot is round (round pen ends)");
    }

    // ------------------------------------------------------------------ "Hi!" on a real frame

    static void Hi(string shots)
    {
        Console.WriteLine("\"Hi!\" written in the air:");
        byte[] y, uv;
        string frame = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\camframes\person4k.nv12"));
        if (File.Exists(frame))
        {
            var raw = File.ReadAllBytes(frame);
            var y4 = new byte[3840 * 2160]; var uv4 = new byte[3840 * 1080];
            Buffer.BlockCopy(raw, 0, y4, 0, y4.Length); Buffer.BlockCopy(raw, y4.Length, uv4, 0, uv4.Length);
            y = new byte[W * H]; uv = new byte[W * H / 2];
            Nv12.Resize(y4, uv4, 3840, 2160, y, uv, W, H);
        }
        else { Console.WriteLine("  (no test\\camframes\\person4k.nv12: on a grey picture)"); y = Grey(out uv); }
        var y0 = (byte[])y.Clone(); var uv0 = (byte[])uv.Clone();
        var c = new CamFxSettings(); c.Repair();
        int pY, pU, pV; AirDrawing.ToYuv(c.PenColor, out pY, out pU, out pV);
        var ink = new AirDrawing(); ink.SetOn(true);
        // The fingertip's path (output pixels): H, i, ! - pen down only while pointing, lifted (another pose) in between.
        var strokes = new List<double[]>
        {
            new double[] { 300, 200, 300, 480 }, new double[] { 480, 200, 480, 480 }, new double[] { 300, 340, 480, 340 },
            new double[] { 600, 300, 600, 480 }, new double[] { 600, 230 },
            new double[] { 720, 200, 720, 400 }, new double[] { 720, 470 },
        };
        double t = 0, fx = 250, fy = 150;
        Action<double, double, bool> look = (x, yy, point) => { ink.Feed((x + Math.Sin(t * 37) * 1.5) / W, (yy + Math.Cos(t * 29) * 1.5) / H, point, t); t += 0.04; fx = x; fy = yy; };
        var sw = new Stopwatch(); double renderMs = 0; int renders = 0;
        byte[] yw = (byte[])y0.Clone(), uvw = (byte[])uv0.Clone();
        Action frameNow = () => { Buffer.BlockCopy(y0, 0, yw, 0, yw.Length); Buffer.BlockCopy(uv0, 0, uvw, 0, uvw.Length); sw.Restart(); ink.Render(yw, uvw, c, t); renderMs += sw.Elapsed.TotalMilliseconds; renders++; };
        foreach (var s in strokes)
        {
            // Move there with the hand in another pose (the pen up), pause, then draw.
            double dx = s[0] - fx, dy = s[1] - fy; int moves = (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 20);
            double sx = fx, sy = fy;
            for (int i = 1; i <= moves; i++) { look(sx + dx * i / moves, sy + dy * i / moves, false); frameNow(); }
            for (int i = 0; i < 3; i++) { look(s[0], s[1], true); frameNow(); }
            if (s.Length == 4)
            {
                double lx = s[2] - s[0], ly = s[3] - s[1]; int n = (int)Math.Ceiling(Math.Sqrt(lx * lx + ly * ly) / 12);
                for (int i = 1; i <= n; i++) { look(s[0] + lx * i / n, s[1] + ly * i / n, true); frameNow(); }
                for (int i = 0; i < 2; i++) { look(s[2], s[3], true); frameNow(); }
            }
            else for (int i = 0; i < 2; i++) { look(s[0], s[1], true); frameNow(); }
            look(fx, fy, false); frameNow();
        }
        Buffer.BlockCopy(y0, 0, y, 0, y.Length); Buffer.BlockCopy(uv0, 0, uv, 0, uv.Length);
        bool drew = ink.Render(y, uv, c, t);
        Func<int, int, bool> pen = (x, yy) => Math.Abs(y[yy * W + x] - pY) <= 3 && Math.Abs(uv[(yy / 2) * W + (x & ~1)] - pU) <= 4 && Math.Abs(uv[(yy / 2) * W + (x & ~1) + 1] - pV) <= 4;
        Func<int, int, bool> same = (x, yy) => y[yy * W + x] == y0[yy * W + x] && uv[(yy / 2) * W + (x & ~1)] == uv0[(yy / 2) * W + (x & ~1)];
        Check(drew && ink.Strokes == 7, "seven lines: H (3), i (2), ! (2) - " + ink.Strokes);
        Check(pen(300, 300) && pen(480, 420) && pen(390, 340) && pen(600, 400) && pen(600, 230) && pen(720, 300) && pen(720, 470), "the lines are solid pen colour where the finger went (H's stems and bar, i, i's dot, !, !'s dot)");
        Check(same(390, 250) && same(390, 430) && same(600, 272) && same(720, 437) && same(540, 340), "the pen was up in between: H's stems apart, the dots apart from their strokes, H apart from i");
        Check(same(1500, 900) && same(100, 1000) && same(1800, 100), "the rest of the picture is untouched");
        Check(renderMs / renders < 8, "drawing it onto each frame: " + (renderMs / renders).ToString("0.00") + " ms a frame on average (" + renders + " frames)");
        c.PenSize = 24;
        sw.Restart(); ink.Render((byte[])y0.Clone(), (byte[])uv0.Clone(), c, t);
        Check(sw.Elapsed.TotalMilliseconds < 40, "redrawing everything (thickness changed): " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
        SavePng(y, uv, Path.Combine(shots, "airdraw.png"));
        Console.WriteLine("  saved " + Path.Combine(shots, "airdraw.png"));
    }

    static void SavePng(byte[] y, byte[] uv, string path)
    {
        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb))
        {
            var px = new byte[W * H * 4];
            for (int yy = 0; yy < H; yy++)
                for (int x = 0; x < W; x++)
                {
                    double Y = 1.164 * (y[yy * W + x] - 16), U = uv[(yy / 2) * W + (x & ~1)] - 128, V = uv[(yy / 2) * W + (x & ~1) + 1] - 128;
                    int o = (yy * W + x) * 4;
                    px[o] = Cl(Y + 2.112 * U); px[o + 1] = Cl(Y - 0.213 * U - 0.533 * V); px[o + 2] = Cl(Y + 1.793 * V); px[o + 3] = 255;
                }
            var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            bmp.UnlockBits(d);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    static byte Cl(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5); }

    // ------------------------------------------------------------------ under the fingertip

    static void Placement()
    {
        Console.WriteLine("Lines under the fingertip (mirror, upside down, zoom):");
        bool all = true; string got = "";
        foreach (bool mirror in new[] { false, true })
            foreach (bool upside in new[] { false, true })
            {
                var c = new CamFxSettings { Mirror = mirror, UpsideDown = upside, Zoom = 100 }; c.Repair();
                var d = new CameraDirector();
                d.Step(c, new FaceBox[0], 0, 3840, 2160, new long[4], new float[9]);
                double ex = upside ? 0.75 : 0.25, ey = upside ? 0.7 : 0.3;
                if (mirror) ex = 1 - ex;
                double u, v; CameraFx.TipToOutput(c, d, 0.25, 0.3, out u, out v);
                // Through the pen onto a picture: where does the ink land?
                var ink = new AirDrawing(); ink.SetOn(true);
                for (int i = 0; i < 4; i++) CameraFx.FeedTip(c, d, ink, 0.25, 0.3, "point", i * 0.04);
                ink.Feed(double.NaN, double.NaN, false, 0.2);
                byte[] uv; var y = Grey(out uv);
                ink.Render(y, uv, c, 0.2);
                double sx = 0, sy = 0; int n = 0;
                for (int yy = 0; yy < H; yy++) for (int x = 0; x < W; x++) if (y[yy * W + x] != 100) { sx += x + 0.5; sy += yy + 0.5; n++; }
                bool ok = Math.Abs(u - ex) < 0.002 && Math.Abs(v - ey) < 0.002 && n > 0 && Math.Abs(sx / n - ex * W) < 2 && Math.Abs(sy / n - ey * H) < 2;
                all &= ok;
                got += " " + (mirror ? "M" : "-") + (upside ? "U" : "-") + "(" + (sx / Math.Max(1, n)).ToString("0") + "," + (sy / Math.Max(1, n)).ToString("0") + ")";
            }
        Check(all, "the camera's point (0.25, 0.30) is drawn where apps see it, for each of mirror off/on x upside down off/on:" + got);
        var cz = new CamFxSettings { Zoom = 200 }; cz.Repair();
        var dz = new CameraDirector(); dz.Step(cz, new FaceBox[0], 0, 3840, 2160, new long[4], new float[9]);
        double zu, zv; CameraFx.TipToOutput(cz, dz, 0.4, 0.45, out zu, out zv);
        Check(Math.Abs(zu - 0.3) < 0.002 && Math.Abs(zv - 0.4) < 0.002, "zoomed in 2x: the point follows the view (" + zu.ToString("0.000") + ", " + zv.ToString("0.000") + ")");
        var cd = new CamFxSettings { Mirror = true, ViewMode = "desk" }; cd.Repair();
        Check(!CameraFx.Mirrored(cd, new CameraDirector()), "desk view is never mirrored, so neither is the drawing on it");
    }

    // ------------------------------------------------------------------ fading, clearing, turning, lifting

    static void FadeAndClear()
    {
        Console.WriteLine("Fading, clearing, turning round:");
        var c = new CamFxSettings(); c.Repair(); c.InkFadeSeconds = 2;
        int pY, pU, pV; AirDrawing.ToYuv(c.PenColor, out pY, out pU, out pV);
        var ink = new AirDrawing(); ink.SetOn(true);
        double t = 10;
        for (int i = 0; i < 8; i++) { ink.Feed((500 + i * 10.0) / W, 500.0 / H, true, t); t += 0.04; }
        ink.Feed(500.0 / W, 600.0 / H, false, t);
        Func<double, int> at = now => { byte[] uv; var y = Grey(out uv); ink.Render(y, uv, c, now); return y[500 * W + 540]; };
        int fresh = at(11.0), half = at(t + 2.5), gone = at(t + 3.1);
        Check(fresh == pY && half > 100 && half < pY && gone == 100 && ink.Strokes == 0, "fade after 2 s: solid, then half faded a second later (" + half + "), then gone");
        c.InkFadeSeconds = 0;
        for (int i = 0; i < 8; i++) { ink.Feed((500 + i * 10.0) / W, 500.0 / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t);
        Check(at(t + 1000) == pY, "fade off: still there 1000 s later");
        ink.Clear();
        byte[] uv2; var y2 = Grey(out uv2);
        Check(!ink.Render(y2, uv2, c, t) && y2.All(b => b == 100), "Clear: nothing drawn any more");
        for (int i = 0; i < 4; i++) { ink.Feed(100.0 / W, 100.0 / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t);
        ink.Rotate180();
        y2 = Grey(out uv2); ink.Render(y2, uv2, c, t);
        Check(y2[(H - 100) * W + (W - 100)] == pY && y2[100 * W + 100] == 100, "the picture turned upside down: the drawing turns with it");
        ink.Clear();
        // The hand lost (no look) or the looks stopped: the line ends there - the next one doesn't join it.
        for (int i = 0; i < 5; i++) { ink.Feed((300 + i * 8.0) / W, 800.0 / H, true, t); t += 0.04; }
        ink.Feed(double.NaN, double.NaN, false, t); t += 0.04;
        for (int i = 0; i < 5; i++) { ink.Feed((600 + i * 8.0) / W, 800.0 / H, true, t); t += 0.04; }
        y2 = Grey(out uv2); ink.Render(y2, uv2, c, t + 0.5);
        for (int i = 0; i < 5; i++) { ink.Feed((900 + i * 8.0) / W, 800.0 / H, true, t + 0.5 + i * 0.04); }
        y2 = Grey(out uv2); ink.Render(y2, uv2, c, t + 0.7);
        Check(ink.Strokes == 3 && y2[800 * W + 470] == 100 && y2[800 * W + 760] == 100 && y2[800 * W + 620] == pY, "hand lost, or no look for a moment: the line ends - three separate lines");
        ink.SetOn(false);
        ink.Feed(0.5, 0.5, true, t + 1); ink.Feed(0.5, 0.5, true, t + 1.04); ink.Feed(0.5, 0.5, true, t + 1.08);
        Check(ink.Strokes == 3, "air drawing off: pointing draws nothing");
    }

    // ------------------------------------------------------------------ settings

    static void Migration()
    {
        Console.WriteLine("Settings:");
        var js = new JavaScriptSerializer();
        var off = js.Deserialize<CamFxSettings>("{\"Gestures\":false,\"FaceTracking\":false}"); off.Repair();
        var on = js.Deserialize<CamFxSettings>("{\"Gestures\":true}"); on.Repair();
        Check(!off.GesturePalm && !off.GestureV && !off.GestureL && !off.GesturePoint && off.GestureVersion == 1 && !off.FaceTracking, "older settings with gestures off: every gesture starts off (tracking untouched)");
        Check(on.GesturePalm && on.GestureV && on.GestureL && on.GesturePoint && on.GestureVersion == 1 && !on.FaceTracking, "older settings with gestures on: every gesture starts on - and tracking stays off");
        var mine = js.Deserialize<CamFxSettings>("{\"Gestures\":true,\"GestureVersion\":1,\"GesturePalm\":false,\"GestureL\":false}"); mine.Repair();
        Check(!mine.GesturePalm && mine.GestureV && !mine.GestureL && mine.GesturePoint, "4.1 settings: each switch kept as it was saved");
        var bad = js.Deserialize<CamFxSettings>("{\"PenSize\":500,\"InkFadeSeconds\":-3,\"VAction\":\"spin\",\"PenColor\":2132948054,\"HdrLast\":0}"); bad.Repair();
        Check(bad.PenSize == 48 && bad.InkFadeSeconds == 0 && bad.VAction == "whiteboard" && bad.PenColor == (2132948054 & 0xFFFFFF) && bad.HdrLast == 10, "out-of-range values repaired");
        var d = AppSettings.Defaults().CamFx;
        Check(d.GesturePalm && d.GestureV && d.GestureL && d.GesturePoint && d.VAction == "whiteboard" && d.PenColor == 0x2BFF4A && d.PenSize == 18 && d.InkFadeSeconds == 0 && !d.FaceTracking,
            "a new install: gestures on, V = whiteboard, bright green 18 px pen, no fading, tracking off");
        var s = AppSettings.Defaults(); s.CamFx.VAction = "draw"; s.CamFx.PenColor = 0xFF0000; s.CamFx.PenSize = 30; s.CamFx.InkFadeSeconds = 5; s.CamFx.GestureL = false;
        var back = js.Deserialize<AppSettings>(js.Serialize(s)).CamFx; back.Repair();
        Check(back.VAction == "draw" && back.PenColor == 0xFF0000 && back.PenSize == 30 && back.InkFadeSeconds == 5 && !back.GestureL && back.GestureV, "saved and loaded back unchanged");
    }

    // ------------------------------------------------------------------ following the hand (the models, CPU)

    static void Following()
    {
        Console.WriteLine("Following the hand while drawing (hand models on the CPU):");
        // The crop cut where the hand was maps back onto the camera picture exactly.
        {
            var y = Enumerable.Repeat((byte)16, W * H).ToArray(); var uv = Enumerable.Repeat((byte)128, W * H / 2).ToArray();
            for (int yy = 398; yy < 403; yy++) for (int x = 1098; x < 1103; x++) y[yy * W + x] = 235;
            var roi = new HandRoi { Cx = 1080, Cy = 420, Side = 200, Angle = 0.6 };
            var crop = new byte[224 * 224 * 3];
            HandGestures.CutCrop(y, uv, W, H, roi, crop);
            double sx = 0, sy = 0, sw = 0;
            for (int i = 0; i < 224; i++) for (int j = 0; j < 224; j++) { double v = crop[(i * 224 + j) * 3]; if (v > 40) { sx += (j + 0.5) * v; sy += (i + 0.5) * v; sw += v; } }
            double X, Y; HandGestures.CropToCamera(roi, sx / sw, sy / sw, out X, out Y);
            Check(Math.Abs(X - 1100.5) < 1 && Math.Abs(Y - 400.5) < 1, "a hand's crop (turned 34 degrees) maps back onto the camera picture: " + X.ToString("0.0") + ", " + Y.ToString("0.0") + " (want 1100.5, 400.5)");
            var lm = Hand("1000", "in", "", 30, 1.5, 600, 300);
            var cam = new double[42];
            for (int i = 0; i < 21; i++) { cam[i * 2] = lm[i * 3]; cam[i * 2 + 1] = lm[i * 3 + 1]; }
            var r = HandGestures.RoiFrom(cam);
            bool inside = true;
            for (int i = 0; i < 21; i++)
            {
                double dx = cam[i * 2] - r.Cx, dy = cam[i * 2 + 1] - r.Cy;
                double qx = dx * Math.Cos(r.Angle) + dy * Math.Sin(r.Angle), qy = -dx * Math.Sin(r.Angle) + dy * Math.Cos(r.Angle);
                inside &= Math.Abs(qx) < r.Side / 2 && Math.Abs(qy) < r.Side / 2;
            }
            Check(inside && Math.Abs(r.Angle * 180 / Math.PI - 30) < 1, "the next look's square holds the whole hand, fingertip too, turned with it (" + (r.Angle * 180 / Math.PI).ToString("0.0") + " degrees)");
        }

        Check(HandGestures.Installed, "hand models installed (ai folder)");
        if (!HandGestures.Installed) return;
        string frame = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\camframes\person4k.nv12"));
        int CW = 3840, CH = 2160;
        byte[] y4 = new byte[CW * CH], uv4 = new byte[CW * CH / 2];
        if (File.Exists(frame)) { var raw = File.ReadAllBytes(frame); Buffer.BlockCopy(raw, 0, y4, 0, y4.Length); Buffer.BlockCopy(raw, y4.Length, uv4, 0, uv4.Length); }
        else { for (int i = 0; i < y4.Length; i++) y4[i] = (byte)(16 + (i * 7 % 200)); for (int i = 0; i < uv4.Length; i++) uv4[i] = 128; }
        var g = new HandGestures();
        var clock = Stopwatch.StartNew();
        Func<double> now = () => clock.Elapsed.TotalSeconds;
        var face = new FaceBox { X0 = 0.69, Y0 = 0.26, X1 = 0.77, Y1 = 0.40, Score = 0.9 };
        // Searching (palm detector first), as between gestures.
        for (int i = 0; i < 30; i++) { while (!g.Offer(y4, uv4, CW, CH, face, now())) Thread.Sleep(1); }
        while (!g.Offer(y4, uv4, CW, CH, face, now())) Thread.Sleep(1);
        Thread.Sleep(100);
        float searchMs = g.LookMs;
        Check(g.Error == null && searchMs > 0, "searching for a hand beside the face: " + searchMs.ToString("0.0") + " ms a look" + (g.Error != null ? " - " + g.Error : ""));
        // Following: the hand was found last time - only the landmark model, on a crop where it was.
        g.Fast = true;
        var hand = new HandRoi { Cx = 0.62 * CW, Cy = 0.55 * CH, Side = 0.18 * CH, Angle = 0.2 };
        var offer = new Stopwatch(); double offerMs = 0; int offers = 0;
        for (int i = 0; i < 40; i++)
        {
            while (true) { g.FollowAt(hand, now()); offer.Restart(); bool ok = g.Offer(y4, uv4, CW, CH, null, now()); if (ok) { offerMs += offer.Elapsed.TotalMilliseconds; offers++; break; } Thread.Sleep(1); }
        }
        Thread.Sleep(100);
        float followMs = g.LookMs; bool following = g.Following;
        // A 30 fps camera offering every frame for 3 seconds.
        int accepted = 0; float lps = 0;
        var cam30 = Stopwatch.StartNew(); double next = 0;
        while (cam30.Elapsed.TotalSeconds < 3.2)
        {
            if (cam30.Elapsed.TotalSeconds < next) { Thread.Sleep(1); continue; }
            next += 1 / 30.0;
            g.FollowAt(hand, now());
            if (g.Offer(y4, uv4, CW, CH, null, now())) accepted++;
            if (cam30.Elapsed.TotalSeconds > 2.1) lps = Math.Max(lps, g.LooksPerSecond);
        }
        g.Dispose();
        Console.WriteLine("  following: " + followMs.ToString("0.0") + " ms a look (landmark model only), cutting the crop " + (offerMs / Math.Max(1, offers)).ToString("0.00") + " ms on the camera thread; searching: " + searchMs.ToString("0.0") + " ms");
        Check(following && followMs < 35, "following a hand: " + followMs.ToString("0.0") + " ms a look - room for " + (1000 / followMs).ToString("0") + " looks a second");
        Check(lps >= 20, "with a 30 fps camera: " + lps.ToString("0") + " looks a second (" + accepted + " of " + (int)(3.2 * 30) + " frames looked at)");
    }
}
