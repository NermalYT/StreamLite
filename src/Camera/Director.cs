using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StreamLite.Camera
{
    /// <summary>A face UltraFace found, in the full camera frame (0..1).</summary>
    internal struct FaceBox
    {
        public double X0, Y0, X1, Y1, Score;
        public double Cx { get { return (X0 + X1) / 2; } }
        public double Cy { get { return (Y0 + Y1) / 2; } }
        public double W { get { return X1 - X0; } }
        public double H { get { return Y1 - Y0; } }
    }

    internal static class Faces
    {
        /// <summary>
        /// UltraFace's 4420 candidates -> the faces (score at least minScore, overlapping candidates merged, at most
        /// out.Length, best first). Its boxes are in the 4:3 picture the 16:9 frame was padded into.
        /// </summary>
        public static int Decode(float[] scores, float[] boxes, FaceBox[] found, double minScore = 0.75)
        {
            var cand = new List<FaceBox>();
            for (int i = 0; i < 4420; i++)
            {
                float s = scores[i * 2 + 1];
                if (s < minScore) continue;
                cand.Add(new FaceBox { X0 = boxes[i * 4], Y0 = (boxes[i * 4 + 1] * 240 - 30) / 180, X1 = boxes[i * 4 + 2], Y1 = (boxes[i * 4 + 3] * 240 - 30) / 180, Score = s });
            }
            cand.Sort((a, b) => b.Score.CompareTo(a.Score));
            int n = 0;
            foreach (var c in cand)
            {
                if (n >= found.Length) break;
                bool dup = false;
                for (int k = 0; k < n && !dup; k++) dup = IoU(found[k], c) > 0.3;
                if (!dup && c.W > 0.005 && c.H > 0.005) found[n++] = c;
            }
            return n;
        }

        public static double IoU(FaceBox a, FaceBox b)
        {
            double ix = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0)), iy = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
            double i = ix * iy, u = a.W * a.H + b.W * b.H - i;
            return u > 0 ? i / u : 0;
        }
    }

    /// <summary>3x3 homographies, row-major double[9].</summary>
    internal static class Homography
    {
        /// <summary>The homography taking (0,0) (1,0) (1,1) (0,1) to q[0..3] (x, y pairs).</summary>
        public static double[] FromUnitSquare(double[] q)
        {
            // Heckbert's closed form for the square-to-quad mapping.
            double x0 = q[0], y0 = q[1], x1 = q[2], y1 = q[3], x2 = q[4], y2 = q[5], x3 = q[6], y3 = q[7];
            double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
            double g, hh;
            if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12) { g = 0; hh = 0; }
            else
            {
                double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
                double den = dx1 * dy2 - dx2 * dy1;
                if (Math.Abs(den) < 1e-15) den = 1e-15;
                g = (sx * dy2 - dx2 * sy) / den;
                hh = (dx1 * sy - sx * dy1) / den;
            }
            return new[] { x1 - x0 + g * x1, x3 - x0 + hh * x3, x0, y1 - y0 + g * y1, y3 - y0 + hh * y3, y0, g, hh, 1.0 };
        }

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        public static double[] Invert(double[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-18) det = 1e-18;
            return new[] { A / det, -(b * i - c * h) / det, (b * f - c * e) / det, B / det, (a * i - c * g) / det, -(a * f - c * d) / det, C / det, -(a * h - b * g) / det, (a * e - b * d) / det };
        }

        public static void Apply(double[] m, double x, double y, out double X, out double Y)
        {
            double w = m[6] * x + m[7] * y + m[8];
            if (Math.Abs(w) < 1e-12) w = 1e-12;
            X = (m[0] * x + m[1] * y + m[2]) / w; Y = (m[3] * x + m[4] * y + m[5]) / w;
        }
    }

    /// <summary>
    /// The camera operator ("virtual gimbal"): decides every frame which part of the camera's picture apps see, and
    /// how. A view is the four points of the camera frame that the output's corners show (any quadrilateral), so the
    /// same machinery pans, zooms, levels, turns an upside-down camera round and squares off a whiteboard or a desk.
    /// What it does, like an Insta360 Link 2 in software:
    /// - tracking one person (the one you click, else whoever it followed, else the closest) or a group (everyone in);
    /// - framing them as a head shot, half body or whole body (or your own zoom), with your headroom;
    /// - a tracking zone it never looks outside of, and up to six pause zones: step into one and it frames that area
    ///   and holds still until you leave (a whiteboard, a product);
    /// - with tracking off: your own pan and zoom (drag / wheel on the preview), and six presets;
    /// - whiteboard (found automatically) and desk view (an area you mark), both squared off;
    /// - gestures: open palm = tracking on/off, V = whiteboard (or air drawing), L = zoom (move your hand up or down);
    ///   none of them turns tracking on except the palm, when you show it.
    /// It moves smoothly (follow speed), ignores small movements, and eases back out when nobody's there.
    /// </summary>
    internal sealed class CameraDirector
    {
        public const int MaxPresets = 6, MaxPauseZones = 6;
        public const double MaxZoom = 4;

        // Current and target corners of the view, in camera pixels (TL, TR, BR, BL as x, y pairs).
        readonly double[] cur = new double[8], tgt = new double[8];
        bool started;
        // Tracking's own target (centre in 0..1, zoom) - changes only past the dead zone.
        double tx = 0.5, ty = 0.5, tz = 1;
        bool hasTarget;
        double selX = -1, selY = -1;          // the person picked by clicking (0..1, follows them)
        double gestureZoom = 1;               // L gesture: zoom on top of the framing (tracking)
        double boost;                          // fast moves for a moment (presets, mode changes)
        readonly Stopwatch clock = Stopwatch.StartNew();
        /// <summary>Seconds (tests set their own clock).</summary>
        internal Func<double> Now;
        double lastSeen = -1e9;
        string lastMode = "";
        int lastPause = -1;
        bool lastUpsideDown;
        readonly ConcurrentQueue<Action<CamFxSettings>> commands = new ConcurrentQueue<Action<CamFxSettings>>();

        /// <summary>The whiteboard the finder sees (camera 0..1 corners TL TR BR BL as x, y pairs), or null.</summary>
        public volatile double[] Board;
        /// <summary>The faces last seen, in output coordinates (0..1, before mirroring): for clicking and drawing.</summary>
        public volatile FaceBox[] OutputFaces = new FaceBox[0];
        /// <summary>Which of OutputFaces is being followed (-1 = none).</summary>
        public volatile int FollowedIndex = -1;
        /// <summary>What it's doing, in words (Camera page).</summary>
        public volatile string State = "";
        /// <summary>The settings were changed here (gesture, preset, pan/zoom): to be saved.</summary>
        public volatile bool SettingsChanged;
        /// <summary>The output's face ellipse (centre and radii, output 0..1) for skin smoothing, or radius 0.</summary>
        public double EllipseX = 0.5, EllipseY = 0.4, EllipseRx, EllipseRy;
        /// <summary>The followed face in output coordinates (face exposure), or width 0.</summary>
        public double FaceOutX0, FaceOutY0, FaceOutX1, FaceOutY1;
        /// <summary>Output (0..1) -> camera pixels, as of the last frame.</summary>
        public double[] OutToCam = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        public int Width = 1920, Height = 1080;

        // ------------------------------------------------------------------ commands (any thread)

        /// <summary>Follow the person at this point of the output (0..1, as apps see it: mirrored if the picture is).</summary>
        public void SelectAt(double u, double v) { commands.Enqueue(c => Select(c, u, v)); }
        /// <summary>Move the view by a share of the output (drag on the preview); tracking off.</summary>
        public void Pan(double du, double dv) { commands.Enqueue(c => PanBy(c, du, dv)); }
        /// <summary>Zoom by a factor around a point of the output (wheel on the preview); tracking off.</summary>
        public void ZoomAt(double factor, double u, double v) { commands.Enqueue(c => ZoomBy(c, factor, u, v)); }
        public void RecallPreset(int i) { commands.Enqueue(c => Recall(c, i)); }
        public void SavePreset(int i, string name) { commands.Enqueue(c => Save(c, i, name)); }
        public void ToggleTracking() { commands.Enqueue(c => { c.FaceTracking = !c.FaceTracking; gestureZoom = 1; boost = 1; SettingsChanged = true; }); }
        public void SetViewMode(string mode) { commands.Enqueue(c => { c.ViewMode = c.ViewMode == mode ? "normal" : mode; boost = 1; SettingsChanged = true; }); }
        /// <summary>The L gesture: zoom by this factor (tracking: on top of the framing; manual: the view's zoom).</summary>
        public void GestureZoom(double factor)
        {
            commands.Enqueue(c =>
            {
                if (c.FaceTracking) gestureZoom = Clamp(gestureZoom * factor, 0.5, 3);
                else { c.Zoom = (int)Clamp(c.Zoom * factor, 100, MaxZoom * 100); SettingsChanged = true; }
            });
        }
        /// <summary>Back to the plain view (Reset view).</summary>
        public void ResetView() { commands.Enqueue(c => { c.ManualX = 0.5; c.ManualY = 0.5; c.Zoom = 100; gestureZoom = 1; selX = -1; boost = 1; SettingsChanged = true; }); }

        // ------------------------------------------------------------------ every frame

        /// <summary>
        /// The view for this frame: fills crop (top, left, height, width in camera pixels, even) and warp (output 0..1 ->
        /// crop 0..1). faces: what UltraFace saw in the last frame.
        /// </summary>
        public void Step(CamFxSettings c, FaceBox[] faces, int n, int W, int H, long[] crop, float[] warp)
        {
            Width = W; Height = H;
            Action<CamFxSettings> cmd;
            while (commands.TryDequeue(out cmd)) cmd(c);
            double now = Now != null ? Now() : clock.Elapsed.TotalSeconds;
            string mode = c.ViewMode ?? "normal";
            if (mode != lastMode) { boost = 1; lastMode = mode; }

            double[] want = null;
            string state;
            var vis = Visible(c, faces, n);
            if (mode == "whiteboard" && Board != null) { want = QuadView(c, Board, true); state = "Whiteboard"; }
            else if (mode == "desk" && c.DeskQuad != null && c.DeskQuad.Length == 8) { want = QuadView(c, c.DeskQuad, false); state = "Desk view"; }
            else
            {
                state = "";
                if (c.FaceTracking) { want = Track(c, faces, n, vis, now, ref state); }
                else
                {
                    want = Rect(c, c.ManualX, c.ManualY, Math.Max(1, c.Zoom / 100.0));
                    state = c.Zoom > 100 ? "Manual view, " + (c.Zoom / 100.0).ToString("0.0") + "x" : "";
                }
                if (mode == "whiteboard") state = "No whiteboard in sight (it needs clear edges, whole in the picture)" + (state != "" ? " - " + state : "");
            }
            Array.Copy(want, tgt, 8);
            // Turned upside down (or back): at once - gliding there would shrink the picture through a point.
            if (!started || c.UpsideDown != lastUpsideDown) { Array.Copy(tgt, cur, 8); started = true; }
            lastUpsideDown = c.UpsideDown;
            double k = 0.01 + 0.12 * (c.TrackSpeed / 100.0) * (c.TrackSpeed / 100.0);
            k = Math.Max(k, 0.18 * boost);
            boost = Math.Max(0, boost - 0.04);
            for (int i = 0; i < 8; i++) cur[i] += (tgt[i] - cur[i]) * k;
            State = state;
            Output(c, faces, n, vis, crop, warp);
        }

        /// <summary>Faces worth following: confident and not tiny next to the biggest (people far behind you).</summary>
        static List<int> Visible(CamFxSettings c, FaceBox[] faces, int n)
        {
            var l = new List<int>();
            double big = 0;
            for (int i = 0; i < n; i++) big = Math.Max(big, faces[i].H);
            for (int i = 0; i < n; i++) if (faces[i].H > big * 0.4 && InZone(c, faces[i].Cx, faces[i].Cy)) l.Add(i);
            return l;
        }

        static bool InZone(CamFxSettings c, double x, double y)
        {
            var z = c.TrackZone;
            return !c.TrackZoneOn || z == null || z.Length != 4 || (x >= z[0] && x <= z[2] && y >= z[1] && y <= z[3]);
        }

        double[] Track(CamFxSettings c, FaceBox[] faces, int n, List<int> vis, double now, ref string state)
        {
            bool group = c.TrackMode == "group";
            int best = -1;
            if (vis.Count > 0)
            {
                // The one picked by clicking, else the one followed so far, else the closest (biggest, most confident).
                double bestScore = double.MinValue;
                foreach (int i in vis)
                {
                    var f = faces[i];
                    double stay = selX >= 0 ? Math.Sqrt((f.Cx - selX) * (f.Cx - selX) + (f.Cy - selY) * (f.Cy - selY)) : 0;
                    double score = f.Score + f.W * f.H * 2 - stay * (selX >= 0 ? 4 : 1.5);
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                var b = faces[best];
                // Follow the chosen person as they move.
                selX = selX < 0 ? b.Cx : selX + 0.5 * (b.Cx - selX); selY = selY < 0 ? b.Cy : selY + 0.5 * (b.Cy - selY);
                lastSeen = now;
            }
            FollowedIndex = best;
            // Pause zones: the person is in one -> that area, held still, until they leave it.
            if (best >= 0 && !group && c.PauseZones != null)
            {
                int zi = -1;
                for (int i = 0; i < c.PauseZones.Count && i < MaxPauseZones; i++)
                {
                    var z = c.PauseZones[i];
                    if (z != null && z.Length == 4 && faces[best].Cx >= z[0] && faces[best].Cx <= z[2] && faces[best].Cy >= z[1] && faces[best].Cy <= z[3]) { zi = i; break; }
                }
                if (zi >= 0)
                {
                    if (zi != lastPause) boost = 0.6;
                    lastPause = zi;
                    var z = c.PauseZones[zi];
                    state = "Holding still (pause zone " + (zi + 1) + ")";
                    double zw = z[2] - z[0], zh = z[3] - z[1];
                    double zoom = 1 / Math.Max(zw, zh) / 1.08;   // the whole zone, a little room around it
                    return Rect(c, (z[0] + z[2]) / 2, (z[1] + z[3]) / 2, zoom);
                }
            }
            lastPause = -1;

            if (vis.Count == 0)
            {
                // Nobody (or only outside the tracking zone): hold for 3 s, then ease out to the whole (zone) view.
                if (now - lastSeen > 3) { hasTarget = false; selX = -1; }
                state = hasTarget ? "Holding - lost you for a moment" : "Waiting for a face";
                if (!hasTarget) return Home(c);
                return Rect(c, tx, ty, tz);
            }

            double fx, fy, fw, fh;
            if (group)
            {
                double x0 = 1, y0 = 1, x1 = 0, y1 = 0, sh = 0;
                foreach (int i in vis) { var f = faces[i]; x0 = Math.Min(x0, f.X0); y0 = Math.Min(y0, f.Y0); x1 = Math.Max(x1, f.X1); y1 = Math.Max(y1, f.Y1); sh += f.H; }
                fx = (x0 + x1) / 2; fy = (y0 + y1) / 2; fw = x1 - x0; fh = sh / vis.Count;
                state = "Tracking " + vis.Count + (vis.Count == 1 ? " person" : " people");
                // Everyone in, with room around them.
                double avgW = fw / Math.Max(1, vis.Count);
                double need = Math.Max(fw + 3 * Math.Max(avgW, fh * 0.8), (y1 - y0) + 2.5 * fh);
                double zComp = Composition(c, fh);
                double zz = Math.Min(zComp, 1 / Math.Max(need, 1e-3));
                return Aim(c, fx, fy, fh, zz);
            }
            else
            {
                var f = faces[best];
                fx = f.Cx; fy = f.Cy; fw = f.W; fh = f.H;
                state = "Tracking" + (vis.Count > 1 ? " (" + vis.Count + " people - click one to switch)" : "");
                return Aim(c, fx, fy, fh, Composition(c, fh));
            }
        }

        /// <summary>The zoom that frames a face of this height (0..1 of the camera's height) as the framing setting says.</summary>
        double Composition(CamFxSettings c, double faceH)
        {
            double share;   // the face's height as a share of the picture's height
            switch (c.Framing)
            {
                case "head": share = 0.30; break;
                case "body": share = 0.075; break;
                case "custom": return Clamp(c.Zoom / 100.0 * gestureZoom, 1, MaxZoom);
                default: share = 0.15; break;   // half body
            }
            return Clamp(share / Math.Max(faceH, 1e-3) * gestureZoom, 1, MaxZoom);
        }

        /// <summary>Where the face sits from the top of the picture (0..1) for the framing, moved by headroom.</summary>
        static double FacePlace(CamFxSettings c)
        {
            double y;
            switch (c.Framing)
            {
                case "head": y = 0.42; break;
                case "body": y = 0.16; break;
                case "custom": y = 0.39; break;
                default: y = 0.32; break;
            }
            return Clamp(y + (c.Headroom - 50) / 100.0 * 0.24, 0.08, 0.7);
        }

        double[] Aim(CamFxSettings c, double fx, double fy, double fh, double zoom)
        {
            double ny = fy + (0.5 - FacePlace(c)) / zoom;
            double half = 0.5 / zoom;
            // Dead zone: talking and nodding don't move the view; a real move does (then it glides).
            if (!hasTarget || Math.Abs(fx - tx) > half * 0.18 || Math.Abs(ny - ty) > half * 0.18 || Math.Abs(zoom - tz) / tz > 0.08)
            { tx = fx; ty = ny; tz = zoom; hasTarget = true; }
            return Rect(c, tx, ty, tz);
        }

        double[] Home(CamFxSettings c)
        {
            var z = c.TrackZone;
            if (c.TrackZoneOn && z != null && z.Length == 4) return Rect(c, (z[0] + z[2]) / 2, (z[1] + z[3]) / 2, 1 / Math.Max(z[2] - z[0], z[3] - z[1]));
            return Rect(c, 0.5, 0.5, 1);
        }

        /// <summary>
        /// A level view: centre (0..1), zoom, turned by the level setting (and 180 degrees for an upside-down camera),
        /// kept inside the camera's picture (and inside the tracking zone when there is one). Corners TL TR BR BL in
        /// camera pixels, as the viewer sees them.
        /// </summary>
        double[] Rect(CamFxSettings c, double cx, double cy, double zoom)
        {
            double W = Width, H = Height;
            double a = (c.Level + (c.UpsideDown ? 180 : 0)) * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
            double aca = Math.Abs(ca), asa = Math.Abs(sa);
            // Turned, the view needs a little zoom to stay inside the picture.
            double zmin = Math.Max(aca + H / W * asa, W / H * asa + aca);
            double z = Clamp(zoom, zmin, MaxZoom);
            double vw = W / z, vh = H / z;
            double ex = vw / 2 * aca + vh / 2 * asa, ey = vw / 2 * asa + vh / 2 * aca;
            double lo = 0, hi = W, top = 0, bot = H;
            var tz = c.TrackZone;
            if (c.FaceTracking && c.TrackZoneOn && tz != null && tz.Length == 4 && c.ViewMode != "desk")
            {
                lo = tz[0] * W; hi = tz[2] * W; top = tz[1] * H; bot = tz[3] * H;
                // A zone narrower than the view: zoom in until the view fits in it.
                double need = Math.Max(2 * ex / Math.Max(1, hi - lo), 2 * ey / Math.Max(1, bot - top));
                if (need > 1) { z = Math.Min(MaxZoom, z * need); vw = W / z; vh = H / z; ex = vw / 2 * aca + vh / 2 * asa; ey = vw / 2 * asa + vh / 2 * aca; }
            }
            double px = Clamp(cx * W, lo + ex, hi - ex), py = Clamp(cy * H, top + ey, bot - ey);
            if (hi - lo < 2 * ex) px = (lo + hi) / 2;
            if (bot - top < 2 * ey) py = (top + bot) / 2;
            var r = new double[8];
            double[] us = { -0.5, 0.5, 0.5, -0.5 }, vs = { -0.5, -0.5, 0.5, 0.5 };
            for (int i = 0; i < 4; i++)
            {
                double dx = us[i] * vw, dy = vs[i] * vh;
                r[i * 2] = px + dx * ca - dy * sa; r[i * 2 + 1] = py + dx * sa + dy * ca;
            }
            return r;
        }

        /// <summary>
        /// A squared-off view of a quadrilateral (camera 0..1 corners TL TR BR BL as the viewer sees them). A board fills
        /// the output's height (or width) at its own proportions, the wall around it fills the rest; the desk area you
        /// marked fills the output exactly (it often reaches the picture's edge, and a desk seen at a slant looks
        /// shorter than it is anyway).
        /// </summary>
        double[] QuadView(CamFxSettings c, double[] q01, bool board)
        {
            double W = Width, H = Height;
            var q = new double[8];
            for (int i = 0; i < 4; i++) { q[i * 2] = q01[i * 2] * W; q[i * 2 + 1] = q01[i * 2 + 1] * H; }
            if (board && c.UpsideDown)
            {
                // The finder reports corners by their place in the camera's picture: upside down, top-left is bottom-right.
                var t = (double[])q.Clone();
                for (int i = 0; i < 4; i++) { q[i * 2] = t[((i + 2) % 4) * 2]; q[i * 2 + 1] = t[((i + 2) % 4) * 2 + 1]; }
            }
            Func<int, int, double> len = (i, j) => Math.Sqrt((q[i * 2] - q[j * 2]) * (q[i * 2] - q[j * 2]) + (q[i * 2 + 1] - q[j * 2 + 1]) * (q[i * 2 + 1] - q[j * 2 + 1]));
            double aspect = (len(0, 1) + len(3, 2)) / Math.Max(1, len(0, 3) + len(1, 2));   // width : height of the quad
            const double outAspect = 16.0 / 9, margin = 1.04;
            // Output (0..1) in the quad's own coordinates: centred, the quad as big as fits, plus a 4% margin.
            double su = 1, sv = 1;
            if (!board) { }
            else if (aspect < outAspect) { sv = margin; su = margin * outAspect / aspect; }
            else { su = margin; sv = margin * aspect / outAspect; }
            var hq = Homography.FromUnitSquare(q);
            var r = new double[8];
            double[] us = { 0, 1, 1, 0 }, vs = { 0, 0, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                double u = 0.5 + (us[i] - 0.5) * su, v = 0.5 + (vs[i] - 0.5) * sv;
                double X, Y; Homography.Apply(hq, u, v, out X, out Y);
                r[i * 2] = X; r[i * 2 + 1] = Y;
            }
            return r;
        }

        void Output(CamFxSettings c, FaceBox[] faces, int n, List<int> vis, long[] crop, float[] warp)
        {
            double W = Width, H = Height;
            // The crop: the view's bounding box (+2 pixels for the filter), even numbers, inside the picture.
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < 4; i++) { x0 = Math.Min(x0, cur[i * 2]); x1 = Math.Max(x1, cur[i * 2]); y0 = Math.Min(y0, cur[i * 2 + 1]); y1 = Math.Max(y1, cur[i * 2 + 1]); }
            long left = Math.Max(0, (long)Math.Floor((x0 - 2) / 2) * 2), top = Math.Max(0, (long)Math.Floor((y0 - 2) / 2) * 2);
            long right = Math.Min((long)W, (long)Math.Ceiling((x1 + 2) / 2) * 2), bottom = Math.Min((long)H, (long)Math.Ceiling((y1 + 2) / 2) * 2);
            if (right - left < 16) { left = Math.Max(0, Math.Min((long)W - 16, left)); right = left + 16; }
            if (bottom - top < 16) { top = Math.Max(0, Math.Min((long)H - 16, top)); bottom = top + 16; }
            crop[0] = top; crop[1] = left; crop[2] = bottom - top; crop[3] = right - left;
            var toCam = Homography.FromUnitSquare(cur);
            OutToCam = toCam;
            double cw = right - left, ch = bottom - top;
            var toCrop = new[] { 1 / cw, 0, -left / cw, 0, 1 / ch, -top / ch, 0, 0, 1.0 };
            var m = Homography.Mul(toCrop, toCam);
            for (int i = 0; i < 9; i++) warp[i] = (float)(m[i] / m[8]);

            // Faces in output coordinates (clicking, drawing, skin smoothing, face exposure).
            var inv = Homography.Invert(toCam);
            var outs = new FaceBox[n];
            for (int i = 0; i < n; i++)
            {
                double ax, ay, bx, by;
                Homography.Apply(inv, faces[i].X0 * W, faces[i].Y0 * H, out ax, out ay);
                Homography.Apply(inv, faces[i].X1 * W, faces[i].Y1 * H, out bx, out by);
                outs[i] = new FaceBox { X0 = Math.Min(ax, bx), Y0 = Math.Min(ay, by), X1 = Math.Max(ax, bx), Y1 = Math.Max(ay, by), Score = faces[i].Score };
            }
            OutputFaces = outs;
            int main = FollowedIndex >= 0 && FollowedIndex < n ? FollowedIndex : vis.Count > 0 ? vis[0] : -1;
            if (main >= 0)
            {
                var f = outs[main];
                EllipseX = f.Cx; EllipseY = f.Cy; EllipseRx = f.W * 0.62; EllipseRy = f.H * 0.72;
                FaceOutX0 = f.X0; FaceOutY0 = f.Y0; FaceOutX1 = f.X1; FaceOutY1 = f.Y1;
            }
            else { EllipseX = 0.5; EllipseY = 0.4; EllipseRx = EllipseRy = 1e-4; FaceOutX0 = FaceOutX1 = 0; }
        }

        /// <summary>A point of the camera's picture (0..1) -> where it shows in the output (0..1, before mirroring), as of
        /// the last frame (air drawing: the fingertip's place on the picture apps see).</summary>
        public void CameraToOutput(double x, double y, out double u, out double v)
        {
            var inv = Homography.Invert(OutToCam);
            Homography.Apply(inv, x * Width, y * Height, out u, out v);
        }

        // ------------------------------------------------------------------ commands, on the camera thread

        void Select(CamFxSettings c, double u, double v)
        {
            if (c.Mirror && (c.ViewMode ?? "normal") == "normal") u = 1 - u;
            double X, Y; Homography.Apply(OutToCam, u, v, out X, out Y);
            X /= Width; Y /= Height;
            // The face under (or nearest to) the click.
            var outs = OutputFaces; double best = double.MaxValue; int bi = -1;
            for (int i = 0; i < outs.Length; i++)
            {
                double d = Math.Max(0, Math.Abs(u - outs[i].Cx) - outs[i].W / 2) + Math.Max(0, Math.Abs(v - outs[i].Cy) - outs[i].H / 2);
                if (d < best) { best = d; bi = i; }
            }
            if (bi < 0 || best > 0.15) return;
            double cx, cy; Homography.Apply(OutToCam, outs[bi].Cx, outs[bi].Cy, out cx, out cy);
            selX = cx / Width; selY = cy / Height; hasTarget = false;
            if (!c.FaceTracking) { c.FaceTracking = true; SettingsChanged = true; }
        }

        void PanBy(CamFxSettings c, double du, double dv)
        {
            if (c.FaceTracking) { c.FaceTracking = false; c.ManualX = tx; c.ManualY = ty; c.Zoom = (int)(tz * 100); }
            if (c.Mirror) du = -du;
            double z = Math.Max(1, c.Zoom / 100.0);
            double a = (c.Level + (c.UpsideDown ? 180 : 0)) * Math.PI / 180;
            // A drag moves the picture with the mouse: the view goes the other way, in the view's own (turned) axes.
            double dx = -du / z, dy = -dv / z;
            c.ManualX = Clamp(c.ManualX + dx * Math.Cos(a) - dy * Math.Sin(a) * Height / Width, 0, 1);
            c.ManualY = Clamp(c.ManualY + dx * Math.Sin(a) * Width / Height + dy * Math.Cos(a), 0, 1);
            Recentre(c);
            SettingsChanged = true;
        }

        void ZoomBy(CamFxSettings c, double factor, double u, double v)
        {
            if (c.FaceTracking) { c.FaceTracking = false; c.ManualX = tx; c.ManualY = ty; c.Zoom = (int)(tz * 100); }
            if (c.Mirror) u = 1 - u;
            double X, Y; Homography.Apply(OutToCam, u, v, out X, out Y);
            double z0 = Math.Max(1, c.Zoom / 100.0), z1 = Clamp(z0 * factor, 1, MaxZoom);
            // Keep the point under the mouse where it is.
            double px = X / Width, py = Y / Height;
            c.ManualX = px + (c.ManualX - px) * z0 / z1; c.ManualY = py + (c.ManualY - py) * z0 / z1;
            c.Zoom = (int)Math.Round(z1 * 100);
            Recentre(c);
            SettingsChanged = true;
        }

        /// <summary>The manual centre where the view can actually be (so a drag back moves at once).</summary>
        void Recentre(CamFxSettings c)
        {
            var r = Rect(c, c.ManualX, c.ManualY, Math.Max(1, c.Zoom / 100.0));
            c.ManualX = (r[0] + r[4]) / 2 / Width; c.ManualY = (r[1] + r[5]) / 2 / Height;
        }

        void Recall(CamFxSettings c, int i)
        {
            if (c.Presets == null || i < 0 || i >= c.Presets.Count || c.Presets[i] == null || !c.Presets[i].Used) return;
            var p = c.Presets[i];
            c.FaceTracking = false; c.ViewMode = "normal";
            c.ManualX = p.X; c.ManualY = p.Y; c.Zoom = p.Zoom;
            boost = 1; SettingsChanged = true;
        }

        void Save(CamFxSettings c, int i, string name)
        {
            if (i < 0 || i >= MaxPresets) return;
            if (c.Presets == null) c.Presets = new List<CamPreset>();
            while (c.Presets.Count <= i) c.Presets.Add(new CamPreset());
            // What apps see right now (also while tracking).
            double X, Y; Homography.Apply(OutToCam, 0.5, 0.5, out X, out Y);
            double vw = Math.Sqrt((cur[2] - cur[0]) * (cur[2] - cur[0]) + (cur[3] - cur[1]) * (cur[3] - cur[1]));
            c.Presets[i] = new CamPreset { Used = true, Name = string.IsNullOrEmpty(name) ? "Preset " + (i + 1) : name, X = X / Width, Y = Y / Height, Zoom = (int)Math.Round(Clamp(Width / Math.Max(1, vw), 1, MaxZoom) * 100) };
            SettingsChanged = true;
        }

        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
