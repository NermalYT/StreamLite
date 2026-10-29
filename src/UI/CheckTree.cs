using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>One line of a CheckTree: a tick box, its text, a note, the lines inside it and - for a device - which device it goes to.</summary>
    internal sealed class CheckNode
    {
        public string Text, Note;
        public object Tag;
        public bool Expanded;
        public readonly List<CheckNode> Children = new List<CheckNode>();
        public CheckNode Parent;
        bool own = true;
        /// <summary>A choice shown at the end of the line ("-> Blue Yeti"), e.g. which device here it goes to. Empty = none.</summary>
        public List<string> Choices = new List<string>();
        public int Choice;
        public string ChoicePrefix = "to ";
        /// <summary>The last choice means "not at all" ("Leave it out"): shown without the prefix.</summary>
        public bool LastChoiceIsOff;

        public CheckNode(string text, string note = null, object tag = null) { Text = text; Note = note; Tag = tag; }

        public CheckNode Add(CheckNode c) { c.Parent = this; Children.Add(c); return c; }
        public CheckNode Add(string text, string note = null, object tag = null) { return Add(new CheckNode(text, note, tag)); }

        /// <summary>Ticked: a line with lines inside is ticked when every one of them is.</summary>
        public bool Checked
        {
            get { return Children.Count == 0 ? own : Children.All(c => c.Checked); }
            set { own = value; foreach (var c in Children) c.Checked = value; }
        }

        /// <summary>Some (not all) of the lines inside are ticked.</summary>
        public bool Partial { get { return Children.Count > 0 && !Checked && Children.Any(c => c.Checked || c.Partial); } }

        /// <summary>This line and every line inside it.</summary>
        public IEnumerable<CheckNode> All() { yield return this; foreach (var c in Children) foreach (var d in c.All()) yield return d; }

        public string ChoiceText { get { return Choices.Count == 0 ? null : Choices[Math.Max(0, Math.Min(Choices.Count - 1, Choice))]; } }
    }

    /// <summary>
    /// A list of tick boxes in groups that fold open (4.1.3: choosing what to export or import, down to single settings
    /// and single devices). Ticking a group ticks everything in it; a half-filled box means some of it is ticked.
    /// </summary>
    internal sealed class CheckTree : Panel
    {
        public readonly List<CheckNode> Roots = new List<CheckNode>();
        public event EventHandler Changed;
        readonly Canvas canvas;
        readonly List<Row> rows = new List<Row>();
        int hoverRow = -1;

        sealed class Row { public CheckNode Node; public int Level; public Rectangle Box, Arrow, ChoiceRect; }

        public CheckTree()
        {
            AutoScroll = true;
            BackColor = Theme.Card;
            canvas = new Canvas(this) { Location = Point.Empty };
            Controls.Add(canvas);
        }

        int RowH { get { return Theme.Px(30); } }

        /// <summary>Call after changing the nodes (adding, expanding from code).</summary>
        public void Rebuild()
        {
            rows.Clear();
            foreach (var r in Roots) Walk(r, 0);
            canvas.Size = new Size(Math.Max(1, ClientSize.Width - (rows.Count * RowH > ClientSize.Height ? SystemInformation.VerticalScrollBarWidth : 0)), Math.Max(ClientSize.Height, rows.Count * RowH + Theme.Px(6)));
            canvas.Invalidate();
        }

        void Walk(CheckNode n, int level)
        {
            rows.Add(new Row { Node = n, Level = level });
            if (n.Expanded) foreach (var c in n.Children) Walk(c, level + 1);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

        sealed class Canvas : Control
        {
            readonly CheckTree tree;
            public Canvas(CheckTree t)
            {
                tree = t;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Card;
            }
            protected override void OnPaint(PaintEventArgs e) { tree.PaintRows(e.Graphics, e.ClipRectangle); }
            protected override void OnMouseMove(MouseEventArgs e) { tree.Hover(e.Location); base.OnMouseMove(e); }
            protected override void OnMouseLeave(EventArgs e) { tree.Hover(new Point(-1, -1)); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { tree.ClickAt(e.Location); base.OnMouseDown(e); }
        }

        void Hover(Point p)
        {
            int r = p.Y < 0 ? -1 : p.Y / RowH;
            if (r >= rows.Count) r = -1;
            if (r == hoverRow) return;
            hoverRow = r; canvas.Cursor = r >= 0 ? Cursors.Hand : Cursors.Default; canvas.Invalidate();
        }

        void PaintRows(Graphics g, Rectangle clip)
        {
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = RowH, box = Theme.Px(16), indent = Theme.Px(22);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var n = row.Node;
                int y = i * h;
                if (y + h < clip.Top || y > clip.Bottom) continue;
                if (i == hoverRow) using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, 0, y, canvas.Width, h);
                int x = Theme.Px(8) + row.Level * indent;
                // Fold arrow.
                row.Arrow = new Rectangle(x, y, Theme.Px(18), h);
                if (n.Children.Count > 0)
                {
                    float cx = x + Theme.Px(8), cy = y + h / 2f, s = Theme.Px(4);
                    var pts = n.Expanded ? new[] { new PointF(cx - s, cy - s / 2), new PointF(cx + s, cy - s / 2), new PointF(cx, cy + s / 1.2f) }
                                         : new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 1.2f, cy), new PointF(cx - s / 2, cy + s) };
                    using (var b = new SolidBrush(Theme.Muted)) g.FillPolygon(b, pts);
                }
                x += Theme.Px(20);
                // Tick box: ticked, half (some inside) or empty.
                var r = new Rectangle(x, y + (h - box) / 2, box, box);
                row.Box = r;
                bool on = n.Checked, part = n.Partial;
                using (var b = new SolidBrush(on ? Theme.Accent : Theme.Field)) g.FillRectangle(b, r);
                using (var p = new Pen(on || part ? Theme.Accent : Theme.Track, Theme.Px(1))) g.DrawRectangle(p, r);
                if (on)
                    using (var p = new Pen(Color.FromArgb(10, 25, 14), Theme.Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new[] { new PointF(r.X + box * 0.22f, r.Y + box * 0.52f), new PointF(r.X + box * 0.42f, r.Y + box * 0.72f), new PointF(r.X + box * 0.78f, r.Y + box * 0.3f) });
                else if (part)
                    using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X + box / 4, r.Y + box / 4, box / 2 + 1, box / 2 + 1);
                x += box + Theme.Px(8);
                // Text, the note after it, and the choice at the end.
                var font = row.Level == 0 ? Theme.Bold : Theme.Body;
                int right = canvas.Width - Theme.Px(10);
                row.ChoiceRect = Rectangle.Empty;
                if (n.ChoiceText != null)
                {
                    string ct = (n.LastChoiceIsOff && n.Choice >= n.Choices.Count - 1 ? "" : n.ChoicePrefix) + n.ChoiceText + "  ▾";
                    var cs = TextRenderer.MeasureText(ct, Theme.Small);
                    int cw = Math.Min(cs.Width + Theme.Px(16), Theme.Px(300));
                    row.ChoiceRect = new Rectangle(right - cw, y + Theme.Px(4), cw, h - Theme.Px(8));
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, row.ChoiceRect);
                    using (var p = new Pen(Theme.Track)) g.DrawRectangle(p, row.ChoiceRect);
                    TextRenderer.DrawText(g, ct, Theme.Small, row.ChoiceRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    right = row.ChoiceRect.Left - Theme.Px(8);
                }
                var ts = TextRenderer.MeasureText(n.Text, font);
                var tr = new Rectangle(x, y, Math.Max(1, right - x), h);
                TextRenderer.DrawText(g, n.Text, font, tr, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (!string.IsNullOrEmpty(n.Note) && x + ts.Width + Theme.Px(10) < right)
                    TextRenderer.DrawText(g, n.Note, Theme.Small, new Rectangle(x + ts.Width + Theme.Px(6), y, right - x - ts.Width - Theme.Px(6), h), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        void ClickAt(Point p)
        {
            int i = p.Y / RowH;
            if (i < 0 || i >= rows.Count) return;
            var row = rows[i]; var n = row.Node;
            if (row.ChoiceRect.Contains(p) && n.Choices.Count > 0) { PickChoice(n, row.ChoiceRect); return; }
            if (row.Box.Contains(p) || (n.Children.Count == 0 && p.X > row.Box.Left))
            {
                n.Checked = !(n.Checked || n.Partial);
                canvas.Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
                return;
            }
            if (n.Children.Count > 0) { n.Expanded = !n.Expanded; Rebuild(); }
        }

        void PickChoice(CheckNode n, Rectangle at)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Body, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
            for (int k = 0; k < n.Choices.Count; k++)
            {
                int kk = k;
                var item = new ToolStripMenuItem(n.Choices[k]) { Checked = k == n.Choice, ForeColor = Theme.Text };
                item.Click += (s, e) => { n.Choice = kk; canvas.Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); };
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(canvas, new Point(at.Left, at.Bottom));
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.FieldHover; } }
            public override Color MenuItemBorder { get { return Theme.Track; } }
            public override Color MenuBorder { get { return Theme.Track; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Field; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Field; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Field; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Field; } }
            public override Color CheckBackground { get { return Theme.AccentDim; } }
            public override Color CheckSelectedBackground { get { return Theme.AccentDim; } }
        }
    }
}
