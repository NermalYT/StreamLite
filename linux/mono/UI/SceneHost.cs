using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Linux: the surface a full-screen scene (Personalize 3D, practice) is drawn on - the same scene code as on Windows,
    /// drawn with GDI+ (GdiCanvas) on a double-buffered control, redrawn about 60 times a second while Dirty, with the
    /// same mouse and key events as the Windows (WPF) host. GDI+ draws on the CPU here: when a frame takes too long the
    /// scene is drawn smaller and scaled up (4.1.4: Personalize 3D ran at ~33 fps full size), and sharp again when it can.
    /// </summary>
    internal sealed class SceneHost : Control
    {
        /// <summary>Called on every frame before drawing (the scene's clock).</summary>
        public event Action Frame;
        /// <summary>Draws the scene for the canvas (pixels, the size of the control).</summary>
        public Action<ICanvas> Draw;
        public bool Dirty = true;
        public event Action<MouseButtons> Down;
        public event Action Moved;
        public event Action<Keys> Key;
        readonly Bitmap[] pics;
        readonly Timer timer = new Timer { Interval = 16 };
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>The last frames' times (ms), for the frame-rate probe.</summary>
        public readonly Queue<double> FrameTimes = new Queue<double>();
        double last;
        /// <summary>The size the scene is drawn at (1 = full; down to 0.5 when frames take too long).</summary>
        public float RenderScale = 1f;
        Bitmap frame;
        double drawAvg;
        int painted; double paintedSince, drawSum;

        public SceneHost(params Bitmap[] pictures)
        {
            // Copied once (as the Windows host does): the scene may dispose its own pictures while a frame is still due.
            pics = Array.ConvertAll(pictures ?? new Bitmap[0], p => p != null ? new Bitmap(p) : null);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            TabStop = true;
            timer.Tick += (s, e) =>
            {
                double now = Clock.Elapsed.TotalMilliseconds;
                FrameTimes.Enqueue(now - last); last = now; while (FrameTimes.Count > 240) FrameTimes.Dequeue();
                if (Frame != null) Frame();
                if (Dirty) { Dirty = false; Invalidate(); }
            };
            timer.Start();
        }

        public void FocusScene() { Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Draw == null) { e.Graphics.Clear(Theme.Bg); return; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (RenderScale >= 0.99f) Draw(new GdiCanvas(e.Graphics, pics));
            else
            {
                int w = Math.Max(1, (int)(Width * RenderScale)), h = Math.Max(1, (int)(Height * RenderScale));
                if (frame == null || frame.Width != w || frame.Height != h) { if (frame != null) frame.Dispose(); frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
                using (var g = Graphics.FromImage(frame)) { g.ScaleTransform(RenderScale, RenderScale); Draw(new GdiCanvas(g, pics)); }
                e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(frame, new Rectangle(0, 0, Width, Height));
            }
            // A frame should take well under 16 ms: smaller while it doesn't, back up when there's room again.
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            drawAvg = drawAvg == 0 ? ms : drawAvg * 0.9 + ms * 0.1;
            // health.log every 10 s while it runs: frames shown a second, at what size, and what a frame takes.
            painted++; drawSum += ms;
            double now = Clock.Elapsed.TotalMilliseconds;
            if (now - paintedSince >= 10000)
            {
                if (paintedSince > 0) try { HealthMonitor.LogLine("3D view: " + (painted * 1000.0 / (now - paintedSince)).ToString("0") + " frames a second, drawn at " + (RenderScale * 100).ToString("0") + "% size, " + (drawSum / painted).ToString("0.0") + " ms a frame"); } catch { }
                painted = 0; drawSum = 0; paintedSince = now;
            }
            if (drawAvg > 11 && RenderScale > 0.5f) { RenderScale = Math.Max(0.5f, RenderScale - 0.125f); drawAvg = 0; }
            else if (RenderScale < 1f && drawAvg * Math.Pow((RenderScale + 0.125) / RenderScale, 2) < 8) { RenderScale = Math.Min(1f, RenderScale + 0.125f); drawAvg = 0; }   // only when the bigger size would still fit
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (Down != null) Down(e.Button); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Moved != null) Moved(); base.OnMouseMove(e); }
        protected override bool IsInputKey(Keys keyData) { return true; }   // arrows, Tab, Space and Esc come here too
        protected override void OnKeyDown(KeyEventArgs e) { if (Key != null) Key(e.KeyCode); e.Handled = true; base.OnKeyDown(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); foreach (var p in pics) if (p != null) p.Dispose(); if (frame != null) frame.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
