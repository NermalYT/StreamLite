using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// What the guided setup shows behind its windows (4.0.4): StreamLite's window fills the primary monitor and this
    /// covers all of it - the settings stay out of sight until the setup is done. Its title at the top, the steps along
    /// the bottom (done, now, still to come); the setup's own windows open in the middle.
    /// </summary>
    internal sealed class SetupCover : Control
    {
        public static readonly string[] Steps = { "Mic & headphones", "Headphone EQ engine", "Your voice", "Headphone EQ", "3D audio" };
        readonly string title, subtitle;
        int step = -1;

        public SetupCover(string title, string subtitle)
        {
            this.title = title; this.subtitle = subtitle;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        /// <summary>The step under way (0-based; -1 = none yet).</summary>
        public int Step { get { return step; } set { step = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var big = new Font(Theme.Title.FontFamily, Theme.Title.Size * 1.6f, FontStyle.Bold))
            {
                var ts = g.MeasureString(title, big);
                using (var b = new SolidBrush(Theme.Text)) g.DrawString(title, big, b, (Width - ts.Width) / 2, Height * 0.08f);
                var ss = g.MeasureString(subtitle, Theme.Body);
                using (var b = new SolidBrush(Theme.Muted)) g.DrawString(subtitle, Theme.Body, b, (Width - ss.Width) / 2, Height * 0.08f + ts.Height + Theme.Px(6));
            }
            // The steps, along the bottom.
            float h = Theme.Px(34), gap = Theme.Px(12), y = Height - Theme.Px(90);
            var widths = new float[Steps.Length]; float total = 0;
            for (int i = 0; i < Steps.Length; i++) { widths[i] = g.MeasureString((i + 1) + "  " + Steps[i], Theme.Bold).Width + Theme.Px(36); total += widths[i] + (i > 0 ? gap : 0); }
            float x = (Width - total) / 2;
            for (int i = 0; i < Steps.Length; i++)
            {
                var r = new RectangleF(x, y, widths[i], h);
                bool done = i < step, now = i == step;
                Color fill = now ? Theme.Accent : done ? Color.FromArgb(60, Theme.Accent) : Theme.Card;
                Color ink = now ? Color.FromArgb(10, 25, 14) : done ? Theme.Accent : Theme.Muted;
                using (var path = Rounded(r, h / 2)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                string s = (done ? "✓" : (i + 1).ToString()) + "  " + Steps[i];
                var sz = g.MeasureString(s, Theme.Bold);
                using (var b = new SolidBrush(ink)) g.DrawString(s, Theme.Bold, b, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
                x += widths[i] + gap;
            }
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }
}
