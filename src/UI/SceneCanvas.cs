using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using W = System.Windows;
using M = System.Windows.Media;

namespace StreamLite.UI
{
    /// <summary>
    /// What a full-screen scene draws with, in pixels: flat translucent shapes, lines, text and one picture. Drawn on the
    /// graphics card in the app (WpfCanvas, every frame of the monitor) and with GDI+ for test pictures (GdiCanvas).
    /// </summary>
    internal interface ICanvas
    {
        void Clear(Color c);
        void Polygon(Color fill, PointF[] pts);
        void Line(Color c, float width, PointF a, PointF b);
        void Ellipse(Color fill, RectangleF r);
        void Ring(Color c, float width, RectangleF r);
        void Rect(Color fill, RectangleF r);
        void Rounded(Color fill, Color stroke, float strokeWidth, RectangleF r, float radius);
        SizeF Measure(string s, Font f, float wrapWidth = 0);
        void Text(string s, Font f, Color c, PointF at);
        void Text(string s, Font f, Color c, RectangleF box);
        /// <summary>A picture given to the canvas when it was made (0 = the window, 1 = the desktop behind it).</summary>
        void Picture(int which, RectangleF dest, float opacity);
    }

    /// <summary>GDI+ (test pictures and the fallback).</summary>
    internal sealed class GdiCanvas : ICanvas
    {
        readonly Graphics g; readonly Bitmap[] pics;
        public GdiCanvas(Graphics g, params Bitmap[] pics) { this.g = g; this.pics = pics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; }

        public void Clear(Color c) { g.Clear(c); }
        public void Polygon(Color fill, PointF[] pts) { using (var b = new SolidBrush(fill)) g.FillPolygon(b, pts); }
        public void Line(Color c, float width, PointF a, PointF b) { using (var p = new Pen(c, width)) g.DrawLine(p, a, b); }
        public void Ellipse(Color fill, RectangleF r) { using (var b = new SolidBrush(fill)) g.FillEllipse(b, r); }
        public void Ring(Color c, float width, RectangleF r) { using (var p = new Pen(c, width)) g.DrawEllipse(p, r); }
        public void Rect(Color fill, RectangleF r) { using (var b = new SolidBrush(fill)) g.FillRectangle(b, r); }
        public void Rounded(Color fill, Color stroke, float strokeWidth, RectangleF r, float radius)
        {
            using (var path = Theme.Rounded(r, radius))
            {
                if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (stroke.A > 0 && strokeWidth > 0) using (var p = new Pen(stroke, strokeWidth)) g.DrawPath(p, path);
            }
        }
        public SizeF Measure(string s, Font f, float wrapWidth = 0) { return wrapWidth > 0 ? g.MeasureString(s, f, (int)wrapWidth) : g.MeasureString(s, f); }
        public void Text(string s, Font f, Color c, PointF at) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, at); }
        public void Text(string s, Font f, Color c, RectangleF box) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, box); }
        public void Picture(int which, RectangleF dest, float opacity)
        {
            if (pics == null || which >= pics.Length || pics[which] == null || opacity <= 0) return;
            var bmp = pics[which];
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
                var old = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(bmp, Rectangle.Round(dest), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                g.InterpolationMode = old;
            }
        }
    }

    /// <summary>WPF: drawn by the graphics card. Coordinates are device pixels (the element undoes the DPI scale).</summary>
    internal sealed class WpfCanvas : ICanvas
    {
        M.DrawingContext dc;
        readonly float dpi;                 // device pixels per inch (font sizes)
        readonly double pixelsPerDip;
        readonly M.ImageSource[] pics;
        readonly Dictionary<int, M.SolidColorBrush> brushes = new Dictionary<int, M.SolidColorBrush>();
        readonly Dictionary<string, M.FormattedText> texts = new Dictionary<string, M.FormattedText>();
        readonly Dictionary<Font, M.Typeface> faces = new Dictionary<Font, M.Typeface>();

        public WpfCanvas(double pixelsPerDip, params M.ImageSource[] pics) { this.pixelsPerDip = pixelsPerDip; dpi = (float)(96 * pixelsPerDip); this.pics = pics; }

        public void Begin(M.DrawingContext dc) { this.dc = dc; if (texts.Count > 600) texts.Clear(); }

        M.SolidColorBrush Brush(Color c)
        {
            int k = c.ToArgb(); M.SolidColorBrush b;
            if (!brushes.TryGetValue(k, out b))
            {
                if (brushes.Count > 4000) brushes.Clear();
                b = new M.SolidColorBrush(M.Color.FromArgb(c.A, c.R, c.G, c.B)); b.Freeze(); brushes[k] = b;
            }
            return b;
        }

        static W.Point P(PointF p) { return new W.Point(p.X, p.Y); }
        static W.Rect R(RectangleF r) { return new W.Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height)); }

        public void Clear(Color c) { dc.DrawRectangle(Brush(c), null, new W.Rect(-10, -10, 100000, 100000)); }

        public void Polygon(Color fill, PointF[] pts)
        {
            if (pts.Length < 3 || fill.A == 0) return;
            var geo = new M.StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(P(pts[0]), true, true);
                for (int i = 1; i < pts.Length; i++) ctx.LineTo(P(pts[i]), false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(Brush(fill), null, geo);
        }

        M.Pen Pen(Color c, float w) { var p = new M.Pen(Brush(c), w); p.Freeze(); return p; }

        public void Line(Color c, float width, PointF a, PointF b) { if (c.A > 0) dc.DrawLine(Pen(c, width), P(a), P(b)); }
        public void Ellipse(Color fill, RectangleF r) { if (fill.A > 0) dc.DrawEllipse(Brush(fill), null, new W.Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2); }
        public void Ring(Color c, float width, RectangleF r) { if (c.A > 0) dc.DrawEllipse(null, Pen(c, width), new W.Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2); }
        public void Rect(Color fill, RectangleF r) { if (fill.A > 0) dc.DrawRectangle(Brush(fill), null, R(r)); }
        public void Rounded(Color fill, Color stroke, float strokeWidth, RectangleF r, float radius)
        {
            dc.DrawRoundedRectangle(fill.A > 0 ? Brush(fill) : null, stroke.A > 0 && strokeWidth > 0 ? Pen(stroke, strokeWidth) : null, R(r), radius, radius);
        }

        M.Typeface Face(Font f)
        {
            M.Typeface t;
            if (!faces.TryGetValue(f, out t))
            {
                string fam = f.FontFamily.Name; var weight = f.Bold ? W.FontWeights.Bold : W.FontWeights.Normal;
                if (fam.EndsWith(" Semibold")) { fam = fam.Substring(0, fam.Length - 9); weight = W.FontWeights.SemiBold; }
                t = new M.Typeface(new M.FontFamily(fam), W.FontStyles.Normal, weight, W.FontStretches.Normal);
                faces[f] = t;
            }
            return t;
        }

        /// <summary>Text laid out once and kept (white; the colour comes from the brush when drawn).</summary>
        M.FormattedText Layout(string s, Font f, Color c, float wrap)
        {
            string key = s + "\u0001" + f.Name + f.SizeInPoints + f.Bold + "\u0001" + (c.ToArgb() | unchecked((int)0xFF000000)) + "\u0001" + (int)wrap;
            M.FormattedText ft;
            if (!texts.TryGetValue(key, out ft))
            {
#pragma warning disable 618
                ft = new M.FormattedText(s ?? "", CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, Face(f), f.SizeInPoints * dpi / 72.0, Brush(Color.FromArgb(255, c)), pixelsPerDip);
#pragma warning restore 618
                if (wrap > 0) ft.MaxTextWidth = wrap;
                texts[key] = ft;
            }
            return ft;
        }

        public SizeF Measure(string s, Font f, float wrapWidth = 0)
        {
            var ft = Layout(s, f, Color.White, wrapWidth);
            return new SizeF((float)ft.WidthIncludingTrailingWhitespace, (float)ft.Height);
        }

        void Draw(M.FormattedText ft, Color c, W.Point at)
        {
            if (c.A == 0) return;
            if (c.A < 255) dc.PushOpacity(c.A / 255.0);
            dc.DrawText(ft, at);
            if (c.A < 255) dc.Pop();
        }

        public void Text(string s, Font f, Color c, PointF at) { Draw(Layout(s, f, c, 0), c, P(at)); }
        public void Text(string s, Font f, Color c, RectangleF box)
        {
            var ft = Layout(s, f, c, box.Width);
            ft.MaxTextHeight = Math.Max(1, box.Height);
            Draw(ft, c, new W.Point(box.X, box.Y));
        }

        public void Picture(int which, RectangleF dest, float opacity)
        {
            if (pics == null || which >= pics.Length || pics[which] == null || opacity <= 0) return;
            if (opacity < 1) dc.PushOpacity(opacity);
            dc.DrawImage(pics[which], R(dest));
            if (opacity < 1) dc.Pop();
        }

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        /// <summary>A GDI+ picture for WPF (copied once).</summary>
        public static M.ImageSource FromBitmap(Bitmap b)
        {
            if (b == null) return null;
            IntPtr h = b.GetHbitmap();
            try
            {
                var s = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, W.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                s.Freeze(); return s;
            }
            finally { DeleteObject(h); }
        }
    }

    /// <summary>
    /// The WPF surface a scene is drawn on, filling a WinForms control: redrawn on the monitor's own frames (every
    /// refresh while Dirty), mouse and keys passed on.
    /// </summary>
    internal sealed class SceneHost : ElementHost
    {
        sealed class Surface : W.FrameworkElement
        {
            public Action<M.DrawingContext> Paint;
            public double Scale = 1;
            protected override void OnRender(M.DrawingContext dc)
            {
                if (Paint == null) return;
                // Draw in device pixels.
                dc.PushTransform(new M.ScaleTransform(1 / Scale, 1 / Scale));
                Paint(dc);
                dc.Pop();
            }
        }

        readonly Surface surface = new Surface { Focusable = true };
        /// <summary>Called on every monitor frame before drawing (the scene's clock).</summary>
        public event Action Frame;
        /// <summary>Draws the scene for the canvas (device pixels, the size of the control).</summary>
        public Action<ICanvas> Draw;
        public bool Dirty = true;
        public event Action<System.Windows.Forms.MouseButtons> Down;
        public event Action Moved;
        public event Action<System.Windows.Forms.Keys> Key;
        WpfCanvas canvas;
        readonly M.ImageSource[] pics;
        bool hooked;
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>The last frames' times (ms), for the frame-rate probe.</summary>
        public readonly Queue<double> FrameTimes = new Queue<double>();

        public SceneHost(params Bitmap[] pictures)
        {
            pics = new M.ImageSource[pictures.Length];
            for (int i = 0; i < pictures.Length; i++) pics[i] = WpfCanvas.FromBitmap(pictures[i]);
            BackColorTransparent = false;
            BackColor = Theme.Bg;
            Child = surface;
            M.RenderOptions.SetBitmapScalingMode(surface, M.BitmapScalingMode.Linear);
            M.RenderOptions.SetEdgeMode(surface, M.EdgeMode.Unspecified);
            W.Media.TextOptions.SetTextFormattingMode(surface, M.TextFormattingMode.Ideal);
            surface.Paint = dc =>
            {
                if (canvas == null) canvas = new WpfCanvas(surface.Scale, pics);
                canvas.Begin(dc);
                if (Draw != null) Draw(canvas);
            };
            surface.MouseDown += (s, e) =>
            {
                surface.Focus();
                var b = e.ChangedButton == W.Input.MouseButton.Right ? System.Windows.Forms.MouseButtons.Right : e.ChangedButton == W.Input.MouseButton.Middle ? System.Windows.Forms.MouseButtons.Middle : System.Windows.Forms.MouseButtons.Left;
                if (Down != null) Down(b);
            };
            surface.MouseMove += (s, e) => { if (Moved != null) Moved(); };
            surface.KeyDown += (s, e) =>
            {
                var k = e.Key == W.Input.Key.System ? e.SystemKey : e.Key;
                if (Key != null) Key((System.Windows.Forms.Keys)W.Input.KeyInterop.VirtualKeyFromKey(k));
                e.Handled = true;
            };
            surface.Loaded += (s, e) =>
            {
                var src = W.PresentationSource.FromVisual(surface);
                if (src != null && src.CompositionTarget != null) surface.Scale = src.CompositionTarget.TransformToDevice.M11;
                canvas = null;
            };
            M.CompositionTarget.Rendering += OnRendering;
            hooked = true;
        }

        public void FocusScene() { Focus(); surface.Focus(); W.Input.Keyboard.Focus(surface); }

        double last;
        void OnRendering(object sender, EventArgs e)
        {
            double now = Clock.Elapsed.TotalMilliseconds;
            FrameTimes.Enqueue(now - last); last = now; while (FrameTimes.Count > 240) FrameTimes.Dequeue();
            if (Frame != null) Frame();
            if (Dirty) { Dirty = false; surface.InvalidateVisual(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (hooked) { M.CompositionTarget.Rendering -= OnRendering; hooked = false; }
            base.Dispose(disposing);
        }
    }
}
