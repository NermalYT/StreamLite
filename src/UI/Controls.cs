using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamLite.UI
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(16, 18, 20);
        public static readonly Color Side = Color.FromArgb(22, 24, 27);
        public static readonly Color Card = Color.FromArgb(28, 31, 35);
        public static readonly Color Field = Color.FromArgb(38, 42, 47);
        public static readonly Color FieldHover = Color.FromArgb(50, 55, 61);
        public static readonly Color Track = Color.FromArgb(55, 60, 67);
        public static readonly Color Text = Color.FromArgb(236, 237, 238);
        public static readonly Color Muted = Color.FromArgb(139, 144, 150);
        public static readonly Color Accent = Color.FromArgb(91, 210, 122);
        public static readonly Color AccentDim = Color.FromArgb(38, 84, 52);
        public static readonly Color Warn = Color.FromArgb(242, 184, 75);
        public static readonly Color Danger = Color.FromArgb(229, 83, 75);

        public static float Scale = 1f;
        public static int Px(int v) { return (int)Math.Round(v * Scale); }

        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Section = new Font("Segoe UI Semibold", 11f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 16f);

        public static void Init(Control c)
        {
            using (var g = c.CreateGraphics()) Scale = g.DpiX / 96f;
        }

        public static Label Label(Control parent, string text, int x, int y, Font font = null, Color? color = null, int width = 0)
        {
            var l = new Label
            {
                Text = text, Font = font ?? Body, ForeColor = color ?? Text, BackColor = Color.Transparent,
                Location = new Point(Px(x), Px(y)), AutoSize = width == 0, UseMnemonic = false
            };
            if (width > 0) { l.Size = new Size(Px(width), Px(20)); l.AutoEllipsis = true; }
            parent.Controls.Add(l);
            return l;
        }

        static ToolTip help;

        /// <summary>Attaches a hover popup that explains what a setting does. Returns the control for chaining.</summary>
        public static T Help<T>(T control, string text) where T : Control
        {
            if (help == null)
            {
                help = new ToolTip { OwnerDraw = true, InitialDelay = 350, AutoPopDelay = 30000, ReshowDelay = 100, ShowAlways = true };
                help.Popup += (s, e) =>
                {
                    var t = help.GetToolTip(e.AssociatedControl);
                    var size = TextRenderer.MeasureText(t, Small, new Size(Px(360), 0), TextFormatFlags.WordBreak);
                    e.ToolTipSize = new Size(size.Width + Px(20), size.Height + Px(16));
                };
                help.Draw += (s, e) =>
                {
                    using (var b = new SolidBrush(Color.FromArgb(34, 38, 43))) e.Graphics.FillRectangle(b, e.Bounds);
                    using (var p = new Pen(Accent)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                    var r = new Rectangle(Px(10), Px(8), e.Bounds.Width - Px(20), e.Bounds.Height - Px(16));
                    TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, r, Text, TextFormatFlags.WordBreak);
                };
            }
            help.SetToolTip(control, text);
            if (control is Label) control.Cursor = Cursors.Help;
            return control;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Px(10)))
            using (var b = new SolidBrush(Theme.Card))
                e.Graphics.FillPath(b, path);
        }
    }

    internal class ToggleSwitch : Control
    {
        bool isChecked;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.Px(42), Theme.Px(22));
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent;
            TabStop = true;
        }

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked == value) return; isChecked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        /// <summary>Sets the state without raising CheckedChanged.</summary>
        public void SetSilently(bool value) { isChecked = value; Invalidate(); }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Checked = !Checked;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color track = !Enabled ? Theme.Field : isChecked ? Theme.Accent : Theme.Track;
            using (var path = Theme.Rounded(r, r.Height / 2))
            using (var b = new SolidBrush(track))
                g.FillPath(b, path);
            float d = r.Height - 6;
            float x = isChecked ? r.Right - d - 3 : r.X + 3;
            using (var b = new SolidBrush(Enabled ? (isChecked ? Color.FromArgb(14, 30, 18) : Theme.Text) : Theme.Muted))
                g.FillEllipse(b, x, r.Y + 3, d, d);
        }
    }

    internal class FlatSlider : Control
    {
        int min, max = 100, value, step = 1;
        bool dragging;
        public event EventHandler ValueChanged;
        public bool Vertical;

        public FlatSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(Theme.Px(200), Theme.Px(22));
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get { return min; } set { min = value; if (this.value < min) this.value = min; Invalidate(); } }
        public int Maximum { get { return max; } set { max = value; if (this.value > max) this.value = max; Invalidate(); } }
        public int Step { get { return step; } set { step = Math.Max(1, value); } }
        public int DefaultValue;

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (step > 1) v = min + (int)Math.Round((v - min) / (double)step) * step;
                v = Math.Max(min, Math.Min(max, v));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilently(int v) { value = Math.Max(min, Math.Min(max, v)); Invalidate(); }

        float Fraction { get { return max == min ? 0 : (value - min) / (float)(max - min); } }

        int Pad { get { return Theme.Px(8); } }

        void SetFromMouse(Point p)
        {
            float f = Vertical ? 1f - (p.Y - Pad) / (float)(Height - 2 * Pad) : (p.X - Pad) / (float)(Width - 2 * Pad);
            f = Math.Max(0, Math.Min(1, f));
            Value = min + (int)Math.Round(f * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Enabled) return;
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; Capture = true; SetFromMouse(e.Location); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromMouse(e.Location); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; base.OnMouseUp(e); }
        protected override void OnDoubleClick(EventArgs e) { if (Enabled) Value = DefaultValue; base.OnDoubleClick(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Enabled) return;
            Value += Math.Sign(e.Delta) * step;
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= step;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += step;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float t = Theme.Px(4), r = Theme.Px(7);
            Color fill = Enabled ? Theme.Accent : Theme.Muted;
            if (Vertical)
            {
                float cx = Width / 2f, top = Pad, bottom = Height - Pad;
                using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, cx - t / 2, top, t, bottom - top);
                float y = bottom - Fraction * (bottom - top);
                // Fill from the zero point for bipolar ranges (EQ bands).
                float zero = min < 0 && max > 0 ? bottom - (-min / (float)(max - min)) * (bottom - top) : bottom;
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, cx - t / 2, Math.Min(y, zero), t, Math.Abs(zero - y));
                using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Muted)) g.FillEllipse(b, cx - r, y - r, 2 * r, 2 * r);
            }
            else
            {
                float cy = Height / 2f, left = Pad, right = Width - Pad;
                using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, left, cy - t / 2, right - left, t);
                float x = left + Fraction * (right - left);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, left, cy - t / 2, x - left, t);
                using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Muted)) g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
            }
            if (Focused)
                using (var p = new Pen(Color.FromArgb(60, Theme.Accent))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    internal class LevelMeter : Control
    {
        float level = -100f;
        public float? ThresholdDb;
        public bool GateOpen = true;
        public const float MinDb = -80f;

        public LevelMeter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.Px(200), Theme.Px(8));
        }

        public float Level { get { return level; } set { if (Math.Abs(value - level) < 0.1f) return; level = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, 0, 0, Width, Height);
            float f = Math.Max(0, Math.Min(1, (level - MinDb) / -MinDb));
            Color c = level > -3 ? Theme.Danger : level > -12 ? Theme.Warn : Theme.Accent;
            if (!GateOpen) c = Theme.Muted;
            using (var b = new SolidBrush(c)) g.FillRectangle(b, 0, 0, f * Width, Height);
            if (ThresholdDb.HasValue)
            {
                float tx = Math.Max(0, Math.Min(1, (ThresholdDb.Value - MinDb) / -MinDb)) * Width;
                using (var p = new Pen(Theme.Text, Theme.Px(2))) g.DrawLine(p, tx, 0, tx, Height);
            }
        }
    }

    internal class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Field;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ItemHeight = Theme.Px(22);
            Width = Theme.Px(300);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.AccentDim : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                    Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal class FlatButton : Button
    {
        public FlatButton(string text, bool primary = false)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = primary ? Theme.Accent : Theme.Field;
            ForeColor = primary ? Color.FromArgb(10, 25, 14) : Theme.Text;
            FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(120, 225, 148) : Theme.FieldHover;
            FlatAppearance.MouseDownBackColor = primary ? Theme.Accent : Theme.Track;
            Font = primary ? Theme.Bold : Theme.Body;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.StandardDoubleClick, false);
            AutoSize = false;
            Size = new Size(Math.Max(Theme.Px(90), TextRenderer.MeasureText(text, Font).Width + Theme.Px(28)), Theme.Px(30));
            UseVisualStyleBackColor = false;
            this.primary = primary;
        }

        readonly bool primary;
        Color enabledBack;

        /// <summary>A green button that can't be pressed yet looks it (4.1.1): grey until it can.</summary>
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (primary)
            {
                if (!Enabled) { if (BackColor != Theme.Track) enabledBack = BackColor; BackColor = Theme.Track; Cursor = Cursors.Default; }
                else { BackColor = enabledBack.IsEmpty ? Theme.Accent : enabledBack; Cursor = Cursors.Hand; }
            }
            base.OnEnabledChanged(e);
        }
    }

    internal class NavButton : Control
    {
        public bool Selected;
        public string Glyph;

        public NavButton(string text, string glyph)
        {
            Text = text; Glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Size = new Size(Theme.Px(184), Theme.Px(40));
            BackColor = Theme.Side;
        }

        bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Selected || hover)
                using (var path = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Px(8)))
                using (var b = new SolidBrush(Selected ? Theme.Field : Color.FromArgb(30, 33, 37)))
                    g.FillPath(b, path);
            if (Selected)
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, Theme.Px(10), Theme.Px(3), Height - Theme.Px(20));
            using (var f = new Font("Segoe MDL2 Assets", 12f))
                TextRenderer.DrawText(g, Glyph, f, new Rectangle(Theme.Px(14), 0, Theme.Px(24), Height), Selected ? Theme.Accent : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Text, Selected ? Theme.Bold : Theme.Body, new Rectangle(Theme.Px(46), 0, Width - Theme.Px(46), Height), Selected ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter);
        }
    }

#if LINUX
    /// <summary>Linux: the window styling and dragging Windows does natively, done by StreamLite (Mono has no user32 / dwmapi).</summary>
    internal static class Win32
    {
        public static bool ReleaseCapture() { return true; }
        public static IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l) { return IntPtr.Zero; }
        public static int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size) { return 0; }
        public static bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info) { return true; }
        public static void StyleWindow(IntPtr hwnd) { }

        /// <summary>Moves the window with the mouse until the button is let go (borderless windows have no title bar of the desktop's).</summary>
        public static void DragWindow(Form f)
        {
            var start = Control.MousePosition; var from = f.Location;
            var t = new Timer { Interval = 10 };
            t.Tick += (s, e) =>
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0 || f.IsDisposed) { t.Stop(); t.Dispose(); return; }
                var now = Control.MousePosition;
                f.Location = new Point(from.X + now.X - start.X, from.Y + now.Y - start.Y);
            };
            t.Start();
        }

        /// <summary>Resizes the window from an edge with the mouse (edges: 1 left, 2 right, 4 top, 8 bottom).</summary>
        public static void ResizeWindow(Form f, int edges)
        {
            var start = Control.MousePosition; var from = f.Bounds; var min = f.MinimumSize;
            var t = new Timer { Interval = 10 };
            t.Tick += (s, e) =>
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0 || f.IsDisposed) { t.Stop(); t.Dispose(); return; }
                var now = Control.MousePosition; int dx = now.X - start.X, dy = now.Y - start.Y;
                var r = from;
                if ((edges & 1) != 0) { r.X = from.X + dx; r.Width = from.Width - dx; }
                if ((edges & 2) != 0) r.Width = from.Width + dx;
                if ((edges & 4) != 0) { r.Y = from.Y + dy; r.Height = from.Height - dy; }
                if ((edges & 8) != 0) r.Height = from.Height + dy;
                if (r.Width < min.Width) { if ((edges & 1) != 0) r.X -= min.Width - r.Width; r.Width = min.Width; }
                if (r.Height < min.Height) { if ((edges & 4) != 0) r.Y -= min.Height - r.Height; r.Height = min.Height; }
                f.Bounds = r;
            };
            t.Start();
        }
    }
#else
    internal static class Win32
    {
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);

        /// <summary>Dark mode + Windows 11 rounded corners for our borderless windows.</summary>
        public static void StyleWindow(IntPtr hwnd)
        {
            int on = 1, round = 2;
            try { DwmSetWindowAttribute(hwnd, 20, ref on, 4); } catch { }
            try { DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }
        }

        /// <summary>Starts a native window drag (keeps Aero Snap).</summary>
        public static void DragWindow(Form f)
        {
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }
    }
#endif

    internal class WindowButton : Control
    {
        string glyph;
        /// <summary>The Segoe MDL2 symbol shown (fullscreen / back to a window).</summary>
        public string Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
        readonly bool danger;
        bool hover;

        public WindowButton(string glyph, bool danger)
        {
            this.glyph = glyph;
            this.danger = danger;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Size = new Size(Theme.Px(46), Theme.Px(40));
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(hover ? (danger ? Color.FromArgb(196, 43, 28) : Theme.Field) : (Parent != null ? Parent.BackColor : Theme.Side));
            using (var f = new Font("Segoe MDL2 Assets", 8.5f))
                TextRenderer.DrawText(e.Graphics, glyph, f, ClientRectangle, hover && danger ? Color.White : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Our own title bar: drag to move, minimize and close buttons.</summary>
    internal class TitleBar : Panel
    {
        public event EventHandler CloseClicked;
        /// <summary>The fullscreen button, or a double-click on the bar (fullscreen: true).</summary>
        public event EventHandler FullScreenClicked;
        public readonly WindowButton CloseButton, MinimizeButton, FullScreenButton;
        /// <summary>false = a popup's bar: no dragging (4.1 - popups only close).</summary>
        public bool Movable = true;
        int lastDown; Point lastAt;
        Label note; Action noteClick;

        /// <summary>A small accent note at the right of the bar, before the window buttons (null = none), clickable.</summary>
        public void ShowNote(string text, Action click)
        {
            if (note == null)
            {
                note = new Label { AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Accent, BackColor = Color.Transparent, Cursor = Cursors.Hand, UseMnemonic = false };
                note.Click += (s, e) => { if (noteClick != null) noteClick(); };
                Controls.Add(note);
            }
            noteClick = click;
            note.Text = text ?? "";
            note.Visible = !string.IsNullOrEmpty(text);
            OnResize(EventArgs.Empty);
        }

        public TitleBar(string title, bool minimize, bool icon, bool fullscreen = false)
        {
            Dock = DockStyle.Top;
            Height = Theme.Px(40);
            BackColor = Theme.Side;
            int x = Theme.Px(14);
            if (icon)
            {
                var pic = new PictureBox { Size = new Size(Theme.Px(18), Theme.Px(18)), Location = new Point(x, Theme.Px(11)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
                try { pic.Image = new Icon(Icon.ExtractAssociatedIcon(Application.ExecutablePath), Theme.Px(18), Theme.Px(18)).ToBitmap(); } catch { }
                Controls.Add(pic);
                pic.MouseDown += Drag;
                x += Theme.Px(28);
            }
            var label = new Label { Text = title, Font = Theme.Bold, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent, Location = new Point(x, Theme.Px(11)), UseMnemonic = false };
            Controls.Add(label);
            label.MouseDown += Drag;

            CloseButton = new WindowButton("\uE8BB", true);
            CloseButton.Click += (s, e) => { if (CloseClicked != null) CloseClicked(this, EventArgs.Empty); };
            Controls.Add(CloseButton);
            if (minimize)
            {
                MinimizeButton = new WindowButton("\uE921", false);
                MinimizeButton.Click += (s, e) => { var f = FindForm(); if (f != null) f.WindowState = FormWindowState.Minimized; };
                Controls.Add(MinimizeButton);
            }
            if (fullscreen)
            {
                FullScreenButton = new WindowButton("\uE740", false);
                FullScreenButton.Click += (s, e) => { if (FullScreenClicked != null) FullScreenClicked(this, EventArgs.Empty); };
                Controls.Add(FullScreenButton);
            }
            MouseDown += Drag;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (CloseButton == null) return;
            CloseButton.SetBounds(Width - CloseButton.Width, 0, CloseButton.Width, Height);
            if (MinimizeButton != null) MinimizeButton.SetBounds(CloseButton.Left - MinimizeButton.Width, 0, MinimizeButton.Width, Height);
            if (FullScreenButton != null) FullScreenButton.SetBounds((MinimizeButton ?? CloseButton).Left - FullScreenButton.Width, 0, FullScreenButton.Width, Height);
            if (note != null) note.Location = new Point((FullScreenButton ?? MinimizeButton ?? CloseButton).Left - note.Width - Theme.Px(14), (Height - note.Height) / 2);
        }

        void Drag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // A double-click on the bar toggles fullscreen (the window move below swallows Windows' own double-click).
            var at = Control.MousePosition;
            if (FullScreenButton != null && Environment.TickCount - lastDown < SystemInformation.DoubleClickTime &&
                Math.Abs(at.X - lastAt.X) <= SystemInformation.DoubleClickSize.Width && Math.Abs(at.Y - lastAt.Y) <= SystemInformation.DoubleClickSize.Height)
            {
                lastDown = 0;
                if (FullScreenClicked != null) FullScreenClicked(this, EventArgs.Empty);
                return;
            }
            lastDown = Environment.TickCount; lastAt = at;
            if (!Movable) return;
            var f = FindForm(); if (f != null) Win32.DragWindow(f);
        }
    }

    /// <summary>
    /// Lists in StreamLite's look (4.1.1): rows on the card colour, the picked one with a green bar - not Windows' bright
    /// blue selection (Priority, Auto EQ apps).
    /// </summary>
    internal static class DarkList
    {
        public static ListBox Style(ListBox l)
        {
            l.DrawMode = DrawMode.OwnerDrawFixed;
            l.ItemHeight = Theme.Px(26);
            l.BorderStyle = BorderStyle.None;
            l.BackColor = Theme.Card;
            l.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.Field : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + Theme.Px(4), Theme.Px(3), e.Bounds.Height - Theme.Px(8));
                var r = new Rectangle(e.Bounds.X + Theme.Px(12), e.Bounds.Y, e.Bounds.Width - Theme.Px(16), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, l.Items[e.Index].ToString(), l.Font, r, sel ? Theme.Accent : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            return l;
        }
    }

    internal class DarkCheck : CheckBox
    {
        public DarkCheck(string text)
        {
            Text = text;
            ForeColor = Theme.Muted;
            Font = Theme.Small;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Theme.Track;
            FlatAppearance.CheckedBackColor = Theme.AccentDim;
            AutoSize = true;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.StandardDoubleClick, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        /// <summary>Drawn by StreamLite (4.1.1): Windows' flat checkbox showed no tick on the dark theme - checked looked unchecked.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int box = Theme.Px(16), top = Math.Max(0, Math.Min((Height - box) / 2, Theme.Px(2)));
            var r = new Rectangle(0, top, box, box);
            using (var b = new SolidBrush(Checked ? Theme.Accent : Theme.Field)) g.FillRectangle(b, r);
            using (var p = new Pen(Checked ? Theme.Accent : Theme.Track, Theme.Px(1))) g.DrawRectangle(p, r);
            if (Checked)
                using (var p = new Pen(Color.FromArgb(10, 25, 14), Theme.Px(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLines(p, new[] { new PointF(r.X + box * 0.22f, r.Y + box * 0.52f), new PointF(r.X + box * 0.42f, r.Y + box * 0.72f), new PointF(r.X + box * 0.78f, r.Y + box * 0.3f) });
            var textRect = new Rectangle(box + Theme.Px(8), 0, Math.Max(1, Width - box - Theme.Px(8)), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | (AutoSize ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top));
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var s = TextRenderer.MeasureText(Text, Font);
            return new Size(s.Width + Theme.Px(26), Math.Max(s.Height, Theme.Px(18)) + Theme.Px(2));
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    }
}
