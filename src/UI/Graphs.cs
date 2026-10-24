using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    internal static class GraphStyle
    {
        public static readonly Color Plot = Color.FromArgb(20, 22, 25);
        public static readonly Color Grid = Color.FromArgb(40, 44, 50);
        public static readonly Color Raw = Color.FromArgb(150, 160, 172);
        public static readonly Color Noise = Color.FromArgb(242, 150, 60);
        public static readonly Color Gate = Color.FromArgb(70, 229, 83, 75);

        // Dark -> violet -> magenta -> orange -> pale yellow (readable "heat" scale on a dark UI).
        static readonly Color[] Stops =
        {
            Color.FromArgb(20, 22, 25), Color.FromArgb(48, 18, 92), Color.FromArgb(150, 30, 120),
            Color.FromArgb(235, 95, 60), Color.FromArgb(252, 200, 80), Color.FromArgb(255, 250, 210)
        };

        public static Color Heat(float t)
        {
            t = Math.Max(0, Math.Min(1, t)) * (Stops.Length - 1);
            int i = Math.Min(Stops.Length - 2, (int)t);
            float f = t - i;
            Color a = Stops[i], b = Stops[i + 1];
            return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
        }

        /// <summary>Draws one legend entry and returns where the next one can start.</summary>
        public static int Legend(Graphics g, int x, int y, Color c, string text, bool line = false)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y + Theme.Px(line ? 7 : 4), Theme.Px(14), Theme.Px(line ? 3 : 9));
            TextRenderer.DrawText(g, text, Theme.Small, new Point(x + Theme.Px(18), y), Theme.Muted);
            return x + Theme.Px(18) + TextRenderer.MeasureText(text, Theme.Small).Width + Theme.Px(14);
        }
    }

    /// <summary>Scrolling spectrogram: time left-to-right, low frequencies at the bottom, loudness as color.</summary>
    internal class Spectrogram : Control
    {
        public const int Columns = 300;                 // ~12 s at the 40 ms UI tick
        readonly Bitmap image = new Bitmap(Columns, SpectrumAnalyzer.Points, PixelFormat.Format32bppRgb);
        int column;
        public string Caption = "";
        public float MinDb = -105f, MaxDb = -25f;

        public Spectrogram()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            using (var g = Graphics.FromImage(image)) g.Clear(GraphStyle.Plot);
        }

        public void Push(float[] db)
        {
            if (db == null || IsDisposed) return;
            var data = image.LockBits(new Rectangle(column, 0, 1, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int i = 0; i < db.Length && i < image.Height; i++)
            {
                float t = (db[i] - MinDb) / (MaxDb - MinDb);
                int argb = GraphStyle.Heat(t).ToArgb();
                System.Runtime.InteropServices.Marshal.WriteInt32(data.Scan0, (image.Height - 1 - i) * data.Stride, argb);
            }
            image.UnlockBits(data);
            column = (column + 1) % Columns;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int left = Theme.Px(34), w = Width - left, h = Height;
            g.Clear(GraphStyle.Plot);
            // Oldest columns (from 'column' to end) on the left, newest on the right.
            int older = Columns - column;
            int splitX = left + (int)(w * (older / (double)Columns));
            g.DrawImage(image, new Rectangle(left, 0, splitX - left, h), new Rectangle(column, 0, older, image.Height), GraphicsUnit.Pixel);
            if (column > 0) g.DrawImage(image, new Rectangle(splitX, 0, left + w - splitX, h), new Rectangle(0, 0, column, image.Height), GraphicsUnit.Pixel);
            foreach (double f in new double[] { 100, 1000, 10000 })
            {
                float y = h - (float)(Math.Log(f / 20) / Math.Log(1000)) * h;
                TextRenderer.DrawText(g, f >= 1000 ? (f / 1000) + "k" : f.ToString(), Theme.Small, new Point(Theme.Px(2), (int)y - Theme.Px(8)), Theme.Muted);
                using (var p = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(p, left, y, Width, y);
            }
            if (Caption.Length > 0)
            {
                var size = TextRenderer.MeasureText(Caption, Theme.Small);
                using (var b = new SolidBrush(Color.FromArgb(170, 16, 18, 20))) g.FillRectangle(b, left + Theme.Px(6), Theme.Px(5), size.Width + Theme.Px(4), size.Height);
                TextRenderer.DrawText(g, Caption, Theme.Small, new Point(left + Theme.Px(8), Theme.Px(5)), Theme.Text);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Spectrum overlay: what the mic picks up, what the remover thinks is noise, and what's left.</summary>
    internal class NoiseSpectrumGraph : Control
    {
        public float[] Input, Noise, Clean;
        const double MinF = 20, MaxF = 20000;

        public NoiseSpectrumGraph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        RectangleF Plot { get { return new RectangleF(Theme.Px(34), Theme.Px(24), Width - Theme.Px(40), Height - Theme.Px(42)); } }
        float X(double f) { var p = Plot; return p.X + (float)(Math.Log(f / MinF) / Math.Log(MaxF / MinF)) * p.Width; }
        float Y(float db) { var p = Plot; float t = Math.Max(0, Math.Min(1, (db + 110f) / 95f)); return p.Bottom - t * p.Height; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(GraphStyle.Plot);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var grid = new Pen(GraphStyle.Grid))
            {
                foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
                {
                    float x = X(f);
                    g.DrawLine(grid, x, p.Y, x, p.Bottom);
                    TextRenderer.DrawText(g, f >= 1000 ? (f / 1000) + "k" : f.ToString(), Theme.Small, new Point((int)x - Theme.Px(10), (int)p.Bottom + Theme.Px(2)), Theme.Muted);
                }
                for (int db = -100; db <= -20; db += 20)
                {
                    float y = Y(db);
                    g.DrawLine(grid, p.X, y, p.Right, y);
                    TextRenderer.DrawText(g, db.ToString(), Theme.Small, new Point(Theme.Px(2), (int)y - Theme.Px(8)), Theme.Muted);
                }
            }
            Fill(g, Input, Color.FromArgb(80, GraphStyle.Raw));
            Fill(g, Clean, Color.FromArgb(120, Theme.Accent));
            Line(g, Noise, GraphStyle.Noise);
            int lx = (int)p.X;
            GraphStyle.Legend(g, lx, Theme.Px(3), Color.FromArgb(150, GraphStyle.Raw), "Your mic (before)");
            GraphStyle.Legend(g, lx + Theme.Px(130), Theme.Px(3), Theme.Accent, "After noise removal");
            GraphStyle.Legend(g, lx + Theme.Px(270), Theme.Px(3), GraphStyle.Noise, "Detected background noise", true);
        }

        void Fill(Graphics g, float[] spec, Color c)
        {
            if (spec == null) return;
            var pts = new List<PointF> { new PointF(X(MinF), Plot.Bottom) };
            for (int i = 0; i < spec.Length; i++) pts.Add(new PointF(X(SpectrumAnalyzer.PointFreq(i)), Y(spec[i])));
            pts.Add(new PointF(X(MaxF), Plot.Bottom));
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts.ToArray());
        }

        void Line(Graphics g, float[] spec, Color c)
        {
            if (spec == null) return;
            var pts = new PointF[spec.Length];
            for (int i = 0; i < spec.Length; i++) pts[i] = new PointF(X(SpectrumAnalyzer.PointFreq(i)), Y(spec[i]));
            using (var pen = new Pen(c, Theme.Px(2)) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, pts);
        }
    }

    /// <summary>Last ~12 s of levels: raw mic, after cleanup, gate threshold and when the gate was closed.</summary>
    internal class LevelHistoryGraph : Control
    {
        public readonly float[] Raw = new float[LevelHistory.Length], Clean = new float[LevelHistory.Length], Output = new float[LevelHistory.Length];
        public readonly bool[] GateOpen = new bool[LevelHistory.Length];
        public float? Threshold;
        public float? NoiseFloor;
        public bool ShowOutput;
        public string RawLabel = "Your mic";
        const float MinDb = -90f, MaxDb = 0f;

        public LevelHistoryGraph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            for (int i = 0; i < Raw.Length; i++) Raw[i] = Clean[i] = Output[i] = -100;
        }

        RectangleF Plot { get { return new RectangleF(Theme.Px(34), Theme.Px(24), Width - Theme.Px(40), Height - Theme.Px(30)); } }
        float Y(float db) { var p = Plot; float t = Math.Max(0, Math.Min(1, (db - MinDb) / (MaxDb - MinDb))); return p.Bottom - t * p.Height; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(GraphStyle.Plot);
            var p = Plot;
            using (var grid = new Pen(GraphStyle.Grid))
                for (int db = -80; db <= 0; db += 20)
                {
                    float y = Y(db);
                    g.DrawLine(grid, p.X, y, p.Right, y);
                    TextRenderer.DrawText(g, db.ToString(), Theme.Small, new Point(Theme.Px(2), (int)y - Theme.Px(8)), Theme.Muted);
                }
            int n = Raw.Length;
            float dx = p.Width / (n - 1);
            // Gate closed = shaded.
            if (Threshold.HasValue)
                using (var b = new SolidBrush(GraphStyle.Gate))
                    for (int i = 0; i < n; i++) if (!GateOpen[i]) g.FillRectangle(b, p.X + i * dx, p.Y, dx + 1, p.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSeries(g, Raw, GraphStyle.Raw, 1.5f);
            DrawSeries(g, ShowOutput ? Output : Clean, Theme.Accent, 2f);
            if (NoiseFloor.HasValue)
                using (var pen = new Pen(GraphStyle.Noise, Theme.Px(1)) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, p.X, Y(NoiseFloor.Value), p.Right, Y(NoiseFloor.Value));
            if (Threshold.HasValue)
                using (var pen = new Pen(Theme.Text, Theme.Px(1)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, p.X, Y(Threshold.Value), p.Right, Y(Threshold.Value));
            int lx = (int)p.X;
            int lx2 = GraphStyle.Legend(g, lx, Theme.Px(3), GraphStyle.Raw, RawLabel, true);
            lx2 = GraphStyle.Legend(g, lx2, Theme.Px(3), Theme.Accent, ShowOutput ? "What apps hear" : "After the gate", true);
            if (NoiseFloor.HasValue) lx2 = GraphStyle.Legend(g, lx2, Theme.Px(3), GraphStyle.Noise, "Noise", true);
            if (Threshold.HasValue)
            {
                lx2 = GraphStyle.Legend(g, lx2, Theme.Px(3), Theme.Text, "Threshold", true);
                GraphStyle.Legend(g, lx2, Theme.Px(3), Color.FromArgb(160, 229, 83, 75), "Gate closed");
            }
            TextRenderer.DrawText(g, "-12 s", Theme.Small, new Point((int)p.X, (int)p.Bottom - Theme.Px(16)), Theme.Muted);
            TextRenderer.DrawText(g, "now", Theme.Small, new Point((int)p.Right - Theme.Px(28), (int)p.Bottom - Theme.Px(16)), Theme.Muted);
        }

        void DrawSeries(Graphics g, float[] s, Color c, float width)
        {
            var p = Plot;
            int n = s.Length, step = Math.Max(1, n / Math.Max(1, (int)p.Width));
            var pts = new List<PointF>();
            for (int i = 0; i < n; i += step)
            {
                float v = s[i];
                for (int k = 1; k < step && i + k < n; k++) v = Math.Max(v, s[i + k]);
                pts.Add(new PointF(p.X + p.Width * i / (n - 1), Y(v)));
            }
            if (pts.Count > 1) using (var pen = new Pen(c, Theme.Px(1) * width / 1.5f)) g.DrawLines(pen, pts.ToArray());
        }

        public static float Percentile(float[] v, double q)
        {
            var copy = (float[])v.Clone();
            Array.Sort(copy);
            return copy[(int)Math.Max(0, Math.Min(copy.Length - 1, q * (copy.Length - 1)))];
        }
    }
}
