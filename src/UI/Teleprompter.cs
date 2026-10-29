using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// The teleprompter of Tune to my voice (4.1): the script in big letters, two lines to read in the middle, the line
    /// you've just read cut off at the top and the next one at the bottom. Every word turns from white to green the
    /// moment it's heard, and the text glides up a line as you reach the next one - redrawn every frame of the
    /// monitor's timer (about 120 a second), eased so it never jumps.
    /// </summary>
    internal sealed class Teleprompter : Control
    {
        sealed class Word { public string Text; public int Line; public float X, W; public float Green; }

        string script = "";
        readonly List<Word> words = new List<Word>();
        int lineCount, heard;
        float scroll;             // the first main line's index, eased towards the target (fractional while gliding)
        readonly Font font;
        readonly Timer timer = new Timer { Interval = 8 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastMs;
        bool laidOut;

        public Teleprompter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            font = Theme.Ui(32f, true);
            timer.Tick += (s, e) => Step();
        }

        /// <summary>Line height: the four visible line slots share the control's height (the top one half cut off).</summary>
        float LineH { get { return Height / 3.5f; } }

        /// <summary>The script to read (from the start: no word heard yet).</summary>
        public string Script
        {
            get { return script; }
            set { script = value ?? ""; heard = 0; scroll = 0; laidOut = false; Invalidate(); }
        }

        /// <summary>Words heard so far (in order): those turn green.</summary>
        public int Heard
        {
            get { return heard; }
            set { heard = Math.Max(0, Math.Min(words.Count == 0 ? value : words.Count, value)); if (!timer.Enabled) Invalidate(); }
        }

        public int WordCount { get { if (!laidOut) Layout(); return words.Count; } }

        /// <summary>Animating while shown: start / stop the frame timer.</summary>
        public void Run(bool on) { timer.Enabled = on; if (on) { lastMs = clock.Elapsed.TotalMilliseconds; } }

        /// <summary>Breaks the script into lines that fit the width (words as WordFollower counts them stay single words).</summary>
        void Layout()
        {
            words.Clear(); lineCount = 0;
            if (Width <= 0) return;
            float max = Width - Theme.Px(40), space;
            using (var g = CreateGraphics())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                space = g.MeasureString("a b", font, PointF.Empty, StringFormat.GenericTypographic).Width - g.MeasureString("ab", font, PointF.Empty, StringFormat.GenericTypographic).Width;
                float x = 0; int line = 0;
                foreach (var t in script.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    float w = g.MeasureString(t, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                    if (x > 0 && x + w > max) { line++; x = 0; }
                    // A word that's only punctuation ("-") rides along with the one before (it isn't counted).
                    bool counted = Audio.WordFollower.Words(t).Length > 0;
                    if (!counted && words.Count > 0 && words[words.Count - 1].Line == line)
                    {
                        var prev = words[words.Count - 1]; prev.Text += " " + t; prev.W = g.MeasureString(prev.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                        x = prev.X + prev.W + space; continue;
                    }
                    words.Add(new Word { Text = t, Line = line, X = x, W = w });
                    x += w + space;
                }
                lineCount = line + 1;
            }
            laidOut = true;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); laidOut = false; Invalidate(); }

        /// <summary>The line the reading is on: the first main line shows it (the one before glides up out of view).</summary>
        float Target()
        {
            if (words.Count == 0) return 0;
            int cur = Math.Min(heard, words.Count - 1);
            return Math.Max(0, Math.Min(words[cur].Line, Math.Max(0, lineCount - 2)));
        }

        /// <summary>One frame: words fade to green, the text glides towards its line (about a fifth of a second).</summary>
        void Step()
        {
            if (!laidOut) Layout();
            double now = clock.Elapsed.TotalMilliseconds, dt = Math.Min(100, now - lastMs); lastMs = now;
            bool moving = false;
            float t = Target(), k = (float)(1 - Math.Exp(-dt / 70.0));
            if (Math.Abs(t - scroll) > 0.001f) { scroll += (t - scroll) * k; if (Math.Abs(t - scroll) < 0.002f) scroll = t; moving = true; }
            float fade = (float)(dt / 140.0);
            for (int i = 0; i < words.Count; i++)
            {
                float want = i < heard ? 1 : 0;
                if (words[i].Green != want) { words[i].Green = want > words[i].Green ? Math.Min(want, words[i].Green + fade) : Math.Max(want, words[i].Green - fade); moving = true; }
            }
            if (moving) Invalidate();
        }

        static Color Mix(Color a, Color b, float t) { return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!laidOut) Layout();
            var g = e.Graphics;
            g.Clear(BackColor);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float lh = LineH;
            // Slot 0 = the line before (half cut off at the top), 1 and 2 = the two to read, 3 = the next one.
            float top = -lh * 0.5f - (scroll - 1) * lh, left = Theme.Px(20);
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                foreach (var w in words)
                {
                    float y = top + w.Line * lh + (lh - font.GetHeight(g)) / 2;
                    if (y > Height || y + lh < 0) continue;
                    // Lines outside the two reading lines are dimmer.
                    float rel = w.Line - scroll, dim = rel < -0.01f ? Math.Max(0.6f, 1 + rel * 0.4f) : rel > 1.01f ? Math.Max(0.55f, 1 - (rel - 1) * 0.45f) : 1f;
                    var c = Mix(Theme.Text, Theme.Accent, w.Green);
                    c = Color.FromArgb((int)(255 * dim), c);
                    using (var b = new SolidBrush(c)) g.DrawString(w.Text, font, b, left + w.X, y, sf);
                }
            // Soft edges: the cut-off lines fade into the background.
            float edgeTop = lh * 0.3f, edgeBottom = lh * 0.25f;
            using (var top1 = new LinearGradientBrush(new RectangleF(0, 0, Width, edgeTop), BackColor, Color.FromArgb(0, BackColor), LinearGradientMode.Vertical))
                g.FillRectangle(top1, 0, 0, Width, edgeTop);
            using (var bot = new LinearGradientBrush(new RectangleF(0, Height - edgeBottom, Width, edgeBottom), Color.FromArgb(0, BackColor), BackColor, LinearGradientMode.Vertical))
                g.FillRectangle(bot, 0, Height - edgeBottom, Width, edgeBottom + 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); font.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
