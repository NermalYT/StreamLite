using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Linux: the surface a full-screen scene (Personalize 3D, practice) is drawn on - the same scene code as on Windows,
    /// drawn with GDI+ (GdiCanvas) on a double-buffered control, redrawn about 60 times a second while Dirty, with the
    /// same mouse and key events as the Windows (WPF) host.
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

        public SceneHost(params Bitmap[] pictures)
        {
            pics = pictures;
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
            Draw(new GdiCanvas(e.Graphics, pics));
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (Down != null) Down(e.Button); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Moved != null) Moved(); base.OnMouseMove(e); }
        protected override bool IsInputKey(Keys keyData) { return true; }   // arrows, Tab, Space and Esc come here too
        protected override void OnKeyDown(KeyEventArgs e) { if (Key != null) Key(e.KeyCode); e.Handled = true; base.OnKeyDown(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
