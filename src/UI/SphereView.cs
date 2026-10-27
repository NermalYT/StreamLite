using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;
using V = StreamLite.Audio.Sphere3D.Vec;

namespace StreamLite.UI
{
    /// <summary>What Personalize 3D zooms into: where its middle is (client pixels), how big it is and how it's turned.</summary>
    internal interface IZoomSource
    {
        PointF Centre { get; }
        float Radius { get; }
        double ViewYaw { get; }
        double ViewPitch { get; }
    }

    /// <summary>
    /// Personalize 3D's sphere seen from outside (4.0.7) - the picture the setup shows and Personalize 3D zooms into:
    /// lines of height every 30 degrees (blue above, the green ear-level ring, orange below), lines of direction, the
    /// six spots footsteps play from and you in the middle facing FRONT. Drag it to turn it; a double-click puts it back.
    /// Drawn with the same camera Personalize 3D starts from, so the zoom carries straight on from it.
    /// </summary>
    internal sealed class SphereView : Control, IZoomSource
    {
        const double DefaultYaw = -20, DefaultPitch = 28;   // from just behind and above you: FRONT up the screen, RIGHT to the right
        double yaw = DefaultYaw, pitch = DefaultPitch;
        bool dragging; Point dragFrom; double yaw0, pitch0;
        static readonly Color AboveCol = Color.FromArgb(110, 165, 240), LevelCol = Color.FromArgb(110, 210, 130), BelowCol = Color.FromArgb(235, 170, 90);

        public SphereView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.SizeAll;
        }

        public PointF Centre { get { return new PointF(Width / 2f, Height / 2f + Theme.Px(6)); } }
        public float Radius { get { return Math.Min(Width, Height) * 0.36f; } }
        public double ViewYaw { get { return yaw; } }
        public double ViewPitch { get { return pitch; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Clicks >= 2) { yaw = DefaultYaw; pitch = DefaultPitch; dragging = false; Invalidate(); return; }
            dragging = true; dragFrom = e.Location; yaw0 = yaw; pitch0 = pitch; Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            yaw = yaw0 - (e.X - dragFrom.X) * 0.5;
            pitch = Math.Max(-80, Math.Min(80, pitch0 + (e.Y - dragFrom.Y) * 0.5));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }

        // The camera: as Personal3DView's first frame (far away, looking at you, turned by yaw, from above by pitch).
        V cr, cu, cf;
        PointF Project(V p, out double depth)
        {
            var c = Centre; float R = Radius;
            depth = p.Dot(cf);
            return new PointF(c.X + (float)(p.Dot(cr) / Sphere3D.Radius * R), c.Y - (float)(p.Dot(cu) / Sphere3D.Radius * R));
        }

        void Seg(Graphics g, V a, V b, Color col, float width, int alpha)
        {
            double da, db; var pa = Project(a, out da); var pb = Project(b, out db);
            bool back = da + db > 0;   // the far side of the sphere: fainter
            using (var pen = new Pen(Color.FromArgb(back ? alpha / 3 : alpha, col), back ? width * 0.8f : width)) g.DrawLine(pen, pa, pb);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            cf = Sphere3D.Forward(yaw, -pitch); cr = Sphere3D.Right(yaw); cu = Sphere3D.Up(yaw, -pitch);
            double R = Sphere3D.Radius;
            // Lines of height (every 30 degrees) and of direction (every 30).
            for (int el = -60; el <= 60; el += 30)
            {
                var col = el > 0 ? AboveCol : el < 0 ? BelowCol : LevelCol;
                double y = Math.Sin(el * Math.PI / 180) * R, r = Math.Cos(el * Math.PI / 180) * R;
                var prev = new V(0, y, r);
                for (int i = 1; i <= 64; i++) { double a = 2 * Math.PI * i / 64; var q = new V(Math.Sin(a) * r, y, Math.Cos(a) * r); Seg(g, prev, q, col, el == 0 ? 2.4f : 1.2f, el == 0 ? 230 : 120); prev = q; }
            }
            for (int m = 0; m < 12; m++)
            {
                double a = m * Math.PI / 6; var prev = new V(0, -R, 0);
                for (int i = 1; i <= 32; i++)
                {
                    double el = -Math.PI / 2 + Math.PI * i / 32;
                    var q = new V(Math.Sin(a) * Math.Cos(el) * R, Math.Sin(el) * R, Math.Cos(a) * Math.Cos(el) * R);
                    Seg(g, prev, q, el > 0.01 ? AboveCol : el < -0.01 ? BelowCol : LevelCol, 1f, m % 3 == 0 ? 120 : 60); prev = q;
                }
            }
            // You in the middle, your nose towards FRONT.
            double d0; var you = Project(new V(0, 0, 0), out d0);
            var nose = Project(new V(0, 0, 0.9), out d0);
            using (var pen = new Pen(Theme.Text, 3f) { EndCap = LineCap.ArrowAnchor }) g.DrawLine(pen, you, nose);
            float yr = Theme.Px(7);
            using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, you.X - yr, you.Y - yr, 2 * yr, 2 * yr);
            // The six spots, nearest last, each with its name.
            using (var font = new Font(Theme.Small.FontFamily, Theme.Small.Size, FontStyle.Bold))
                foreach (var d in Sphere3D.Dirs.OrderByDescending(x => { double z; Project(Sphere3D.Point(x), out z); return z; }))
                {
                    double z; var p = Project(Sphere3D.Point(d), out z);
                    var col = d == Sphere3D.Dir.Up ? AboveCol : d == Sphere3D.Dir.Down ? BelowCol : LevelCol;
                    float r = Theme.Px(z > 0 ? 6 : 8);
                    using (var b = new SolidBrush(Color.FromArgb(z > 0 ? 120 : 235, col))) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                    string s = Sphere3D.Label(d); var sz = g.MeasureString(s, font);
                    using (var b = new SolidBrush(Color.FromArgb(z > 0 ? 140 : 235, Theme.Text))) g.DrawString(s, font, b, p.X - sz.Width / 2, p.Y - r - sz.Height);
                }
            using (var hint = new SolidBrush(Color.FromArgb(150, Theme.Muted)))
                g.DrawString(dragging ? "turning..." : "drag to turn", Theme.Small, hint, Theme.Px(4), Theme.Px(4));
        }
    }
}
