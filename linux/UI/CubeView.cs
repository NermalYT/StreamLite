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
    /// The 3D sound sphere (same as on Windows; a cube until 4.0.9): you in the middle, the green ear-level ring, blue
    /// lines above, orange below. Right-drag turns it, a right double-click resets the view.
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
            ToolTip.SetTip(this, "Sound sphere: you're the white dot in the middle, nose to the FRONT. Blue lines above you, the green EAR LEVEL ring, orange lines below.\nRight-drag to turn it, right double-click to reset.");
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
        Point Dir(double az, double z, double scale) { double a = az * Math.PI / 180; return P(-Math.Sin(a) * scale, Math.Cos(a) * scale, z); }

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
            cx = size.Width / 2 + 10; cy = size.Height / 2; R = Math.Min(size.Width - 60, size.Height - 40) / 2 / 1.15;
            var radar = proc.Radar;
            bool on = running() || radar.TestSphere;
            int testFloor = radar.TestSphere ? SoundCube.Floor(radar.TestEl) : 99;
            // The sphere: lines of height every 30 degrees (the part the above / below test plays in lit), lines of direction.
            for (int el = -60; el <= 60; el += 30)
            {
                var col = el > 0 ? AboveCol : el < 0 ? BelowCol : LevelCol;
                bool lit = (el > 0 && testFloor == 1) || (el < 0 && testFloor == -1) || (el == 0 && testFloor == 0);
                double z = Math.Sin(el * Math.PI / 180), r = Math.Cos(el * Math.PI / 180);
                for (int i = 0; i < 72; i++)
                {
                    double a0 = 2 * Math.PI * i / 72, a1 = 2 * Math.PI * (i + 1) / 72;
                    Line(g, r * Math.Sin(a0), r * Math.Cos(a0), z, r * Math.Sin(a1), r * Math.Cos(a1), z, col, el == 0 || lit ? (byte)230 : (byte)120, el == 0 ? 2.4 : lit ? 1.8 : 1.1, false);
                }
            }
            for (int m = 0; m < 12; m++)
            {
                double a = m * Math.PI / 6;
                for (int i = 0; i < 24; i++)
                {
                    double e0 = -Math.PI / 2 + Math.PI * i / 24, e1 = -Math.PI / 2 + Math.PI * (i + 1) / 24;
                    Line(g, Math.Sin(a) * Math.Cos(e0), Math.Cos(a) * Math.Cos(e0), Math.Sin(e0), Math.Sin(a) * Math.Cos(e1), Math.Cos(a) * Math.Cos(e1), Math.Sin(e1),
                        e0 + e1 > 0.01 ? AboveCol : e0 + e1 < -0.01 ? BelowCol : LevelCol, m % 3 == 0 ? (byte)110 : (byte)55, 1, false);
                }
            }
            Line(g, 0, 0, -1, 0, 0, 1, AboveCol, 110, 1, true);
            var ringPts = Enumerable.Range(0, 72).Select(i => Dir(i * 5, 0, 1)).ToArray();
            g.DrawGeometry(B(LevelCol, testFloor == 0 ? (byte)60 : (byte)24), null, new PolylineGeometry(ringPts, true));
            var floorLabels = new List<Tuple<string, Point, Color, bool>>
            {
                Tuple.Create("ABOVE", P(0, 0, 1.12), AboveCol, testFloor == 1),
                Tuple.Create("EAR LEVEL", ringPts.OrderBy(p => p.X).First(), LevelCol, testFloor == 0),
                Tuple.Create("BELOW", P(0, 0, -1.12), BelowCol, testFloor == -1),
            };
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
                double ta = radar.TestAz * Math.PI / 180, te = radar.TestEl * Math.PI / 180, d;
                double x = -Math.Sin(ta) * Math.Cos(te), y = Math.Cos(ta) * Math.Cos(te), z = Math.Sin(te);
                var at = P(x * 0.92, y * 0.92, z * 0.92, out d); var foot = P(x * 0.92, y * 0.92, 0);
                if (Math.Abs(radar.TestEl) > 5) { g.DrawLine(new Pen(B(Amber, 170), 1.5, DashStyle.Dash), foot, at); g.DrawEllipse(B(Amber, 120), null, foot, 3, 2); }
                double pulse = 1 + 0.25 * Math.Sin(Environment.TickCount / 90.0), sz = 9 * pulse * (d > 0 ? 1.15 : 0.9);
                g.DrawEllipse(B(Amber, 70), null, at, sz, sz);
                g.DrawEllipse(B(Amber, 255), null, at, sz / 2, sz / 2);
            }

            foreach (var fl in floorLabels.Where(x => x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, fl.Item1 == "EAR LEVEL");
            foreach (var dd in new[] { Tuple.Create("FRONT", 0.0, 1.18), Tuple.Create("BEHIND", 0.0, -1.18), Tuple.Create("L", -1.15, 0.0), Tuple.Create("R", 1.15, 0.0) })
            {
                var at = P(dd.Item2, dd.Item3, 0);
                double ddx = at.X - head.X, ddy = at.Y - head.Y;
                if (ddx * ddx + ddy * ddy > R * R * 0.09) Label(g, dd.Item1, at, LevelCol, false);
            }
            foreach (var fl in floorLabels.Where(x => !x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, fl.Item1 == "EAR LEVEL");
            if (!radar.TestSphere && !running()) Label(g, "3D audio is off", new Point(size.Width / 2, size.Height - 12), Muted, false);
            g.DrawText(new FormattedText(dragging ? "turning..." : "right-drag to turn", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, B(Muted, 150)), new Point(2, 2));
        }
    }
}
