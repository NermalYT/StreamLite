using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// The 3D sound cube (same as on Windows): three floors - below, ear level, above - each split into 9 spots, you in
    /// the middle of the ear-level floor. Right-drag turns it, a right double-click resets the view.
    /// </summary>
    internal sealed class CubeView : Control
    {
        readonly SpatialProcessor proc;
        readonly Func<bool> running;
        readonly SoundCube cube = new SoundCube();
        double cx, cy, R;
        bool dragging; Point dragFrom; double yaw0, pitch0;
        readonly List<Rect> labels = new List<Rect>();

        static readonly Color AboveCol = Color.FromRgb(110, 165, 240), LevelCol = Color.FromRgb(110, 210, 130), BelowCol = Color.FromRgb(235, 170, 90);
        static readonly Color Accent = Color.FromRgb(76, 194, 122), Muted = Color.FromRgb(140, 148, 156), Amber = Color.FromRgb(255, 196, 80);

        public CubeView(SpatialProcessor proc, Func<bool> running)
        {
            this.proc = proc; this.running = running;
            ClipToBounds = true;
            Focusable = false;
            ToolTip.SetTip(this, "Sound cube: BELOW (orange), EAR LEVEL (green, you're the white dot) and ABOVE (blue), each split into 9 spots.\nRight-drag to turn it, right double-click to reset.");
            new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (s, e) => InvalidateVisual()).Start();
        }

        public SoundCube Cube { get { return cube; } }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsRightButtonPressed) return;
            if (e.ClickCount == 2) { cube.Reset(); InvalidateVisual(); return; }
            dragging = true; dragFrom = pt.Position; yaw0 = cube.Yaw; pitch0 = cube.Pitch;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging) return;
            var p = e.GetPosition(this);
            cube.Yaw = yaw0 + (p.X - dragFrom.X) * 0.6;
            cube.Pitch = pitch0 + (p.Y - dragFrom.Y) * 0.6;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!dragging || e.InitialPressMouseButton != MouseButton.Right) return;
            dragging = false; e.Pointer.Capture(null);
        }

        Point P(double x, double y, double z, out double depth)
        {
            double sx, sy; cube.Project(x, y, z, out sx, out sy, out depth);
            return new Point(cx + R * sx, cy + R * sy);
        }
        Point P(double x, double y, double z) { double d; return P(x, y, z, out d); }
        Point Dir(double az, double z, double scale) { double x, y, zz; SoundCube.Direction(az, 0, out x, out y, out zz); return P(x * scale, y * scale, z); }

        static IBrush B(Color c, byte a) { return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); }

        void Line(DrawingContext g, double x0, double y0, double z0, double x1, double y1, double z1, Color c, byte a, double w, bool dashBack)
        {
            double d0, d1; var p0 = P(x0, y0, z0, out d0); var p1 = P(x1, y1, z1, out d1);
            bool back = d0 + d1 < -0.05;
            var pen = new Pen(B(c, back ? (byte)(a / 2) : a), w, back && dashBack ? DashStyle.Dash : null);
            g.DrawLine(pen, p0, p1);
        }

        void Label(DrawingContext g, string text, Point at, Color c, bool alignRight)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, B(c, 230));
            var r = new Rect(alignRight ? at.X - ft.Width : at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height);
            foreach (var o in labels) if (o.Intersects(r)) return;
            labels.Add(r);
            g.DrawText(ft, r.Position);
        }

        public override void Render(DrawingContext g)
        {
            labels.Clear();
            var size = Bounds.Size;
            g.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 34, 39)), new Rect(size));
            cx = size.Width / 2 + 16; cy = size.Height / 2; R = Math.Min(size.Width - 70, size.Height) / 2 / 1.62;
            var radar = proc.Radar;
            bool on = running() || radar.TestSphere;
            int testFloor = radar.TestSphere ? SoundCube.Floor(radar.TestEl) : 99;
            var floors = new List<Tuple<double, Color, int, string>> { Tuple.Create(-1.0, BelowCol, -1, "BELOW"), Tuple.Create(0.0, LevelCol, 0, "EAR LEVEL"), Tuple.Create(1.0, AboveCol, 1, "ABOVE") };
            if (cube.Pitch < 0) floors.Reverse();
            var floorLabels = new List<Tuple<string, Point, Color, bool>>();
            const double t = 1.0 / 3;
            foreach (var f in floors)
            {
                double z = f.Item1; bool active = testFloor == f.Item3;
                var corners = new[] { P(-1, -1, z), P(1, -1, z), P(1, 1, z), P(-1, 1, z) };
                var geo = new PolylineGeometry(corners, true);
                g.DrawGeometry(B(f.Item2, active ? (byte)78 : (byte)26), new Pen(B(f.Item2, active ? (byte)230 : (byte)150), active ? 2 : 1.3), geo);
                byte ga = active ? (byte)150 : (byte)70;
                Line(g, -t, -1, z, -t, 1, z, f.Item2, ga, 1, false); Line(g, t, -1, z, t, 1, z, f.Item2, ga, 1, false);
                Line(g, -1, -t, z, 1, -t, z, f.Item2, ga, 1, false); Line(g, -1, t, z, 1, t, z, f.Item2, ga, 1, false);
                var left = corners.OrderBy(p => p.X).First();
                floorLabels.Add(Tuple.Create(f.Item4, new Point(left.X - 4, left.Y), f.Item2, active));
            }
            var pillar = Color.FromRgb(120, 130, 140);
            foreach (var c in new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } }) Line(g, c[0], c[1], -1, c[0], c[1], 1, pillar, 150, 1.2, true);
            Line(g, 0, 0, -1, 0, 0, 1, AboveCol, 110, 1, true);

            var ring = radar.Ring;
            if (on && !radar.TestSphere)
            {
                if (ring != null && !radar.StereoMode)
                {
                    int n = ring.Length; double step = 360.0 / n;
                    for (int b = 0; b < n; b++)
                    {
                        double lvl = Math.Max(0, Math.Min(1, (ring[b] + 70) / 55));
                        if (lvl <= 0.01) continue;
                        g.DrawLine(new Pen(B(Accent, (byte)(30 + 210 * lvl)), 3 + 5 * lvl, lineCap: PenLineCap.Round), Dir(b * step, 0, 1), Dir((b + 1) * step, 0, 1));
                    }
                }
                else
                    for (int s = 0; s < SpatialProcessor.Slots; s++)
                    {
                        if (!radar.SlotUsed[s]) continue;
                        double lvl = Math.Max(0, Math.Min(1, (radar.SlotDb[s] + 70) / 55));
                        var pen = new Pen(B(Accent, (byte)(40 + 200 * lvl)), 3 + 5 * lvl, lineCap: PenLineCap.Round);
                        for (int k = -2; k < 2; k++) g.DrawLine(pen, Dir(radar.SlotAz[s] + k * 6, 0, 1), Dir(radar.SlotAz[s] + (k + 1) * 6, 0, 1));
                    }
                long now = Environment.TickCount;
                foreach (var b in radar.Snapshot(now, 2000))
                {
                    double age = (now - b.Time) / 2000.0, sz = 6 + Math.Max(0, (b.Db + 60) / 4);
                    var p = Dir(b.Az, 0, 0.7);
                    g.DrawEllipse(B(Amber, (byte)Math.Max(0, 255 * (1 - age))), null, p, sz / 2, sz / 2);
                }
            }

            var head = P(0, 0, 0); var nose = P(0, 0.22, 0);
            g.DrawEllipse(Brushes.White, null, head, 5, 5);
            double dx = nose.X - head.X, dy = nose.Y - head.Y, len = Math.Sqrt(dx * dx + dy * dy) + 1e-3, ux = dx / len, uy = dy / len;
            if (len > 3) g.DrawGeometry(Brushes.White, null, new PolylineGeometry(new[] { nose, new Point(head.X - uy * 5, head.Y + ux * 5), new Point(head.X + uy * 5, head.Y - ux * 5) }, true));

            if (radar.TestSphere)
            {
                double x, y, z, d; SoundCube.Direction(radar.TestAz, radar.TestEl, out x, out y, out z);
                var at = P(x * 0.92, y * 0.92, z, out d); var foot = P(x * 0.92, y * 0.92, 0);
                if (z != 0) { g.DrawLine(new Pen(B(Amber, 170), 1.5, DashStyle.Dash), foot, at); g.DrawEllipse(B(Amber, 120), null, foot, 3, 2); }
                double pulse = 1 + 0.25 * Math.Sin(Environment.TickCount / 90.0), sz = 9 * pulse * (d > 0 ? 1.15 : 0.9);
                g.DrawEllipse(B(Amber, 70), null, at, sz, sz);
                g.DrawEllipse(B(Amber, 255), null, at, sz / 2, sz / 2);
            }

            foreach (var fl in floorLabels.Where(x => x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, true);
            foreach (var dd in new[] { Tuple.Create("FRONT", 0.0, 1.42), Tuple.Create("BEHIND", 0.0, -1.42), Tuple.Create("L", -1.3, 0.0), Tuple.Create("R", 1.3, 0.0) })
            {
                var at = P(dd.Item2, dd.Item3, 0);
                double ddx = at.X - head.X, ddy = at.Y - head.Y;
                if (ddx * ddx + ddy * ddy > R * R * 0.09) Label(g, dd.Item1, at, LevelCol, false);
            }
            foreach (var fl in floorLabels.Where(x => !x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, true);
            if (!radar.TestSphere && !running()) Label(g, "3D audio is off", new Point(size.Width / 2, size.Height - 12), Muted, false);
            g.DrawText(new FormattedText(dragging ? "turning..." : "right-drag to turn", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, B(Muted, 150)), new Point(2, 2));
        }
    }
}
