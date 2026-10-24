using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// Frequency-response graph. Graphic mode: drag the 10 band dots up/down. Parametric mode: drag nodes
    /// (left/right = frequency, up/down = gain), mouse wheel = width (Q), double-click = add band, right-click = type/delete.
    /// </summary>
    internal class EqGraph : Control
    {
        public EqProfile Profile;
        public float[] RawSpectrum, OutSpectrum;
        public bool Interactive = true;
        public int Selected = -1;
        public event EventHandler Edited, SelectionChanged;
        const double MinF = 20, MaxF = 20000, RangeDb = 15;
        int dragBand = -1;
        bool dragging;
        static readonly Color[] NodeColors =
        {
            Color.FromArgb(91, 210, 122), Color.FromArgb(96, 165, 250), Color.FromArgb(244, 114, 182), Color.FromArgb(250, 204, 21),
            Color.FromArgb(167, 139, 250), Color.FromArgb(45, 212, 191), Color.FromArgb(251, 146, 60), Color.FromArgb(226, 232, 240)
        };

        public EqGraph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, true);
            BackColor = GraphStyle.Plot;
        }

        bool Parametric { get { return Profile != null && Profile.IsParametric; } }
        RectangleF Plot { get { return new RectangleF(Theme.Px(34), Theme.Px(22), Width - Theme.Px(42), Height - Theme.Px(40)); } }
        float X(double f) { var p = Plot; return p.X + (float)(Math.Log(f / MinF) / Math.Log(MaxF / MinF)) * p.Width; }
        double F(float x) { var p = Plot; return MinF * Math.Pow(MaxF / MinF, Math.Max(0, Math.Min(1, (x - p.X) / p.Width))); }
        float Y(double db) { var p = Plot; return p.Y + (float)((RangeDb - db) / (2 * RangeDb)) * p.Height; }
        double Db(float y) { var p = Plot; return RangeDb - (y - p.Y) / p.Height * 2 * RangeDb; }
        float SpecY(float db) { var p = Plot; float f = Math.Max(0, Math.Min(1, (db + 100f) / 90f)); return p.Bottom - f * p.Height; }

        static bool HasGain(EqFilter f) { return f.Type == "PK" || f.Type == "LSC" || f.Type == "HSC"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var grid = new Pen(GraphStyle.Grid))
            {
                foreach (double f in new double[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    float x = X(f);
                    g.DrawLine(grid, x, p.Y, x, p.Bottom);
                    string t = f >= 1000 ? (f / 1000) + "k" : f.ToString();
                    int tw = TextRenderer.MeasureText(t, Theme.Small).Width;
                    int labelX = Math.Max(0, Math.Min(Width - tw, (int)x - tw / 2));   // centered, but never cut off at the edges
                    TextRenderer.DrawText(g, t, Theme.Small, new Point(labelX, (int)p.Bottom + Theme.Px(3)), Theme.Muted);
                }
                for (int db = -12; db <= 12; db += 6)
                {
                    float y = Y(db);
                    g.DrawLine(grid, p.X, y, p.Right, y);
                    TextRenderer.DrawText(g, (db > 0 ? "+" : "") + db, Theme.Small, new Point(Theme.Px(2), (int)y - Theme.Px(8)), Theme.Muted);
                }
            }
            DrawSpectrum(g, RawSpectrum, Color.FromArgb(70, 120, 140, 160));
            DrawSpectrum(g, OutSpectrum, Color.FromArgb(60, Theme.Accent));
            using (var flat = new Pen(Color.FromArgb(130, 139, 144, 150), Theme.Px(1)) { DashStyle = DashStyle.Dash })
                g.DrawLine(flat, p.X, Y(0), p.Right, Y(0));

            if (Profile != null)
            {
                var filters = Profile.AllFilters();
                double pre = Profile.AutoPreamp ? 0 : Profile.Preamp;
                if (Parametric && Selected >= 0 && Selected < Profile.Parametric.Count)
                    DrawCurve(g, new List<EqFilter> { Profile.Parametric[Selected] }, 0, Color.FromArgb(110, NodeColors[Selected % NodeColors.Length]), 1.5f);
                DrawCurve(g, filters, pre, Profile.Enabled ? Theme.Accent : Theme.Muted, 2.5f);
                if (Interactive) DrawNodes(g);
            }
            int lx = (int)p.Right - Theme.Px(250);
            GraphStyle.Legend(g, lx, Theme.Px(2), Color.FromArgb(139, 144, 150), "Original (flat)", true);
            GraphStyle.Legend(g, lx + Theme.Px(110), Theme.Px(2), Theme.Accent, "Your EQ", true);

        }

        void DrawCurve(Graphics g, List<EqFilter> filters, double pre, Color c, float width)
        {
            var pts = new PointF[201];
            for (int i = 0; i <= 200; i++)
            {
                double f = MinF * Math.Pow(MaxF / MinF, i / 200.0);
                double db = Biquad.ResponseDb(filters, f, 48000) + pre;
                pts[i] = new PointF(X(f), Y(Math.Max(-RangeDb - 3, Math.Min(RangeDb + 3, db))));
            }
            using (var pen = new Pen(c, Theme.Px(1) * width)) g.DrawLines(pen, pts);
        }

        void DrawNodes(Graphics g)
        {
            if (Parametric)
            {
                for (int i = 0; i < Profile.Parametric.Count; i++)
                {
                    var f = Profile.Parametric[i];
                    var pt = NodePoint(f);
                    float r = Theme.Px(i == Selected ? 9 : 7);
                    Color c = NodeColors[i % NodeColors.Length];
                    using (var b = new SolidBrush(f.On ? c : Theme.Muted)) g.FillEllipse(b, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    if (i == Selected) using (var pen = new Pen(Theme.Text, Theme.Px(2))) g.DrawEllipse(pen, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                    TextRenderer.DrawText(g, (i + 1).ToString(), Theme.Small, new Rectangle((int)(pt.X - r), (int)(pt.Y - r), (int)(2 * r), (int)(2 * r)),
                        Color.FromArgb(14, 16, 18), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            else if (Profile.IsFine)
            {
                for (int i = 0; i < EqProfile.FineFreqs.Length; i++)
                {
                    float x = X(EqProfile.FineFreqs[i]), y = Y(Profile.Fine[i]);
                    bool sel = i == FineSelected;
                    float r = Theme.Scale * (sel ? 6f : 3.5f);
                    using (var b = new SolidBrush(sel ? Theme.Accent : (i == hoverFine ? Theme.Text : Color.FromArgb(200, 236, 237, 238)))) g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r);
                    if (sel) using (var pen = new Pen(Theme.Text, Theme.Scale * 1.5f)) g.DrawEllipse(pen, x - r, y - r, 2 * r, 2 * r);
                }
                int show = hoverFine >= 0 ? hoverFine : FineSelected;
                if (show >= 0)
                {
                    string txt = FormatFreq(EqProfile.FineFreqs[show]) + "   " + (Profile.Fine[show] > 0 ? "+" : "") + Profile.Fine[show].ToString("0.0") + " dB";
                    var p = Plot;
                    float x = X(EqProfile.FineFreqs[show]);
                    var size = TextRenderer.MeasureText(txt, Theme.Bold);
                    int tx = (int)Math.Max(p.X, Math.Min(p.Right - size.Width, x - size.Width / 2));
                    using (var b = new SolidBrush(Color.FromArgb(220, 28, 31, 35))) g.FillRectangle(b, tx - 4, p.Bottom - size.Height - Theme.Px(6), size.Width + 8, size.Height + 2);
                    TextRenderer.DrawText(g, txt, Theme.Bold, new Point(tx, (int)p.Bottom - size.Height - Theme.Px(5)), Theme.Text);
                }
            }
            else if (Profile.Bands != null)
                for (int i = 0; i < EqProfile.BandFreqs.Length; i++)
                {
                    float x = X(EqProfile.BandFreqs[i]), y = Y(Profile.Bands[i]);
                    float r = Theme.Px(i == dragBand ? 6 : 4);
                    using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r);
                }
        }

        PointF NodePoint(EqFilter f)
        {
            double db = HasGain(f) ? f.Gain : Biquad.ResponseDb(new List<EqFilter> { f }, f.Freq, 48000);
            return new PointF(X(f.Freq), Y(Math.Max(-RangeDb, Math.Min(RangeDb, db))));
        }

        void DrawSpectrum(Graphics g, float[] spec, Color c)
        {
            if (spec == null) return;
            var p = Plot;
            var pts = new List<PointF> { new PointF(X(20), p.Bottom) };
            for (int i = 0; i < spec.Length; i++) pts.Add(new PointF(X(SpectrumAnalyzer.PointFreq(i)), SpecY(spec[i])));
            pts.Add(new PointF(X(20000), p.Bottom));
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts.ToArray());
        }

        int HitNode(Point pt)
        {
            if (!Parametric) return -1;
            for (int i = Profile.Parametric.Count - 1; i >= 0; i--)
            {
                var np = NodePoint(Profile.Parametric[i]);
                if (Math.Abs(np.X - pt.X) <= Theme.Px(11) && Math.Abs(np.Y - pt.Y) <= Theme.Px(11)) return i;
            }
            return -1;
        }

        void SelectNode(int i) { Selected = i; Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
        void Fire() { Invalidate(); if (Edited != null) Edited(this, EventArgs.Empty); }

        // ---- Fine (31-point) mode
        public int FineSelected = 17;   // 1 kHz
        int hoverFine = -1;
        bool fineDrag;

        public static string FormatFreq(double f) { return f >= 1000 ? (f / 1000).ToString("0.##") + " kHz" : f.ToString("0.#") + " Hz"; }

        int NearestFine(int x)
        {
            double f = F(x), best = double.MaxValue; int idx = 0;
            for (int i = 0; i < EqProfile.FineFreqs.Length; i++)
            {
                double d = Math.Abs(Math.Log(EqProfile.FineFreqs[i] / f));
                if (d < best) { best = d; idx = i; }
            }
            return idx;
        }

        public void SelectFine(int i)
        {
            FineSelected = Math.Max(0, Math.Min(EqProfile.FineFreqs.Length - 1, i));
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void NudgeFine(double db)
        {
            if (Profile == null || !Profile.IsFine) return;
            Profile.Fine[FineSelected] = Math.Max(-15, Math.Min(15, Math.Round((Profile.Fine[FineSelected] + db) * 10) / 10));
            Fire();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Profile != null && Profile.IsFine && Interactive)
            {
                double step = e.Shift ? 1.0 : 0.1;
                if (e.KeyCode == Keys.Left) SelectFine(FineSelected - 1);
                else if (e.KeyCode == Keys.Right) SelectFine(FineSelected + 1);
                else if (e.KeyCode == Keys.Up) NudgeFine(step);
                else if (e.KeyCode == Keys.Down) NudgeFine(-step);
                else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) NudgeFine(-Profile.Fine[FineSelected]);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverFine = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (!Interactive || Profile == null) return;
            if (Profile.IsFine)
            {
                if (e.Button != MouseButtons.Left) return;
                SelectFine(NearestFine(e.X));
                fineDrag = true;
                Capture = true;
                return;   // a click only selects; dragging changes the value
            }
            if (Parametric)
            {
                int hit = HitNode(e.Location);
                if (e.Button == MouseButtons.Right) { if (hit >= 0) { SelectNode(hit); ShowNodeMenu(hit, e.Location); } return; }
                if (hit >= 0) { SelectNode(hit); dragging = true; Capture = true; }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            double f = F(e.X), best = double.MaxValue;
            for (int i = 0; i < EqProfile.BandFreqs.Length; i++)
            {
                double d = Math.Abs(Math.Log(EqProfile.BandFreqs[i] / f));
                if (d < best) { best = d; dragBand = i; }
            }
            Capture = true;
            OnMouseMove(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Profile != null && Profile.IsFine)
            {
                if (fineDrag)
                {
                    // 0.1 dB precision; hold Shift to snap to 0.5 dB.
                    double fineDb = Math.Max(-15, Math.Min(15, Db(e.Y)));
                    fineDb = (ModifierKeys & Keys.Shift) != 0 ? Math.Round(fineDb * 2) / 2 : Math.Round(fineDb * 10) / 10;
                    Profile.Fine[FineSelected] = fineDb;
                    Fire();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                }
                else
                {
                    int h = Interactive ? NearestFine(e.X) : -1;
                    if (h != hoverFine) { hoverFine = h; Invalidate(); }
                    Cursor = Interactive ? Cursors.SizeNS : Cursors.Default;
                }
                return;
            }
            if (Parametric)
            {
                if (!dragging || Selected < 0) { Cursor = HitNode(e.Location) >= 0 ? Cursors.SizeAll : (Interactive ? Cursors.Cross : Cursors.Default); return; }
                var n = Profile.Parametric[Selected];
                n.Freq = RoundFreq(F(e.X));
                if (HasGain(n)) n.Gain = Math.Round(Math.Max(-RangeDb, Math.Min(RangeDb, Db(e.Y))) * 2) / 2;
                Fire();
                return;
            }
            if (dragBand < 0) { Cursor = Interactive ? Cursors.Hand : Cursors.Default; return; }
            double db = Math.Round(Math.Max(-12, Math.Min(12, Db(e.Y))) * 2) / 2;
            Profile.Bands[dragBand] = db;
            Fire();
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragBand = -1; dragging = false; fineDrag = false; Capture = false; Invalidate(); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (!Interactive || !Parametric || e.Button != MouseButtons.Left || HitNode(e.Location) >= 0) return;
            if (Profile.Parametric.Count >= EqEditor.MaxBands) return;
            Profile.Parametric.Add(new EqFilter { Type = "PK", Freq = RoundFreq(F(e.X)), Gain = Math.Round(Math.Max(-RangeDb, Math.Min(RangeDb, Db(e.Y))) * 2) / 2, Q = 1.0 });
            SelectNode(Profile.Parametric.Count - 1);
            Fire();
        }

        /// <summary>True when the wheel edits something here (31-point value, or a selected band's width); otherwise it scrolls the page.</summary>
        public bool UsesWheel { get { return Interactive && Profile != null && (Profile.IsFine || (Parametric && Selected >= 0)); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Profile != null && Profile.IsFine && Interactive)
            {
                NudgeFine((e.Delta > 0 ? 1 : -1) * ((ModifierKeys & Keys.Shift) != 0 ? 1.0 : 0.1));
                var hw = e as HandledMouseEventArgs;
                if (hw != null) hw.Handled = true;
                return;
            }
            if (!Parametric || Selected < 0) return;
            var n = Profile.Parametric[Selected];
            n.Q = Math.Round(Math.Max(0.3, Math.Min(10, n.Q * (e.Delta > 0 ? 1.15 : 1 / 1.15))) * 100) / 100;
            Fire();
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        void ShowNodeMenu(int i, Point at)
        {
            var menu = new ContextMenuStrip();
            foreach (var t in EqEditor.FilterTypes)
            {
                var type = t;
                var item = new ToolStripMenuItem(type.Value) { Checked = Profile.Parametric[i].Type == type.Key };
                item.Click += (s, e) => { Profile.Parametric[i].Type = type.Key; Fire(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Delete band", null, (s, e) => { Profile.Parametric.RemoveAt(i); SelectNode(-1); Fire(); });
            menu.Show(this, at);
        }

        static double RoundFreq(double f)
        {
            f = Math.Max(20, Math.Min(20000, f));
            double mag = Math.Pow(10, Math.Floor(Math.Log10(f)) - 1);
            return Math.Round(f / mag) * mag;
        }
    }

    /// <summary>
    /// Full EQ editor: graphic (10 sliders) or parametric (up to 8 movable bands), presets, normalize,
    /// optional import / auto-EQ / A-B compare. Changed fires live; Committed fires once you let go.
    /// </summary>
    internal class EqEditor : Panel
    {
        public const int MaxBands = 10;
        public static readonly KeyValuePair<string, string>[] FilterTypes =
        {
            new KeyValuePair<string, string>("PK", "Bell"), new KeyValuePair<string, string>("LSC", "Low shelf"),
            new KeyValuePair<string, string>("HSC", "High shelf"), new KeyValuePair<string, string>("HP", "Low cut"),
            new KeyValuePair<string, string>("LP", "High cut")
        };

        public readonly EqGraph Graph = new EqGraph();
        readonly FlatSlider[] sliders = new FlatSlider[10];
        readonly Label[] values = new Label[10];
        readonly Panel graphicArea = new Panel(), paramArea = new Panel(), fineArea = new Panel();
        readonly FlatSlider fineSl = new FlatSlider();
        Label fineTitle, fineVal;
        FlatButton fineBtn;
        readonly ToggleSwitch enabled = new ToggleSwitch(), autoPre = new ToggleSwitch();
        readonly FlatButton graphicBtn, paramBtn, removeExtra;
        readonly DarkCombo presets = new DarkCombo(), typeCombo = new DarkCombo();
        readonly FlatSlider freqSl = new FlatSlider(), gainSl = new FlatSlider(), qSl = new FlatSlider();
        readonly Label freqVal, gainVal, qVal, bandTitle, extraLabel, preLabel, paramHint, preTitle;
        string fixedPreNote;

        /// <summary>For EQs whose output has its own limiter: hide the distortion trim and show a note instead.</summary>
        public void UseFixedPreampNote(string note)
        {
            fixedPreNote = note;
            autoPre.Visible = false; preTitle.Visible = false;
            preLabel.Left = 0; preLabel.Width = Theme.Px(680);
            UpdatePreLabel();
        }
        readonly FlatButton addBand, delBand;
        public readonly FlatButton AutoEqButton, CompareButton;
        readonly Dictionary<string, double[]> presetMap;
        readonly Timer commitTimer = new Timer { Interval = 450 };
        EqProfile profile;
        bool loading;
        public event EventHandler Changed, Committed;

        public static Dictionary<string, double[]> MicPresets()
        {
            return new Dictionary<string, double[]>
            {
                { "Flat (original)", new double[10] },
                { "Voice clarity",   new double[] { -6, -3, -1, -1, 0, 0, 2, 3, 2, 1 } },
                { "Broadcast warm",  new double[] { -2, 2, 3, 1, -1, 0, 1, 2, 1, 0 } },
                { "Podcast",         new double[] { -8, -4, 1, -2, -1, 0, 2, 3, 1, 0 } },
                { "Reduce boominess",new double[] { -2, -2, -3, -4, -2, 0, 0, 0, 0, 0 } },
                { "Reduce harshness",new double[] { 0, 0, 0, 0, 0, 0, -1, -3, -4, -2 } },
                { "Bright / airy",   new double[] { 0, 0, 0, 0, 0, 0, 1, 3, 4, 3 } },
            };
        }

        public static Dictionary<string, double[]> HeadphonePresets()
        {
            return new Dictionary<string, double[]>
            {
                { "Flat (original)", new double[10] },
                { "Bass boost",      new double[] { 6, 5, 3, 1, 0, 0, 0, 0, 0, 0 } },
                { "Bass reduce",     new double[] { -5, -4, -2, -1, 0, 0, 0, 0, 0, 0 } },
                { "V-shape",         new double[] { 5, 4, 2, 0, -1, -2, 0, 2, 4, 3 } },
                { "Vocal / podcast", new double[] { -2, -1, 0, -1, 1, 2, 3, 2, 0, 0 } },
                { "Treble soften",   new double[] { 0, 0, 0, 0, 0, 0, 0, -1, -3, -4 } },
                { "Gaming footsteps",new double[] { -3, -3, -2, 0, 0, 1, 3, 4, 2, 0 } },
                { "Movies",          new double[] { 4, 3, 1, 0, 0, 1, 1, 0, 1, 1 } },
                // Loudness contour: at low volume ears lose bass and some treble first (ISO 226), so lift them a little.
                { "Quiet listening", new double[] { 5, 4, 2, 0.5, 0, 0, 0, 0.5, 1.5, 2 } },
            };
        }

        public EqEditor(Dictionary<string, double[]> presetList, bool allowImport, bool allowAutoEq, bool allowCompare)
        {
            presetMap = presetList;
            BackColor = Theme.Card;
            Size = new Size(Theme.Px(680), Theme.Px(520));

            // Row 1: on/off, mode, presets, reset, normalize
            enabled.Location = new Point(0, Theme.Px(4));
            enabled.CheckedChanged += (s, e) => { if (profile != null && !loading) { profile.Enabled = enabled.Checked; Fire(true); } };
            Controls.Add(enabled);
            Theme.Label(this, "On", 48, 5, Theme.Bold);
            graphicBtn = AddButton("10-band", 84, 0, 78);
            graphicBtn.Click += (s, e) => SetMode("graphic");
            Theme.Help(graphicBtn, "Classic 10-band EQ: one slider per octave. Quick and easy.");
            fineBtn = AddButton("31-point", 166, 0, 80);
            fineBtn.Click += (s, e) => SetMode("fine");
            Theme.Help(fineBtn, "Precision EQ: 31 points, one every 1/3 octave, adjustable in 0.1 dB steps.\nDrag a point, use the mouse wheel, or the arrow keys (Shift = 1 dB steps).");
            paramBtn = AddButton("Parametric", 250, 0, 96);
            paramBtn.Click += (s, e) => SetMode("parametric");
            Theme.Help(paramBtn, "Pro EQ: up to 10 bands you place anywhere, with adjustable width (Q) and type (bell, shelf, cut).");
            presets.Location = new Point(Theme.Px(352), Theme.Px(1));
            presets.Width = Theme.Px(126);
            presets.Items.Add("Presets...");
            foreach (var k in presetMap.Keys) presets.Items.Add(k);
            presets.SelectedIndex = 0;
            presets.SelectionChangeCommitted += (s, e) =>
            {
                if (presets.SelectedIndex <= 0 || profile == null) return;
                profile.Bands = (double[])presetMap[(string)presets.SelectedItem].Clone();
                profile.Mode = "graphic";
                LoadValues();
                Fire(true);
                presets.SelectedIndex = 0;
            };
            Controls.Add(presets);
            var reset = AddButton("Reset", 484, 0, 84);
            reset.Click += (s, e) =>
            {
                if (profile == null) return;
                if (profile.IsParametric) profile.Parametric.Clear();
                else if (profile.IsFine) profile.Fine = new double[31];
                else profile.Bands = new double[10];
                Graph.Selected = -1;
                LoadValues();
                Fire(true);
            };
            Theme.Help(reset, "Sets every band in the current EQ mode back to 0 dB (flat).");
            var normalize = AddButton("Normalize", 574, 0, 106);
            normalize.Click += (s, e) => { if (profile == null) return; profile.Normalize(48000); LoadValues(); Fire(true); };
            Theme.Help(normalize, "Keeps the same EQ shape but turns boosts into cuts, so the loudest point sits at 0 dB.\nNo volume trim is needed afterwards, which avoids distortion and the 'everything got quieter' effect.");
            Theme.Help(presets, "Ready-made starting curves. They load into the 10-band EQ.");

            // Row 2: optional tools
            int x = 0;
            if (allowImport)
            {
                var import = AddButton("Import correction...", x, 36, 0);
                import.Click += (s, e) => ImportFile();
                x += (int)(import.Width / Theme.Scale) + 8;
            }
            if (allowAutoEq)
            {
                AutoEqButton = AddButton("Auto-EQ my voice", x, 36, 0); x += (int)(AutoEqButton.Width / Theme.Scale) + 8;
                Theme.Help(AutoEqButton, "Listens while you talk normally for 8 seconds, compares your voice to a clear broadcast voice, and suggests a starting EQ.");
            }
            if (allowCompare)
            {
                CompareButton = AddButton("Hold: hear original", x, 36, 0);
                Theme.Help(CompareButton, "While you hold this, apps get your untouched microphone (no noise removal, gate, EQ or voice tools). Let go to hear the difference.\nTurn on \"Hear me\" to listen yourself.");
            }

            Graph.Location = new Point(0, Theme.Px(74));
            Graph.Size = new Size(Width, Theme.Px(220));
            Graph.Edited += (s, e) => { LoadValues(); Fire(false); };
            Graph.SelectionChanged += (s, e) => { LoadParamDetails(); LoadFineDetails(); };
            Controls.Add(Graph);

            // Graphic sliders
            int top = Theme.Px(300);
            graphicArea.SetBounds(0, top, Width, Theme.Px(160));
            graphicArea.BackColor = Theme.Card;
            Controls.Add(graphicArea);
            int colW = Width / 10;
            for (int i = 0; i < 10; i++)
            {
                int idx = i;
                values[i] = new Label { ForeColor = Theme.Text, Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(i * colW, 0), Size = new Size(colW, Theme.Px(18)), BackColor = Color.Transparent };
                graphicArea.Controls.Add(values[i]);
                var sl = new FlatSlider { Vertical = true, Minimum = -120, Maximum = 120, Step = 5, DefaultValue = 0 };
                sl.Location = new Point(i * colW + (colW - Theme.Px(30)) / 2, Theme.Px(18));
                sl.Size = new Size(Theme.Px(30), Theme.Px(118));
                sl.ValueChanged += (s, e) =>
                {
                    if (loading || profile == null) return;
                    profile.Bands[idx] = sliders[idx].Value / 10.0;
                    values[idx].Text = FormatDb(profile.Bands[idx]);
                    Fire(false);
                };
                sliders[i] = sl;
                graphicArea.Controls.Add(sl);
                double f = EqProfile.BandFreqs[i];
                graphicArea.Controls.Add(new Label { Text = f >= 1000 ? (f / 1000) + "k" : f.ToString(), ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(i * colW, Theme.Px(138)), Size = new Size(colW, Theme.Px(18)), BackColor = Color.Transparent });
            }
            Responsive.Proportional(graphicArea);   // slider columns spread out when the window is wider

            // Parametric band details
            paramArea.SetBounds(0, top, Width, Theme.Px(160));
            paramArea.BackColor = Theme.Card;
            paramArea.Visible = false;
            Controls.Add(paramArea);
            bandTitle = Theme.Label(paramArea, "", 0, 4, Theme.Bold, Theme.Text, 200);
            typeCombo.Location = new Point(Theme.Px(210), 0);
            typeCombo.Width = Theme.Px(140);
            foreach (var t in FilterTypes) typeCombo.Items.Add(t.Value);
            typeCombo.SelectionChangeCommitted += (s, e) =>
            {
                var n = SelectedNode(); if (n == null) return;
                n.Type = FilterTypes[typeCombo.SelectedIndex].Key;
                LoadParamDetails();
                Fire(true);
            };
            paramArea.Controls.Add(typeCombo);
            addBand = new FlatButton("+ Add band") { Location = new Point(Theme.Px(436), 0), Width = Theme.Px(120) };
            addBand.Click += (s, e) => AddBand();
            paramArea.Controls.Add(addBand);
            delBand = new FlatButton("Delete") { Location = new Point(Theme.Px(562), 0), Width = Theme.Px(118) };
            delBand.Click += (s, e) => { if (profile == null || Graph.Selected < 0) return; profile.Parametric.RemoveAt(Graph.Selected); Graph.Selected = -1; LoadValues(); Fire(true); };
            paramArea.Controls.Add(delBand);
            freqVal = ParamRow("Frequency", 44, freqSl, 0, 1000, v => { var n = SelectedNode(); if (n != null) n.Freq = Math.Round(20 * Math.Pow(1000, v / 1000.0)); });
            gainVal = ParamRow("Gain", 82, gainSl, -150, 150, v => { var n = SelectedNode(); if (n != null) n.Gain = v / 10.0; });
            qVal = ParamRow("Width (Q)", 120, qSl, 0, 1000, v => { var n = SelectedNode(); if (n != null) n.Q = Math.Round(0.3 * Math.Pow(10 / 0.3, v / 1000.0) * 100) / 100; });
            paramHint = Theme.Label(paramArea, "Click a band dot to edit it. Drag = move, mouse wheel = width, right-click = type/delete, double-click empty graph = new band (up to 10).", 0, 60, Theme.Body, Theme.Muted, 600);
            paramHint.AutoEllipsis = false;
            paramHint.Height = Theme.Px(44);

            // 31-point fine EQ details
            fineArea.SetBounds(0, top, Width, Theme.Px(160));
            fineArea.BackColor = Theme.Card;
            fineArea.Visible = false;
            Controls.Add(fineArea);
            fineTitle = Theme.Label(fineArea, "", 0, 4, Theme.Bold, Theme.Text, 300);
            var prev = new FlatButton("\u25C0") { Location = new Point(Theme.Px(330), 0), Width = Theme.Px(40) };
            prev.Click += (s, e) => { Graph.SelectFine(Graph.FineSelected - 1); Graph.Focus(); };
            fineArea.Controls.Add(prev);
            var next = new FlatButton("\u25B6") { Location = new Point(Theme.Px(374), 0), Width = Theme.Px(40) };
            next.Click += (s, e) => { Graph.SelectFine(Graph.FineSelected + 1); Graph.Focus(); };
            fineArea.Controls.Add(next);
            var zero = new FlatButton("Zero point") { Location = new Point(Theme.Px(420), 0), Width = Theme.Px(110) };
            zero.Click += (s, e) => { if (profile == null) return; profile.Fine[Graph.FineSelected] = 0; LoadValues(); Fire(true); };
            fineArea.Controls.Add(zero);
            var smooth = new FlatButton("Smooth curve") { Location = new Point(Theme.Px(536), 0), Width = Theme.Px(144) };
            smooth.Click += (s, e) => { SmoothFine(); LoadValues(); Fire(true); };
            Theme.Help(smooth, "Averages each point with its neighbours, removing sharp spikes and dips for a more natural sound.");
            fineArea.Controls.Add(smooth);
            Theme.Label(fineArea, "Gain", 0, 46, Theme.Body, Theme.Muted);
            fineSl.Minimum = -150; fineSl.Maximum = 150; fineSl.DefaultValue = 0;
            fineSl.Location = new Point(Theme.Px(100), Theme.Px(44));
            fineSl.Size = new Size(Theme.Px(480), Theme.Px(24));
            fineSl.ValueChanged += (s, e) =>
            {
                if (loading || profile == null) return;
                profile.Fine[Graph.FineSelected] = fineSl.Value / 10.0;
                LoadFineDetails();
                Graph.Invalidate();
                Fire(false);
            };
            fineArea.Controls.Add(fineSl);
            fineVal = Theme.Label(fineArea, "", 590, 46, Theme.Body, Theme.Text, 90);
            var fineHint = Theme.Label(fineArea, "Click a point on the graph, then drag it, scroll the mouse wheel, or use the arrow keys: \u2190/\u2192 picks a point, \u2191/\u2193 = \u00B10.1 dB (hold Shift for 1 dB). Delete = back to 0.", 0, 84, Theme.Small, Theme.Muted, 680);
            fineHint.AutoEllipsis = false;
            fineHint.Height = Theme.Px(40);

            // Bottom: trim, correction
            int by = Theme.Px(468);
            autoPre.Location = new Point(0, by);
            autoPre.CheckedChanged += (s, e) => { if (profile != null && !loading) { profile.AutoPreamp = autoPre.Checked; Fire(true); } };
            Controls.Add(autoPre);
            preTitle = Theme.Help(Theme.Label(this, "Prevent distortion", 48, (int)(by / Theme.Scale) + 1, Theme.Bold),
                "When an EQ band boosts, loud sounds can go past the maximum and crackle.\nThis turns the overall volume down by the biggest boost so that can't happen.\nTip: use Normalize (cuts instead of boosts) to avoid the volume drop.");
            preLabel = Theme.Label(this, "", 190, (int)(by / Theme.Scale) + 2, Theme.Small, Theme.Muted, 490);
            extraLabel = Theme.Label(this, "", 0, (int)(by / Theme.Scale) + 30, Theme.Small, Theme.Muted, 500);
            removeExtra = new FlatButton("Remove correction");
            removeExtra.Location = new Point(Width - removeExtra.Width, by + Theme.Px(24));
            removeExtra.Click += (s, e) => { if (profile == null) return; profile.Extra.Clear(); profile.ExtraName = ""; LoadValues(); Fire(true); };
            Controls.Add(removeExtra);

            commitTimer.Tick += (s, e) =>
            {
                if (Control.MouseButtons != MouseButtons.None) return; // still dragging
                commitTimer.Stop();
                if (Committed != null) Committed(this, EventArgs.Empty);
            };
        }

        FlatButton AddButton(string text, int x, int y, int w)
        {
            var b = new FlatButton(text) { Location = new Point(Theme.Px(x), Theme.Px(y)) };
            if (w > 0) b.Width = Theme.Px(w);
            Controls.Add(b);
            return b;
        }

        Label ParamRow(string label, int y, FlatSlider sl, int min, int max, Action<int> set)
        {
            Theme.Label(paramArea, label, 0, y + 2, Theme.Body, Theme.Muted);
            sl.Minimum = min; sl.Maximum = max;
            sl.Location = new Point(Theme.Px(100), Theme.Px(y));
            sl.Size = new Size(Theme.Px(480), Theme.Px(24));
            paramArea.Controls.Add(sl);
            var val = Theme.Label(paramArea, "", 590, y + 2, Theme.Body, Theme.Text, 90);
            sl.ValueChanged += (s, e) => { if (loading) return; set(sl.Value); LoadParamDetails(); Graph.Invalidate(); Fire(false); };
            return val;
        }

        EqFilter SelectedNode()
        {
            if (profile == null || !profile.IsParametric || Graph.Selected < 0 || Graph.Selected >= profile.Parametric.Count) return null;
            return profile.Parametric[Graph.Selected];
        }

        void AddBand()
        {
            if (profile == null || profile.Parametric.Count >= MaxBands) return;
            // Spread new bands across the range so they don't stack on each other.
            double[] spots = { 120, 400, 1000, 2500, 5000, 8000, 250, 12000, 60, 700 };
            double f = spots.FirstOrDefault(s => !profile.Parametric.Any(p => Math.Abs(Math.Log(p.Freq / s)) < 0.3));
            if (f == 0) f = 1000;
            profile.Parametric.Add(new EqFilter { Type = "PK", Freq = f, Gain = 0, Q = 1.0 });
            Graph.Selected = profile.Parametric.Count - 1;
            LoadValues();
            Fire(true);
        }

        void SetMode(string mode)
        {
            if (profile == null || profile.Mode == mode) return;
            // Start the new mode from the current curve so switching doesn't change the sound.
            if (mode == "parametric" && profile.Parametric.Count == 0 && profile.Mode == "graphic")
                for (int i = 0; i < 10; i++)
                    if (Math.Abs(profile.Bands[i]) > 0.01) profile.Parametric.Add(new EqFilter { Type = "PK", Freq = EqProfile.BandFreqs[i], Gain = profile.Bands[i], Q = 1.41 });
            if (mode == "fine" && profile.Fine.All(v => Math.Abs(v) < 0.01)) profile.SeedFineFromCurve(48000);
            profile.Mode = mode;
            Graph.Selected = -1;
            LoadValues();
            Fire(true);
        }

        void SmoothFine()
        {
            if (profile == null) return;
            var f = profile.Fine;
            var s = new double[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                double a = i > 0 ? f[i - 1] : f[i], b = i < f.Length - 1 ? f[i + 1] : f[i];
                s[i] = Math.Round((0.25 * a + 0.5 * f[i] + 0.25 * b) * 10) / 10;
            }
            profile.Fine = s;
        }

        void LoadFineDetails()
        {
            if (profile == null || !profile.IsFine) return;
            bool was = loading;
            loading = true;
            int i = Graph.FineSelected;
            fineTitle.Text = "Point " + (i + 1) + " of 31  \u00B7  " + EqGraph.FormatFreq(EqProfile.FineFreqs[i]);
            fineSl.SetSilently((int)Math.Round(profile.Fine[i] * 10));
            fineVal.Text = (profile.Fine[i] > 0 ? "+" : "") + profile.Fine[i].ToString("0.0") + " dB";
            loading = was;
        }

        static string FormatDb(double v) { return (v > 0 ? "+" : "") + v.ToString("0.#"); }

        public void SetProfile(EqProfile p)
        {
            profile = p;
            if (p != null) p.Repair();
            Graph.Profile = p;
            Graph.Selected = -1;
            LoadValues();
        }

        public EqProfile Profile { get { return profile; } }

        void LoadValues()
        {
            loading = true;
            if (profile != null)
            {
                enabled.SetSilently(profile.Enabled);
                autoPre.SetSilently(profile.AutoPreamp);
                bool par = profile.IsParametric, fine = profile.IsFine;
                graphicArea.Visible = !par && !fine;
                paramArea.Visible = par;
                fineArea.Visible = fine;
                graphicBtn.BackColor = !par && !fine ? Theme.AccentDim : Theme.Field;
                fineBtn.BackColor = fine ? Theme.AccentDim : Theme.Field;
                paramBtn.BackColor = par ? Theme.AccentDim : Theme.Field;
                LoadFineDetails();
                for (int i = 0; i < 10; i++)
                {
                    sliders[i].SetSilently((int)Math.Round(profile.Bands[i] * 10));
                    values[i].Text = FormatDb(profile.Bands[i]);
                }
                bool hasExtra = profile.Extra.Count > 0;
                extraLabel.Text = hasExtra ? "Under your EQ: " + profile.ExtraName + " (" + profile.Extra.Count + " filters)" : "";
                removeExtra.Visible = hasExtra;
                LoadParamDetails();
                UpdatePreLabel();
            }
            loading = false;
            Graph.Invalidate();
        }

        void LoadParamDetails()
        {
            bool was = loading;
            loading = true;
            var n = SelectedNode();
            bool has = n != null;
            foreach (Control c in paramArea.Controls) if (c != addBand && c != paramHint) c.Visible = has;
            paramHint.Visible = !has;
            addBand.Visible = true;
            addBand.Enabled = profile != null && profile.Parametric.Count < MaxBands;
            if (has)
            {
                bandTitle.Text = "Band " + (Graph.Selected + 1);
                typeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(FilterTypes, t => t.Key == n.Type));
                freqSl.SetSilently((int)Math.Round(1000 * Math.Log(n.Freq / 20) / Math.Log(1000)));
                freqVal.Text = n.Freq >= 1000 ? (n.Freq / 1000).ToString("0.##") + " kHz" : n.Freq.ToString("0") + " Hz";
                bool gain = n.Type == "PK" || n.Type == "LSC" || n.Type == "HSC";
                gainSl.Enabled = gain;
                gainSl.SetSilently((int)Math.Round(n.Gain * 10));
                gainVal.Text = gain ? FormatDb(n.Gain) + " dB" : "-";
                qSl.SetSilently((int)Math.Round(1000 * Math.Log(n.Q / 0.3) / Math.Log(10 / 0.3)));
                qVal.Text = n.Q.ToString("0.00");
            }
            loading = was;
        }

        void UpdatePreLabel()
        {
            if (profile == null) return;
            if (fixedPreNote != null) { preLabel.ForeColor = Theme.Muted; preLabel.Text = fixedPreNote; return; }
            double trim = profile.EffectivePreamp(48000);
            preLabel.ForeColor = trim < -3 ? Theme.Warn : Theme.Muted;
            preLabel.Text = !profile.AutoPreamp ? "Off - boosts can clip and crackle on loud audio"
                : trim > -0.05 ? "No volume trim needed"
                : "Turned down " + (-trim).ToString("0.0") + " dB so boosts can't distort - cuts work better than boosts.";
        }

        void Fire(bool commitNow)
        {
            UpdatePreLabel();
            Graph.Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            commitTimer.Stop();
            if (commitNow) { if (Committed != null) Committed(this, EventArgs.Empty); }
            else commitTimer.Start();
        }

        /// <summary>Applies new graphic band values from outside (e.g. auto-EQ) and commits them.</summary>
        public void ApplyBands(double[] bands)
        {
            if (profile == null) return;
            profile.Bands = bands;
            profile.Mode = "graphic";
            profile.Enabled = true;
            LoadValues();
            Fire(true);
        }

        void ImportFile()
        {
            using (var dlg = new OpenFileDialog { Title = "Import EQ correction (Equalizer APO / AutoEq ParametricEQ.txt)", Filter = "EQ text files (*.txt)|*.txt|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    double pre;
                    var filters = EqualizerApo.ParseParametric(File.ReadAllText(dlg.FileName), out pre);
                    if (filters.Count == 0)
                    {
                        MessageBox.Show(FindForm(), "No parametric filters found. Use a file in Equalizer APO \"ParametricEQ\" format (lines like \"Filter 1: ON PK Fc 100 Hz Gain 3 dB Q 1.41\").", "Import EQ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    profile.Extra = filters;
                    profile.ExtraName = Path.GetFileNameWithoutExtension(dlg.FileName);
                    profile.Enabled = true;
                    LoadValues();
                    Fire(true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), ex.Message, "Import EQ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}



