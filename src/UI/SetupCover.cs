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

        // Personalize 3D's page (4.0.6; the sphere from 4.0.7): Personalize 3D's sphere, what happens, Start and Later.
        SphereView radar;
        FlatButton start, later;
        string pageText;
        Rectangle headRect, textRect;
        const string PageHead = "3D audio, tuned to your ears";

        /// <summary>
        /// Personalize 3D's page: its sphere seen from outside (drag it to turn it), what happens and Start - Personalize
        /// 3D zooms into this sphere - or Later. The sphere is returned (Personalize 3D opens from it).
        /// </summary>
        public SphereView ShowPersonalize(AppCore core, string text, Action onStart, Action onLater)
        {
            if (radar != null) return radar;
            pageText = text;
            radar = new SphereView();
            start = new FlatButton("Start", true) { Width = Theme.Px(170) };
            later = new FlatButton("Later") { Width = Theme.Px(120) };
            start.Click += (s, e) => onStart();
            later.Click += (s, e) => onLater();
            Controls.Add(radar); Controls.Add(start); Controls.Add(later);
            PlacePage();
            Invalidate();
            start.Focus();
            return radar;
        }

        void PlacePage()
        {
            if (radar == null) return;
            float top = Height * 0.08f + Theme.Px(96), bottom = Height - Theme.Px(110);
            int textW = Math.Min(Width - Theme.Px(80), Theme.Px(720)), textH, headH = Theme.Px(34), gap = Theme.Px(16);
            using (var g = CreateGraphics()) textH = (int)Math.Ceiling(g.MeasureString(pageText, Theme.Body, textW).Height);
            int side = (int)Math.Max(Theme.Px(160), Math.Min(Theme.Px(460), bottom - top - headH - textH - start.Height - 3 * gap));
            int total = headH + side + textH + start.Height + 3 * gap;
            int y = (int)(top + Math.Max(0, (bottom - top - total) / 2));
            headRect = new Rectangle(0, y, Width, headH);
            radar.Bounds = new Rectangle((Width - side) / 2, y + headH, side, side);
            textRect = new Rectangle((Width - textW) / 2, radar.Bottom + gap, textW, textH);
            int bw = start.Width + Theme.Px(12) + later.Width, bx = (Width - bw) / 2;
            start.Location = new Point(bx, textRect.Bottom + gap);
            later.Location = new Point(start.Right + Theme.Px(12), start.Top);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); PlacePage(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (radar != null)
            {
                var hs = g.MeasureString(PageHead, Theme.Title);
                using (var b = new SolidBrush(Theme.Text)) g.DrawString(PageHead, Theme.Title, b, (Width - hs.Width) / 2, headRect.Top);
                using (var b = new SolidBrush(Theme.Muted)) using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(pageText, Theme.Body, b, textRect, sf);
            }
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
