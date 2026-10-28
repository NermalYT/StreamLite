using System;
using System.Collections.Generic;

namespace StreamLite.Camera
{
    /// <summary>
    /// Air drawing: your index fingertip is a light pen. While your hand points (index finger out, the others curled)
    /// it leaves a thick line on the picture apps see - smoothed (a One Euro filter on the fingertip) and joined into
    /// a continuous curve between looks (centripetal Catmull-Rom), with round ends. Lines are kept in the output
    /// picture's own pixels (1920 x 1080, as apps see it - mirrored or not), so they stay where you drew them.
    /// Clear wipes them; with a fade time they fade out that long after each line.
    /// Fed by the gesture thread (Feed), drawn by the camera thread (Render).
    /// </summary>
    internal sealed class AirDrawing
    {
        public const int W = CameraFx.OW, H = CameraFx.OH;
        const int MaxStrokes = 300, MaxPoints = 4000;
        /// <summary>A pointing finger counts as the pen after this many looks in a row (a pose passing through isn't ink).</summary>
        internal const int PenLooks = 2;

        sealed class Stroke
        {
            public readonly List<double> P = new List<double>();   // x, y pairs (output pixels, filtered)
            public bool Done;
            public double EndT;    // when the last point came (fading)
            public int Final;      // segments (or the dot) already in the ink layer
            public float Alpha = 1;
        }

        readonly object gate = new object();
        readonly List<Stroke> strokes = new List<Stroke>();
        Stroke cur;
        readonly OneEuro2D filter = new OneEuro2D(1.5, 0.012, 1.0);
        int pointLooks; double lastFeed = -1e9, lastHand = -1e9;
        // The ink layer (finished segments) and the end of the line being drawn, as coverage 0..255, with the boxes
        // that hold anything.
        byte[] ink, tail;
        int ix0 = int.MaxValue, iy0 = int.MaxValue, ix1 = -1, iy1 = -1, tx0 = int.MaxValue, ty0 = int.MaxValue, tx1 = -1, ty1 = -1;
        bool dirty = true;
        int color = -1, size = -1, penY, penU, penV;
        volatile bool on;

        /// <summary>Air drawing is on (V gesture or hotkey): a pointing finger draws.</summary>
        public bool On { get { return on; } }
        /// <summary>The finger is drawing right now.</summary>
        public volatile bool PenDown;
        public int Strokes { get { lock (gate) return strokes.Count; } }
        /// <summary>Tests: off = the points drawn exactly as fed (to see the joining on its own).</summary>
        internal bool Smoothing = true;

        public void SetOn(bool value) { lock (gate) { on = value; pointLooks = 0; EndStroke(); } }

        public void Clear() { lock (gate) { strokes.Clear(); cur = null; PenDown = false; pointLooks = 0; dirty = true; } }

        /// <summary>The picture was turned upside down (or back): the drawing turns with it.</summary>
        public void Rotate180()
        {
            lock (gate)
            {
                foreach (var s in strokes) for (int i = 0; i < s.P.Count; i += 2) { s.P[i] = W - s.P[i]; s.P[i + 1] = H - s.P[i + 1]; }
                filter.Reset();
                dirty = true;
            }
        }

        /// <summary>
        /// One look at the hand (any thread): the fingertip on the output picture (0..1, as apps see it; NaN = no hand),
        /// whether the hand points (and pointing may draw), and when the frame was taken (seconds).
        /// </summary>
        public void Feed(double u, double v, bool pointing, double t)
        {
            lock (gate)
            {
                lastFeed = t;
                if (double.IsNaN(u) || double.IsNaN(v)) { pointLooks = 0; EndStroke(); return; }
                if (t - lastHand > 0.25) filter.Reset();   // the hand is back after a while: no smoothing from where it was
                lastHand = t;
                double x = u * W, y = v * H;
                if (Smoothing) filter.Filter(x, y, t, out x, out y);
                pointLooks = pointing && on ? pointLooks + 1 : 0;
                if (pointLooks < PenLooks) { EndStroke(); return; }
                if (cur == null)
                {
                    cur = new Stroke();
                    strokes.Add(cur);
                    if (strokes.Count > MaxStrokes) { strokes.RemoveAt(0); dirty = true; }
                    cur.P.Add(x); cur.P.Add(y);
                }
                else
                {
                    int n = cur.P.Count;
                    double dx = x - cur.P[n - 2], dy = y - cur.P[n - 1];
                    if (dx * dx + dy * dy >= 2.25) { cur.P.Add(x); cur.P.Add(y); }
                }
                cur.EndT = t;
                PenDown = true;
                if (cur.P.Count >= MaxPoints * 2) EndStroke();
            }
        }

        void EndStroke()
        {
            if (cur != null) { cur.Done = true; cur = null; }
            PenDown = false;
        }

        // ------------------------------------------------------------------ drawing (camera thread)

        /// <summary>
        /// Draws the lines onto the output picture (NV12, 1920 x 1080) at time now (seconds, the same clock as Feed).
        /// False: nothing to draw.
        /// </summary>
        public bool Render(byte[] y, byte[] uv, CamFxSettings c, double now)
        {
            lock (gate)
            {
                if (PenDown && now - lastFeed > 0.3) EndStroke();   // the hand stopped being looked at: the line ends
                if (ink == null) { if (strokes.Count == 0) return false; ink = new byte[W * H]; tail = new byte[W * H]; }
                // Fading: each finished line fades over a second, InkFadeSeconds after it was drawn.
                for (int i = strokes.Count - 1; i >= 0; i--)
                {
                    var s = strokes[i];
                    float a = 1;
                    if (c.InkFadeSeconds > 0 && s.Done)
                    {
                        double age = now - s.EndT - c.InkFadeSeconds;
                        if (age >= 1) { strokes.RemoveAt(i); dirty = true; continue; }
                        if (age > 0) a = (float)(1 - age);
                    }
                    if (a != s.Alpha) { s.Alpha = a; dirty = true; }
                }
                if (c.PenColor != color || c.PenSize != size)
                {
                    color = c.PenColor; size = c.PenSize; dirty = true;
                    ToYuv(color, out penY, out penU, out penV);
                }
                if (dirty)
                {
                    if (ix1 >= 0) for (int yy = iy0; yy <= iy1; yy++) Array.Clear(ink, yy * W + ix0, ix1 - ix0 + 1);
                    ix0 = iy0 = int.MaxValue; ix1 = iy1 = -1;
                    foreach (var s in strokes) s.Final = 0;
                    dirty = false;
                }
                double r = size / 2.0;
                foreach (var s in strokes) DrawFinal(s, r);
                if (tx1 >= 0) for (int yy = ty0; yy <= ty1; yy++) Array.Clear(tail, yy * W + tx0, tx1 - tx0 + 1);
                tx0 = ty0 = int.MaxValue; tx1 = ty1 = -1;
                if (cur != null) DrawTail(cur, r);
                if (ix1 < 0 && tx1 < 0) return false;
                Composite(y, uv);
                return true;
            }
        }

        /// <summary>The segments of a line that won't change any more, into the ink layer: all once it's finished,
        /// else all but the last (its curve still depends on the next point).</summary>
        void DrawFinal(Stroke s, double r)
        {
            int n = s.P.Count / 2;
            if (n == 1)
            {
                if (s.Done && s.Final == 0) { Capsule(s.P[0], s.P[1], s.P[0], s.P[1], r, s.Alpha, true); s.Final = 1; }
                return;
            }
            int last = s.Done ? n - 2 : n - 3;
            for (int i = s.Final; i <= last; i++) Segment(s, i, r, true);
            if (last + 1 > s.Final) s.Final = last + 1;
        }

        void DrawTail(Stroke s, double r)
        {
            int n = s.P.Count / 2;
            if (n == 1) Capsule(s.P[0], s.P[1], s.P[0], s.P[1], r, 1, false);
            else Segment(s, n - 2, r, false);
        }

        /// <summary>Segment i of a line (point i to i + 1) as a centripetal Catmull-Rom curve, drawn as short round-capped
        /// pieces. At the line's ends the missing neighbour is guessed by carrying on the curve of the three nearest
        /// points (a straight carry-on with only two).</summary>
        void Segment(Stroke s, int i, double r, bool toInk)
        {
            var P = s.P; int n = P.Count / 2;
            double x1 = P[i * 2], y1 = P[i * 2 + 1], x2 = P[i * 2 + 2], y2 = P[i * 2 + 3];
            double x0, y0, x3, y3;
            if (i > 0) { x0 = P[i * 2 - 2]; y0 = P[i * 2 - 1]; }
            else if (n > 2) { x0 = 3 * x1 - 3 * x2 + P[4]; y0 = 3 * y1 - 3 * y2 + P[5]; }
            else { x0 = 2 * x1 - x2; y0 = 2 * y1 - y2; }
            if (i + 2 < n) { x3 = P[i * 2 + 4]; y3 = P[i * 2 + 5]; }
            else if (i > 0) { x3 = 3 * x2 - 3 * x1 + P[i * 2 - 2]; y3 = 3 * y2 - 3 * y1 + P[i * 2 - 1]; }
            else { x3 = 2 * x2 - x1; y3 = 2 * y2 - y1; }
            double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 5));
            double px = x1, py = y1;
            for (int k = 1; k <= steps; k++)
            {
                double qx, qy;
                CatmullRom(x0, y0, x1, y1, x2, y2, x3, y3, k / (double)steps, out qx, out qy);
                Capsule(px, py, qx, qy, r, s.Alpha, toInk);
                px = qx; py = qy;
            }
        }

        /// <summary>Centripetal Catmull-Rom (Barry-Goldman) between p1 and p2 at u (0..1): no loops or overshoot when
        /// the points come unevenly spaced.</summary>
        internal static void CatmullRom(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double u, out double x, out double y)
        {
            double t0 = 0, t1 = t0 + Knot(x0, y0, x1, y1), t2 = t1 + Knot(x1, y1, x2, y2), t3 = t2 + Knot(x2, y2, x3, y3);
            double t = t1 + (t2 - t1) * u;
            double a1x = ((t1 - t) * x0 + (t - t0) * x1) / (t1 - t0), a1y = ((t1 - t) * y0 + (t - t0) * y1) / (t1 - t0);
            double a2x = ((t2 - t) * x1 + (t - t1) * x2) / (t2 - t1), a2y = ((t2 - t) * y1 + (t - t1) * y2) / (t2 - t1);
            double a3x = ((t3 - t) * x2 + (t - t2) * x3) / (t3 - t2), a3y = ((t3 - t) * y2 + (t - t2) * y3) / (t3 - t2);
            double b1x = ((t2 - t) * a1x + (t - t0) * a2x) / (t2 - t0), b1y = ((t2 - t) * a1y + (t - t0) * a2y) / (t2 - t0);
            double b2x = ((t3 - t) * a2x + (t - t1) * a3x) / (t3 - t1), b2y = ((t3 - t) * a2y + (t - t1) * a3y) / (t3 - t1);
            x = ((t2 - t) * b1x + (t - t1) * b2x) / (t2 - t1); y = ((t2 - t) * b1y + (t - t1) * b2y) / (t2 - t1);
        }

        static double Knot(double ax, double ay, double bx, double by) { return Math.Max(1e-3, Math.Pow((bx - ax) * (bx - ax) + (by - ay) * (by - ay), 0.25)); }

        /// <summary>A round-capped line piece of radius r into the ink layer or the tail (coverage kept at the maximum,
        /// so overlapping pieces never darken the edges), soft by one pixel at its edge.</summary>
        void Capsule(double ax, double ay, double bx, double by, double r, float alpha, bool toInk)
        {
            var buf = toInk ? ink : tail;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r - 1)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r + 1));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - r - 1)), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(ay, by) + r + 1));
            if (x0 > x1 || y0 > y1) return;
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double reach = (r + 0.5) * (r + 0.5);
            bool any = false;
            for (int yy = y0; yy <= y1; yy++)
            {
                double py = yy + 0.5 - ay;
                int row = yy * W;
                for (int xx = x0; xx <= x1; xx++)
                {
                    double px = xx + 0.5 - ax;
                    double t = l2 > 0 ? (px * dx + py * dy) / l2 : 0;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    double ex = px - t * dx, ey = py - t * dy, d2 = ex * ex + ey * ey;
                    if (d2 >= reach) continue;
                    double cov = r + 0.5 - Math.Sqrt(d2);
                    int v = cov >= 1 ? (int)(255 * alpha + 0.5) : (int)(255 * cov * alpha + 0.5);
                    if (v > buf[row + xx]) { buf[row + xx] = (byte)v; any = true; }
                }
            }
            if (!any) return;
            if (toInk) { ix0 = Math.Min(ix0, x0); iy0 = Math.Min(iy0, y0); ix1 = Math.Max(ix1, x1); iy1 = Math.Max(iy1, y1); }
            else { tx0 = Math.Min(tx0, x0); ty0 = Math.Min(ty0, y0); tx1 = Math.Max(tx1, x1); ty1 = Math.Max(ty1, y1); }
        }

        /// <summary>The ink layer and the tail over the picture in the pen's colour (luma per pixel, colour per 2 x 2).</summary>
        void Composite(byte[] y, byte[] uv)
        {
            int x0 = Math.Min(ix0, tx0) & ~1, y0 = Math.Min(iy0, ty0) & ~1, x1 = Math.Min(W - 1, Math.Max(ix1, tx1) | 1), y1 = Math.Min(H - 1, Math.Max(iy1, ty1) | 1);
            byte[] a = ink, b = tail;
            int pY = penY, pU = penU, pV = penV;
            System.Threading.Tasks.Parallel.For(y0 / 2, y1 / 2 + 1, cy =>
            {
                int r0 = cy * 2 * W, r1 = r0 + W, rc = cy * W;
                for (int xx = x0; xx <= x1; xx += 2)
                {
                    int c00 = Math.Max(a[r0 + xx], b[r0 + xx]), c01 = Math.Max(a[r0 + xx + 1], b[r0 + xx + 1]);
                    int c10 = Math.Max(a[r1 + xx], b[r1 + xx]), c11 = Math.Max(a[r1 + xx + 1], b[r1 + xx + 1]);
                    if ((c00 | c01 | c10 | c11) == 0) continue;
                    if (c00 > 0) y[r0 + xx] = (byte)(y[r0 + xx] + (pY - y[r0 + xx]) * c00 / 255);
                    if (c01 > 0) y[r0 + xx + 1] = (byte)(y[r0 + xx + 1] + (pY - y[r0 + xx + 1]) * c01 / 255);
                    if (c10 > 0) y[r1 + xx] = (byte)(y[r1 + xx] + (pY - y[r1 + xx]) * c10 / 255);
                    if (c11 > 0) y[r1 + xx + 1] = (byte)(y[r1 + xx + 1] + (pY - y[r1 + xx + 1]) * c11 / 255);
                    int ca = (c00 + c01 + c10 + c11 + 2) / 4;
                    int o = rc + xx;
                    uv[o] = (byte)(uv[o] + (pU - uv[o]) * ca / 255); uv[o + 1] = (byte)(uv[o + 1] + (pV - uv[o + 1]) * ca / 255);
                }
            });
        }

        /// <summary>An RGB colour (0xRRGGBB) as limited-range BT.709 Y, U, V - the virtual camera's NV12.</summary>
        internal static void ToYuv(int rgb, out int Y, out int U, out int V)
        {
            double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            Y = (int)Math.Round(16 + 219 * l);
            U = Math.Max(16, Math.Min(240, (int)Math.Round(128 + 224 * (b - l) / 1.8556)));
            V = Math.Max(16, Math.Min(240, (int)Math.Round(128 + 224 * (r - l) / 1.5748)));
        }
    }

    /// <summary>
    /// The One Euro filter (Casiez et al.) on a point: smooths hard while the point is still (the fingertip's jitter
    /// disappears), hardly at all while it moves fast (the line keeps up with your finger). Units: pixels, seconds.
    /// </summary>
    internal sealed class OneEuro2D
    {
        readonly double minCutoff, beta, dCutoff;
        double x, y, dx, dy, last;
        bool has;

        public OneEuro2D(double minCutoff, double beta, double dCutoff) { this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff; }

        public void Reset() { has = false; }

        public void Filter(double nx, double ny, double t, out double ox, out double oy)
        {
            if (!has) { x = nx; y = ny; dx = dy = 0; last = t; has = true; ox = x; oy = y; return; }
            double dt = Math.Max(1e-3, t - last); last = t;
            double ad = Alpha(dCutoff, dt);
            dx += ad * ((nx - x) / dt - dx); dy += ad * ((ny - y) / dt - dy);
            double a = Alpha(minCutoff + beta * Math.Sqrt(dx * dx + dy * dy), dt);
            x += a * (nx - x); y += a * (ny - y);
            ox = x; oy = y;
        }

        static double Alpha(double cutoff, double dt) { double tau = 1 / (2 * Math.PI * cutoff); return 1 / (1 + tau / dt); }
    }
}
