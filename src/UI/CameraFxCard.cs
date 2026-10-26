using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamLite.Camera;

namespace StreamLite.UI
{
    /// <summary>
    /// Camera page: "StreamLite Camera" - the AI virtual camera (AI green screen / blur / picture / video backgrounds,
    /// auto light, HDR, denoise, sharpen, skin smoothing, colour) with a live preview of what apps get - which also
    /// steers the view (click a face, drag, wheel). Tracking and framing: CameraTrackingCard.
    /// </summary>
    internal sealed class CameraFxCard
    {
        readonly AppCore core;
        readonly Card card;
        readonly FxPreview preview;
        readonly Label status;
        readonly ToggleSwitch previewToggle;
        readonly List<Action> loaders = new List<Action>();
        readonly Timer timer = new Timer { Interval = 500 };
        readonly Action stopRawPreview;
        bool loading;
        CamFxSettings C { get { return core.S.CamFx; } }

        static readonly string[] BgKeys = { "off", "blur", "green", "image", "video", "dim" };
        static readonly string[] BgNames = { "Off (your room)", "Blur the room", "AI green screen (solid green)", "Picture...", "Video (loops)...", "Dim the room" };

        public CameraFxCard(AppCore core, Panel page, int y, Action stopRawPreview)
        {
            this.core = core;
            this.stopRawPreview = stopRawPreview;
            card = new Card { Location = new Point(0, Theme.Px(y)), Size = new Size(Theme.Px(690), Theme.Px(690)) };
            page.Controls.Add(card);
            Theme.Help(Theme.Label(card, "StreamLite Camera (AI)", 20, 18, Theme.Section),
                "A virtual camera called \"" + VirtualCamera.FriendlyName + "\" that every app can pick (Discord, OBS, Teams, Zoom, browsers, the Camera app). It's your real camera with AI effects, processed on your graphics card.\nIt only runs (and only uses the GPU) while an app is actually showing it, or while the preview below is on.");
            var on = Place(card, new ToggleSwitch(), 630, 18);
            loaders.Add(() => on.SetSilently(C.Enabled));
            on.CheckedChanged += (s, e) => { if (loading) return; C.Enabled = on.Checked; core.SaveSoon(); };
            status = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(48)), Size = new Size(Theme.Px(650), Theme.Px(34)), UseMnemonic = false };
            card.Controls.Add(status);

            // ---- preview + framing
            preview = new FxPreview { Core = core, Location = new Point(Theme.Px(20), Theme.Px(86)), Size = new Size(Theme.Px(400), Theme.Px(225)) };
            card.Controls.Add(preview);
            previewToggle = Place(card, new ToggleSwitch(), 20, 320);
            Theme.Help(Theme.Label(card, "Preview (what apps see)", 74, 320, Theme.Bold), "Shows the virtual camera's picture here, with every effect. Your camera's plain preview (above) turns off while this is on.\nOn the preview: click a face to follow that person, drag to move the view, mouse wheel to zoom, double-click for the whole picture.");
            previewToggle.CheckedChanged += (s, e) =>
            {
                if (previewToggle.Checked) stopRawPreview();
                core.CamFx.PreviewWanted = previewToggle.Checked;
                preview.Active = previewToggle.Checked;
                if (!previewToggle.Checked) preview.Clear();
            };
            core.CamFx.PreviewReady += preview.SetFrame;

            int x = 440;
            var track = Place(card, new ToggleSwitch(), x, 88);
            Theme.Help(Theme.Label(card, "AI tracking", x + 54, 88, Theme.Bold),
                "Follows you and keeps you framed (more below: AI tracking & framing).\nGesture: an open palm beside your face.");
            loaders.Add(() => track.SetSilently(C.FaceTracking));
            track.CheckedChanged += (s, e) => { if (loading) return; C.FaceTracking = track.Checked; core.SaveSoon(); };
            Slider(card, x, 124, "HDR", 0, 100, () => C.Hdr, v => C.Hdr = v, v => v + "%", "Local tone mapping: brings up dark corners and tames a bright window behind you, keeping the detail in each.", 0, 95, 105);
            var fe = Place(card, new ToggleSwitch(), x, 166);
            Theme.Help(Theme.Label(card, "Face exposure", x + 54, 166, Theme.Bold), "Auto light measures your face instead of the whole picture: a face against a bright window comes up, a lit face in a dark room comes down.");
            loaders.Add(() => fe.SetSilently(C.FaceExposure));
            fe.CheckedChanged += (s, e) => { if (loading) return; C.FaceExposure = fe.Checked; core.SaveSoon(); };
            var k4 = Place(card, new ToggleSwitch(), x, 214);
            Theme.Help(Theme.Label(card, "4K capture", x + 54, 214, Theme.Bold),
                "Reads your camera in 4K and scales to 1080p: sharper, less noisy picture and up to 2x lossless zoom. Needs a 4K camera. Off while Low VRAM is on.");
            loaders.Add(() => k4.SetSilently(C.Capture4K));
            k4.CheckedChanged += (s, e) => { if (loading) return; C.Capture4K = k4.Checked; core.SaveSoon(); core.CamFx.Restart(); };
            var mirror = Place(card, new ToggleSwitch(), x, 248);
            Theme.Help(Theme.Label(card, "Mirror", x + 54, 248, Theme.Bold), "Flips the picture left/right for everyone (most apps already mirror your own self-view).");
            loaders.Add(() => mirror.SetSilently(C.Mirror));
            mirror.CheckedChanged += (s, e) => { if (loading) return; C.Mirror = mirror.Checked; core.SaveSoon(); };
            // Where the camera's AI runs and how big its models are: AI quality in Settings (with voice isolation).
            Theme.Help(Theme.Label(card, "AI quality", x, 284, Theme.Bold),
                "Set in Settings > AI quality, for voice isolation and the camera together.\nLow VRAM: the effects in 16-bit at 1080p (4K capture off). Normal: the full effects. High VRAM: the cut-out worked out at full HD - the cleanest hair and edges. Full CPU: no graphics card (slow: the camera plays without effects when they can't keep up).");
            var tier = Theme.Label(card, "", x, 306, Theme.Small, Theme.Muted, 230);
            tier.Height = Theme.Px(36); tier.AutoEllipsis = false;
            tier.Anchor = AnchorStyles.Top | AnchorStyles.Right; Responsive.Fixed(tier);   // moves with its column, wraps inside it
            loaders.Add(() => { string t = core.S.AiTier ?? AiTiers.Normal; tier.Text = AiTiers.Label(t) + " - " + AiTiers.Budget(t) + " (Settings)"; k4.Enabled = !core.S.LowVram; });   // Low VRAM captures 1080p

            // ---- background (AI green screen)
            Theme.Help(Theme.Label(card, "Background", 20, 364, Theme.Bold),
                "AI green screen: a matting model (Robust Video Matting) cuts you out of the room every frame - no real green screen needed.\n- Blur: like a big lens. - AI green screen: solid green behind you, for OBS's Chroma Key. - Picture / Video: your own image or a looping clip (mp4, mov, wmv...). - Dim: darker, greyer room.");
            var bg = Place(card, new DarkCombo(), 130, 360, 250);
            bg.Items.AddRange(BgNames);
            loaders.Add(() => bg.SelectedIndex = Math.Max(0, Array.IndexOf(BgKeys, C.Background)));
            var file = Place(card, new FlatButton("Choose file..."), 390, 358, 130);
            var fileName = Theme.Label(card, "", 530, 362, Theme.Small, Theme.Muted, 150);
            fileName.UseMnemonic = false;
            Action showFile = () =>
            {
                string f = C.Background == "image" ? C.BackgroundImage : C.Background == "video" ? C.BackgroundVideo : null;
                file.Visible = C.Background == "image" || C.Background == "video";
                fileName.Text = string.IsNullOrEmpty(f) ? (file.Visible ? "(none yet)" : "") : System.IO.Path.GetFileName(f);
            };
            loaders.Add(showFile);
            Func<bool> pick = () =>
            {
                bool video = C.Background == "video";
                using (var d = new OpenFileDialog
                {
                    Title = video ? "Background video" : "Background picture",
                    Filter = video ? "Videos|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.m4v;*.webm|All files|*.*" : "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"
                })
                {
                    if (d.ShowDialog(card.FindForm()) != DialogResult.OK) return false;
                    if (video) C.BackgroundVideo = d.FileName; else C.BackgroundImage = d.FileName;
                    core.SaveSoon();
                    return true;
                }
            };
            file.Click += (s, e) => { pick(); showFile(); };
            bg.SelectionChangeCommitted += (s, e) =>
            {
                C.Background = BgKeys[Math.Max(0, bg.SelectedIndex)];
                if ((C.Background == "image" && string.IsNullOrEmpty(C.BackgroundImage)) || (C.Background == "video" && string.IsNullOrEmpty(C.BackgroundVideo))) pick();
                core.SaveSoon(); showFile();
            };
            Slider(card, 20, 400, "Blur amount", 0, 100, () => C.BlurAmount, v => C.BlurAmount = v, v => v + "%", "Blur mode: how much the room is blurred.", 100);
            Slider(card, 360, 400, "Edge sharpness", 0, 100, () => C.EdgeSharpness, v => C.EdgeSharpness = v, v => v + "%", "Softer (natural hair) or tighter (crisper outline) cut-out edges.", 30);

            // ---- image quality
            var al = Place(card, new ToggleSwitch(), 20, 452);
            Theme.Help(Theme.Label(card, "Auto light", 74, 452, Theme.Bold),
                "Auto exposure on top of the camera's own: measures your picture and adjusts the tone curve - brightens dark rooms and lifts faces without blowing out windows. Adjusts slowly so it never pumps.");
            loaders.Add(() => al.SetSilently(C.AutoLight));
            al.CheckedChanged += (s, e) => { if (loading) return; C.AutoLight = al.Checked; core.SaveSoon(); };
            Slider(card, 20, 488, "Brightness", -50, 50, () => C.Brightness, v => C.Brightness = v, v => (v > 0 ? "+" : "") + v, "Overall brightness.", 0);
            Slider(card, 360, 488, "Contrast", -50, 50, () => C.Contrast, v => C.Contrast = v, v => (v > 0 ? "+" : "") + v, "Contrast.", 0);
            Slider(card, 20, 532, "Denoise", 0, 100, () => C.Denoise, v => C.Denoise = v, v => v + "%", "Removes grain (low light) while keeping edges sharp (guided filter on the GPU).", 40);
            Slider(card, 360, 532, "Sharpen", 0, 100, () => C.Sharpen, v => C.Sharpen = v, v => v + "%", "Crisper detail. Apply after denoise; too high looks artificial.", 30);
            Slider(card, 20, 576, "Skin smoothing", 0, 100, () => C.SkinSmoothing, v => C.SkinSmoothing = v, v => v + "%", "Softens skin inside your face only (follows the face detector) - eyes, hair and background stay sharp.", 0);
            Slider(card, 360, 576, "Saturation", 0, 200, () => C.Saturation, v => C.Saturation = v, v => v + "%", "Colour intensity. 100% = as the camera sees it.", 100);
            Slider(card, 20, 620, "Warmth", -50, 50, () => C.Warmth, v => C.Warmth = v, v => (v > 0 ? "+" : "") + v, "Cooler (blue) or warmer (orange) colour.", 0);
            var reset = Place(card, new FlatButton("Reset effects"), 360, 618, 150);
            reset.Click += (s, e) =>
            {
                var d = new CamFxSettings();
                C.AutoLight = d.AutoLight; C.Brightness = d.Brightness; C.Contrast = d.Contrast; C.Denoise = d.Denoise; C.Sharpen = d.Sharpen;
                C.SkinSmoothing = d.SkinSmoothing; C.Saturation = d.Saturation; C.Warmth = d.Warmth; C.EdgeSharpness = d.EdgeSharpness; C.BlurAmount = d.BlurAmount;
                core.SaveSoon(); Load();
            };

            timer.Tick += (s, e) =>
            {
                var fx = core.CamFx;
                status.Text = fx.Status + (fx.PrivacyShowing ? " - privacy (apps see \"Camera paused\")" : !string.IsNullOrEmpty(fx.Director.State) && fx.Fps > 0 ? " - " + fx.Director.State : "");
                if (fx.Director.SettingsChanged) Load();   // a gesture or the preview changed the view
            };
            card.VisibleChanged += (s, e) => { if (card.Visible) { Load(); timer.Start(); } else { timer.Stop(); } };
            Load();
        }

        public int Bottom { get { return card.Bottom; } }

        /// <summary>The page is being left: stop the preview (the camera keeps running only if an app watches).</summary>
        public void Hidden()
        {
            previewToggle.SetSilently(false);
            core.CamFx.PreviewWanted = false;
            preview.Active = false;
            preview.Clear();
        }

        /// <summary>The plain camera preview started: the effects preview gives the camera up.</summary>
        public void RawPreviewStarted() { Hidden(); }

        void Load()
        {
            loading = true;
            foreach (var l in loaders) l();
            loading = false;
        }

        void Slider(Control parent, int x, int y, string name, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string help, int def, int labelW = 110, int sliderW = 150)
        {
            Theme.Help(Theme.Label(parent, name, x, y + 2, Theme.Body), help);
            var sl = Place(parent, new FlatSlider { Minimum = min, Maximum = max, DefaultValue = def }, x + labelW, y, sliderW);
            var val = Theme.Label(parent, "", x + labelW + sliderW + 6, y + 2, Theme.Small, Theme.Muted, 44);
            Theme.Help(sl, help + " Double-click to reset.");
            sl.ValueChanged += (s, e) => { val.Text = fmt(sl.Value); if (loading) return; set(sl.Value); core.SaveSoon(); };
            loaders.Add(() => { sl.Value = get(); val.Text = fmt(get()); });
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
    /// Draws the virtual camera's 640x360 preview frames (arriving on the effects thread) at the control size, with
    /// the faces tracking sees - and steers the view: click a face to follow it, drag to move, wheel to zoom,
    /// double-click for the whole picture.
    /// </summary>
    internal sealed class FxPreview : Control
    {
        Bitmap frame;
        readonly object gate = new object();
        volatile bool queued;
        /// <summary>Off: frames still produced for a watching app (or while the pipeline winds down) aren't shown.</summary>
        public volatile bool Active;
        public AppCore Core;
        Point down, last; bool pressed, moved;

        public FxPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        public void SetFrame(byte[] bgra)
        {
            if (!Active || queued || !IsHandleCreated) return;    // UI still drawing the last one: skip (keeps the effects thread free)
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

        public void Clear()
        {
            lock (gate) { if (frame != null) { frame.Dispose(); frame = null; } }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            lock (gate)
            {
                if (frame == null)
                {
                    g.Clear(Color.Black);
                    TextRenderer.DrawText(g, "Preview is off", Theme.Small, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(frame, ClientRectangle);
            }
            // The faces tracking sees (the followed one in green): click one to follow that person.
            var fx = Core != null ? Core.CamFx : null;
            if (fx == null || fx.PrivacyShowing || !Core.S.CamFx.FaceTracking || (Core.S.CamFx.ViewMode ?? "normal") != "normal") return;
            var faces = fx.Director.OutputFaces; int followed = fx.Director.FollowedIndex;
            bool mirror = Core.S.CamFx.Mirror;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i];
                double x0 = mirror ? 1 - f.X1 : f.X0, x1 = mirror ? 1 - f.X0 : f.X1;
                var r = RectangleF.FromLTRB((float)(x0 * Width), (float)(f.Y0 * Height), (float)(x1 * Width), (float)(f.Y1 * Height));
                using (var p = new Pen(i == followed ? Theme.Accent : Color.FromArgb(160, 255, 255, 255), i == followed ? 2f : 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
        }

        // ---- steering the view
        double U(int x) { return Math.Max(0, Math.Min(1, x / (double)Math.Max(1, Width))); }
        double V(int y) { return Math.Max(0, Math.Min(1, y / (double)Math.Max(1, Height))); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { pressed = true; moved = false; down = last = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pressed || Core == null || !Active) return;
            if (!moved && Math.Abs(e.X - down.X) + Math.Abs(e.Y - down.Y) < 6) return;
            moved = true;
            Core.CamFx.Director.Pan((e.X - last.X) / (double)Width, (e.Y - last.Y) / (double)Height);
            last = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!pressed) return;
            pressed = false;
            if (!moved && Core != null && Active) Core.CamFx.Director.SelectAt(U(e.X), V(e.Y));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Core != null && Active && e.Button == MouseButtons.Left) Core.CamFx.Director.ResetView();
        }

        /// <summary>The mouse wheel over the preview (the page's wheel router sends it here): zoom around the mouse.</summary>
        internal void Wheel(int delta, Point at)
        {
            if (Core == null || !Active) return;
            Core.CamFx.Director.ZoomAt(Math.Pow(1.15, delta / 120.0), U(at.X), V(at.Y));
        }
    }
}
