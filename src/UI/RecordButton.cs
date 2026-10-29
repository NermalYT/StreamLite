using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// The big round record button of "Tune to my voice" (4.0): green with a white dot to start, then the 5-second
    /// countdown inside it (the ring running out), red while recording - "Done" once there's enough voice.
    /// </summary>
    internal sealed class RecordButton : Control
    {
        public enum Look { Ready, Countdown, Recording }
        Look look = Look.Ready;
        int count; double frac;
        bool hot, canStop;

        public RecordButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(Theme.Px(132), Theme.Px(132));
        }

        public Look Mode { get { return look; } }
        public void Ready() { look = Look.Ready; Cursor = Cursors.Hand; Invalidate(); }
        /// <summary>The countdown: whole seconds left, and how much of this second is left (1..0).</summary>
        public void Countdown(int seconds, double ofSecond) { look = Look.Countdown; count = seconds; frac = ofSecond; Cursor = Cursors.Default; Invalidate(); }
        /// <summary>Recording; canStop = enough voice to finish early (then a click is "Done").</summary>
        public void Recording(bool canStop) { look = Look.Recording; this.canStop = canStop; Cursor = canStop ? Cursors.Hand : Cursors.Default; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float pad = Theme.Px(6), d = Math.Min(Width, Height) - 2 * pad;
            var r = new RectangleF((Width - d) / 2, (Height - d) / 2, d, d);
            Color fill = look == Look.Recording ? Color.FromArgb(220, 64, 60) : Theme.Accent;
            if (hot && (look == Look.Ready || (look == Look.Recording && canStop))) fill = ControlPaint.Light(fill, 0.25f);
            using (var glow = new SolidBrush(Color.FromArgb(50, fill))) g.FillEllipse(glow, RectangleF.Inflate(r, pad * 0.8f, pad * 0.8f));
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
            var ink = Color.White;
            if (look == Look.Ready)
            {
                float dot = d * 0.26f;
                using (var b = new SolidBrush(ink)) g.FillEllipse(b, r.X + (d - dot) / 2, r.Y + d * 0.30f, dot, dot);
                Centered(g, "Record", Theme.Bold, ink, r.Y + d * 0.66f);
            }
            else if (look == Look.Countdown)
            {
                using (var p = new Pen(Color.FromArgb(200, ink), Theme.Px(4))) g.DrawArc(p, RectangleF.Inflate(r, -Theme.Px(8), -Theme.Px(8)), -90, (float)(360 * Math.Max(0, Math.Min(1, frac))));
                using (var f = Theme.Ui(d * 0.30f, true, GraphicsUnit.Pixel)) Centered(g, count.ToString(), f, ink, r.Y + d / 2, true);
            }
            else
            {
                float sq = d * 0.24f;
                using (var b = new SolidBrush(ink)) g.FillRectangle(b, r.X + (d - sq) / 2, r.Y + d * 0.30f, sq, sq);
                Centered(g, canStop ? "Done" : "Recording", Theme.Bold, ink, r.Y + d * 0.66f);
            }
        }

        void Centered(Graphics g, string s, Font f, Color c, float y, bool middle = false)
        {
            var sz = g.MeasureString(s, f);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, (Width - sz.Width) / 2, middle ? y - sz.Height / 2 : y);
        }
    }
}
