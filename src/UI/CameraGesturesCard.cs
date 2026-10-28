using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StreamLite.Camera;

namespace StreamLite.UI
{
    /// <summary>
    /// Camera page: "Gestures &amp; air drawing" - each hand sign's own switch, what the V does, air drawing's pen
    /// (colour, thickness, fading, clear) and the camera animations to try (they're also hotkeys). Says plainly when
    /// the hand models or the camera's AI aren't there instead of switching anything else on.
    /// </summary>
    internal sealed class CameraGesturesCard
    {
        readonly AppCore core;
        readonly Card card;
        readonly List<Action> loaders = new List<Action>();
        readonly Timer timer = new Timer { Interval = 400 };
        readonly Label status;
        readonly List<FlatButton> swatches = new List<FlatButton>();
        bool loading;
        CamFxSettings C { get { return core.S.CamFx; } }

        static readonly int[] PenColors = { 0x2BFF4A, 0xFF3B3B, 0xFFE03B, 0x3BD8FF, 0xFFFFFF, 0xFF4FD8 };
        static readonly string[] PenNames = { "Green", "Red", "Yellow", "Blue", "White", "Pink" };

        /// <param name="top">Where the card starts on the page (pixels).</param>
        public CameraGesturesCard(AppCore core, Panel page, int top)
        {
            this.core = core;
            card = new Card { Location = new Point(0, top), Size = new Size(Theme.Px(690), Theme.Px(352)) };
            page.Controls.Add(card);
            Theme.Help(Theme.Label(card, "Gestures & air drawing", 20, 18, Theme.Section),
                "Hand signs beside your face, held for half a second, each with its own switch. They only do what you show them - none of them turns AI tracking on by itself.\nThey need the camera running with its AI effects (faces are found to know where your hands are). Uses a little CPU.");
            status = Theme.Label(card, "", 20, 46, Theme.Small, Theme.Muted, 650);

            // ---- one switch per sign
            Sign(20, "Palm: tracking", "Open palm (all five fingers): AI tracking on / off.", () => C.GesturePalm, v => C.GesturePalm = v);
            Sign(190, "V sign", "Index and middle finger: whiteboard or air drawing on / off (V does, below). While air drawing, a V always stops it.", () => C.GestureV, v => C.GestureV = v);
            Sign(360, "L: zoom", "Thumb and index finger in an L: move your hand up to zoom in, down to zoom out.", () => C.GestureL, v => C.GestureL = v);
            Sign(530, "Point: draw", "Air drawing: point with your index finger (the others curled) and it draws; any other pose lifts the pen.", () => C.GesturePoint, v => C.GesturePoint = v);

            // ---- what the V does, air drawing now
            Theme.Help(Theme.Label(card, "V does", 20, 122, Theme.Body), "Whiteboard: squares off a whiteboard it finds in the picture.\nAir drawing: your index finger draws on the picture apps see, like a light pen - write \"Hi!\" in the air and it stays there.");
            var vdoes = Place(card, new DarkCombo(), 130, 118, 230);
            vdoes.Items.AddRange(new object[] { "Whiteboard on / off", "Air drawing on / off" });
            loaders.Add(() => vdoes.SelectedIndex = C.VAction == "draw" ? 1 : 0);
            vdoes.SelectionChangeCommitted += (s, e) => { C.VAction = vdoes.SelectedIndex == 1 ? "draw" : "whiteboard"; core.SaveSoon(); };
            var drawOn = Place(card, new ToggleSwitch(), 390, 122);
            Theme.Help(Theme.Label(card, "Air drawing", 444, 122, Theme.Bold), "On now: point your index finger to draw. Also a V (when V does air drawing) or a hotkey (Hotkeys page: Air drawing).\nTurning it off keeps what you drew; Clear drawing wipes it.");
            loaders.Add(() => { drawOn.SetSilently(core.CamFx != null && core.CamFx.Ink.On); drawOn.Enabled = core.CamFx != null && HandGestures.Installed; });
            drawOn.CheckedChanged += (s, e) => { if (loading || core.CamFx == null) return; core.CamFx.Ink.SetOn(drawOn.Checked); };

            // ---- the pen
            Theme.Help(Theme.Label(card, "Pen", 20, 164, Theme.Body), "The line's colour. \"...\" picks any colour.");
            for (int i = 0; i < PenColors.Length; i++)
            {
                int rgb = PenColors[i];
                var b = Place(card, new FlatButton(""), 130 + i * 32, 160, 26);
                b.BackColor = Color.FromArgb(rgb | unchecked((int)0xFF000000));
                b.FlatAppearance.MouseOverBackColor = b.BackColor; b.FlatAppearance.MouseDownBackColor = b.BackColor;
                b.FlatAppearance.BorderColor = Theme.Text;
                b.Tag = rgb;
                Theme.Help(b, PenNames[i]);
                b.Click += (s, e) => { C.PenColor = rgb; core.SaveSoon(); Load(); };
                swatches.Add(b);
            }
            var more = Place(card, new FlatButton("..."), 130 + PenColors.Length * 32, 160, 36);
            more.Width = Theme.Px(36);
            Theme.Help(more, "Any colour.");
            more.Click += (s, e) =>
            {
                using (var d = new ColorDialog { FullOpen = true, Color = Color.FromArgb(C.PenColor | unchecked((int)0xFF000000)) })
                    if (d.ShowDialog(card.FindForm()) == DialogResult.OK) { C.PenColor = d.Color.ToArgb() & 0xFFFFFF; core.SaveSoon(); Load(); }
            };
            loaders.Add(() => { foreach (var b in swatches) b.FlatAppearance.BorderSize = (int)b.Tag == C.PenColor ? Theme.Px(2) : 0; });
            Slider(card, 390, 164, "Thickness", 4, 48, () => C.PenSize, v => C.PenSize = v, v => v + " px", "How thick the line is, in pixels of the 1080p picture.", 18, 80, 130);
            Slider(card, 20, 206, "Fade after", 0, 60, () => C.InkFadeSeconds, v => C.InkFadeSeconds = v, v => v == 0 ? "never" : v + " s",
                "Each line fades away this long after you drew it. Never: it stays until you clear it.", 0, 110, 180);
            var clear = Place(card, new FlatButton("Clear drawing"), 390, 202, 150);
            Theme.Help(clear, "Wipes everything drawn (hotkey: Clear the drawing).");
            clear.Click += (s, e) => { if (core.CamFx != null) core.CamFx.Ink.Clear(); };

            // ---- animations
            Theme.Help(Theme.Label(card, "Animations", 20, 254, Theme.Bold),
                "Short camera moves apps see, smooth and eased. Each ends exactly where it started - except Flip, which leaves the camera upside down (flip again to turn it back).\nEvery one can be a hotkey (Hotkeys page, Camera).");
            for (int i = 0; i < CameraAnimation.Kinds.Length; i++)
            {
                string k = CameraAnimation.Kinds[i];
                var b = Place(card, new FlatButton(CameraAnimation.Label(k)), 20 + i * 132, 280, 124);
                b.Click += (s, e) => { if (core.CamFx != null && !core.CamFx.Play(k) && k == "flip") { core.SaveSoon(); Load(); } };
            }
            var hint = Theme.Label(card, "Plays while the camera runs (turn on the preview above to watch).", 20, 320, Theme.Small, Theme.Muted, 650);
            hint.UseMnemonic = false;

            timer.Tick += (s, e) => { status.Text = Status(); if (core.CamFx != null && drawOn.Checked != core.CamFx.Ink.On) drawOn.SetSilently(core.CamFx.Ink.On); };
            card.VisibleChanged += (s, e) => { if (card.Visible) { Load(); timer.Start(); } else timer.Stop(); };
            Load();
        }

        public int Bottom { get { return card.Bottom; } }

        public Card Card { get { return card; } }

        /// <summary>What the gestures are doing, or exactly why they can't.</summary>
        string Status()
        {
            if (!HandGestures.Installed) return "The hand models aren't installed - reinstall StreamLite to use gestures and air drawing.";   // (ai\" + PalmFile / LandmarkFile: too long for its line)
            if (!CameraFx.ModelsInstalled) return "The camera's AI files (with its face detector) are missing - reinstall StreamLite; gestures need them to find your face.";
            var fx = core.CamFx;
            if (fx == null) return "";
            var c = C;
            bool drawing = fx.Ink.On;
            if (!c.GesturePalm && !c.GestureV && !c.GestureL && !drawing) return "All gestures are off.";
            if (!fx.Running) return "Gestures work while the camera runs (an app shows StreamLite Camera, or the preview above is on).";
            if (fx.PlainWhy != null) return "Gestures need the camera's AI effects, which aren't running: " + fx.PlainWhy + ".";
            var h = fx.Hands;
            if (h != null && h.Error != null) return "Gestures failed: " + h.Error;
            string speed = h != null && h.LookMs > 0 ? " - " + h.LooksPerSecond.ToString("0") + " looks a second, " + h.LookMs.ToString("0") + " ms each" : "";
            if (drawing)
            {
                if (!c.GesturePoint) return "Air drawing is on, but Point: draw is off.";
                string pen = fx.Ink.PenDown ? "drawing" : h != null && h.Following ? "following your hand - point to draw" : fx.HandsHaveFace ? "looking for your hand beside your face" : "looking for a face";
                return "Air drawing: " + pen + speed;
            }
            if (!fx.HandsHaveFace) return "Looking for a face (hand signs count beside it)" + speed;
            string g = fx.GestureStatus;
            string seen = g == "palm" ? "an open palm" : g == "v" ? "a V" : g == "l" ? "an L - move up / down to zoom" : g == "point" ? "a pointing finger" : g == "fist" ? "a fist" : "";
            return (seen != "" ? "I see " + seen : "Watching for hand signs beside your face") + speed;
        }

        void Sign(int x, string name, string help, Func<bool> get, Action<bool> set)
        {
            var t = Place(card, new ToggleSwitch(), x, 80);
            Theme.Help(Theme.Label(card, name, x + 54, 80, Theme.Bold), help);
            loaders.Add(() => { t.SetSilently(get()); t.Enabled = HandGestures.Installed; });
            t.CheckedChanged += (s, e) => { if (loading) return; set(t.Checked); core.SaveSoon(); };
        }

        void Load()
        {
            loading = true;
            foreach (var l in loaders) l();
            loading = false;
            status.Text = Status();
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
}
