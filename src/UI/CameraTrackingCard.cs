using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamLite.Camera;

namespace StreamLite.UI
{
    /// <summary>
    /// Camera page: "AI tracking &amp; framing" - StreamLite Camera as a virtual gimbal camera (an Insta360 Link 2's
    /// software, with any webcam): tracking one person or a group, head / half body / whole body framing, gestures,
    /// tracking and pause zones, presets, whiteboard and desk view, level and upside-down mounting, 9:16 guide.
    /// </summary>
    internal sealed class CameraTrackingCard
    {
        readonly AppCore core;
        readonly Card card;
        readonly List<Action> loaders = new List<Action>();
        readonly Timer timer = new Timer { Interval = 400 };
        readonly Label state;
        readonly FlatButton[] presetButtons = new FlatButton[CameraDirector.MaxPresets];
        bool loading;
        CamFxSettings C { get { return core.S.CamFx; } }
        CameraDirector D { get { return core.CamFx.Director; } }

        static readonly string[] FramingKeys = { "head", "half", "body", "custom" };
        static readonly string[] FramingNames = { "Head and shoulders", "Half body", "Whole body", "My own zoom" };
        static readonly string[] ViewKeys = { "normal", "whiteboard", "desk" };
        static readonly string[] ViewNames = { "Normal", "Whiteboard (squared off)", "Desk view" };

        /// <param name="top">Where the card starts on the page (pixels).</param>
        public CameraTrackingCard(AppCore core, Panel page, int top)
        {
            this.core = core;
            card = new Card { Location = new Point(0, top), Size = new Size(Theme.Px(690), Theme.Px(468)) };
            page.Controls.Add(card);
            Theme.Help(Theme.Label(card, "AI tracking & framing", 20, 18, Theme.Section),
                "StreamLite Camera works like an AI gimbal camera (Insta360 Link 2 style) with any webcam: it moves, zooms and turns a window inside your camera's picture - with a 4K camera, up to 2x without losing any sharpness.\nOn the preview above: click a face to follow that person, drag to move the view, mouse wheel to zoom, double-click to see everything again.");
            state = Theme.Label(card, "", 20, 46, Theme.Small, Theme.Muted, 650);
            state.UseMnemonic = false;

            // ---- tracking
            var track = Place(card, new ToggleSwitch(), 20, 78);
            Theme.Help(Theme.Label(card, "AI tracking", 74, 78, Theme.Bold),
                "Follows you and keeps you framed, like a camera operator: small movements (talking, nodding) don't move the view; it glides when you really move. Lost you? It holds for 3 seconds, then shows the whole room until you're back.\nGesture: show an open palm beside your face.");
            loaders.Add(() => track.SetSilently(C.FaceTracking));
            track.CheckedChanged += (s, e) => { if (loading) return; C.FaceTracking = track.Checked; core.SaveSoon(); };
            var mode = Place(card, new DarkCombo(), 200, 74, 160);
            mode.Items.AddRange(new object[] { "One person", "Group (everyone)" });
            Theme.Help(mode, "One person: the closest one, or whoever you click on the preview.\nGroup: everyone in front of the camera stays in the picture.");
            loaders.Add(() => mode.SelectedIndex = C.TrackMode == "group" ? 1 : 0);
            mode.SelectionChangeCommitted += (s, e) => { C.TrackMode = mode.SelectedIndex == 1 ? "group" : "single"; core.SaveSoon(); };

            Theme.Help(Theme.Label(card, "Framing", 20, 118, Theme.Body), "How much of you the tracking shows.\nMy own zoom: the Zoom slider sets it.");
            var framing = Place(card, new DarkCombo(), 130, 114, 230);
            framing.Items.AddRange(FramingNames);
            loaders.Add(() => framing.SelectedIndex = Math.Max(0, Array.IndexOf(FramingKeys, C.Framing)));
            framing.SelectionChangeCommitted += (s, e) => { C.Framing = FramingKeys[Math.Max(0, framing.SelectedIndex)]; core.SaveSoon(); };
            Slider(card, 390, 118, "Headroom", 0, 100, () => C.Headroom, v => C.Headroom = v, v => (v - 50 > 0 ? "+" : "") + (v - 50), "Where your face sits: lower (more room above you) or higher.", 50, 80, 130);
            Slider(card, 20, 158, "Follow speed", 0, 100, () => C.TrackSpeed, v => C.TrackSpeed = v, v => v + "%", "How quickly the view follows you (and moves to presets).", 40, 110, 180);
            Slider(card, 390, 158, "Zoom", 100, 400, () => C.Zoom, v => C.Zoom = v, v => (v / 100.0).ToString("0.0") + "x", "Tracking off: how close the view is (or use the mouse wheel on the preview). Tracking with \"My own zoom\": how close it frames you.", 100, 80, 130);

            // ---- view
            Theme.Help(Theme.Label(card, "View", 20, 204, Theme.Body),
                "Whiteboard: finds a whiteboard in the picture by itself, squares it off and makes the writing stand out (gesture: V). Works best when the board stands out from the wall.\nDesk view: the area you mark (Zones & desk area...) squared off - for showing things on your desk.");
            var view = Place(card, new DarkCombo(), 130, 200, 230);
            view.Items.AddRange(ViewNames);
            loaders.Add(() => view.SelectedIndex = Math.Max(0, Array.IndexOf(ViewKeys, C.ViewMode)));
            view.SelectionChangeCommitted += (s, e) => { C.ViewMode = ViewKeys[Math.Max(0, view.SelectedIndex)]; core.SaveSoon(); };
            var zones = Place(card, new FlatButton("Zones && desk area..."), 390, 198, 180);
            zones.Click += (s, e) => { using (var f = new ZoneEditor(core)) f.ShowDialog(card.FindForm()); Load(); };
            var zoneOn = Place(card, new ToggleSwitch(), 20, 240);
            Theme.Help(Theme.Label(card, "Stay inside the tracking zone", 74, 240, Theme.Bold),
                "Tracking never shows anything outside the area you mark (Zones & desk area...) - a door, a bed, a messy corner.\nPause zones (also marked there): step into one and the camera frames that area and holds still until you step out - for a whiteboard, a product, an instrument.");
            loaders.Add(() => { zoneOn.SetSilently(C.TrackZoneOn); zoneOn.Enabled = C.TrackZone != null; });
            zoneOn.CheckedChanged += (s, e) => { if (loading) return; C.TrackZoneOn = zoneOn.Checked && C.TrackZone != null; core.SaveSoon(); };
            var zoneInfo = Theme.Label(card, "", 390, 242, Theme.Small, Theme.Muted, 280);
            loaders.Add(() => zoneInfo.Text = (C.TrackZone == null ? "No tracking zone marked" : "Tracking zone marked") + ", " + C.PauseZones.Count + " pause zone" + (C.PauseZones.Count == 1 ? "" : "s"));

            // ---- mounting
            Slider(card, 20, 282, "Level", -15, 15, () => C.Level, v => C.Level = v, v => (v > 0 ? "+" : "") + v + "°", "Straightens a tilted camera (turns the picture by up to 15 degrees; zooms in a little to keep the corners filled).", 0, 110, 180);
            var flip = Place(card, new ToggleSwitch(), 390, 282);
            Theme.Help(Theme.Label(card, "Upside down", 444, 282, Theme.Bold), "For a camera mounted upside down (hanging from a shelf, overhead on an arm).");
            loaders.Add(() => flip.SetSilently(C.UpsideDown));
            flip.CheckedChanged += (s, e) => { if (loading) return; C.UpsideDown = flip.Checked; core.SaveSoon(); };
            var vert = Place(card, new ToggleSwitch(), 20, 320);
            Theme.Help(Theme.Label(card, "Vertical 9:16 guide", 74, 320, Theme.Bold),
                "For vertical streams (TikTok, Shorts, Reels): tracking keeps you in the middle and the sides outside the 9:16 column are darkened, so a vertical crop of the camera (OBS, TikTok Live Studio) always has you in it. The camera itself stays 16:9.");
            loaders.Add(() => vert.SetSilently(C.Vertical));
            vert.CheckedChanged += (s, e) => { if (loading) return; C.Vertical = vert.Checked; core.SaveSoon(); };

            // ---- presets
            Theme.Help(Theme.Label(card, "Presets", 20, 364, Theme.Bold),
                "Saved views: click one to go there (tracking turns off). Hotkeys can call them too (Hotkeys page: Camera preset).\n\"Save view\" keeps what apps see right now; right-click a preset to save over it or clear it.");
            for (int i = 0; i < presetButtons.Length; i++)
            {
                int k = i;
                var b = Place(card, new FlatButton((i + 1).ToString()), 100 + i * 42, 360, 38);   // (one column: the page keeps them together)
                b.Click += (s, e) => { D.RecallPreset(k); timer.Start(); };
                // Right-click: keep the current view here, or empty it.
                var pm = new ContextMenuStrip();
                pm.Items.Add("Save the current view here", null, (s, e) => { D.SavePreset(k, null); DelayedLoad(); });
                pm.Items.Add("Clear", null, (s, e) => { if (k < C.Presets.Count) { C.Presets[k] = new CamPreset(); core.SaveSoon(); Load(); } });
                b.ContextMenuStrip = pm;
                presetButtons[i] = b;
            }
            var save = Place(card, new FlatButton("Save view..."), 390, 360, 130);
            var menu = new ContextMenuStrip();
            for (int i = 0; i < CameraDirector.MaxPresets; i++)
            {
                int k = i;
                menu.Items.Add("Save as preset " + (i + 1), null, (s, e) => { D.SavePreset(k, null); DelayedLoad(); });
            }
            save.Click += (s, e) => menu.Show(save, new Point(0, save.Height));
            loaders.Add(() =>
            {
                for (int i = 0; i < presetButtons.Length; i++)
                {
                    var p = i < C.Presets.Count ? C.Presets[i] : null;
                    bool used = p != null && p.Used;
                    presetButtons[i].ForeColor = used ? Theme.Text : Theme.Muted;
                    Theme.Help(presetButtons[i], used ? (p.Name ?? "Preset " + (i + 1)) + " - " + (p.Zoom / 100.0).ToString("0.0") + "x" : "Empty - \"Save view...\" keeps the current view here.");
                }
            });
            var reset = Place(card, new FlatButton("Reset view"), 20, 410, 130);
            Theme.Help(reset, "Back to the whole picture (also: double-click the preview).");
            reset.Click += (s, e) => D.ResetView();
            var priv = Place(card, new FlatButton("Privacy"), 160, 410, 130);
            Theme.Help(priv, "Apps see a \"Camera paused\" picture instead of you, until you click again (hotkey: Privacy).");
            priv.Click += (s, e) => { core.CamFx.Privacy = !core.CamFx.Privacy; };
            var auto = Place(card, new ToggleSwitch(), 390, 414);
            Theme.Help(Theme.Label(card, "Pause when I leave", 444, 414, Theme.Bold), "Shows the \"Camera paused\" picture once nobody has been in front of the camera for 15 seconds, and you again as soon as you're back.");
            loaders.Add(() => auto.SetSilently(C.AutoPrivacy));
            auto.CheckedChanged += (s, e) => { if (loading) return; C.AutoPrivacy = auto.Checked; core.SaveSoon(); };

            timer.Tick += (s, e) =>
            {
                var fx = core.CamFx;
                string g = fx.GestureStatus;
                state.Text = (fx.PrivacyShowing ? "Privacy: apps see \"Camera paused\". " : "") +
                    (string.IsNullOrEmpty(D.State) ? (fx.Fps > 0 ? "Whole picture" : "Runs while an app shows StreamLite Camera, or the preview is on") : D.State) +
                    (g == "palm" ? " - I see an open palm" : g == "v" ? " - I see a V" : g == "l" ? " - I see an L: move up / down to zoom" : g != null && g.StartsWith("gestures failed") ? " - " + g : "") +
                    (fx.Animation != null ? " - " + CameraAnimation.Label(fx.Animation) : "");
                priv.Text = fx.Privacy ? "Camera back on" : "Privacy";
                if (D.SettingsChanged || settleLoads > 0) { if (--settleLoads <= 0) Load(); }
            };
            card.VisibleChanged += (s, e) => { if (card.Visible) { Load(); timer.Start(); } else timer.Stop(); };
            Load();
            // Gestures, air drawing and animations: their own card, right below.
            gestures = new CameraGesturesCard(core, page, card.Bottom + Theme.Px(14));
        }

        int settleLoads;
        void DelayedLoad() { settleLoads = 2; }
        readonly CameraGesturesCard gestures;

        /// <summary>The bottom of this card and the gestures card below it.</summary>
        public int Bottom { get { return gestures.Bottom; } }

        void Load()
        {
            loading = true;
            foreach (var l in loaders) l();
            loading = false;
        }

        void Slider(Control parent, int x, int y, string name, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string help, int def, int labelW, int sliderW)
        {
            Theme.Help(Theme.Label(parent, name, x, y + 2, Theme.Body), help);
            var sl = Place(parent, new FlatSlider { Minimum = min, Maximum = max, DefaultValue = def }, x + labelW, y, sliderW);
            var val = Theme.Label(parent, "", x + labelW + sliderW + 6, y + 2, Theme.Small, Theme.Muted, 50);
            Theme.Help(sl, help + " Double-click to reset.");
            sl.ValueChanged += (s, e) => { val.Text = fmt(sl.Value); if (loading) return; set(sl.Value); core.SaveSoon(); };
            loaders.Add(() => { sl.Value = Math.Max(min, Math.Min(max, get())); val.Text = fmt(get()); });
        }

        static T Place<T>(Control parent, T c, int x, int y, int w = 0) where T : Control
        {
            c.Location = new Point(Theme.Px(x), Theme.Px(y));
            if (w > 0) c.Width = Theme.Px(w);
            parent.Controls.Add(c);
            return c;
        }
    }

    /// <summary>
    /// Marks the tracking zone, the pause zones and the desk area on the whole camera picture (shown as viewers see it:
    /// mirrored / turned round like the camera is set up; stored as the camera sees it).
    /// </summary>
    internal sealed class ZoneEditor : DarkDialog
    {
        readonly AppCore core;
        readonly Canvas canvas;
        CamFxSettings C { get { return core.S.CamFx; } }
        string tool = "zone";
        readonly Label hint;
        readonly bool wasPreview;

        public ZoneEditor(AppCore core) : base("Zones & desk area", 820, 590)
        {
            this.core = core;
            canvas = new Canvas(this) { Location = new Point(Theme.Px(20), Theme.Px(16)), Size = new Size(Theme.Px(780), Theme.Px(439)) };
            Body.Controls.Add(canvas);
            hint = Text2("", 20, 462, 780, 40, Theme.Small, Theme.Muted);
            var tools = new[] { new KeyValuePair<string, string>("zone", "Tracking zone"), new KeyValuePair<string, string>("pause", "Pause zones"), new KeyValuePair<string, string>("desk", "Desk area") };
            var buttons = new List<FlatButton>();
            for (int i = 0; i < tools.Length; i++)
            {
                var t = tools[i];
                var b = new FlatButton(t.Value) { Location = new Point(Theme.Px(20 + i * 150), Theme.Px(512)), Width = Theme.Px(140) };
                b.Click += (s, e) => { tool = t.Key; foreach (var o in buttons) o.ForeColor = Theme.Muted; b.ForeColor = Theme.Accent; Hint(); canvas.Invalidate(); };
                Body.Controls.Add(b); buttons.Add(b);
            }
            buttons[0].ForeColor = Theme.Accent;
            var clear = new FlatButton("Clear") { Location = new Point(Theme.Px(480), Theme.Px(512)), Width = Theme.Px(100) };
            clear.Click += (s, e) =>
            {
                if (tool == "zone") { C.TrackZone = null; C.TrackZoneOn = false; }
                else if (tool == "pause") C.PauseZones.Clear();
                else C.DeskQuad = new[] { 0.22, 0.58, 0.78, 0.58, 1.0, 1.0, 0.0, 1.0 };
                core.SaveSoon(); canvas.Invalidate();
            };
            Body.Controls.Add(clear);
            var done = new FlatButton("Done", true) { Location = new Point(Theme.Px(680), Theme.Px(512)), Width = Theme.Px(120) };
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(done);
            Hint();
            wasPreview = core.CamFx.PreviewWanted;
            core.CamFx.FullFrameReady += canvas.SetFrame;
            core.CamFx.FullFrameWanted = true;
            core.CamFx.PreviewWanted = true;   // the camera runs while the editor is open
            FormClosed += (s, e) =>
            {
                core.CamFx.FullFrameReady -= canvas.SetFrame;
                core.CamFx.FullFrameWanted = false;
                core.CamFx.PreviewWanted = wasPreview;
                core.SaveSoon();
            };
        }

        void Hint()
        {
            hint.Text = tool == "zone" ? "Drag a rectangle: tracking never shows anything outside it (switch it on with \"Stay inside the tracking zone\")."
                : tool == "pause" ? "Drag rectangles (up to 6): step into one and the camera frames that area and holds still. Right-click one to remove it."
                : "Drag the four corners onto the desk area to show - it's squared off (View: Desk view).";
        }

        // Shown as viewers see it; stored as the camera sees it.
        bool FlipX { get { return C.Mirror ^ C.UpsideDown; } }
        bool FlipY { get { return C.UpsideDown; } }
        double ToShownX(double x) { return FlipX ? 1 - x : x; }
        double ToShownY(double y) { return FlipY ? 1 - y : y; }

        sealed class Canvas : Control
        {
            readonly ZoneEditor ed;
            Bitmap frame; readonly object gate = new object(); volatile bool queued;
            Point down; bool dragging; int corner = -1;
            Rectangle drawn;

            public Canvas(ZoneEditor ed)
            {
                this.ed = ed;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Black;
            }

            public void SetFrame(byte[] bgra)
            {
                if (queued || !IsHandleCreated) return;
                lock (gate)
                {
                    if (frame == null) frame = new Bitmap(640, 360, PixelFormat.Format32bppRgb);
                    var d = frame.LockBits(new Rectangle(0, 0, 640, 360), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    Marshal.Copy(bgra, 0, d.Scan0, Math.Min(bgra.Length, 640 * 360 * 4));
                    frame.UnlockBits(d);
                }
                queued = true;
                try { BeginInvoke(new Action(() => { queued = false; Invalidate(); })); } catch { queued = false; }
            }

            // Shown 0..1 <-> control pixels; stored (camera) 0..1 <-> shown.
            PointF Px(double sx, double sy) { return new PointF((float)(sx * Width), (float)(sy * Height)); }
            double CamX(int px) { double s = Math.Max(0, Math.Min(1, px / (double)Width)); return ed.FlipX ? 1 - s : s; }
            double CamY(int py) { double s = Math.Max(0, Math.Min(1, py / (double)Height)); return ed.FlipY ? 1 - s : s; }
            RectangleF Shown(double[] z)
            {
                double x0 = ed.ToShownX(z[0]), x1 = ed.ToShownX(z[2]), y0 = ed.ToShownY(z[1]), y1 = ed.ToShownY(z[3]);
                return RectangleF.FromLTRB((float)(Math.Min(x0, x1) * Width), (float)(Math.Min(y0, y1) * Height), (float)(Math.Max(x0, x1) * Width), (float)(Math.Max(y0, y1) * Height));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                lock (gate)
                {
                    if (frame == null) { g.Clear(Color.Black); TextRenderer.DrawText(g, "Starting the camera...", Theme.Small, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
                    else
                    {
                        // The camera's raw picture, flipped to how viewers see it.
                        var st = g.Save();
                        g.TranslateTransform(ed.FlipX ? Width : 0, ed.FlipY ? Height : 0);
                        g.ScaleTransform(ed.FlipX ? -1 : 1, ed.FlipY ? -1 : 1);
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.DrawImage(frame, new Rectangle(0, 0, Width, Height));
                        g.Restore(st);
                    }
                }
                var c = ed.C;
                using (var zonePen = new Pen(Theme.Accent, 2f))
                using (var pausePen = new Pen(Color.FromArgb(255, 196, 80), 2f))
                using (var deskPen = new Pen(Color.FromArgb(110, 165, 240), 2f))
                using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                using (var fill = new SolidBrush(Color.FromArgb(40, 255, 196, 80)))
                {
                    if (c.TrackZone != null)
                    {
                        var r = Shown(c.TrackZone);
                        using (var outside = new Region(ClientRectangle)) { outside.Exclude(r); g.FillRegion(shade, outside); }
                        g.DrawRectangle(zonePen, r.X, r.Y, r.Width, r.Height);
                        DrawTag(g, "Tracking zone", r.Location, Theme.Accent);
                    }
                    for (int i = 0; i < c.PauseZones.Count; i++)
                    {
                        var r = Shown(c.PauseZones[i]);
                        g.FillRectangle(fill, r); g.DrawRectangle(pausePen, r.X, r.Y, r.Width, r.Height);
                        DrawTag(g, "Pause " + (i + 1), r.Location, Color.FromArgb(255, 196, 80));
                    }
                    if (ed.tool == "desk" || c.ViewMode == "desk")
                    {
                        var q = c.DeskQuad; var pts = new PointF[4];
                        for (int i = 0; i < 4; i++) pts[i] = Px(ed.ToShownX(q[i * 2]), ed.ToShownY(q[i * 2 + 1]));
                        g.DrawPolygon(deskPen, pts);
                        foreach (var p in pts) using (var b = new SolidBrush(Color.FromArgb(110, 165, 240))) g.FillEllipse(b, p.X - 6, p.Y - 6, 12, 12);
                        DrawTag(g, "Desk area", pts[0], Color.FromArgb(110, 165, 240));
                    }
                    if (dragging && corner < 0) using (var p = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, drawn);
                }
            }

            static void DrawTag(Graphics g, string s, PointF at, Color c)
            {
                TextRenderer.DrawText(g, s, Theme.Small, new Point((int)at.X + 4, (int)at.Y + 2), c);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                var c = ed.C;
                if (e.Button == MouseButtons.Right && ed.tool == "pause")
                {
                    for (int i = c.PauseZones.Count - 1; i >= 0; i--) if (Shown(c.PauseZones[i]).Contains(e.Location)) { c.PauseZones.RemoveAt(i); ed.core.SaveSoon(); Invalidate(); return; }
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                down = e.Location; dragging = true; corner = -1;
                if (ed.tool == "desk")
                {
                    double best = 18;
                    for (int i = 0; i < 4; i++)
                    {
                        var p = Px(ed.ToShownX(c.DeskQuad[i * 2]), ed.ToShownY(c.DeskQuad[i * 2 + 1]));
                        double d = Math.Sqrt((p.X - e.X) * (p.X - e.X) + (p.Y - e.Y) * (p.Y - e.Y));
                        if (d < best) { best = d; corner = i; }
                    }
                    if (corner < 0) dragging = false;
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!dragging) return;
                if (corner >= 0)
                {
                    var q = (double[])ed.C.DeskQuad.Clone();
                    q[corner * 2] = CamX(e.X); q[corner * 2 + 1] = CamY(e.Y);
                    ed.C.DeskQuad = q;
                }
                else drawn = Rectangle.FromLTRB(Math.Min(down.X, e.X), Math.Min(down.Y, e.Y), Math.Max(down.X, e.X), Math.Max(down.Y, e.Y));
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!dragging) return;
                dragging = false;
                var c = ed.C;
                if (corner < 0 && drawn.Width > 12 && drawn.Height > 12)
                {
                    double ax = CamX(drawn.Left), bx = CamX(drawn.Right), ay = CamY(drawn.Top), by = CamY(drawn.Bottom);
                    var z = new[] { Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by) };
                    if (ed.tool == "zone") { c.TrackZone = z; c.TrackZoneOn = true; }
                    else if (ed.tool == "pause" && c.PauseZones.Count < CameraDirector.MaxPauseZones) c.PauseZones.Add(z);
                }
                corner = -1; drawn = Rectangle.Empty;
                ed.core.SaveSoon();
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) lock (gate) { if (frame != null) frame.Dispose(); frame = null; }
                base.Dispose(disposing);
            }
        }
    }
}
