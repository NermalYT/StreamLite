using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Makes pages built at a fixed design width follow the window width: wide things (cards, graphs, combos,
    /// sliders reaching the right edge) stretch, small things on the right stick to the right, everything else stays.
    /// </summary>
    internal static class Responsive
    {
        static readonly HashSet<Control> proportional = new HashSet<Control>();
        static readonly HashSet<Control> fixedOnes = new HashSet<Control>();

        /// <summary>Children of this container keep their relative horizontal positions (e.g. a row of EQ sliders).</summary>
        public static void Proportional(Control container)
        {
            proportional.Add(container);
            int designW = container.Width;
            var lefts = new Dictionary<Control, int>();
            foreach (Control c in container.Controls) lefts[c] = c.Left + c.Width / 2;
            container.Resize += (s, e) =>
            {
                if (designW <= 0) return;
                foreach (Control c in container.Controls)
                {
                    int center;
                    if (!lefts.TryGetValue(c, out center)) { lefts[c] = center = c.Left + c.Width / 2; }
                    c.Left = (int)Math.Round(center * (double)container.Width / designW) - c.Width / 2;
                }
            };
        }

        /// <summary>Leave this control (and its children) exactly as built.</summary>
        public static void Fixed(Control c) { fixedOnes.Add(c); }

        static bool Stretchable(Control c)
        {
            if (c is Card || c is EqEditor || c is EqGraph || c is LevelMeter || c is TextBox || c is DarkCombo) return true;
            if (c is FlatSlider) return !((FlatSlider)c).Vertical;
            if (c is Label) return !((Label)c).AutoSize;
            if (c is Button || c is ToggleSwitch || c is CheckBox) return false;
            string ns = c.GetType().Namespace ?? "";
            return c is Panel || (ns.StartsWith("StreamLite.UI") && c.Width >= 200);   // graphs
        }

        public static void Apply(Control parent)
        {
            if (fixedOnes.Contains(parent)) return;
            int w = parent.Width;
            if (w <= 0) return;
            var stretched = new List<Control>();
            foreach (Control c in parent.Controls)
            {
                if (fixedOnes.Contains(c)) continue;
                if (!proportional.Contains(parent))
                {
                    var a = AnchorStyles.Top | AnchorStyles.Left;
                    bool wide = c.Width >= Math.Max(Theme.Px(100), w / 6);
                    if (Stretchable(c) && wide && c.Right >= w * 0.84 && c.Left < w * 0.8) { a = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; stretched.Add(c); }
                    else if (c.Left >= w * 0.5) a = AnchorStyles.Top | AnchorStyles.Right;
                    c.Anchor = a;
                }
                if (c.Controls.Count > 0 && !(c is ToggleSwitch) && !(c is FlatSlider)) Apply(c);
            }
            // A caption sitting just left of a stretching control stays with it (e.g. "Strength" before its slider).
            foreach (Control c in parent.Controls)
            {
                if (!(c is Label) || fixedOnes.Contains(c) || (c.Anchor & AnchorStyles.Right) == 0 || (c.Anchor & AnchorStyles.Left) != 0) continue;
                foreach (var s in stretched)
                {
                    int gap = s.Left - c.Right;
                    bool sameRow = c.Top < s.Bottom && s.Top < c.Bottom;
                    if (sameRow && gap >= -2 && gap <= Theme.Px(110)) { c.Anchor = AnchorStyles.Top | AnchorStyles.Left; break; }
                }
            }
        }
    }

    /// <summary>Thin dark scrollbar drawn over the right edge of a ScrollHost.</summary>
    internal sealed class DarkScrollBar : Control
    {
        public ScrollHost Host;
        bool dragging, hover;
        int dragStartY, dragStartOffset;

        public DarkScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Default;
        }

        Rectangle Thumb()
        {
            if (Host == null || Host.MaxOffset <= 0) return Rectangle.Empty;
            float vis = Host.ClientSize.Height / (float)(Host.MaxOffset + Host.ClientSize.Height);
            int h = Math.Max(Theme.Px(36), (int)(Height * vis));
            int y = (int)((Height - h) * (Host.Offset / (float)Host.MaxOffset));
            return new Rectangle(0, y, Width, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var t = Thumb();
            if (t.IsEmpty) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.Px(2);
            var r = new RectangleF(pad, t.Y + pad, Width - 2 * pad, t.Height - 2 * pad);
            using (var path = Theme.Rounded(r, r.Width / 2))
            using (var b = new SolidBrush(dragging ? Theme.Accent : hover ? Theme.Muted : Theme.Track))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var t = Thumb();
            if (t.IsEmpty) return;
            if (t.Contains(e.Location)) { dragging = true; dragStartY = e.Y; dragStartOffset = Host.Offset; Capture = true; }
            else Host.Offset += (e.Y < t.Y ? -1 : 1) * Host.ClientSize.Height * 9 / 10;   // page up / down
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            var t = Thumb();
            int track = Height - t.Height;
            if (track > 0) Host.Offset = dragStartOffset + (int)((e.Y - dragStartY) * (Host.MaxOffset / (float)track));
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); }
    }

    /// <summary>
    /// A scrolling viewport with a slim dark scrollbar instead of the Windows one. The content keeps its own height;
    /// its width follows the viewport (minus margins) so pages fill the window.
    /// </summary>
    internal sealed class ScrollHost : Panel
    {
        readonly DarkScrollBar bar = new DarkScrollBar();
        Control content;
        int offset;
        public int MarginX = 28, MarginTop = 18, MarginBottom = 20, MinContentWidth = 690, MaxContentWidth = 0;
        public event EventHandler Scrolled;

        public ScrollHost()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            bar.Host = this;
            bar.Width = Theme.Px(10);
            Controls.Add(bar);
        }

        public Control Content
        {
            get { return content; }
            set
            {
                if (content == value) return;
                if (content != null) content.SizeChanged -= ContentSizeChanged;
                content = value;
                if (content != null)
                {
                    if (content.Parent != this) Controls.Add(content);
                    content.SizeChanged += ContentSizeChanged;
                }
                bar.BringToFront();
                offset = 0;
                LayoutContent();
            }
        }

        void ContentSizeChanged(object s, EventArgs e) { if (!inLayout) LayoutContent(); }

        public int MaxOffset { get { return content == null ? 0 : Math.Max(0, content.Height + Theme.Px(MarginTop + MarginBottom) - ClientSize.Height); } }

        public int Offset
        {
            get { return offset; }
            set
            {
                int v = Math.Max(0, Math.Min(MaxOffset, value));
                if (v == offset) return;
                offset = v;
                if (content != null) content.Top = Theme.Px(MarginTop) - offset;
                bar.Invalidate();
                if (Scrolled != null) Scrolled(this, EventArgs.Empty);
            }
        }

        public void ScrollBy(int delta) { Offset += delta; }

        public void Relayout() { LayoutContent(); }

        bool inLayout;
        void LayoutContent()
        {
            bar.SetBounds(ClientSize.Width - bar.Width, 0, bar.Width, ClientSize.Height);
            if (content == null) return;
            inLayout = true;
            int w = Math.Max(Theme.Px(MinContentWidth), ClientSize.Width - Theme.Px(MarginX) * 2);
            int x = Theme.Px(MarginX);
            if (MaxContentWidth > 0 && w > Theme.Px(MaxContentWidth))
            {
                // Very wide windows: keep lines readable and center the page.
                w = Theme.Px(MaxContentWidth);
                x = Math.Max(Theme.Px(MarginX), (ClientSize.Width - bar.Width - w) / 2);
            }
            content.SetBounds(x, Theme.Px(MarginTop) - offset, w, content.Height);
            inLayout = false;
            if (offset > MaxOffset) Offset = MaxOffset;
            else content.Top = Theme.Px(MarginTop) - offset;
            bar.Visible = MaxOffset > 0;
            bar.Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutContent(); }

        /// <summary>Scrolls so a control inside the content sits near the top.</summary>
        public void ScrollIntoView(Control c)
        {
            if (content == null) return;
            int y = 0;
            for (var x = c; x != null && x != content; x = x.Parent) y += x.Top;
            Offset = y - Theme.Px(12);
        }
    }

    /// <summary>
    /// A scrolling list inside a dialog with the slim dark scrollbar (never the Windows one): add controls to the panel
    /// Make returns, then call Fit after changing them.
    /// </summary>
    internal static class ScrollLists
    {
        public static Panel Make(Control parent, Rectangle bounds, Color back, out ScrollHost host)
        {
            host = new ScrollHost { Bounds = bounds, BackColor = back, MarginX = 0, MarginTop = 0, MarginBottom = 6, MinContentWidth = 0 };
            var inner = new Panel { BackColor = back, Height = 10 };
            host.Content = inner;
            parent.Controls.Add(host);
            return inner;
        }

        /// <summary>The list as tall as what's in it, scrolled to the top.</summary>
        public static void Fit(ScrollHost host)
        {
            var c = host.Content; if (c == null) return;
            int b = 0; foreach (Control x in c.Controls) if (x.Visible) b = Math.Max(b, x.Bottom);
            c.Height = b + Theme.Px(6);
            host.Relayout(); host.Offset = 0;
        }
    }

    /// <summary>
    /// Mouse wheel goes to the page under the cursor (not just the focused control), except where a control uses the
    /// wheel itself (EQ graph, an open dropdown, a focused slider).
    /// </summary>
    internal sealed class WheelRouter : IMessageFilter
    {
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A) return false;   // WM_MOUSEWHEEL
            var pt = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
            var c = Control.FromHandle(WindowFromPoint(pt));
            if (c == null) return false;
            bool overSetting = false;   // a closed dropdown or idle slider must never change because you scrolled over it
            for (var x = c; x != null; x = x.Parent)
            {
                var graph = x as EqGraph;
                if (graph != null && graph.UsesWheel) return false;
                var combo = x as ComboBox;
                if (combo != null) { if (combo.DroppedDown) return false; overSetting = true; }
                var sl = x as FlatSlider;
                if (sl != null) { if (sl.Focused) return false; overSetting = true; }
                var host = x as ScrollHost;
                if (host != null)
                {
                    if (host.MaxOffset <= 0) continue;   // nothing to scroll here: let an outer host scroll
                    int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                    host.ScrollBy(-delta * Theme.Px(90) / 120);
                    return true;
                }
            }
            return overSetting;   // nothing to scroll: swallow it rather than change the setting
        }
    }
}
