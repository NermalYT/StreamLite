// The camera operator (virtual gimbal) without a camera: the view it picks for tracking one person or a group, the
// framings, pause and tracking zones, losing someone, manual pan / zoom, presets, level, upside down, whiteboard and
// desk view; the homographies it hands the effects graph; face decoding; hand-gesture classification; the
// whiteboard finder. Usage: CameraDirectorTest
using System;
using System.Linq;
using StreamLite;
using StreamLite.Camera;

class CameraDirectorTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int W = 3840, H = 2160;
    static double t;

    static FaceBox Face(double cx, double cy, double h) { double w = h * 0.75 * H / W; return new FaceBox { X0 = cx - w / 2, X1 = cx + w / 2, Y0 = cy - h / 2, Y1 = cy + h / 2, Score = 0.99 }; }

    static void Run(CameraDirector d, CamFxSettings c, FaceBox[] f, int frames, long[] crop, float[] warp)
    {
        for (int i = 0; i < frames; i++) { t += 1 / 30.0; d.Step(c, f, f.Length, W, H, crop, warp); }
    }

    static CamFxSettings Settings() { var c = new CamFxSettings { TrackSpeed = 60 }; c.Repair(); return c; }

    static void Corner(CameraDirector d, double u, double v, out double x, out double y) { Homography.Apply(d.OutToCam, u, v, out x, out y); }

    static void Main()
    {
        var crop = new long[4]; var warp = new float[9];

        Console.WriteLine("Homographies:");
        {
            var q = new[] { 100.0, 50, 900, 120, 860, 700, 60, 640 };
            var m = Homography.FromUnitSquare(q);
            double worst = 0;
            double[] us = { 0, 1, 1, 0 }, vs = { 0, 0, 1, 1 };
            for (int i = 0; i < 4; i++) { double X, Y; Homography.Apply(m, us[i], vs[i], out X, out Y); worst = Math.Max(worst, Math.Abs(X - q[i * 2]) + Math.Abs(Y - q[i * 2 + 1])); }
            var back = Homography.Mul(Homography.Invert(m), m);
            double id = Enumerable.Range(0, 9).Max(i => Math.Abs(back[i] / back[8] - (i % 4 == 0 ? 1 : 0)));
            Check(worst < 1e-6 && id < 1e-9, "square -> any quadrilateral, and back (corner error " + worst.ToString("0.0e0") + ")");
        }

        Console.WriteLine("Whole picture (tracking off, no zoom):");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.Zoom = 100;
            Run(d, c, new FaceBox[0], 5, crop, warp);
            bool ident = Math.Abs(warp[0] - 1) < 1e-4 && Math.Abs(warp[4] - 1) < 1e-4 && Math.Abs(warp[2]) < 1e-3 && Math.Abs(warp[5]) < 1e-3 && Math.Abs(warp[6]) < 1e-6 && Math.Abs(warp[7]) < 1e-6;
            Check(crop[0] == 0 && crop[1] == 0 && crop[2] == H && crop[3] == W && ident, "the crop is the whole frame and the warp is no warp");
        }

        Console.WriteLine("Tracking one person:");
        foreach (var fr in new[] { new { Key = "head", Share = 0.30 }, new { Key = "half", Share = 0.15 }, new { Key = "body", Share = 0.075 } })
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.FaceTracking = true; c.Framing = fr.Key;
            var f = new[] { Face(0.62, 0.45, 0.06) };
            Run(d, c, f, 400, crop, warp);
            var o = d.OutputFaces[0];
            double want = Math.Min(fr.Share, 0.06 * CameraDirector.MaxZoom);
            bool fits = fr.Key == "body" ? o.H <= 0.08 : Math.Abs(o.H - want) < 0.02;   // whole body: as far out as the picture goes
            Check(fits && Math.Abs(o.Cx - 0.5) < 0.06, fr.Key + ": the face is " + (o.H * 100).ToString("0") + "% of the picture's height (want " + (want * 100).ToString("0") + "%), centred (" + o.Cx.ToString("0.00") + ")");
        }
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.FaceTracking = true; c.Framing = "half";
            Run(d, c, new[] { Face(0.4, 0.5, 0.08) }, 400, crop, warp);
            double before = d.OutputFaces[0].Cx;
            Run(d, c, new[] { Face(0.403, 0.505, 0.08) }, 60, crop, warp);   // a nod
            double nod = Math.Abs(d.OutputFaces[0].Cx - before);
            Run(d, c, new[] { Face(0.6, 0.5, 0.08) }, 400, crop, warp);      // walks over
            Check(nod < 0.03 && Math.Abs(d.OutputFaces[0].Cx - 0.5) < 0.06, "small moves don't move the view (" + nod.ToString("0.000") + "), a real move is followed");
            Run(d, c, new FaceBox[0], 30, crop, warp);
            double held = d.OutputFaces.Length;
            double hx, hy; Corner(d, 0.5, 0.5, out hx, out hy);
            Run(d, c, new FaceBox[0], 200, crop, warp);   // > 3 s gone
            double x0, y0; Corner(d, 0, 0, out x0, out y0); double x1, y1; Corner(d, 1, 1, out x1, out y1);
            Check(Math.Abs(hx / W - 0.6) < 0.08 && x0 < 40 && y0 < 40 && x1 > W - 40 && y1 > H - 40, "lost: holds where you were for 3 s, then shows the whole room");
            // Two people: the one clicked is followed.
            var two = new[] { Face(0.3, 0.45, 0.08), Face(0.7, 0.45, 0.07) };
            Run(d, c, two, 300, crop, warp);
            double ax, ay; Corner(d, 0.5, 0.5, out ax, out ay);
            var outs = d.OutputFaces; int other = Math.Abs(outs[0].Cx - 0.5) < Math.Abs(outs[1].Cx - 0.5) ? 1 : 0;
            d.SelectAt(c.Mirror ? 1 - outs[other].Cx : outs[other].Cx, outs[other].Cy);
            Run(d, c, two, 400, crop, warp);
            double bx, by; Corner(d, 0.5, 0.5, out bx, out by);
            Check(Math.Abs(ax / W - 0.3) < 0.08 && Math.Abs(bx / W - 0.7) < 0.08, "two people: the closer one first, then the one clicked (" + (ax / W).ToString("0.00") + " -> " + (bx / W).ToString("0.00") + ")");
        }

        Console.WriteLine("Group:");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.FaceTracking = true; c.TrackMode = "group"; c.Framing = "half";
            var g = new[] { Face(0.25, 0.45, 0.07), Face(0.5, 0.4, 0.075), Face(0.72, 0.47, 0.07) };
            Run(d, c, g, 400, crop, warp);
            bool all = d.OutputFaces.All(o => o.X0 > 0.02 && o.X1 < 0.98 && o.Y0 > 0.02 && o.Y1 < 0.98);
            Check(all, "everyone is in the picture, with room around them");
        }

        Console.WriteLine("Zones:");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.FaceTracking = true; c.Framing = "head";
            c.PauseZones.Add(new[] { 0.6, 0.3, 0.9, 0.7 });
            Run(d, c, new[] { Face(0.75, 0.5, 0.06) }, 400, crop, warp);
            double x0, y0; Corner(d, 0, 0, out x0, out y0); double x1, y1; Corner(d, 1, 1, out x1, out y1);
            bool zone = x0 / W < 0.6 + 0.02 && x1 / W > 0.9 - 0.02 && y0 / H < 0.3 + 0.02 && y1 / H > 0.7 - 0.02 && d.State.Contains("pause zone");
            Run(d, c, new[] { Face(0.72, 0.55, 0.06) }, 120, crop, warp);   // moving about inside it
            double x0b, y0b; Corner(d, 0, 0, out x0b, out y0b);
            Check(zone && Math.Abs(x0b - x0) < 4 && Math.Abs(y0b - y0) < 4, "inside a pause zone: the whole zone, held still while you move in it");
            Run(d, c, new[] { Face(0.3, 0.5, 0.06) }, 400, crop, warp);
            Check(Math.Abs(d.OutputFaces[0].Cx - 0.5) < 0.06, "stepping out: tracking again");
            var d2 = new CameraDirector { Now = () => t }; var c2 = Settings(); c2.FaceTracking = true; c2.Framing = "half";
            c2.TrackZone = new[] { 0.0, 0.0, 0.5, 1.0 }; c2.TrackZoneOn = true;
            Run(d2, c2, new[] { Face(0.46, 0.5, 0.06) }, 400, crop, warp);
            double maxX = 0; foreach (var u in new[] { 0.0, 1.0 }) foreach (var v in new[] { 0.0, 1.0 }) { double X, Y; Corner(d2, u, v, out X, out Y); maxX = Math.Max(maxX, X); }
            Run(d2, c2, new[] { Face(0.8, 0.5, 0.06) }, 300, crop, warp);   // outside the zone: not followed
            double maxX2 = 0; foreach (var u in new[] { 0.0, 1.0 }) foreach (var v in new[] { 0.0, 1.0 }) { double X, Y; Corner(d2, u, v, out X, out Y); maxX2 = Math.Max(maxX2, X); }
            Check(maxX <= W * 0.5 + 4 && maxX2 <= W * 0.5 + 4, "tracking zone: never shows anything outside it (right edge at " + (maxX / W).ToString("0.000") + ", " + (maxX2 / W).ToString("0.000") + " of the picture)");
        }

        Console.WriteLine("Manual view and presets:");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.Zoom = 100;
            Run(d, c, new FaceBox[0], 5, crop, warp);
            d.ZoomAt(2, 0.25, 0.25);
            Run(d, c, new FaceBox[0], 200, crop, warp);
            double cx, cy; Corner(d, 0.5, 0.5, out cx, out cy);
            Check(c.Zoom == 200 && Math.Abs(cx / W - 0.375) < 0.01 && Math.Abs(cy / H - 0.375) < 0.01, "wheel: 2x around the point under the mouse (" + c.Zoom + "%, centre " + (cx / W).ToString("0.000") + ")");
            d.Pan(-0.1, 0);
            Run(d, c, new FaceBox[0], 200, crop, warp);
            double px, py; Corner(d, 0.5, 0.5, out px, out py);
            Check(Math.Abs(px / W - (0.375 + 0.05)) < 0.01, "drag left by 10% of the view: the view moves right by 5% of the picture");
            d.SavePreset(2, "Desk");
            d.ResetView(); Run(d, c, new FaceBox[0], 200, crop, warp);
            double rx, ry; Corner(d, 0, 0, out rx, out ry);
            d.RecallPreset(2); Run(d, c, new FaceBox[0], 200, crop, warp);
            double qx, qy; Corner(d, 0.5, 0.5, out qx, out qy);
            Check(rx < 4 && ry < 4 && c.Presets[2].Used && c.Presets[2].Zoom == 200 && Math.Abs(qx - px) < 6 && Math.Abs(qy - py) < 6, "preset 3 saved, reset to the whole picture, recalled exactly");
            d.GestureZoom(1.5); Run(d, c, new FaceBox[0], 5, crop, warp);
            Check(c.Zoom == 300, "L gesture / zoom hotkey with tracking off: the view zooms (" + c.Zoom + "%)");
        }

        Console.WriteLine("Mounting:");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.Level = 10; c.Zoom = 100;
            Run(d, c, new FaceBox[0], 5, crop, warp);
            double ax, ay; Corner(d, 0, 0, out ax, out ay); double bx, by; Corner(d, 1, 0, out bx, out by);
            double ang = Math.Atan2(by - ay, bx - ax) * 180 / Math.PI;
            bool inside = true;
            foreach (var u in new[] { 0.0, 1.0 }) foreach (var v in new[] { 0.0, 1.0 }) { double X, Y; Corner(d, u, v, out X, out Y); inside &= X >= -1 && Y >= -1 && X <= W + 1 && Y <= H + 1; }
            Check(Math.Abs(ang - 10) < 0.2 && inside, "level +10 degrees: the picture is turned by " + ang.ToString("0.0") + " and its corners stay filled");
            var d2 = new CameraDirector { Now = () => t }; var c2 = Settings(); c2.UpsideDown = true; c2.Zoom = 100;
            Run(d2, c2, new FaceBox[0], 5, crop, warp);
            double ux, uy; Corner(d2, 0, 0, out ux, out uy);
            Check(ux > W - 4 && uy > H - 4, "upside down: the top-left of the output is the camera's bottom-right");
        }

        Console.WriteLine("Whiteboard and desk view:");
        {
            var d = new CameraDirector { Now = () => t }; var c = Settings(); c.ViewMode = "whiteboard";
            var board = new[] { 0.3, 0.2, 0.75, 0.25, 0.72, 0.7, 0.33, 0.65 };
            d.Board = board;
            Run(d, c, new FaceBox[0], 120, crop, warp);
            var inv = Homography.Invert(d.OutToCam);
            var o = new double[8];
            for (int i = 0; i < 4; i++) Homography.Apply(inv, board[i * 2] * W, board[i * 2 + 1] * H, out o[i * 2], out o[i * 2 + 1]);
            bool square = Math.Abs(o[0] - o[6]) < 0.01 && Math.Abs(o[2] - o[4]) < 0.01 && Math.Abs(o[1] - o[3]) < 0.01 && Math.Abs(o[5] - o[7]) < 0.01;
            bool fills = Math.Min(o[1], o[3]) < 0.05 && Math.Max(o[5], o[7]) > 0.95;
            Check(square && fills && d.State == "Whiteboard", "the board comes out square (its edges straight and level) and fills the height");
            var p = new float[24]; CameraFx.FillParams(c, p, d);
            c.Mirror = true; CameraFx.FillParams(c, p, d);
            Check(p[14] == 1 && p[12] == 0, "the board is enhanced and never mirrored (writing stays readable)");
            var d2 = new CameraDirector { Now = () => t }; var c2 = Settings(); c2.ViewMode = "desk";
            Run(d2, c2, new FaceBox[0], 120, crop, warp);
            var inv2 = Homography.Invert(d2.OutToCam); double bx0, by0, bx1, by1;
            Homography.Apply(inv2, c2.DeskQuad[6] * W, c2.DeskQuad[7] * H, out bx0, out by0); Homography.Apply(inv2, c2.DeskQuad[4] * W, c2.DeskQuad[5] * H, out bx1, out by1);
            Check(Math.Abs(by0 - by1) < 0.01 && by0 > 0.99 && Math.Abs(bx0) < 0.01 && Math.Abs(bx1 - 1) < 0.01, "desk view: the area you marked fills the picture, squared off");
        }

        Console.WriteLine("Faces from UltraFace:");
        {
            var scores = new float[4420 * 2]; var boxes = new float[4420 * 4];
            Action<int, float, double, double, double, double> put = (i, s, x0, y0, x1, y1) => { scores[i * 2 + 1] = s; boxes[i * 4] = (float)x0; boxes[i * 4 + 1] = (float)((y0 * 180 + 30) / 240); boxes[i * 4 + 2] = (float)x1; boxes[i * 4 + 3] = (float)((y1 * 180 + 30) / 240); };
            put(10, 0.97f, 0.40, 0.30, 0.50, 0.48); put(11, 0.95f, 0.41, 0.31, 0.51, 0.49); put(12, 0.9f, 0.39, 0.29, 0.49, 0.47);
            put(500, 0.92f, 0.70, 0.35, 0.78, 0.50); put(900, 0.5f, 0.1, 0.1, 0.2, 0.2);
            var f = new FaceBox[8]; int n = Faces.Decode(scores, boxes, f);
            Check(n == 2 && Math.Abs(f[0].Y0 - 0.30) < 1e-4 && f[0].Score > 0.96, "overlapping detections merged, unsure ones dropped: " + n + " faces, boxes back in frame coordinates");
        }

        Console.WriteLine("Hand gestures (landmarks):");
        {
            Func<string, string, string, float[]> hand = (fingers, thumb, spread) =>
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
                else { s(1, 80, 185); s(2, 72, 170); s(3, 78, 158); s(4, 88, 150); }
                return p;
            };
            Check(HandGestures.Classify(hand("1111", "out", "")) == "palm", "open palm");
            Check(HandGestures.Classify(hand("1100", "in", "v")) == "v", "V (index and middle finger)");
            Check(HandGestures.Classify(hand("1000", "out", "")) == "l", "L (thumb and index finger)");
            Check(HandGestures.Classify(hand("0000", "in", "")) == "fist", "a fist (no gesture)");
            Check(HandGestures.Classify(hand("1000", "in", "")) == "point", "pointing (air drawing's pen, no gesture)");
            var a = HandGestures.MakeAnchors();
            Check(a.Length == 4032 && Math.Abs(a[0] - 1 / 48f) < 1e-6 && Math.Abs(a[4030] - 23 / 24f) < 1e-6, "palm detector anchors (2016, MediaPipe's layout)");
        }

        Console.WriteLine("Whiteboard finder:");
        {
            var rnd = new Random(4);
            Func<double[], int, int, byte[][]> scene = (quad, wall, sat) =>
            {
                var l = new byte[320 * 180]; var s = new byte[320 * 180];
                for (int i = 0; i < l.Length; i++) { l[i] = (byte)Math.Max(0, Math.Min(255, wall + rnd.Next(-8, 9))); s[i] = (byte)sat; }
                if (quad != null)
                    for (int y = 0; y < 180; y++)
                        for (int x = 0; x < 320; x++)
                        {
                            if (!Inside(quad, x + 0.5, y + 0.5)) continue;
                            bool ink = (y % 23 < 2 && x % 40 < 30) || (x % 37 < 2 && y % 50 < 25);
                            l[y * 320 + x] = (byte)(ink ? 50 : 215 + rnd.Next(-6, 7)); s[y * 320 + x] = (byte)(ink ? 40 : 6);
                        }
                return new[] { l, s };
            };
            var q = new[] { 60.0, 30, 250, 42, 244, 150, 72, 140 };
            var fnd = new WhiteboardFinder();
            var sc = scene(q, 95, 12);
            var r = fnd.Find(sc[0], sc[1]);
            double err = 0; if (r != null) for (int i = 0; i < 4; i++) err = Math.Max(err, Math.Abs(r[i * 2] * 320 - q[i * 2]) + Math.Abs(r[i * 2 + 1] * 180 - q[i * 2 + 1]));
            Check(r != null && err < 6, "a tilted board with writing on it: its corners found (within " + err.ToString("0.0") + " px of 320)");
            var none = scene(null, 95, 12);
            Check(new WhiteboardFinder().Find(none[0], none[1]) == null, "no board: nothing");
            var whiteWall = scene(new[] { 0.0, 0, 320, 0, 320, 180, 0, 180 }, 95, 12);
            Check(new WhiteboardFinder().Find(whiteWall[0], whiteWall[1]) == null, "a white wall filling the picture isn't a board");
            var corner = scene(new[] { 0.0, 0, 180, 0, 160, 90, 0, 110 }, 95, 12);
            Check(new WhiteboardFinder().Find(corner[0], corner[1]) == null, "a bright wall running off the picture's edge isn't a board");
            var col = scene(q, 95, 12); for (int i = 0; i < col[1].Length; i++) col[1][i] = 60;
            Check(new WhiteboardFinder().Find(col[0], col[1]) == null, "a bright coloured area isn't a board");
        }

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    static bool Inside(double[] q, double x, double y)
    {
        bool sign = false;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            double cross = (q[j * 2] - q[i * 2]) * (y - q[i * 2 + 1]) - (q[j * 2 + 1] - q[i * 2 + 1]) * (x - q[i * 2]);
            if (i == 0) sign = cross > 0; else if ((cross > 0) != sign) return false;
        }
        return true;
    }
}
