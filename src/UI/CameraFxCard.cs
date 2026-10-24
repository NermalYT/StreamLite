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
    /// Camera page: "StreamLite Camera" - the AI virtual camera (face tracking, AI green screen / blur / picture /
    /// video backgrounds, auto light, denoise, sharpen, skin smoothing, colour) with a live preview of what apps get.
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
            preview = new FxPreview { Location = new Point(Theme.Px(20), Theme.Px(86)), Size = new Size(Theme.Px(400), Theme.Px(225)) };
            card.Controls.Add(preview);
            previewToggle = Place(card, new ToggleSwitch(), 20, 320);
            Theme.Help(Theme.Label(card, "Preview (what apps see)", 74, 320, Theme.Bold), "Shows the virtual camera's picture here, with every effect. Your camera's plain preview (above) turns off while this is on.");
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
            Theme.Help(Theme.Label(card, "Face tracking", x + 54, 88, Theme.Bold),
                "AI auto-framing: finds your face and keeps you centred, like a camera operator. Small movements (talking, nodding) don't move the frame; it glides when you really move.\nWith a 4K camera the zoom is lossless.");
            loaders.Add(() => track.SetSilently(C.FaceTracking));
            track.CheckedChanged += (s, e) => { if (loading) return; C.FaceTracking = track.Checked; core.SaveSoon(); };
            Slider(card, x, 122, "Zoom", 100, 250, () => C.Zoom, v => C.Zoom = v, v => v + "%", "How close the framing is. With face tracking off it's a centred digital zoom.", 100, 95, 105);
            Slider(card, x, 166, "Follow speed", 0, 100, () => C.TrackSpeed, v => C.TrackSpeed = v, v => v + "%", "How quickly the framing follows you.", 40, 95, 105);
            var k4 = Place(card, new ToggleSwitch(), x, 214);
            Theme.Help(Theme.Label(card, "4K capture", x + 54, 214, Theme.Bold),
                "Reads your camera in 4K and scales to 1080p: sharper, less noisy picture and up to 2x lossless zoom. Needs a 4K camera. Off while Low VRAM is on.");
            loaders.Add(() => k4.SetSilently(C.Capture4K));
            k4.CheckedChanged += (s, e) => { if (loading) return; C.Capture4K = k4.Checked; core.SaveSoon(); core.CamFx.Restart(); };
            var mirror = Place(card, new ToggleSwitch(), x, 248);
            Theme.Help(Theme.Label(card, "Mirror", x + 54, 248, Theme.Bold), "Flips the picture left/right for everyone (most apps already mirror your own self-view).");
            loaders.Add(() => mirror.SetSilently(C.Mirror));
            mirror.CheckedChanged += (s, e) => { if (loading) return; C.Mirror = mirror.Checked; core.SaveSoon(); };
            var gpu = Place(card, new ToggleSwitch(), x, 282);
            Theme.Help(Theme.Label(card, "Use the graphics card", x + 54, 282, Theme.Bold),
                "Runs the AI on your GPU (DirectML): ~10 ms per frame, ~380 MB of video memory (~140 MB with Low VRAM). Off = CPU (much slower; only if the GPU path fails).");
            loaders.Add(() => gpu.SetSilently(C.Gpu));
            gpu.CheckedChanged += (s, e) => { if (loading) return; C.Gpu = gpu.Checked; core.SaveSoon(); core.CamFx.Restart(); };
            var low = Place(card, new ToggleSwitch(), x, 316);
            Theme.Help(Theme.Label(card, "Low VRAM", x + 54, 316, Theme.Bold), NoiseEngines.LowVramHelp + "\n(The same switch as on the Microphone page.)");
            loaders.Add(() => { low.SetSilently(core.S.LowVram); k4.Enabled = !core.S.LowVram; });   // Low VRAM captures 1080p
            low.CheckedChanged += (s, e) => { if (loading) return; core.SetLowVram(low.Checked); k4.Enabled = !low.Checked; };

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

            timer.Tick += (s, e) => status.Text = core.CamFx.Status + (core.CamFx.FaceFound && C.FaceTracking ? " - face locked" : "");
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

    /// <summary>Draws the virtual camera's 640x360 preview frames (arriving on the effects thread) at the control size.</summary>
    internal sealed class FxPreview : Control
    {
        Bitmap frame;
        readonly object gate = new object();
        volatile bool queued;
        /// <summary>Off: frames still produced for a watching app (or while the pipeline winds down) aren't shown.</summary>
        public volatile bool Active;

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
        }
    }
}
