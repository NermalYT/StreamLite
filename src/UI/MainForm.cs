using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Camera;
using StreamLite.Interop;

namespace StreamLite.UI
{
    internal class MainForm : Form
    {
        readonly AppCore core;
        readonly Panel side = new Panel();
        readonly ScrollHost content = new ScrollHost();
        ScrollHost propHost;
        readonly List<NavButton> navs = new List<NavButton>();
        readonly List<Panel> pages = new List<Panel>();
        readonly Label statusLabel;
        readonly Timer uiTimer = new Timer { Interval = 33 };
        public bool AllowClose;

        // Microphone page
        DarkCombo micCombo, outCombo;
        FlatButton tidyNow;
        ToggleSwitch micOn, nrToggle, gateToggle, eqToggle, autoLevelToggle, defaultToggle, monitorToggle, compToggle, essToggle, shareToggle, farToggle;
        Label nrWhat, micTier, studioWhat;
        ToggleSwitch studioToggle;
        FlatSlider studioStrength;
        Label studioVal;
        DarkCombo gateMode, eqPreset, lowCut, deHum;
        FlatSlider strength, threshold, volume;
        Label strengthVal, thresholdVal, volumeVal, micInfo, eqSummary, autoLevelVal;
        FlatButton getCable, volFix, privacyFix;
        LevelMeter inMeter, outMeter;
        Panel micPage;
        MicTuningPanel tuning;
        Label tuningHeader;
        FlatButton tuningToggle, tuningOpen;

        // Output page
        DarkCombo spkCombo;
        ToggleSwitch spkEqToggle;
        EqGraph spkGraph;
        Label apoStatus;
        FlatButton apoGet, apoEnable, apoApply;
        Panel outPage;
        OutputEqPanel outEq;
        Label outEqHeader;
        FlatButton outEqToggle, outEqOpen;

        // Camera page
        DarkCombo camCombo, formatCombo;
        Panel previewHost, propList;
        Label previewMsg, fpsInfo;
        ToggleSwitch previewToggle, lockToggle;
        CameraPreview preview;
        CameraFxCard fxCard;
        CameraDevice camDev;
        readonly List<Action> camRefreshers = new List<Action>();

        // Settings page
        ToggleSwitch startupToggle, minimizedToggle, adminToggle, tidyToggle, formatToggle, updateToggle, duckToggle;
        Label duckInfo;
        Label formatInfo, updateStatus;
        DarkCombo closeCombo;
        Label adminStatus;

        bool loading;
        GameAudioPage gamePage;
        const int CameraPage = 3;
        public event EventHandler ShowRequested;
        const int ResizeBorder = 5;

        public MainForm(AppCore core)
        {
            this.core = core;
            Theme.Init(this);
            Text = Program.WindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.Side;                       // the thin frame used for resizing
            Padding = new Padding(Theme.Px(ResizeBorder));
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(Theme.Px(960 + 2 * ResizeBorder), Theme.Px(740 + 2 * ResizeBorder));
            MinimumSize = new Size(Theme.Px(820), Theme.Px(560));
            StartPosition = FormStartPosition.CenterScreen;
            var wb = core.S.WindowBounds;
            if (wb != null && wb.Length == 4 && wb[2] >= MinimumSize.Width && wb[3] >= MinimumSize.Height)
            {
                var r = new Rectangle(wb[0], wb[1], wb[2], wb[3]);
                // Only if it is still on a connected screen (monitors can change).
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X + 40, r.Y + 10, r.Width - 80, 40))))
                {
                    // Keep the whole window on that screen (not under the taskbar or past an edge).
                    var wa = Screen.FromRectangle(r).WorkingArea;
                    r.Width = Math.Min(r.Width, wa.Width); r.Height = Math.Min(r.Height, wa.Height);
                    r.X = Math.Max(wa.Left, Math.Min(r.X, wa.Right - r.Width)); r.Y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - r.Height));
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                }
            }
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            titleBar = new TitleBar(Program.WindowTitle, true, true, true);
            titleBar.CloseClicked += (s, e) => Program.CloseWindow();
            titleBar.FullScreenClicked += (s, e) => ToggleFullScreen();
            Theme.Help(titleBar.FullScreenButton, "Fullscreen (F11)");
            side.Dock = DockStyle.Left;
            side.Width = Theme.Px(212);
            side.BackColor = Theme.Side;
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Bg;
            content.MaxContentWidth = 1280;
            Controls.Add(content);
            Controls.Add(side);
            Controls.Add(titleBar);

            AddNav("Microphone", "\uE720");
            AddNav("Headphones", "\uE7F6");
            AddNav("3D Audio", "\uE8D6");
            AddNav("Camera", "\uE722");
            AddNav("Hotkeys", "\uE765");
            AddNav("Health", "");
            AddNav("Settings", "\uE713");
            var bugRow = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(94), BackColor = Theme.Side };
            var credits = new NavButton("Credits", "\uE734") { Location = new Point(Theme.Px(14), Theme.Px(4)) };
            credits.Click += (s, e) => { using (var w = new CreditsWindow(ShowLicenses)) w.ShowDialog(this); };
            Theme.Help(credits, "The projects, AI models and data StreamLite is built on, with links to their source - for this version and every earlier release.");
            bugRow.Controls.Add(credits);
            var bug = new NavButton("Report a bug", "\uEBE8") { Location = new Point(Theme.Px(14), Theme.Px(50)) };
            bug.Click += (s, e) => Program.ReportBug();
            Theme.Help(bug, "Opens StreamLite's bug report form on GitHub (free GitHub account needed), with your StreamLite and Windows version already filled in. You can also see known problems there.");
            bugRow.Controls.Add(bug);
            side.Controls.Add(bugRow);
            statusLabel = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Side, Dock = DockStyle.Bottom, Height = Theme.Px(60), Padding = new Padding(Theme.Px(16), 0, Theme.Px(8), Theme.Px(10)), TextAlign = ContentAlignment.BottomLeft };
            side.Controls.Add(statusLabel);

            pages.Add(BuildMicPage());
            pages.Add(BuildOutputPage());
            gamePage = new GameAudioPage(core);
            pages.Add(gamePage);
            pages.Add(BuildCameraPage());
            hotkeysPage = new HotkeysPage(core, () => Hotkeys);
            pages.Add(hotkeysPage);
            healthPage = new HealthPage(core, Fix);
            pages.Add(healthPage);
            pages.Add(BuildSettingsPage());
            foreach (var p in pages) { p.Visible = false; content.Controls.Add(p); Responsive.Apply(p); }
            Application.AddMessageFilter(new WheelRouter());
            ShowPage(0);

            uiTimer.Tick += (s, e) => UiTick();
            core.Cameras.DevicesChanged += (s, e) => { if (Visible) LoadCameras(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX: taskbar click minimizes/restores a borderless window
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.StyleWindow(Handle);
            // Lets a second, non-elevated launch ask an elevated copy to show itself.
            try { Win32.ChangeWindowMessageFilterEx(Handle, Program.ShowMessage, 1, IntPtr.Zero); } catch { }
            try { Win32.ChangeWindowMessageFilterEx(Handle, Program.QuitMessage, 1, IntPtr.Zero); } catch { }
            try { Win32.ChangeWindowMessageFilterEx(Handle, Program.CheckMessage, 1, IntPtr.Zero); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.QuitMessage) { BeginInvoke(new Action(Program.QuitNow)); return; }
            if (m.Msg == Program.CheckMessage) { BeginInvoke(new Action(CheckToFile)); return; }
            if (m.Msg == Program.ShowMessage)
            {
                if (ShowRequested != null) ShowRequested(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            // Borderless window: resize from the thin frame around the content.
            if (m.Msg == 0x84 && m.Result == (IntPtr)1 && WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = Theme.Px(ResizeBorder + 2);
                bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                int hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 1;
                m.Result = (IntPtr)hit;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Track)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// Personalize 3D runs over the whole window: the pages, the side bar and the title bar step aside (nothing
        /// underneath re-lays itself out while the window grows to fill its screen), and come back afterwards.
        /// </summary>
        internal void SetChrome(bool visible)
        {
            Immersive = !visible;
            foreach (Control c in Controls) if (!(c is Personal3DView)) c.Visible = visible;
        }
        internal bool Immersive;

        // ---- fullscreen (title bar button, F11, double-click on the title bar)
        TitleBar titleBar;
        bool fullScreen;
        Rectangle beforeFullScreen;

        /// <summary>The window fills its whole screen (taskbar included), or goes back to the size it had.</summary>
        /// <summary>
        /// A newer StreamLite is out: "Update to X" stays in the title bar on every page (also after Skip), until it's
        /// installed. click: what it does (the update prompt, or the download page).
        /// </summary>
        public void SetUpdateAvailable(string version, Action click)
        {
            titleBar.ShowNote(version == null ? null : "\u2B06  Update to " + version, click);
            if (version != null) Theme.Help(titleBar, "StreamLite " + version + " is out - click \"Update to " + version + "\" to install it.");
        }

        internal void ToggleFullScreen()
        {
            if (Immersive) return;
            if (!fullScreen)
            {
                beforeFullScreen = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                fullScreen = true;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                fullScreen = false;
                Bounds = beforeFullScreen;
            }
            titleBar.FullScreenButton.Glyph = fullScreen ? "\uE73F" : "\uE740";
            Theme.Help(titleBar.FullScreenButton, fullScreen ? "Back to a window (F11)" : "Fullscreen (F11)");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11) { ToggleFullScreen(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void RememberBounds()
        {
            if (WindowState != FormWindowState.Normal || !Visible || Immersive || fullScreen) return;
            var b = Bounds;
            core.S.WindowBounds = new[] { b.X, b.Y, b.Width, b.Height };
            core.SaveSoon();
        }

        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); RememberBounds(); }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible && core.S.WindowBounds == null) { }
            base.OnVisibleChanged(e);
            if (Visible) { LoadAll(); uiTimer.Start(); }
            else { uiTimer.Stop(); StopPreview(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                BeginInvoke(new Action(Program.CloseWindow));
                return;
            }
            StopPreview();
            base.OnFormClosing(e);
        }

        void AddNav(string text, string glyph)
        {
            var b = new NavButton(text, glyph) { Location = new Point(Theme.Px(14), Theme.Px(14 + navs.Count * 46)) };
            int idx = navs.Count;
            b.Click += (s, e) => ShowPage(idx);
            navs.Add(b);
            side.Controls.Add(b);
        }

        void ShowPage(int idx)
        {
            for (int i = 0; i < navs.Count; i++) { navs[i].Selected = i == idx; navs[i].Invalidate(); }
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == idx;
            if (idx != CameraPage) { StopPreview(); if (fxCard != null) fxCard.Hidden(); }
            content.Content = pages[idx];
        }

        Panel NewPage(string title)
        {
            var p = new Panel { Location = new Point(Theme.Px(28), Theme.Px(18)), Size = new Size(Theme.Px(690), Theme.Px(660)), BackColor = Theme.Bg };
            Theme.Label(p, title, 0, 0, Theme.Title);
            return p;
        }

        Card NewCard(Panel page, int y, int h, int x = 0, int w = 690)
        {
            var c = new Card { Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(w), Theme.Px(h)) };
            page.Controls.Add(c);
            return c;
        }

        static T Place<T>(Control parent, T c, int x, int y, int w = 0) where T : Control
        {
            c.Location = new Point(Theme.Px(x), Theme.Px(y));
            if (w > 0) c.Width = Theme.Px(w);
            parent.Controls.Add(c);
            return c;
        }

        // ------------------------------------------------------------------ Microphone

        Panel BuildMicPage()
        {
            var page = NewPage("Microphone");
            micPage = page;
            Theme.Help(Theme.Label(page, "Virtual mic", 560, 10, Theme.Bold, Theme.Muted), "Turns the whole cleaned-up microphone on or off. When on, your mic is processed here and sent to the virtual cable that Discord uses.");
            micOn = Place(page, new ToggleSwitch(), 640, 8);
            micOn.CheckedChanged += (s, e) => { if (loading) return; core.S.VirtualMicOn = micOn.Checked; core.RestartAudio(); RefreshMicInfo(); };

            var dev = NewCard(page, 54, 150);
            micsCard = dev;
            Theme.Help(Theme.Label(dev, "Microphones", 20, 16, Theme.Section),
                "The real microphones StreamLite listens to and cleans up: USB mics, headset mics, audio interfaces, webcam mics...\nMicrophone 1 is your main mic. Add more for two (or more) people on one PC: Discord, OBS or any app then hears everyone through \"" + AppCore.MicName + "\", each person on their own mic. Every mic gets the same noise removal, EQ and auto level.\nUnplugged mics wait and join again when plugged back in. Added mics stay shared, so other apps can still use them too.");
            micCombo = Place(dev, new DarkCombo(), 50, 50, 290);
            micCombo.DropDown += (s, e) => FillAudioCombo(micCombo, core.MicChoices(), core.ActiveMicId);
            micCombo.SelectionChangeCommitted += (s, e) =>
            {
                var d = micCombo.SelectedItem as AudioDeviceInfo; if (d == null) return;
                core.S.MicId = d.Id;
                core.S.ExtraMics.RemoveAll(m => m.Id == d.Id);   // it's the main mic now
                core.RestartAudio(); RefreshMicInfo(); BuildMicRows();
            };
            sendLabel = Theme.Label(dev, "Send clean audio to", 20, 60, Theme.Bold);
            Theme.Help(sendLabel, "Where the processed voice goes: the internal \"" + AppCore.MicFeedName + "\" device. In Discord, Zoom, OBS or games pick \"" + AppCore.MicName + "\" as your mic.\nIt is written straight into the cable driver (the Mic Feed is hidden from every app), so Discord screen-share audio can't pick up your voice.\nOnly virtual cables are listed: sending your voice to speakers or to StreamLite - Out (game audio) would break the mic.");
            outCombo = Place(dev, new DarkCombo(), 230, 56, 430);
            outCombo.DropDown += (s, e) => FillAudioCombo(outCombo, core.MicCableChoices(), core.ActiveOutId);
            outCombo.SelectionChangeCommitted += (s, e) => { var d = outCombo.SelectedItem as AudioDeviceInfo; if (d == null) return; core.S.VirtualOutId = d.Id; core.RestartAudio(); RefreshMicInfo(); };
            micInfo = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(96)), Size = new Size(Theme.Px(480), Theme.Px(52)) };
            dev.Controls.Add(micInfo);
            getCable = Place(dev, new FlatButton("Install VB-CABLE (free)", true), 510, 104);
            Theme.Help(getCable, "Downloads VB-CABLE (the free virtual audio cable from VB-Audio) from vb-audio.com and installs it - Windows asks for permission once. StreamLite sends your clean mic through it; it shows up as \"" + AppCore.MicName + "\" in Discord and every other app.");
            getCable.Click += (s, e) => InstallVbCable();
            volFix = Place(dev, new FlatButton("Set its volume to 100"), 510, 104);
            volFix.Visible = false;
            Theme.Help(volFix, "Windows has a volume slider on " + AppCore.MicName + " too, applied after StreamLite's Auto level. At 100 apps get exactly the level StreamLite sends; change your loudness with Auto level (Tuning > Output) instead.");
            volFix.Click += (s, e) => { var c = core.PairedCapture(core.ActiveOutId); if (c != null) AudioDevices.SetVolume(c.Id, 1f); RefreshMicInfo(); };
            privacyFix = Place(dev, new FlatButton("Open privacy settings", true), 510, 104);
            privacyFix.Visible = false;
            Theme.Help(privacyFix, "Opens Windows Settings > Privacy & security > Microphone. Turn on \"Microphone access\" and \"Let desktop apps access your microphone\", then StreamLite hears you within a few seconds.");
            privacyFix.Click += (s, e) => DeviceErrors.OpenMicPrivacy();

            var fx = NewCard(page, 218, 388);
            nrToggle = Place(fx, new ToggleSwitch(), 20, 22);
            nrWhat = Theme.Label(fx, "", 200, 24, Theme.Small, Theme.Muted, 180);
            Theme.Help(nrWhat, NoiseEngines.Help);
            Theme.Help(Theme.Label(fx, "Noise removal", 74, 22, Theme.Bold), "AI voice isolation, built in: a neural network keeps your voice and removes keyboards, fans, PC hum, AC, wind, TV and room noise. It always runs the best noise remover your AI quality allows (Settings). Fine-tune it in Tuning & EQ > Noise removal.");
            nrToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.NoiseRemoval = nrToggle.Checked; strength.Enabled = nrToggle.Checked; core.PushEngineParams(); };
            Theme.Help(Theme.Label(fx, "Strength", 386, 22, Theme.Small, Theme.Muted), "How hard noise is removed. 50% = stock AI voice isolation; 60-80% removes more in your pauses without touching your voice; lower is gentler.");
            strength = Place(fx, new FlatSlider { Minimum = 0, Maximum = 100, DefaultValue = 70 }, 450, 20, 150);
            strengthVal = Theme.Label(fx, "", 610, 22, Theme.Body, Theme.Text, 60);
            strength.ValueChanged += (s, e) => { strengthVal.Text = strength.Value + "%"; if (loading) return; core.S.Strength = strength.Value; core.PushEngineParams(); };

            gateToggle = Place(fx, new ToggleSwitch(), 20, 70);
            Theme.Help(Theme.Label(fx, "Noise gate", 74, 70, Theme.Bold), "Turns your mic down between words so leftover room noise and keyboard clicks are not heard. Uses the smooth expander mode by default.");
            gateToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.Gate = gateToggle.Checked; threshold.Enabled = gateToggle.Checked; core.PushEngineParams(); };
            gateMode = Place(fx, new DarkCombo(), 200, 66, 170);
            gateMode.Items.AddRange(new object[] { "Smooth (expander)", "Hard gate" });
            Theme.Help(gateMode, "Smooth: quiet sounds are turned down gradually (natural, no choppy word endings).\nHard gate: fully mutes below the threshold (silent between words, can clip soft syllables).");
            gateMode.SelectionChangeCommitted += (s, e) => { core.S.GateExpander = gateMode.SelectedIndex == 0; core.PushEngineParams(); if (tuning.Visible) tuning.Reload(); };
            Theme.Help(Theme.Label(fx, "Threshold", 386, 70, Theme.Small, Theme.Muted), "Sounds quieter than this are turned down. Set it just above your background noise and below your voice (the white line on the meter).");
            threshold = Place(fx, new FlatSlider { Minimum = -80, Maximum = -10, DefaultValue = -50 }, 450, 68, 150);
            thresholdVal = Theme.Label(fx, "", 610, 70, Theme.Body, Theme.Text, 70);
            threshold.ValueChanged += (s, e) => { thresholdVal.Text = threshold.Value + " dB"; if (loading) return; core.S.GateThreshold = threshold.Value; core.PushEngineParams(); };
            Theme.Help(Theme.Label(fx, "Mic level", 74, 104, Theme.Small, Theme.Muted), "Live level of your mic after noise removal. The white line is the gate threshold; grey bar = gate is turning you down.");
            inMeter = Place(fx, new LevelMeter(), 140, 110, 530);

            eqToggle = Place(fx, new ToggleSwitch(), 20, 150);
            Theme.Help(Theme.Label(fx, "Equalizer", 74, 150, Theme.Bold), "Shapes the tone of your voice (warmer, clearer, less boomy). Edit it in Tuning & EQ: 10-band, 31-point or parametric.");
            eqToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.MicEq.Enabled = eqToggle.Checked; core.PushEngineParams(); RefreshEqSummary(); if (tuning.Visible) tuning.Reload(); };
            eqPreset = Place(fx, new DarkCombo(), 200, 146, 170);
            eqPreset.Items.Add("Presets...");
            foreach (var k in EqEditor.MicPresets().Keys) eqPreset.Items.Add(k);
            Theme.Help(eqPreset, "Quick voice EQ curves. Picking one turns the equalizer on; fine-tune in Tuning & EQ.");
            eqPreset.SelectionChangeCommitted += (s, e) =>
            {
                if (eqPreset.SelectedIndex <= 0) return;
                VoiceStyles.RememberMine(core.S);   // "My settings" can bring your EQ back
                var p = core.S.MicEq;
                p.Bands = (double[])EqEditor.MicPresets()[(string)eqPreset.SelectedItem].Clone();
                p.Mode = "graphic"; p.Enabled = true;
                core.PushEngineParams(); RefreshEqSummary();
                loading = true; eqToggle.Checked = true; loading = false;
                eqPreset.SelectedIndex = 0;
                if (tuning.Visible) tuning.Reload();
            };
            eqSummary = Theme.Label(fx, "", 386, 152, Theme.Small, Theme.Muted, 150);
            tuningOpen = Place(fx, new FlatButton("Tuning && EQ"), 540, 145);
            tuningOpen.Width = Theme.Px(130);
            tuningOpen.Click += (s, e) => SetMicTuning(true);

            compToggle = Place(fx, new ToggleSwitch(), 20, 196);
            Theme.Help(Theme.Label(fx, "Voice compressor", 74, 196, Theme.Bold), "Evens out loud and quiet words so you are always easy to understand. Settings in Tuning & EQ > Voice.");
            compToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.Compressor = compToggle.Checked; core.PushEngineParams(); };
            essToggle = Place(fx, new ToggleSwitch(), 386, 196);
            Theme.Help(Theme.Label(fx, "De-esser", 440, 196, Theme.Bold), "Softens sharp \"s\" and \"sh\" sounds that can hiss through headsets.");
            essToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.DeEsser = essToggle.Checked; core.PushEngineParams(); };
            Theme.Help(Theme.Label(fx, "Low cut", 74, 240, Theme.Bold), "Removes rumble below this pitch: desk bumps, keyboard thumps, traffic, mic stand noise. 80 Hz suits most voices.");
            lowCut = Place(fx, new DarkCombo(), 200, 236, 170);
            lowCut.Items.AddRange(new object[] { "Off", "60 Hz", "80 Hz", "100 Hz", "120 Hz", "150 Hz" });
            lowCut.SelectionChangeCommitted += (s, e) => { int[] v = { 0, 60, 80, 100, 120, 150 }; core.S.HighPassHz = v[lowCut.SelectedIndex]; core.PushEngineParams(); };
            Theme.Help(Theme.Label(fx, "De-hum", 440, 240, Theme.Bold), "Removes electrical mains hum: 60 Hz in the US/Canada, 50 Hz in Europe and most other countries.");
            deHum = Place(fx, new DarkCombo(), 510, 236, 90);
            deHum.Items.AddRange(new object[] { "Off", "50 Hz", "60 Hz" });
            deHum.SelectionChangeCommitted += (s, e) => { core.S.DeHumHz = deHum.SelectedIndex == 1 ? 50 : deHum.SelectedIndex == 2 ? 60 : 0; core.PushEngineParams(); };
            Theme.Help(Theme.Label(fx, "AI quality", 74, 280, Theme.Bold), "Where voice isolation and the AI camera run and how big their models are: Low VRAM, Normal, High VRAM or Full CPU. Set in Settings.");
            micTier = Theme.Label(fx, "", 200, 282, Theme.Small, Theme.Muted, 330);
            var tierGo = Place(fx, new FlatButton("Change..."), 560, 276, 110);
            tierGo.Click += (s, e) => { ShowPage(navs.Count - 1); ScrollTo(pages[pages.Count - 1], aiCard); };

            // Studio voice: any mic made to sound like a broadcast mic.
            studioToggle = Place(fx, new ToggleSwitch(), 20, 322);
            Theme.Help(Theme.Label(fx, "Studio voice", 74, 322, Theme.Bold),
                "Makes any mic sound like a broadcast mic. While you talk it learns how your voice comes through this mic and compares it with speech through a good close mic (the standard speech spectrum, with a broadcast mic's warmth, presence and air):\n" +
                "- the mic's colouring is taken out - thin, boxy, harsh, muffled (at most -10 / +8 dB, smooth)\n" +
                "- treble a cheap mic cuts off (headsets, laptops, Bluetooth: 7-13 kHz) is rebuilt from the octave below, following every syllable\n" +
                "- plosive pops (\"p\", \"b\" into the mic) are caught and their rumble taken out\n" +
                "Learned in about 5 seconds of talking and kept per mic. Adds no delay.");
            studioToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.StudioVoice = studioToggle.Checked; studioStrength.Enabled = studioToggle.Checked; core.PushEngineParams(); core.SaveSoon(); };
            Theme.Help(Theme.Label(fx, "Strength", 386, 322, Theme.Small, Theme.Muted), "How far your mic is moved towards the broadcast sound (and how much treble is rebuilt). 75% suits most mics; 100% = all the way.");
            studioStrength = Place(fx, new FlatSlider { Minimum = 0, Maximum = 100, DefaultValue = 75 }, 450, 320, 150);
            studioVal = Theme.Label(fx, "", 610, 322, Theme.Body, Theme.Text, 60);
            studioStrength.ValueChanged += (s, e) => { studioVal.Text = studioStrength.Value + "%"; if (loading) return; core.S.StudioStrength = studioStrength.Value; core.PushEngineParams(); core.SaveSoon(); };
            studioWhat = Theme.Label(fx, "", 74, 352, Theme.Small, Theme.Muted, 470);
            var relearn = Place(fx, new FlatButton("Learn again"), 560, 346, 110);
            Theme.Help(relearn, "Forgets what Studio voice learned about this mic and learns it again from your next few seconds of talking (after moving the mic or changing rooms).");
            relearn.Click += (s, e) => core.RelearnStudioVoice();

            BuildVoiceTestCard(page, 620);
            var outCard = NewCard(page, 834, 278);
            Theme.Help(Theme.Label(outCard, "Volume", 20, 22, Theme.Bold), "A fixed boost or cut applied after all effects.");
            volume = Place(outCard, new FlatSlider { Minimum = -20, Maximum = 30, DefaultValue = 0 }, 140, 20, 460);
            volumeVal = Theme.Label(outCard, "", 610, 22, Theme.Body, Theme.Text, 70);
            volume.ValueChanged += (s, e) => { volumeVal.Text = (volume.Value > 0 ? "+" : "") + volume.Value + " dB"; if (loading) return; core.S.VolumeDb = volume.Value; core.PushEngineParams(); };
            Theme.Help(Theme.Label(outCard, "Output level", 20, 58, Theme.Small, Theme.Muted), "Live level of what Discord receives. Yellow/red means very loud; the limiter stops it from distorting.");
            outMeter = Place(outCard, new LevelMeter(), 140, 64, 530);
            autoLevelToggle = Place(outCard, new ToggleSwitch(), 20, 94);
            Theme.Help(Theme.Label(outCard, "Auto level", 74, 94, Theme.Bold), "Automatically lifts a quiet voice to a steady loudness while you talk, without boosting silence.");
            autoLevelVal = Theme.Label(outCard, "", 160, 96, Theme.Small, Theme.Muted, 500);
            autoLevelToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.AutoLevel = autoLevelToggle.Checked; core.PushEngineParams(); };
            defaultToggle = Place(outCard, new ToggleSwitch(), 20, 130);
            Theme.Help(Theme.Label(outCard, "Make it the Windows default microphone (restored when the app exits)", 74, 130), "Makes " + AppCore.MicName + " the default mic in Windows, so apps set to Default use your cleaned-up voice. Your old default comes back when the app exits.");
            defaultToggle.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                core.S.MakeDefaultMic = defaultToggle.Checked;
                if (defaultToggle.Checked) core.ApplyDefaultMic(); else core.RestoreDefaultMic();
                core.SaveSoon();
            };
            monitorToggle = Place(outCard, new ToggleSwitch(), 20, 166);
            Theme.Help(Theme.Label(outCard, "Hear myself (plays the cleaned mic on your default speakers)", 74, 166), "Plays your processed voice in your headphones so you can hear exactly what your friends hear.");
            monitorToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.Monitor = monitorToggle.Checked; core.RestartAudio(); };
            shareToggle = Place(outCard, new ToggleSwitch(), 20, 202);
            Theme.Help(Theme.Label(outCard, "Let other apps use the raw mic too", 74, 202),
                "On: Discord, OBS or a game can still pick your real microphone directly (without StreamLite's cleanup) while StreamLite runs. StreamLite then gets it in Windows' 10 ms packets.\nOff: StreamLite holds the mic exclusively in its smallest packets (about 7 ms less delay) - other apps get silence or an error from the raw mic until StreamLite closes.");
            Theme.Label(outCard, "Off = StreamLite holds it alone (about 7 ms less delay)", 330, 204, Theme.Small, Theme.Muted, 350);
            shareToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.ShareMic = shareToggle.Checked; core.SaveSoon(); core.RestartAudio(); };
            farToggle = Place(outCard, new ToggleSwitch(), 20, 238);
            Theme.Help(Theme.Label(outCard, "Across the room (I talk from further away)", 74, 238),
                "For talking from your bed or the sofa with the mic on the desk: a far voice reaches the mic about 20 dB quieter, so auto level may lift it up to 40 dB (following your voice's average, not its loudest syllables) and the treble distance takes off is given back. Noise removal keeps fans and the room out while it does.\nLeave it off if you always talk right at your mic.");
            Theme.Label(outCard, "Off = you talk right at your mic", 380, 240, Theme.Small, Theme.Muted, 300);
            farToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.FarVoice = farToggle.Checked; core.PushEngineParams(); core.SaveSoon(); };

            // Collapsed "Tuning & EQ" section: stays in this window, opens below when asked.
            tuningHeader = Theme.Label(page, "Tuning & EQ", 0, 1134, Theme.Section);
            Theme.Label(page, "EQ, noise removal, gate", 120, 1138, Theme.Small, Theme.Muted);
            var tuneVoice = Place(page, new FlatButton("Tune to my voice", true), 262, 1128, 150);
            Theme.Help(tuneVoice, "Five short recordings (right at your mic, further away, loud, quiet, your silly voice), about a minute: StreamLite then sets itself up for your voice and learns it for Only my voice.");
            tuneVoice.Click += (s, e) => OpenVoiceSetup();
            tuningToggle = Place(page, new FlatButton("Show  \u25BE"), 580, 1128);
            var resetVoice = Place(page, new FlatButton("Reset voice settings"), 420, 1128, 150);
            Theme.Help(resetVoice, "Every voice setting back to how StreamLite starts out (noise removal tuning, gate, EQs, compressor, levels, Studio voice; Across the room, Remove a specific sound and Only my voice off). Your mics, AI quality and anything recorded or learned stay.");
            resetVoice.Click += (s, e) =>
            {
                using (var d = new ConfirmDialog("Reset voice settings", "Put every voice setting back to StreamLite's defaults?\n\nNoise removal, gate, EQ, compressor, levels and Studio voice go back to how a new install starts; Across the room, Remove a specific sound and Only my voice are switched off. Your mics, AI quality and anything you recorded or learned stay (switch them back on any time).", "Reset", "Cancel"))
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                core.ResetVoiceSettings();
                LoadMic();
            };
            tuningToggle.Width = Theme.Px(110);
            tuningToggle.Click += (s, e) => SetMicTuning(!tuning.Visible);
            tuning = new MicTuningPanel(core) { Location = new Point(0, Theme.Px(1174)), Visible = false };
            tuning.Changed += (s, e) => RefreshMicValues();
            tuning.SizeChanged += (s, e) => FitMicPage();   // tabs have different heights
            page.Controls.Add(tuning);
            page.Height = Theme.Px(1136);
            BuildMicsCard(page);   // last: it moves everything below it as it grows
            return page;
        }

        FlatButton testRec, testPlay, useCompared;
        ToggleSwitch testOrig, matchToggle;
        DarkCombo compareCombo;
        List<string> compareKeys;
        Label matchInfo;
        Label testInfo, styleInfo;
        LevelMeter testMeter;
        DarkCombo styleCombo;
        string testWarn;
        DateTime testWarnUntil;

        /// <summary>What voice isolation runs now, and the AI quality, on the Microphone page.</summary>
        void ShowVoiceIsolation()
        {
            if (nrWhat == null) return;
            string w = core.VoiceIsolationText();
            w = w.Replace("DeepFilterNet 3 on the graphics card", "DeepFilterNet 3 (GPU)").Replace("DeepFilterNet 3 on the CPU", "DeepFilterNet 3 (CPU)");
            if (nrWhat.Text != w) nrWhat.Text = w;
            string tier = AiTiers.Label(core.S.AiTier ?? AiTiers.Normal) + " - " + AiTiers.Budget(core.S.AiTier ?? AiTiers.Normal);
            if (micTier.Text != tier) micTier.Text = tier;
            string st = core.Engine.Running ? core.Engine.Fx.Studio.Describe() : core.S.StudioVoice ? "Starts with the virtual mic" : "Off";
            if (studioWhat.Text != st) studioWhat.Text = st;
        }

        /// <summary>Record your raw voice, then hear it looped through your current settings while you change them.</summary>
        void BuildVoiceTestCard(Panel page, int y)
        {
            var c = NewCard(page, y, 200);
            // Compare noise removal on the same recording, at matched loudness.
            Theme.Help(Theme.Label(c, "Compare", 20, 132, Theme.Bold),
                "Plays your recording without noise removal - only the recording; your live mic keeps its settings until you click \"Use this one\". Switch while it plays: the same sentence, back to back.\nWith \"Same loudness\" on, every version (and Original) plays at your recording's loudness, so you judge the sound, not the volume - louder always seems better.");
            compareCombo = Place(c, new DarkCombo(), 130, 128, 210);
            compareKeys = new List<string> { null, "off" };
            foreach (var k in compareKeys) compareCombo.Items.Add(k == null ? "Your settings" : k == "off" ? "No noise removal" : NoiseEngines.Label(k, false));
            compareCombo.SelectedIndex = 0;
            compareCombo.SelectionChangeCommitted += (s, e) => { core.VoiceTest.Compare(compareKeys[compareCombo.SelectedIndex]); useCompared.Enabled = compareCombo.SelectedIndex > 0; };
            useCompared = Place(c, new FlatButton("Use this one"), 350, 126, 120);
            useCompared.Enabled = false;
            Theme.Help(useCompared, "Makes the noise remover you're comparing your live mic's (Microphone page > Noise removal).");
            useCompared.Click += (s, e) =>
            {
                string k = compareKeys[compareCombo.SelectedIndex];
                if (k == null) return;
                if (k == "off") core.S.NoiseRemoval = false; else { core.S.NoiseRemoval = true; core.S.NsEngine = k; }
                core.VoiceTest.Compare(null);
                compareCombo.SelectedIndex = 0; useCompared.Enabled = false;
                core.PushEngineParams(); LoadMic();
            };
            matchToggle = Place(c, new ToggleSwitch(), 494, 131);
            matchToggle.Checked = true;
            var matchLabel = Theme.Label(c, "Same loudness", 548, 132, Theme.Body);
            Theme.Help(matchLabel, "Plays every version at your recording's loudness (auto level and volume are evened out for the comparison only).");
            matchToggle.CheckedChanged += (s, e) => core.VoiceTest.MatchLoudness = matchToggle.Checked;
            matchInfo = Theme.Label(c, "", 130, 166, Theme.Small, Theme.Muted, 540);
            Theme.Help(Theme.Label(c, "Test your voice", 20, 14, Theme.Bold),
                "Record up to 15 seconds of your raw mic, then play it back through your current settings on your headphones - in a loop. Change noise removal, gate, EQ, compressor... while it plays and you hear the difference immediately, exactly as your friends would.\nThe recording stays on this PC and is never sent anywhere.");
            testRec = Place(c, new FlatButton("●  Record"), 20, 38, 120);
            Theme.Help(testRec, "Records your raw microphone (before any effects). Talk normally - include some quiet moments so you can judge noise removal. Click again to stop (15 s max).");
            testRec.Click += (s, e) =>
            {
                var vt = core.VoiceTest;
                if (vt.Recording)
                {
                    vt.StopRecording();
                    if (vt.HasClip) vt.Play(core.GamePhonesId ?? core.RealDefaultOutput());   // straight to listening
                }
                else if (!core.Engine.Status.StartsWith("Running"))
                {
                    testWarn = core.S.VirtualMicOn ? "Your mic isn't running yet (" + core.Engine.Status + ")" : "Turn on Virtual mic (top right) to record";
                    testWarnUntil = DateTime.Now.AddSeconds(6);
                }
                else vt.StartRecording();
                RefreshVoiceTest();
            };
            testPlay = Place(c, new FlatButton("▶  Play with my settings"), 150, 38, 190);
            Theme.Help(testPlay, "Loops the recording through your current settings on your headphones. Adjust anything while it plays.");
            testPlay.Click += (s, e) =>
            {
                var vt = core.VoiceTest;
                if (vt.Playing) vt.StopPlaying();
                else vt.Play(core.GamePhonesId ?? core.RealDefaultOutput());
                RefreshVoiceTest();
            };
            // Caption, toggle and meter stay together left to right whatever the window width.
            var origLabel = Theme.Label(c, "Original", 356, 42, Theme.Body);
            Theme.Help(origLabel, "On = hear the recording untouched (raw mic), off = with your settings. Flip it while playing to compare.");
            testOrig = Place(c, new ToggleSwitch(), 420, 41);
            Responsive.Fixed(origLabel); Responsive.Fixed(testOrig);
            testOrig.CheckedChanged += (s, e) => { core.VoiceTest.Original = testOrig.Checked; };
            testInfo = Theme.Label(c, "", 160, 15, Theme.Small, Theme.Muted, 510);
            testMeter = Place(c, new LevelMeter(), 486, 46, 184);

            Theme.Help(Theme.Label(c, "Voice style", 20, 86, Theme.Bold),
                "One click sets the whole chain - noise removal, gate, low cut, EQ, compressor, de-esser and auto level - for a sound. Your volume isn't changed.\nTry them while your recording plays. Your own tuning is saved the first time you pick a style: choose \"" + VoiceStyles.Mine + "\" to get it back.");
            styleCombo = Place(c, new DarkCombo(), 130, 82, 210);
            styleCombo.Items.Add(VoiceStyles.Mine);
            foreach (var st in VoiceStyles.All) styleCombo.Items.Add(st.Name);
            styleCombo.SelectionChangeCommitted += (s, e) =>
            {
                string name = (string)styleCombo.SelectedItem;
                if (name == VoiceStyles.Mine) { if (!VoiceStyles.RestoreMine(core.S)) { RefreshStyle(); return; } }
                else VoiceStyles.Apply(core.S, name);
                core.PushEngineParams();
                RefreshMicValues();
                if (tuning.Visible) tuning.Reload();
            };
            styleInfo = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, AutoEllipsis = true,
                Location = new Point(Theme.Px(356), Theme.Px(78)), Size = new Size(Theme.Px(314), Theme.Px(36)) };
            c.Controls.Add(styleInfo);
            core.ParamsChanged += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(RefreshStyle)); };
        }

        /// <summary>Re-reads the voice controls after a change made elsewhere (tray menu).</summary>
        public void ReloadVoice()
        {
            RefreshMicValues();
            if (tuning.Visible) tuning.Reload();
        }

        /// <summary>Shows which voice style the current settings are (or "My settings" once you've tweaked anything).</summary>
        void RefreshStyle()
        {
            if (styleCombo == null || styleCombo.DroppedDown) return;
            string m = VoiceStyles.Match(core.S);
            int idx = m == null ? 0 : styleCombo.Items.IndexOf(m);
            if (styleCombo.SelectedIndex != idx) styleCombo.SelectedIndex = idx;
            var st = m != null ? VoiceStyles.Find(m) : null;
            styleInfo.Text = st != null ? st.Description
                : "Your own tuning. Pick a style to try it - your tuning is saved and comes back here.";
        }

        void RefreshVoiceTest()
        {
            if (testRec == null) return;
            var vt = core.VoiceTest;
            testRec.Text = vt.Recording ? "■  Stop" : "●  Record";
            testRec.BackColor = vt.Recording ? Theme.Danger : Theme.Field;
            testRec.FlatAppearance.MouseOverBackColor = vt.Recording ? ControlPaint.Light(Theme.Danger, 0.2f) : Theme.FieldHover;   // stays red under the mouse
            testPlay.Enabled = vt.HasClip && !vt.Recording;
            testPlay.Text = vt.Playing ? "■  Stop playback" : "▶  Play with my settings";
            if (testWarn != null && DateTime.Now < testWarnUntil) testInfo.Text = testWarn;
            else if (vt.Recording) testInfo.Text = "Recording... " + vt.RecordedSeconds.ToString("0.0") + " s of " + VoiceTest.MaxSeconds + " s - talk normally";
            else if (vt.Error != null) testInfo.Text = "Playback problem: " + vt.Error;
            else if (vt.Playing) testInfo.Text = (vt.Original ? "Original" : "With your settings") + "  -  " + vt.PlaySeconds.ToString("0.0") + " / " + vt.ClipSeconds.ToString("0.0") + " s (looping)";
            else if (vt.HasClip) testInfo.Text = vt.ClipSeconds.ToString("0.0") + " s recorded - press Play, then tweak your settings";
            else testInfo.Text = "Record a few sentences to hear your settings on your own voice";
            testMeter.Level = vt.Playing ? vt.Level : vt.Recording ? core.Engine.InputLevelDb : -100f;
            matchInfo.Text = !vt.HasClip ? "Record first - your recording is kept on this PC so you can compare again later."
                : vt.Playing && !vt.Original && vt.MatchLoudness ? "Same loudness: this version plays " + (vt.MatchDb >= 0 ? "+" : "") + vt.MatchDb.ToString("0.0") + " dB to match your recording."
                : vt.Playing ? "" : "Press Play, then switch versions while it loops.";
        }

        void SetMicTuning(bool open)
        {
            if (open) tuning.Reload();
            tuning.Visible = open;
            tuningToggle.Text = open ? "Hide  \u25B4" : "Show  \u25BE";
            FitMicPage();
            if (open) ScrollTo(micPage, tuningHeader);
        }

        void FitMicPage()
        {
            if (micPage == null || tuning == null) return;
            bool open = tuningToggle.Text.StartsWith("Hide");
            micPage.Height = open ? tuning.Bottom + Theme.Px(20) : tuning.Top - Theme.Px(2);
        }

        // ---- Microphones: 1 = your main mic, 2, 3... = mics you added (two people, one PC)

        Card micsCard;
        Label sendLabel, autoMixLabel;
        ToggleSwitch autoMixToggle;
        FlatButton calibrate;
        DarkCombo addMic;
        readonly List<Control> micRows = new List<Control>();
        readonly List<Action> micRowTicks = new List<Action>();

        void BuildMicsCard(Panel page)
        {
            autoMixToggle = Place(micsCard, new ToggleSwitch(), 470, 14);
            autoMixLabel = Theme.Label(micsCard, "Auto mix", 524, 15, Theme.Bold);
            Theme.Help(autoMixLabel, "With more than one mic: turns each mic up while its person talks and the others down (they hear that person too, a desk away - mixed in, that sounds hollow and doubled). Both people can still talk at once, and the background noise of several open mics doesn't add up.\nOff = every mic always at its own volume.");
            Responsive.Fixed(autoMixLabel); Responsive.Fixed(autoMixToggle);
            autoMixToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.MicAutoMix = autoMixToggle.Checked; core.PushEngineParams(); };
            calibrate = Place(micsCard, new FlatButton("Calibrate..."), 330, 10, 124);
            Responsive.Fixed(calibrate);
            Theme.Help(calibrate, "A 20-second test: everyone is quiet for 3 seconds, then each person talks on their own mic in turn. StreamLite measures how much each mic hears the others and sets the volumes and the auto mix so one voice isn't heard twice, while two people talking at once both stay in. You see the result first.");
            calibrate.Click += (s, e) =>
            {
                using (var d = new MicCalibrationDialog(core))
                    if (d.ShowDialog(this) == DialogResult.OK) BuildMicRows();
            };
            addMic = new DarkCombo();
            micsCard.Controls.Add(addMic);
            Theme.Help(addMic, "Adds another microphone as the next number. Discord and every other app keep using \"" + AppCore.MicName + "\" - it now carries both (all) mics.");
            addMic.DropDown += (s, e) =>
            {
                addMic.BeginUpdate();
                addMic.Items.Clear();
                addMic.Items.Add("+  Add microphone " + (core.S.ExtraMics.Count + 2) + "...");
                foreach (var d in core.ExtraMicChoices()) addMic.Items.Add(d);
                if (addMic.Items.Count == 1) addMic.Items.Add("(no other microphone found - plug one in)");
                addMic.SelectedIndex = 0;
                addMic.EndUpdate();
            };
            addMic.SelectionChangeCommitted += (s, e) =>
            {
                var d = addMic.SelectedItem as AudioDeviceInfo;
                if (d != null) { core.AddExtraMic(d); BuildMicRows(); }
                else addMic.SelectedIndex = 0;
            };
            BuildMicRows();
        }

        /// <summary>
        /// One numbered row per registered mic: 1 = the main mic (micCombo), then each added one, each with its own
        /// device menu, live level, volume trim and Remove. Then "+ Add microphone", then where the clean mic goes.
        /// </summary>
        void BuildMicRows()
        {
            if (micsCard == null || addMic == null) return;
            bool was = loading; loading = true;
            try { BuildMicRowsInner(); } finally { loading = was; }
        }

        void BuildMicRowsInner()
        {
            foreach (var c in micRows) { micsCard.Controls.Remove(c); c.Dispose(); }
            micRows.Clear(); micRowTicks.Clear();
            var S = core.S;
            bool several = S.ExtraMics.Count > 0;
            autoMixToggle.Visible = autoMixLabel.Visible = calibrate.Visible = several;
            calibrate.Text = S.MicBleed.Count > 0 ? "Calibrated ✓" : "Calibrate...";
            int y = 50;
            // 1: the main mic.
            micCombo.Location = new Point(Theme.Px(50), Theme.Px(y));
            micCombo.Width = Theme.Px(260);
            MicRow(y, 1, several ? (Func<int>)(() => S.MainMicGainDb) : null, v => { S.MainMicGainDb = v; core.PushEngineParams(); },
                () => core.Engine.InputLevelDb, () => core.Engine.Mixer.MainShare, () => core.Engine.Muted ? "muted (hotkey)" : core.Engine.PttClosed ? "push to talk" : core.Engine.Mixer.MainMuted ? "muted" : core.Engine.Running ? null : core.Engine.Status,
                several ? (Action)(() =>
                {
                    // The main mic goes: microphone 2 moves up to 1.
                    var next = S.ExtraMics[0];
                    S.ExtraMics.RemoveAt(0);
                    S.MicId = next.Id; S.MainMicGainDb = next.GainDb;
                    S.MainMicEq = next.Eq ?? new EqProfile(); core.RestartAudio(); LoadMic();
                }) : null, several ? S.MainMicEq : null);
            y += 40;
            int number = 2;
            foreach (var m in S.ExtraMics.ToList())
            {
                var set = m;
                var combo = Place(micsCard, new DarkCombo(), 50, y, 260);
                micRows.Add(combo);
                FillExtraMicCombo(combo, set);
                combo.DropDown += (s, e) => FillExtraMicCombo(combo, set);
                combo.SelectionChangeCommitted += (s, e) =>
                {
                    var d = combo.SelectedItem as AudioDeviceInfo;
                    if (d == null || d.Id == set.Id) return;
                    set.Id = d.Id; set.Name = d.Name;
                    core.SaveSoon(); core.ApplyExtraMics();
                };
                MicRow(y, number++, () => set.GainDb, v => { set.GainDb = v; core.PushEngineParams(); },
                    () => { var x = Live(set.Id); return x != null ? x.LevelDb : -100f; },
                    () => { var x = Live(set.Id); return x != null ? x.Share : 0f; },
                    () => { var x = Live(set.Id); return x == null ? "starting" : core.IsMicMuted(set.Id) ? "muted" : x.Running ? null : x.Status; },
                    () => { core.RemoveExtraMic(set.Id); BuildMicRows(); }, set.Eq);
                y += 40;
            }
            addMic.Items.Clear();
            addMic.Items.Add("+  Add microphone " + number + "...");
            addMic.SelectedIndex = 0;
            addMic.Location = new Point(Theme.Px(50), Theme.Px(y + 2));
            addMic.Width = Theme.Px(260);
            y += 46;
            // Where the clean voice goes, and the hints / fix buttons under it.
            sendLabel.Location = new Point(Theme.Px(20), Theme.Px(y + 4));
            outCombo.Location = new Point(Theme.Px(230), Theme.Px(y));
            micInfo.Location = new Point(Theme.Px(20), Theme.Px(y + 40));
            foreach (var b in new Control[] { getCable, volFix, privacyFix }) b.Top = Theme.Px(y + 48);
            int h = y + 100;
            int delta = Theme.Px(h) - micsCard.Height;
            if (delta != 0)
            {
                // Everything below the card moves with it.
                int below = micsCard.Bottom - Theme.Px(2);
                foreach (Control c in micPage.Controls)
                    if (c != micsCard && c.Top >= below) c.Top += delta;
                micsCard.Height += delta;
                FitMicPage();
            }
        }

        /// <summary>An added mic's device menu: its own device (also while unplugged) and the inputs not in use yet.</summary>
        void FillExtraMicCombo(DarkCombo combo, ExtraMicSetting set)
        {
            var list = core.ExtraMicChoices();
            var mine = core.Mics().FirstOrDefault(d => d.Id == set.Id);
            list.Insert(0, mine ?? new AudioDeviceInfo { Id = set.Id, Name = (set.Name ?? "Microphone") + "  (unplugged)" });
            combo.BeginUpdate();
            combo.Items.Clear();
            foreach (var d in list) combo.Items.Add(d);
            combo.SelectedIndex = 0;
            combo.EndUpdate();
        }

        ExtraMic Live(string id) { return core.Engine.Mixer.Mics.FirstOrDefault(x => x.Id == id); }

        /// <summary>The number, live level and (with several mics) volume trim and Remove of one mic row.</summary>
        void MicRow(int y, int number, Func<int> get, Action<int> set, Func<float> level, Func<float> share, Func<string> problem, Action remove, EqProfile ownEq = null)
        {
            var num = Theme.Label(micsCard, number.ToString(), 24, y + 3, Theme.Bold, Theme.Accent, 22);
            Theme.Help(num, number == 1 ? "Microphone 1: your main mic." : "Microphone " + number + ": mixed in with the others.");
            var meter = Place(micsCard, new LevelMeter(), 320, y + 8, get != null ? 90 : 350);
            var note = Theme.Label(micsCard, "", 320, y + 18, Theme.Small, Theme.Muted, get != null ? 96 : 350);
            Theme.Help(meter, "Live level of this mic. With Auto mix, the bar is bright while this mic has the mix (its person is talking).");
            micRows.Add(num); micRows.Add(meter); micRows.Add(note);
            if (ownEq != null)
            {
                var eqb = Place(micsCard, new FlatButton(ownEq.Enabled && ownEq.AllFilters().Count > 0 ? "EQ •" : "EQ"), 552, y - 2, 44);
                Theme.Help(eqb, "This mic's own EQ: shape each person's voice separately (a boomy mic, a thin headset...). It applies before the mics are mixed; Tuning & EQ below then applies to everyone.");
                eqb.Click += (s, e) =>
                {
                    using (var d = new DeviceEqDialog("Microphone " + number + " EQ", "Only this microphone, before the mics are mixed. Tuning & EQ (below the mic settings) still applies to everyone after it.",
                        ownEq, EqEditor.MicPresets(), () => { core.PushMicDeviceParams(); core.SaveSoon(); }))
                        d.ShowDialog(this);
                    eqb.Text = ownEq.Enabled && ownEq.AllFilters().Count > 0 ? "EQ •" : "EQ";
                };
                micRows.Add(eqb);
            }
            if (get != null)
            {
                var slider = Place(micsCard, new FlatSlider { Minimum = -20, Maximum = 20, DefaultValue = 0 }, 418, y, 90);
                var val = Theme.Label(micsCard, "", 510, y + 3, Theme.Small, Theme.Muted, 42);
                Theme.Help(slider, "Volume of this mic in the mix (-20 to +20 dB), to even out a quiet and a loud mic. Double-click to reset.");
                slider.Value = get();
                val.Text = (get() > 0 ? "+" : "") + get() + " dB";
                slider.ValueChanged += (s, e) => { val.Text = (slider.Value > 0 ? "+" : "") + slider.Value + " dB"; if (!loading) set(slider.Value); };
                micRows.Add(slider); micRows.Add(val);
            }
            if (remove != null)
            {
                var x = Place(micsCard, new FlatButton("Remove"), 600, y - 2, 70);
                Theme.Help(x, number == 1 ? "Stops using this mic; microphone 2 becomes your main mic." : "Stops using this mic. The others keep working.");
                x.Click += (s, e) => remove();
                micRows.Add(x);
            }
            micRowTicks.Add(() =>
            {
                string p = problem();
                meter.Level = p == null ? level() : -100f;
                meter.GateOpen = !core.S.MicAutoMix || core.S.ExtraMics.Count == 0 || share() > 0.3f;
                string t = p != null ? p : core.S.MicAutoMix && core.S.ExtraMics.Count > 0 && share() > 0.5f ? "talking" : "";
                if (note.Text != t) { note.Text = t; note.ForeColor = p != null ? Theme.Warn : Theme.Accent; }
            });
        }

        // ---- Outputs: 1 = your headphones, 2, 3... = more outputs that play everything too

        Card outsCard;
        DarkCombo addOut;
        FlatButton syncOuts;
        Label outsNote;
        readonly List<Control> outRows = new List<Control>();
        readonly List<Action> outRowTicks = new List<Action>();
        int outsSpan;

        void BuildOutsCard(Panel page)
        {
            outsCard = NewCard(page, 54, 0);
            outsSpan = 0;
            Theme.Help(Theme.Label(outsCard, "Outputs", 20, 16, Theme.Section),
                "Where your PC's sound plays. Output 1 is your headphones. Add more - a friend's headphones, speakers - and everything plays on all of them at once: games, Discord, music, videos.\nStreamLite does it through \"" + AppCore.GameOutName + "\" (made the Windows default while there are several outputs). Every output has its own volume and EQ, and keeps in sync with its own clock. With Game 3D audio on, every output gets the 3D sound; with it off, plain stereo.\nUnplugged outputs wait and play again when plugged back in.");
            addOut = new DarkCombo();
            outsCard.Controls.Add(addOut);
            Theme.Help(addOut, "Adds another output as the next number. Everything then plays on it too.");
            addOut.DropDown += (s, e) =>
            {
                addOut.BeginUpdate();
                addOut.Items.Clear();
                addOut.Items.Add("+  Add output " + (core.S.ExtraOutputs.Count + 2) + "...");
                foreach (var d in core.ExtraOutputChoices()) addOut.Items.Add(d);
                if (addOut.Items.Count == 1) addOut.Items.Add("(no other output found - plug one in)");
                addOut.SelectedIndex = 0;
                addOut.EndUpdate();
            };
            addOut.SelectionChangeCommitted += (s, e) =>
            {
                var d = addOut.SelectedItem as AudioDeviceInfo;
                if (d != null) { core.AddExtraOutput(d); BuildOutRows(); }
                else addOut.SelectedIndex = 0;
            };
            syncOuts = Place(outsCard, new FlatButton("Sync outputs..."), 520, 10, 150);
            Responsive.Fixed(syncOuts);
            Theme.Help(syncOuts, "Bluetooth and some USB headphones play later than others. Measure each output with your microphone (or set it by ear with test clicks) and StreamLite holds the faster ones back, so everyone hears the same moment. Saved per device.");
            syncOuts.Click += (s, e) => { using (var d = new OutputSyncDialog(core)) d.ShowDialog(this); BuildOutRows(); };
            outsNote = Theme.Label(outsCard, "", 20, 0, Theme.Small, Theme.Muted, 650);
            outsNote.Height = Theme.Px(34);
            BuildOutRows();
        }

        void BuildOutRows()
        {
            if (outsCard == null) return;
            bool was = loading; loading = true;
            try { BuildOutRowsInner(); } finally { loading = was; }
        }

        void BuildOutRowsInner()
        {
            foreach (var c in outRows) { outsCard.Controls.Remove(c); c.Dispose(); }
            outRows.Clear(); outRowTicks.Clear();
            var S = core.S;
            bool several = S.ExtraOutputs.Count > 0;
            syncOuts.Visible = several;
            int maxDelay = several ? new[] { core.GamePhonesId }.Concat(S.ExtraOutputs.Select(o => o.Id)).Max(id => core.OutputDelay(id)) : 0;
            syncOuts.Text = maxDelay > 0 ? "Synced ✓" : "Sync outputs...";
            int y = 50;
            // 1: your headphones (the same as 3D Audio > Headphones).
            var main = Place(outsCard, new DarkCombo(), 50, y, 280);
            outRows.Add(main);
            Action fillMain = () =>
            {
                var list = core.Outputs().Where(d => !AudioDevices.LooksVirtual(d) && !S.ExtraOutputs.Any(o => o.Id == d.Id)).ToList();
                FillAudioCombo(main, list, core.GamePhonesId ?? S.Game.HeadphonesId ?? core.RealDefaultOutput());
            };
            fillMain();
            main.DropDown += (s, e) => fillMain();
            main.SelectionChangeCommitted += (s, e) =>
            {
                var d = main.SelectedItem as AudioDeviceInfo; if (d == null) return;
                S.Game.HeadphonesId = d.Id;
                if (S.Game.Enabled || core.MultiOut) core.RestartGameAudio(); else AudioDevices.SetDefault(d.Id);
                core.SaveSoon(); BuildOutRows();
            };
            OutRow(y, 1, null, null, () =>
            {
                if (!core.Game.Running || core.GamePhonesId == null) return core.MultiOut ? "starting" : "";
                return core.Game.Status.StartsWith("Headphones:") ? core.Game.Status.Substring(12) : null;
            }, several ? (Action)(() =>
            {
                // Output 1 goes: output 2 moves up to 1.
                var next = S.ExtraOutputs[0];
                S.ExtraOutputs.RemoveAt(0);
                S.Game.HeadphonesId = next.Id;
                core.RestartGameAudio(); BuildOutRows();
            }) : null, () =>
            {
                // Output 1's EQ is the headphone EQ below (profiles + correction, through Equalizer APO).
                var id = core.GamePhonesId ?? S.Game.HeadphonesId; if (id == null) return;
                core.S.OutputDeviceId = id;
                FillAudioCombo(spkCombo, core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList(), id);
                LoadSpeakerEq();
                SetOutputEq(true);
            }, null);
            y += 40;
            int number = 2;
            foreach (var o in S.ExtraOutputs.ToList())
            {
                var set = o;
                int num = number;
                var combo = Place(outsCard, new DarkCombo(), 50, y, 280);
                outRows.Add(combo);
                Action fill = () =>
                {
                    var list = core.ExtraOutputChoices();
                    var mine = core.Outputs().FirstOrDefault(d => d.Id == set.Id);
                    list.Insert(0, mine ?? new AudioDeviceInfo { Id = set.Id, Name = (set.Name ?? "Output") + "  (unplugged)" });
                    combo.BeginUpdate(); combo.Items.Clear(); foreach (var d in list) combo.Items.Add(d); combo.SelectedIndex = 0; combo.EndUpdate();
                };
                fill();
                combo.DropDown += (s, e) => fill();
                combo.SelectionChangeCommitted += (s, e) =>
                {
                    var d = combo.SelectedItem as AudioDeviceInfo;
                    if (d == null || d.Id == set.Id) return;
                    set.Id = d.Id; set.Name = d.Name;
                    core.SaveSoon(); core.ApplyExtraOutputs();
                };
                OutRow(y, num, () => set.GainDb, v => { set.GainDb = v; core.PushOutputDeviceParams(); },
                    () => { var x = core.Game.ExtraOutputs.FirstOrDefault(k => k.Id == set.Id); return x == null ? "starting" : x.Playing ? null : x.Status; },
                    () => { core.RemoveExtraOutput(set.Id); BuildOutRows(); },
                    () =>
                    {
                        using (var d = new DeviceEqDialog("Output " + num + " EQ", "Only this output (applied by StreamLite on its own stream - no Equalizer APO setup needed).",
                            set.Eq, EqEditor.HeadphonePresets(), () => core.PushOutputDeviceParams()))
                            d.ShowDialog(this);
                    }, set.Eq);
                y += 40;
                number++;
            }
            addOut.Items.Clear();
            addOut.Items.Add("+  Add output " + number + "...");
            addOut.SelectedIndex = 0;
            addOut.Location = new Point(Theme.Px(50), Theme.Px(y + 2));
            addOut.Width = Theme.Px(280);
            y += 44;
            outsNote.Top = Theme.Px(y);
            outsNote.Text = several
                ? "Everything plays on all " + (number - 1) + " outputs through \"" + AppCore.GameOutName + "\" (the Windows default now)" + (S.Game.Enabled ? ", in 3D." : " - plain stereo (Game 3D audio is off).")
                : "Someone else listening too? Add their headphones - everything then plays on both, each with its own volume and EQ.";
            y += 38;
            int h = y;
            int span = Theme.Px(h + 14);
            int delta = span - outsSpan;
            outsCard.Height = Theme.Px(h);
            if (delta != 0)
            {
                int below = outsCard.Top + outsSpan - Theme.Px(2);
                foreach (Control c in outPage.Controls)
                    if (c != outsCard && c.Top >= below) c.Top += delta;
                outsSpan = span;
                outPage.Height += delta;
            }
        }

        void OutRow(int y, int number, Func<int> get, Action<int> set, Func<string> problem, Action remove, Action openEq, EqProfile ownEq)
        {
            var num = Theme.Label(outsCard, number.ToString(), 24, y + 3, Theme.Bold, Theme.Accent, 22);
            Theme.Help(num, number == 1 ? "Output 1: your headphones (the 3D Audio page's headphones too)." : "Output " + number + ": plays everything too.");
            var note = Theme.Label(outsCard, "", 338, y + 3, Theme.Small, Theme.Muted, get != null ? 90 : 200);
            outRows.Add(num); outRows.Add(note);
            if (get != null)
            {
                var slider = Place(outsCard, new FlatSlider { Minimum = -20, Maximum = 20, DefaultValue = 0 }, 432, y, 76);
                var val = Theme.Label(outsCard, "", 510, y + 3, Theme.Small, Theme.Muted, 40);
                Theme.Help(slider, "Volume of this output (-20 to +20 dB) on top of its Windows volume. Double-click to reset.");
                slider.Value = get();
                val.Text = (get() > 0 ? "+" : "") + get() + " dB";
                slider.ValueChanged += (s, e) => { val.Text = (slider.Value > 0 ? "+" : "") + slider.Value + " dB"; if (!loading) set(slider.Value); };
                outRows.Add(slider); outRows.Add(val);
            }
            var eqb = Place(outsCard, new FlatButton(ownEq != null && ownEq.Enabled && ownEq.AllFilters().Count > 0 ? "EQ •" : "EQ"), 552, y - 2, 44);
            Theme.Help(eqb, number == 1 ? "Opens the headphone EQ below for these headphones (game profiles and headphone correction)." : "This output's own EQ.");
            eqb.Click += (s, e) => { openEq(); if (ownEq != null) eqb.Text = ownEq.Enabled && ownEq.AllFilters().Count > 0 ? "EQ •" : "EQ"; };
            outRows.Add(eqb);
            if (remove != null)
            {
                var x = Place(outsCard, new FlatButton("Remove"), 600, y - 2, 70);
                Theme.Help(x, number == 1 ? "Stops playing here; output 2 becomes output 1." : "Stops playing on this output.");
                x.Click += (s, e) => remove();
                outRows.Add(x);
            }
            outRowTicks.Add(() =>
            {
                string p = problem();
                string t = p ?? (core.MultiOut ? "playing" : "");
                if (note.Text != t) { note.Text = t; note.ForeColor = p != null && p != "starting" ? Theme.Warn : Theme.Accent; }
            });
        }

        void TickOuts() { foreach (var t in outRowTicks) t(); }

        void TickMics() { foreach (var t in micRowTicks) t(); }

        /// <summary>Scrolls the page area so a section header sits near the top.</summary>
        void ScrollTo(Control page, Control header)
        {
            content.ScrollIntoView(header);
        }

        /// <summary>Re-reads the main-page controls that the tuning section can also change.</summary>
        void RefreshMicValues()
        {
            loading = true;
            var s = core.S;
            strength.Value = s.Strength; strengthVal.Text = s.Strength + "%";
            threshold.Value = s.GateThreshold; thresholdVal.Text = s.GateThreshold + " dB";
            volume.Value = s.VolumeDb; volumeVal.Text = (s.VolumeDb > 0 ? "+" : "") + s.VolumeDb + " dB";
            autoLevelToggle.Checked = s.AutoLevel;
            nrToggle.Checked = s.NoiseRemoval; gateToggle.Checked = s.Gate; eqToggle.Checked = s.MicEq.Enabled;
            studioToggle.Checked = s.StudioVoice; studioStrength.Value = s.StudioStrength; studioVal.Text = s.StudioStrength + "%"; studioStrength.Enabled = s.StudioVoice;
            ShowVoiceIsolation();
            gateMode.SelectedIndex = s.GateExpander ? 0 : 1;
            compToggle.Checked = s.Compressor; essToggle.Checked = s.DeEsser;
            deHum.SelectedIndex = s.DeHumHz == 50 ? 1 : s.DeHumHz == 60 ? 2 : 0;
            lowCut.SelectedIndex = LowCutIndex(s.HighPassHz);
            strength.Enabled = s.NoiseRemoval; threshold.Enabled = s.Gate;
            loading = false;
            RefreshEqSummary();
            RefreshStyle();
        }

        static int LowCutIndex(int hz)
        {
            int[] cuts = { 0, 60, 80, 100, 120, 150 };
            int best = 0; for (int i = 0; i < cuts.Length; i++) if (Math.Abs(cuts[i] - hz) < Math.Abs(cuts[best] - hz)) best = i;
            return best;
        }

        void FillAudioCombo(DarkCombo combo, List<AudioDeviceInfo> list, string selectedId)
        {
            combo.BeginUpdate();
            combo.Items.Clear();
            foreach (var d in list) combo.Items.Add(d);
            combo.SelectedItem = list.FirstOrDefault(d => d.Id == selectedId);
            combo.EndUpdate();
        }

        void LoadMic()
        {
            loading = true;
            var s = core.S;
            micOn.Checked = s.VirtualMicOn;
            var mics = core.MicChoices();
            if (core.ActiveMicId != null && !mics.Any(d => d.Id == core.ActiveMicId))
            {
                // The chosen mic is unplugged: still show it so it's clear what the app is waiting for.
                var gone = AudioDevices.List(EDataFlow.Capture, 4 | 8).FirstOrDefault(d => d.Id == core.ActiveMicId);
                if (gone != null) mics.Insert(0, new AudioDeviceInfo { Id = gone.Id, Name = gone.Name + "  (unplugged)", Adapter = gone.Adapter });
            }
            FillAudioCombo(micCombo, mics, core.ActiveMicId);
            FillAudioCombo(outCombo, core.MicCableChoices(), core.ActiveOutId);
            nrToggle.Checked = s.NoiseRemoval;
            strength.Value = s.Strength; strength.Enabled = s.NoiseRemoval; strengthVal.Text = s.Strength + "%";
            gateToggle.Checked = s.Gate;
            threshold.Value = s.GateThreshold; threshold.Enabled = s.Gate; thresholdVal.Text = s.GateThreshold + " dB";
            eqToggle.Checked = s.MicEq.Enabled;
            studioToggle.Checked = s.StudioVoice; studioStrength.Value = s.StudioStrength; studioVal.Text = s.StudioStrength + "%"; studioStrength.Enabled = s.StudioVoice;
            ShowVoiceIsolation();
            gateMode.SelectedIndex = s.GateExpander ? 0 : 1;
            eqPreset.SelectedIndex = 0;
            compToggle.Checked = s.Compressor;
            essToggle.Checked = s.DeEsser;
            lowCut.SelectedIndex = LowCutIndex(s.HighPassHz);
            deHum.SelectedIndex = s.DeHumHz == 50 ? 1 : s.DeHumHz == 60 ? 2 : 0;
            if (tuning.Visible) tuning.Reload();
            volume.Value = s.VolumeDb; volumeVal.Text = (s.VolumeDb > 0 ? "+" : "") + s.VolumeDb + " dB";
            autoLevelToggle.Checked = s.AutoLevel;
            defaultToggle.Checked = s.MakeDefaultMic;
            shareToggle.Checked = s.ShareMic;
            farToggle.Checked = s.FarVoice;
            monitorToggle.Checked = s.Monitor;
            autoMixToggle.Checked = s.MicAutoMix;
            BuildMicRows();
            loading = false;
            RefreshMicInfo();
            RefreshEqSummary();
            RefreshStyle();
        }

        void RefreshEqSummary()
        {
            var eq = core.S.MicEq;
            int n = eq.AllFilters().Count;
            eqSummary.Text = !eq.Enabled ? "Off" : n == 0 ? "Flat - open Tuning to shape your voice" : n + " bands adjusted";
        }

        /// <summary>"Mic to cable 5.1 ms (low latency) + 20 ms noise removal - sent privately".</summary>
        string LatencyLine()
        {
            var e = core.Engine;
            float ai = core.S.NoiseRemoval ? e.Suppressor.DelayMs : 0;
            return "Mic to cable " + e.LatencyMs.ToString("0.0") + " ms" + (e.ExclusiveCapture ? " (low latency)" : " (shared: " + (e.ExclusiveError ?? "starting") + ")") +
                (ai > 0 ? " + " + ai.ToString("0") + " ms noise removal" : "") + (e.PrivateOutput ? " - sent privately" : "");
        }

        void RefreshMicInfo()
        {
            var cap = core.PairedCapture(core.ActiveOutId);
            bool hasVirtual = core.FindVirtualOutput() != null;
            getCable.Visible = !hasVirtual;
            float vol = 1, volDb = 0;
            bool quieter = cap != null && AudioDevices.GetVolume(cap.Id, out vol, out volDb) && vol < 0.99f;
            bool blocked = core.S.VirtualMicOn && DeviceErrors.MicBlocked();
            string micError = core.Engine.Status != null && core.Engine.Status.StartsWith("Microphone: ") ? core.Engine.Status.Substring(12) : null;
            privacyFix.Visible = blocked;
            volFix.Visible = hasVirtual && quieter && core.S.VirtualMicOn && !blocked;
            if (getCable.Visible && blocked) getCable.Visible = false;
            if (blocked)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "Windows keeps apps away from your microphone (privacy settings), so StreamLite only hears silence. Turn on microphone access for desktop apps.";
            }
            else if (core.S.VirtualMicOn && micError != null)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = micError;
            }
            else if (core.S.VirtualMicOn && core.MicSilentSeconds >= 15)
            {
                var m = core.Mics().FirstOrDefault(d => d.Id == core.ActiveMicId);
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "StreamLite hears nothing at all from \"" + (m != null ? m.Name : "your microphone") + "\". Pick the mic you talk into under Microphones, check it isn't muted (the headset's mute switch, or Windows Sound settings > Recording > its level), and that Windows lets desktop apps use the microphone (Settings > Privacy & security > Microphone).";
            }
            else if (core.S.VirtualMicOn && core.LockBlocksAll)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "Only my voice turned down everything it heard in the last minute - it may have learned someone else's voice. Learn your voice again, or switch it off (Tuning & EQ > Noise removal).";
            }
            else if (core.S.VirtualMicOn && core.Engine.Suppressor.FallbackNote != null && core.S.NoiseRemoval)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = core.Engine.Suppressor.FallbackNote;
            }
            else if (core.S.VirtualMicOn && core.Engine.Fx.Clip.Clipping)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "Your microphone is clipping (too loud before StreamLite gets it - no effect can repair that). Turn its gain down on the mic or audio interface, or its level in Windows Sound settings > Recording.";
            }
            else if (!core.S.VirtualMicOn)
            {
                micInfo.ForeColor = Theme.Muted;
                micInfo.Text = "Virtual mic is off. Turn it on (top right) to clean up your microphone.";
            }
            else if (!hasVirtual)
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "No virtual audio cable found. Install VB-CABLE (free), then pick \"" + AppCore.MicName + "\" as the mic in Discord, Zoom, OBS, games...";
            }
            else if (cap != null && core.Mics().Any(d => d.Id == core.ActiveMicId && DeviceFormats.IsBluetooth(d)))
            {
                micInfo.ForeColor = Theme.Warn;
                micInfo.Text = "Pick \"" + cap.Name + "\" in Discord & co. This is a Bluetooth headset mic: while it's in use the headset drops to call quality (a Bluetooth limit). A USB or wired mic keeps your headphones in full quality.";
            }
            else if (cap != null)
            {
                micInfo.ForeColor = Theme.Muted;
                micInfo.Text = "In Discord, Zoom, Teams, OBS or games choose \"" + cap.Name + "\" as your microphone." +
                    (quieter ? "\nWindows has its volume at " + (vol * 100).ToString("0") + " (" + volDb.ToString("0.0") + " dB): apps hear you that much quieter." :
                     core.Engine.Running && core.Engine.PacketMs > 0 ? "\n" + LatencyLine() :
                     core.Engine.PrivateOutput ? "\nSent privately: Discord / OBS screen-share audio can't pick up your voice." :
                     core.Engine.Running ? "\nShared output (exclusive mode unavailable) - screen shares may include your voice." : "");
            }
            else
            {
                micInfo.ForeColor = Theme.Muted;
                micInfo.Text = "Clean audio is playing on the selected device (useful for testing).";
            }
            defaultToggle.Enabled = cap != null;
        }

        // ------------------------------------------------------------------ Headphones

        Panel BuildOutputPage()
        {
            var page = NewPage("Speakers & Headphones");
            outPage = page;
            var dev = NewCard(page, 54, 330);
            Theme.Help(Theme.Label(dev, "Device", 20, 20, Theme.Bold), "The headphones or speakers this EQ applies to.");
            spkCombo = Place(dev, new DarkCombo(), 120, 16, 380);
            spkCombo.DropDown += (s, e) => FillAudioCombo(spkCombo, core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList(), SelectedSpeakerId());
            spkCombo.SelectionChangeCommitted += (s, e) => { var d = spkCombo.SelectedItem as AudioDeviceInfo; if (d != null) { core.S.OutputDeviceId = d.Id; core.SaveSoon(); } LoadSpeakerEq(); };
            var makeDefault = Place(dev, new FlatButton("Make default"), 520, 14);
            makeDefault.Width = Theme.Px(150);
            Theme.Help(makeDefault, "Makes this the Windows playback device. While Game 3D audio is on, it becomes the 3D audio headphones instead (Windows keeps playing games into the 3D device).");
            makeDefault.Click += (s, e) =>
            {
                var id = SelectedSpeakerId();
                if (id == null) return;
                if ((core.S.Game.Enabled || core.MultiOut) && !core.GameSinkCandidates().Any(d => d.Id == id))
                {
                    // Switching Windows to the headphones would silently bypass 3D audio: route 3D output there instead.
                    core.S.Game.HeadphonesId = id;
                    core.RestartGameAudio();
                    AudioDevices.SetDefaultRole(id, ERole.Communications);
                }
                else AudioDevices.SetDefault(id);
            };

            spkEqToggle = Place(dev, new ToggleSwitch(), 20, 64);
            Theme.Help(Theme.Label(dev, "Equalizer for this device", 74, 64, Theme.Bold), "Turns the headphone/speaker EQ on or off for every app and game (applied through Equalizer APO).");
            spkEqToggle.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                var id = SelectedSpeakerId(); if (id == null) return;
                core.OutputProfile(id).Enabled = spkEqToggle.Checked;
                core.OutputEqChanged();
                spkGraph.Invalidate();
            };
            outEqOpen = Place(dev, new FlatButton("Adjust EQ"), 520, 58);
            outEqOpen.Width = Theme.Px(150);
            outEqOpen.Click += (s, e) => SetOutputEq(true);
            spkGraph = Place(dev, new EqGraph { Interactive = false }, 20, 104, 650);
            spkGraph.Height = Theme.Px(205);
            spkGraph.Click += (s, e) => SetOutputEq(true);

            BuildProfilesCard(page, 398);
            var apo = NewCard(page, 688, 170);
            Theme.Help(Theme.Label(apo, "System-wide EQ engine (Equalizer APO)", 20, 18, Theme.Bold), "Windows lets no app change other apps' sound directly, so headphone EQ runs inside Equalizer APO, a free open-source audio add-on.");
            apoStatus = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(46)), Size = new Size(Theme.Px(650), Theme.Px(70)) };
            apo.Controls.Add(apoStatus);
            apoGet = Place(apo, new FlatButton("Install Equalizer APO (free)", true), 20, 122);
            Theme.Help(apoGet, "Downloads Equalizer APO (free, open source) from SourceForge and installs it - Windows asks for permission once. Then tick your headphones in its device list and restart Windows.");
            apoGet.Click += (s, e) => InstallEqualizerApo();
            apoEnable = Place(apo, new FlatButton("Enable on this device (admin)..."), 20, 122);
            apoEnable.Click += (s, e) => { EqualizerApo.OpenConfigurator(); };
            apoApply = Place(apo, new FlatButton("Apply EQ now"), 0, 122);
            apoApply.Click += (s, e) =>
            {
                string err = core.WriteOutputEq(true);
                core.OutputEqError = err;
                RefreshApoStatus();
                if (err != null) DarkMessage.Show(this, err, "Equalizer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            // Collapsed EQ editor section.
            outEqHeader = Theme.Label(page, "Equalizer editor", 0, 878, Theme.Section);
            Theme.Label(page, "Edits the profile in use; headphone correction stays underneath", 150, 882, Theme.Small, Theme.Muted, 420);
            outEqToggle = Place(page, new FlatButton("Show  \u25BE"), 580, 872);
            outEqToggle.Width = Theme.Px(110);
            outEqToggle.Click += (s, e) => SetOutputEq(!outEq.Visible);
            outEq = new OutputEqPanel(core) { Location = new Point(0, Theme.Px(918)), Visible = false };
            outEq.Changed += (s, e) =>
            {
                loading = true;
                spkEqToggle.Checked = core.OutputProfile(SelectedSpeakerId()).Enabled;
                loading = false;
                spkGraph.Invalidate();
            };
            page.Controls.Add(outEq);
            page.Height = Theme.Px(916);
            BuildOutsCard(page);   // last: it moves everything below it as it grows
            return page;
        }

        DarkCombo profCombo, prof3d;
        ToggleSwitch profCorr;
        Label profNow, profWhy, profInfo, profCorrName;

        void BuildProfilesCard(Panel page, int y)
        {
            var c = NewCard(page, y, 276);
            Theme.Help(Theme.Label(c, "Game profiles", 20, 16, Theme.Section),
                "Auto EQ: every app gets the headphone EQ of its profile, switched automatically while you use it. Every profile = your headphones corrected to neutral (pick them with \"Find my headphones\") + an EQ for that game or kind of app. Music while Spotify plays, Chat while Discord, Zoom, Teams or TeamSpeak is open, otherwise Movies & videos. \"Auto EQ apps...\" changes which apps belong to which profile.\nWindows plays every app through one headphone EQ at a time, so when several are in use the one higher in your priority order wins (\"Priority...\"): normally the game, then music, then chat.");
            var prio = Place(c, new FlatButton("Priority..."), 400, 12);
            prio.Width = Theme.Px(110);
            Theme.Help(prio, "Which profile wins when apps of several profiles are in use, and whether every profile plays equally loud.");
            prio.Click += (s, e) =>
            {
                using (var d = new ProfilePriorityDialog(core.S.Profiles))
                {
                    d.ShowDialog(this);
                    if (d.Changed) { core.SaveSoon(); core.Profiles.Reapply(); RefreshProfiles(); }
                }
            };
            var editApps = Place(c, new FlatButton("Auto EQ apps..."), 520, 12);
            editApps.Width = Theme.Px(150);
            Theme.Help(editApps, "Which apps switch your headphones to which profile: add any program (or one that's running now) to a profile, remove it, or move it to another. Also picks the profile used when none of them is in use.");
            editApps.Click += (s, e) =>
            {
                using (var d = new AutoEqAppsDialog(core.S.Profiles, core.Profiles.ActiveName))
                {
                    d.ShowDialog(this);
                    if (d.Changed) { core.SaveSoon(); core.Profiles.Reapply(); RefreshProfiles(); }
                }
            };
            Theme.Label(c, "Now using", 20, 54, Theme.Bold);
            profNow = Theme.Label(c, "", 140, 54, Theme.Body, Theme.Accent, 520);
            profWhy = Theme.Label(c, "", 140, 78, Theme.Small, Theme.Muted, 530);
            profWhy.AutoEllipsis = true;
            Theme.Help(Theme.Label(c, "Profile", 20, 114, Theme.Bold), "Auto EQ (the first entry) switches the EQ by the app you're using, every 3 seconds: a game first, then music while it plays, then a voice chat app, otherwise your fallback profile. Picking any other profile keeps that one until you pick Auto EQ again.");
            profCombo = Place(c, new DarkCombo(), 140, 110, 250);
            profCombo.DropDown += (s, e) => FillProfiles();
            profCombo.SelectionChangeCommitted += (s, e) =>
            {
                var n = profCombo.SelectedItem as string; if (n == null) return;
                bool auto = n == GameProfiles.AutoEq;
                core.S.Profiles.AutoSwitch = auto; core.S.Profiles.ManualProfile = auto ? null : n; core.SaveSoon();
                core.Profiles.Reapply();
            };
            prof3d = Place(c, new DarkCombo(), 410, 110, 260);
            prof3d.Items.AddRange(new object[] { "Game's own HRTF (stereo)", "StreamLite true 360° 3D" });
            Theme.Help(prof3d, "How this game is spatialized while Game 3D audio is on:\n- Game's own HRTF: the game plays plain stereo straight to your headphones (best for CS2, Valorant, Fortnite, Marvel Rivals, Overwatch - use their 3D/HRTF option).\n- StreamLite true 360°: the game sends surround and StreamLite places every sound at its exact angle around you (best for games without their own HRTF, like Siege). Set the game to 7.1 / surround - that's only how it hands StreamLite the directions.");
            prof3d.SelectionChangeCommitted += (s, e) =>
            {
                var p = core.Profiles.Find(core.Profiles.ActiveName); if (p == null) return;
                p.ThreeD = prof3d.SelectedIndex == 0 ? "game" : "bl"; core.SaveSoon(); core.Profiles.Reapply();
            };
            profCorr = Place(c, new ToggleSwitch(), 20, 148);
            profCorrName = Theme.Label(c, "", 74, 149, Theme.Bold, Theme.Text, 430);
            Theme.Help(profCorrName,
                "Flattens your headphones using their measured response (from AutoEq: thousands of headphones and earbuds measured on calibrated rigs) toward a neutral target, so every game, music and chat profile sounds the way it was designed on any headphones. Applies under every profile.\nEach headset or pair of earbuds keeps its own correction: switch devices and StreamLite switches corrections.\nKeep your headphones' own app EQ flat / neutral - that's what the measurements were made with.");
            profCorr.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                var corr = core.S.Profiles.CorrectionFor(core.ProfileDeviceId());
                if (profCorr.Checked && (corr == null || corr.Filters == null || corr.Filters.Count == 0))
                {
                    loading = true; profCorr.Checked = false; loading = false;
                    FindHeadphones();
                    return;
                }
                if (corr != null) corr.On = profCorr.Checked;
                core.SaveSoon(); core.Profiles.Reapply();
            };
            var find = Place(c, new FlatButton("Find my headphones..."), 520, 144);
            Responsive.Fixed(find);
            find.Click += (s, e) => FindHeadphones();
            profInfo = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(182)), Size = new Size(Theme.Px(650), Theme.Px(88)), UseMnemonic = false };
            c.Controls.Add(profInfo);
            core.Profiles.Changed += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(RefreshProfiles)); };
        }

        void InstallVbCable()
        {
            getCable.Enabled = false;
            new System.Threading.Thread(() =>
            {
                string err = DriverInstaller.InstallVbCable(() => core.FindVirtualOutput() != null,
                    msg => BeginInvoke(new Action(() => getCable.Text = msg)));
                BeginInvoke(new Action(() =>
                {
                    getCable.Enabled = true; getCable.Text = "Install VB-CABLE (free)";
                    if (err != null) { DarkMessage.Show(this, err, "VB-CABLE", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    core.S.VirtualOutId = null;          // pick the new cable
                    core.RestartAudio();
                    LoadAll();
                }));
            }) { IsBackground = true }.Start();
        }

        void InstallEqualizerApo()
        {
            apoGet.Enabled = false;
            new System.Threading.Thread(() =>
            {
                string err = DriverInstaller.InstallEqualizerApo(() => EqualizerApo.IsInstalled,
                    msg => BeginInvoke(new Action(() => apoGet.Text = msg)));
                BeginInvoke(new Action(() =>
                {
                    apoGet.Enabled = true; apoGet.Text = "Install Equalizer APO (free)";
                    if (err != null) { DarkMessage.Show(this, err, "Equalizer APO", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    using (var d = new ConfirmDialog("Equalizer APO", "Installed. Next: tick your headphones in the list that opens now, click OK, then restart Windows. After that this page controls your headphone EQ.", "Open the list", "Later"))
                        if (d.ShowDialog(this) == DialogResult.OK) EqualizerApo.OpenConfigurator();
                    LoadOutput();
                }));
            }) { IsBackground = true }.Start();
        }

        void FindHeadphones()
        {
            string dev = core.ProfileDeviceId();
            var d = dev != null ? core.Outputs().FirstOrDefault(o => o.Id == dev) : null;
            using (var f = new HeadphoneFinder(d != null ? d.Name : ""))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.ChosenFilters == null) return;
                if (dev == null) { DarkMessage.Show(this, "Pick your headphones as the device at the top of this page first.", "Find my headphones", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                core.S.Profiles.SetCorrection(dev, f.ChosenName, f.ChosenFilters);   // just for these headphones
                core.SaveSoon();
                core.Profiles.Reapply();
                RefreshProfiles();
            }
        }

        void FillProfiles()
        {
            profCombo.BeginUpdate();
            profCombo.Items.Clear();
            profCombo.Items.Add(GameProfiles.AutoEq);
            foreach (var p in core.S.Profiles.Profiles) profCombo.Items.Add(p.Name);
            profCombo.SelectedItem = core.S.Profiles.AutoSwitch ? GameProfiles.AutoEq : core.Profiles.ActiveName;
            profCombo.EndUpdate();
        }

        void RefreshProfiles()
        {
            if (profNow == null) return;
            loading = true;
            var corr = core.S.Profiles.CorrectionFor(core.ProfileDeviceId());
            bool has = corr != null && corr.Filters != null && corr.Filters.Count > 0;
            profCorr.Checked = has && corr.On;
            profCorrName.Text = "Headphone correction: " + (has ? corr.Name : "none yet for these headphones - find them");
            FillProfiles();
            var p = core.Profiles.Find(core.Profiles.ActiveName);
            profNow.Text = ProfileNowText();
            profWhy.Text = ProfileWhyText();
            Theme.Help(profWhy, profWhy.Text);
            prof3d.SelectedIndex = p != null && p.ThreeD == "bl" ? 1 : 0;
            prof3d.Enabled = p != null && p.Exes.Count > 0;
            profInfo.Text = p == null ? "" : (p.Basis + (string.IsNullOrEmpty(p.Tips) ? "" : "\nIn-game: " + p.Tips) +
                (p.Cinematic && core.S.Game.NaturalWhenNoGame && core.S.Game.Enabled ? "\n3D audio plays sound as it was mixed with this profile (no footstep boost, tamer or cue sharpening)." : ""));
            loading = false;
            LoadSpeakerEq();
        }

        /// <summary>"Why: cs2 is running. Also in use: Spotify (Music) - lower in your priority order", or a switch on its way.</summary>
        string ProfileWhyText()
        {
            string pending = core.Profiles.Pending;
            if (pending != null) return "Switching to " + pending + " if it's still in use at the next check (a few seconds)...";
            string why = core.Profiles.Why;
            if (string.IsNullOrEmpty(why)) return "";
            var p = core.Profiles.Find(core.Profiles.ActiveName);
            return "Why: " + why + (core.S.Profiles.MatchLoudness && p != null && p.Eq.MatchDb < -0.05 ? ". Turned down " + (-p.Eq.MatchDb).ToString("0.0") + " dB to match your other profiles" : "") + ".";
        }

        /// <summary>"Counter-Strike 2 (cs2.exe) - straight to your headphones": the profile in use and where the game's sound goes.</summary>
        string ProfileNowText()
        {
            var p = core.Profiles.Find(core.Profiles.ActiveName);
            if (p == null) return "-";
            string exe = core.Profiles.DetectedExe;
            if (exe == null) return p.Name + (core.S.Profiles.AutoSwitch ? "  (Auto EQ: none of its apps in use)" : "  (picked by hand)");
            if (p.ChatApp || p.WhilePlaying)
                return p.Name + "  (Auto EQ: " + exe + (p.WhilePlaying ? " playing)" : " open)");
            string where = "";
            if (core.S.Game.Enabled && core.Game.Running && core.GamePhonesId != null)
            {
                if (p.ThreeD == "bl") where = " - through StreamLite 3D";
                else
                {
                    bool routed = false;
                    try { routed = StreamLite.Interop.AppRouting.IsRouted(exe, core.GamePhonesId); } catch { }
                    where = routed ? " - straight to your headphones" : " - waiting for its sound to route it";
                }
            }
            return p.Name + "  (" + exe + ".exe)" + where;
        }

        void SetOutputEq(bool open)
        {
            var id = SelectedSpeakerId();
            if (open && id == null) return;
            if (open) outEq.SetDevice(id);
            outEq.Visible = open;
            outPage.Height = open ? outEq.Bottom + Theme.Px(20) : outEqHeader.Top + Theme.Px(38);
            outEqToggle.Text = open ? "Hide  \u25B4" : "Show  \u25BE";
            if (open) ScrollTo(outPage, outEqHeader);
        }

        string SelectedSpeakerId()
        {
            var d = spkCombo.SelectedItem as AudioDeviceInfo;
            return d != null ? d.Id : null;
        }

        void LoadOutput()
        {
            RefreshProfiles();
            BuildOutRows();
            var outs = core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList();
            string want = core.S.OutputDeviceId;
            if (want == null || !outs.Any(d => d.Id == want)) want = core.RealDefaultOutput();
            FillAudioCombo(spkCombo, core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList(), want);
            LoadSpeakerEq();
        }

        void LoadSpeakerEq()
        {
            var id = SelectedSpeakerId();
            loading = true;
            if (id != null)
            {
                var p = core.OutputProfile(id);
                spkEqToggle.Checked = p.Enabled;
                spkGraph.Profile = p;
            }
            else spkGraph.Profile = null;
            loading = false;
            spkGraph.Invalidate();
            RefreshApoStatus();
            if (outEq.Visible && id != null) outEq.SetDevice(id);
        }

        void RefreshApoStatus()
        {
            var id = SelectedSpeakerId();
            bool installed = EqualizerApo.IsInstalled;
            apoGet.Visible = !installed;
            apoEnable.Visible = installed;
            apoApply.Visible = installed;
            apoApply.Left = apoEnable.Right + Theme.Px(10);
            if (!installed)
            {
                apoStatus.ForeColor = Theme.Warn;
                apoStatus.Text = "Windows doesn't let apps change other apps' sound directly, so headphone/speaker EQ runs through Equalizer APO - a free, open-source add-on that works with any device, including USB headsets and USB/Bluetooth dongles. Install it (tick your headphones in its installer), restart, and this page controls it.";
                return;
            }
            bool? attached = id != null ? EqualizerApo.IsEnabledForDevice(id) : null;
            if (attached == false)
            {
                apoStatus.ForeColor = Theme.Warn;
                apoStatus.Text = "Equalizer APO is installed but not attached to this device yet. Click \"Enable on this device\", tick it in the list, then restart Windows.";
            }
            else if (core.OutputEqError != null)
            {
                apoStatus.ForeColor = Theme.Warn;
                apoStatus.Text = "Couldn't save the EQ: " + core.OutputEqError + " Click \"Apply EQ now\" to grant access (admin prompt).";
            }
            else
            {
                apoStatus.ForeColor = Theme.Accent;
                apoStatus.Text = "Active. EQ changes apply instantly to this device in every app and game." +
                    "\nYour headphone correction and the game profiles are applied here automatically (see Game profiles above).";
            }
        }

        // ------------------------------------------------------------------ Camera

        Panel BuildCameraPage()
        {
            var page = NewPage("Camera");
            var left = NewCard(page, 54, 520, 0, 370);
            Theme.Label(left, "Camera", 20, 20, Theme.Bold);
            camCombo = Place(left, new DarkCombo(), 90, 16, 260);
            camCombo.SelectionChangeCommitted += (s, e) => { var d = camCombo.SelectedItem as VideoDeviceInfo; if (d != null) { core.S.LastCamera = d.Key; core.SaveSoon(); } OpenCamera(); };

            previewHost = new Panel { BackColor = Color.Black, Location = new Point(Theme.Px(20), Theme.Px(58)), Size = new Size(Theme.Px(330), Theme.Px(186)) };
            left.Controls.Add(previewHost);
            previewMsg = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Black, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Preview is off" };
            previewHost.Controls.Add(previewMsg);
            previewToggle = Place(left, new ToggleSwitch(), 20, 258);
            Theme.Help(Theme.Label(left, "Preview", 74, 258), "Shows your camera here. Turn it off before using the camera in Discord - only one app can use it at a time.");
            previewToggle.CheckedChanged += (s, e) => { if (previewToggle.Checked) StartPreview(); else StopPreview(); };
            formatCombo = Place(left, new DarkCombo(), 150, 254, 200);
            formatCombo.DropDownWidth = Theme.Px(260);
            formatCombo.SelectionChangeCommitted += (s, e) =>
            {
                if (camDev == null) return;
                var c = formatCombo.SelectedItem as FormatChoice;
                core.Cameras.Profile(camDev.Info.Key).PreviewMode = c != null ? c.Key : null;
                core.SaveSoon();
                previewToggle.SetSilently(true);
                StartPreview();
            };
            fpsInfo = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(284)), Size = new Size(Theme.Px(330), Theme.Px(34)) };
            left.Controls.Add(fpsInfo);
            fpsInfo.Click += (s, e) => { if (restrictedModes) OpenUrl("ms-settings:camera"); };

            lockToggle = Place(left, new ToggleSwitch(), 20, 324);
            Theme.Help(Theme.Label(left, "Lock my settings", 74, 324, Theme.Bold), "Keeps your image settings applied: if Discord, Zoom or Teams change brightness/exposure/focus, they are put back within 2 seconds.");
            var lockHint = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(74), Theme.Px(346)), Size = new Size(Theme.Px(280), Theme.Px(34)), Text = "Stops Teams, Zoom, Discord... from resetting brightness, exposure or focus." };
            left.Controls.Add(lockHint);
            lockToggle.CheckedChanged += (s, e) =>
            {
                if (loading || camDev == null) return;
                var p = core.Cameras.Profile(camDev.Info.Key);
                p.Lock = lockToggle.Checked;
                if (p.Lock) { CaptureAllToProfile(p); CameraService.Enforce(camDev, p); }
                core.SaveSoon();
            };

            var reset = Place(left, new FlatButton("Reset to defaults"), 20, 384);
            reset.Width = Theme.Px(160);
            reset.Click += (s, e) => ResetCamera();
            var restore = Place(left, new FlatButton("Restore my look"), 190, 384);
            restore.Width = Theme.Px(160);
            restore.Click += (s, e) => RestoreLook();
            var adv = Place(left, new FlatButton("Driver settings..."), 20, 422);
            adv.Width = Theme.Px(160);
            adv.Click += (s, e) => { if (camDev != null) { camDev.ShowDriverDialog(this); RefreshCameraValues(); } };
            var save = Place(left, new FlatButton("Save current look"), 190, 422);
            save.Width = Theme.Px(160);
            save.Click += (s, e) =>
            {
                if (camDev == null) return;
                CaptureAll(core.Cameras.Profile(camDev.Info.Key).SavedLook);
                core.SaveSoon();
                DarkMessage.Show(this, "Saved. \"Restore my look\" brings these image settings back any time.", "Camera look", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            var manage = Place(left, new FlatButton("Enable / disable cameras..."), 20, 460);
            manage.Width = Theme.Px(330);
            manage.Click += (s, e) => { using (var f = new ManageCamerasForm()) f.ShowDialog(this); core.Cameras.Refresh(); LoadCameras(); };

            var right = NewCard(page, 54, 520, 384, 306);
            Theme.Label(right, "Image settings", 20, 20, Theme.Bold);
            propHost = new ScrollHost { Location = new Point(Theme.Px(12), Theme.Px(50)), Size = new Size(Theme.Px(290), Theme.Px(460)), BackColor = Theme.Card, MarginX = 0, MarginTop = 0, MarginBottom = 4, MinContentWidth = 260 };
            propList = new Panel { Size = new Size(Theme.Px(278), Theme.Px(460)), BackColor = Theme.Card };
            propHost.Content = propList;
            right.Controls.Add(propHost);
            fxCard = new CameraFxCard(core, page, 588, StopPreview);
            var tracking = new CameraTrackingCard(core, page, fxCard.Bottom + Theme.Px(14));
            page.Height = tracking.Bottom + Theme.Px(20);
            return page;
        }

        void LoadCameras()
        {
            var list = core.Cameras.Devices;
            string keep = camDev != null ? camDev.Info.Key : core.S.LastCamera;
            camCombo.BeginUpdate();
            camCombo.Items.Clear();
            foreach (var d in list) camCombo.Items.Add(d);
            camCombo.SelectedItem = list.FirstOrDefault(d => d.Key == keep) ?? list.FirstOrDefault();
            camCombo.EndUpdate();
            OpenCamera();
        }

        class FormatChoice
        {
            public int W, H, Fps;
            public bool Native;
            public string Name;
            public string Key { get { return W + "x" + H + "@" + Fps; } }
            public override string ToString() { return Name + "  " + Fps + " fps" + (Native ? "  \u2713" : "  (force)"); }
        }

        void LoadFormats(CameraProfile profile)
        {
            var modes = camDev.Modes();
            var choices = new List<FormatChoice>();
            var tiers = new[] { new object[] { 3840, 2160, "4K 2160p" }, new object[] { 2560, 1440, "1440p" }, new object[] { 1920, 1080, "1080p" }, new object[] { 1280, 720, "720p" } };
            foreach (var t in tiers)
                foreach (int fps in new[] { 60, 30 })
                {
                    int w = (int)t[0], h = (int)t[1];
                    choices.Add(new FormatChoice { W = w, H = h, Fps = fps, Name = (string)t[2], Native = modes.Any(m => m.Width == w && m.Height == h && m.Fps >= fps - 0.5) });
                }
            // Other real modes of 720 lines or more (e.g. 1600x896) so nothing the camera offers is hidden.
            foreach (var m in modes.Where(m => m.Height >= 720 && !choices.Any(c => c.W == m.Width && c.H == m.Height)))
                choices.Add(new FormatChoice { W = m.Width, H = m.Height, Fps = (int)Math.Round(m.Fps), Name = m.Width + "x" + m.Height, Native = true });

            formatCombo.BeginUpdate();
            formatCombo.Items.Clear();
            formatCombo.Items.Add("Camera default");
            foreach (var c in choices) formatCombo.Items.Add(c);
            formatCombo.SelectedItem = (object)choices.FirstOrDefault(c => c.Key == profile.PreviewMode) ?? "Camera default";
            formatCombo.EndUpdate();
            if (modes.Count == 0) { fpsBase = fpsInfo.Text = "This camera doesn't report its modes."; return; }
            var best = modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Fps).First();
            restrictedModes = modes.Count <= 2;
            fpsInfo.Cursor = restrictedModes ? Cursors.Hand : Cursors.Default;
            fpsInfo.ForeColor = restrictedModes ? Theme.Warn : Theme.Muted;
            // Windows 11 "Multiple app camera mode" decodes the camera to one shared format and hides its real modes.
            fpsBase = fpsInfo.Text = restrictedModes
                ? "Windows only offers " + best + ". If \"Multiple app camera mode\" is on, turn it off to unlock all modes - click here."
                : "\u2713 = camera lists it, (force) = asked for anyway. Camera's best: " + best + ".";
        }

        string fpsBase = "", fpsResult = "";
        bool restrictedModes;

        void OpenCamera()
        {
            StopPreview();
            camRefreshers.Clear();
            propList.Controls.Clear();
            propList.Width = Theme.Px(278);      // lay out at design width, then let it stretch
            camDev = null;
            var info = camCombo.SelectedItem as VideoDeviceInfo;
            if (info == null)
            {
                Theme.Label(propList, "No camera found.", 8, 8, Theme.Body, Theme.Muted);
                return;
            }
            try { camDev = core.Cameras.Get(info); }
            catch (Exception ex) { Theme.Label(propList, "Couldn't open camera: " + ex.Message, 8, 8, Theme.Small, Theme.Warn, 260); return; }

            var profile = core.Cameras.Profile(info.Key);
            loading = true;
            lockToggle.Checked = profile.Lock;
            loading = false;
            LoadFormats(profile);

            int y = 0;
            foreach (var prop in camDev.Properties)
            {
                var p = prop;
                var name = Theme.Label(propList, p.Name, 8, y, Theme.Body);
                var val = new Label { ForeColor = Theme.Text, Font = Theme.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight, Location = new Point(Theme.Px(150), Theme.Px(y)), Size = new Size(Theme.Px(56), Theme.Px(20)) };
                propList.Controls.Add(val);
                DarkCheck auto = null;
                if (p.SupportsAuto)
                {
                    auto = new DarkCheck("Auto") { Location = new Point(Theme.Px(212), Theme.Px(y)) };
                    propList.Controls.Add(auto);
                }
                var sl = new FlatSlider { Minimum = p.Min, Maximum = p.Max, Step = p.Step, DefaultValue = p.Default, Location = new Point(0, Theme.Px(y + 22)), Size = new Size(Theme.Px(266), Theme.Px(22)) };
                propList.Controls.Add(sl);
                bool busy = false;
                Action refresh = () =>
                {
                    int v; bool a;
                    if (!camDev.Get(p, out v, out a)) return;
                    busy = true;
                    sl.SetSilently(v);
                    val.Text = v.ToString();
                    if (auto != null) auto.Checked = a;
                    sl.Enabled = !a || !p.SupportsAuto;
                    busy = false;
                };
                camRefreshers.Add(refresh);
                sl.ValueChanged += (s, e) =>
                {
                    if (busy) return;
                    camDev.Set(p, sl.Value, false);
                    val.Text = sl.Value.ToString();
                    Remember(p, sl.Value, false);
                };
                if (auto != null)
                    auto.CheckedChanged += (s, e) =>
                    {
                        if (busy) return;
                        camDev.Set(p, sl.Value, auto.Checked);
                        sl.Enabled = !auto.Checked;
                        Remember(p, sl.Value, auto.Checked);
                        refresh();
                    };
                refresh();
                y += 56;
            }
            if (camDev.Properties.Count == 0) Theme.Label(propList, "This camera doesn't expose adjustable settings.", 8, 8, Theme.Small, Theme.Muted, 260);
            propList.Height = Math.Max(Theme.Px(40), Theme.Px(y));
            Responsive.Apply(propList);
            propHost.Relayout();
        }

        void Remember(CameraProperty p, int value, bool auto)
        {
            if (camDev == null) return;
            var profile = core.Cameras.Profile(camDev.Info.Key);
            profile.Values[p.Key] = new CameraPropSetting { Value = value, Auto = auto };
            core.SaveSoon();
        }

        void CaptureAllToProfile(CameraProfile profile) { CaptureAll(profile.Values); }

        void CaptureAll(Dictionary<string, CameraPropSetting> target)
        {
            target.Clear();
            foreach (var p in camDev.Properties)
            {
                int v; bool a;
                if (camDev.Get(p, out v, out a)) target[p.Key] = new CameraPropSetting { Value = v, Auto = a && p.SupportsAuto };
            }
        }

        void RestoreLook()
        {
            if (camDev == null) return;
            var profile = core.Cameras.Profile(camDev.Info.Key);
            if (profile.SavedLook.Count == 0)
            {
                DarkMessage.Show(this, "No saved look for this camera yet. Set it up, then click \"Save current look\".", "Camera look", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (var p in camDev.Properties)
            {
                CameraPropSetting v;
                if (profile.SavedLook.TryGetValue(p.Key, out v)) camDev.Set(p, v.Value, v.Auto && p.SupportsAuto);
            }
            profile.Values = new Dictionary<string, CameraPropSetting>(profile.SavedLook);
            core.SaveSoon();
            RefreshCameraValues();
        }

        void ResetCamera()
        {
            if (camDev == null) return;
            foreach (var p in camDev.Properties) camDev.Set(p, p.Default, p.SupportsAuto && (p.Name == "Exposure" || p.Name == "Focus" || p.Name == "White balance"));
            var profile = core.Cameras.Profile(camDev.Info.Key);
            profile.Values.Clear();
            if (profile.Lock) CaptureAllToProfile(profile);
            core.SaveSoon();
            RefreshCameraValues();
        }

        void RefreshCameraValues() { foreach (var r in camRefreshers) r(); }

        void StartPreview()
        {
            var info = camCombo.SelectedItem as VideoDeviceInfo;
            if (info == null) { previewToggle.SetSilently(false); return; }
            if (fxCard != null) fxCard.RawPreviewStarted();
            if (preview == null) preview = new CameraPreview();
            previewMsg.Visible = false;
            var c = formatCombo.SelectedItem as FormatChoice;
            string err = c != null ? preview.Start(info, previewHost, c.W, c.H, c.Fps) : preview.Start(info, previewHost, 0, 0, 0);
            if (err != null)
            {
                previewMsg.Text = err;
                previewMsg.Visible = true;
                previewToggle.SetSilently(false);
                fpsInfo.Text = fpsBase;
                return;
            }
            var a = preview.ActiveMode;
            string got = a != null ? a.Width + "x" + a.Height : "?";
            fpsResult = c == null ? "Camera default: " + got
                : preview.RequestRefused ? "Camera refused " + c.W + "x" + c.H + " " + c.Fps + " fps - closest it does: " + got
                : "Camera accepted " + c.W + "x" + c.H + " " + c.Fps + " fps";
            fpsInfo.Text = fpsResult + ". Measuring real fps...";
        }

        void StopPreview()
        {
            if (preview != null) { preview.Dispose(); preview = null; }
            if (previewToggle != null) previewToggle.SetSilently(false);
            if (previewMsg != null) { previewMsg.Text = "Preview is off"; previewMsg.Visible = true; }
        }

        // ------------------------------------------------------------------ Settings

        static string ThirdPartyNotices()
        {
            string sadie = "";
            try
            {
                using (var st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SADIE-LICENSE.txt"))
                using (var r = new System.IO.StreamReader(st)) sadie = r.ReadToEnd();
            }
            catch { }
            return "\r\n\r\n----------------------------------------------------------------\r\n" +
                "3D Audio head-related impulse responses: SADIE II database, Department of Electronic Engineering, University of York.\r\n" +
                "C. Armstrong, L. Thresh, D. Murphy, G. Kearney, \"A Perceptual Evaluation of Individual and Non-Individual HRTFs: A Case Study of the SADIE II Database\", Applied Sciences 8(11), 2018.\r\n" +
                "Converted (diffuse-field normalized, trimmed to 192 taps) by StreamLite. Licensed under the Apache License 2.0:\r\n\r\n" + sadie +
                "\r\n\r\n----------------------------------------------------------------\r\n" +
                "Per-app audio routing interface layout from EarTrumpet (https://github.com/File-New-Project/EarTrumpet), MIT License, Copyright (c) File-New-Project.\r\n" +
                "HeSuVi / Impulcifer 14-channel HRIR track order from Impulcifer (https://github.com/jaakkopasanen/Impulcifer), MIT License, Copyright (c) Jaakko Pasanen.\r\n" +
                "\r\n----------------------------------------------------------------\r\n" +
                "DeepFilterNet 3 noise removal (https://github.com/Rikorose/DeepFilterNet), MIT OR Apache-2.0, Copyright (c) Hendrik Schroeter; streaming export by torchDF.\r\n" +
                "ONNX Runtime (https://onnxruntime.ai), MIT License, Copyright (c) Microsoft Corporation. DirectML, Microsoft DirectML license.\r\n" +
                "Full texts: the \"ai\" folder next to StreamLite.exe.\r\n" +
                "\r\n----------------------------------------------------------------\r\n" +
                "Everything StreamLite " + Program.Version + " uses (also in the Credits window):\r\n\r\n" + Credits.PlainText(Program.Version.ToString());
        }

        Panel BuildSettingsPage()
        {
            var page = NewPage("Settings");
            var c = NewCard(page, 54, 248);
            Theme.Label(c, "Startup", 20, 16, Theme.Section);
            startupToggle = Place(c, new ToggleSwitch(), 20, 54);
            Theme.Help(Theme.Label(c, "Launch when Windows starts", 74, 54, Theme.Bold), "Starts StreamLite when you sign in, so your clean mic is ready before you open Discord.");
            Theme.Label(c, "Shows up in Task Manager > Startup apps, where you can also turn it off.", 74, 76, Theme.Small, Theme.Muted);
            startupToggle.CheckedChanged += (s, e) => { if (loading) return; StartupRegistration.Set(startupToggle.Checked); };
            minimizedToggle = Place(c, new ToggleSwitch(), 20, 110);
            Theme.Help(Theme.Label(c, "Start hidden in the system tray when launched at startup", 74, 110, Theme.Bold), "At startup the app runs quietly in the tray instead of opening its window.");
            minimizedToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.StartMinimized = minimizedToggle.Checked; core.SaveSoon(); };
            adminToggle = Place(c, new ToggleSwitch(), 20, 154);
            Theme.Help(Theme.Label(c, "Run as administrator", 74, 154, Theme.Bold), "Runs with admin rights (no prompt each time) so enabling/disabling cameras and Equalizer APO changes never ask for permission.");
            Theme.Label(c, "Starts with admin rights without a UAC prompt every time (uses a Windows scheduled task).", 74, 176, Theme.Small, Theme.Muted);
            Theme.Label(c, "Windows asks for permission once when you turn this on or off.", 74, 194, Theme.Small, Theme.Muted);
            adminStatus = Theme.Label(c, "", 74, 216, Theme.Small, Theme.Muted, 590);
            adminToggle.CheckedChanged += (s, e) => { if (!loading) SetRunAsAdmin(adminToggle.Checked); };

            var close = NewCard(page, 316, 96);
            Theme.Label(close, "When I close the window", 20, 16, Theme.Section);
            Theme.Label(close, "The X button can keep your mic and camera effects running in the tray, or stop everything.", 20, 44, Theme.Small, Theme.Muted);
            closeCombo = Place(close, new DarkCombo(), 20, 62, 0);
            closeCombo.Location = new Point(Theme.Px(420), Theme.Px(16));
            closeCombo.Width = Theme.Px(250);
            closeCombo.Items.AddRange(new object[] { "Ask me every time", "Exit app, keep service running", "Stop service and quit" });
            closeCombo.SelectionChangeCommitted += (s, e) =>
            {
                core.S.CloseAction = closeCombo.SelectedIndex == 1 ? "background" : closeCombo.SelectedIndex == 2 ? "exit" : "ask";
                core.SaveSoon();
            };

            BuildAiQualityCard(page, 426);

            var devs = NewCard(page, 646, 250);
            Theme.Label(devs, "Audio devices", 20, 16, Theme.Section);
            tidyToggle = Place(devs, new ToggleSwitch(), 20, 54);
            Theme.Help(Theme.Label(devs, "Keep the device list tidy", 74, 54, Theme.Bold),
                "Names the virtual devices StreamLite - In (your clean mic), StreamLite - Out (game sound for 3D audio) and StreamLite - Mic Feed, and switches off the cable devices nothing uses (CABLE In 16ch, Hi-Fi Cable Output) and other virtual cables nothing is set to use (Virtual Audio Cable's lines, more VB-Audio cables - never Voicemeeter or Steam's).\nThe Mic Feed is hidden too when StreamLite runs as administrator: it then writes your mic straight into the cable driver. Discord shows the devices as \"StreamLite Mic\" and \"StreamLite Game Audio\". Without admin rights Windows asks for permission once; off brings everything back.");
            Theme.Label(devs, "Windows lists only your real devices plus StreamLite - In and StreamLite - Out.", 74, 76, Theme.Small, Theme.Muted);
            tidyNow = Place(devs, new FlatButton("Tidy up now", true), 540, 50, 130);
            Theme.Help(tidyNow, "Not tidy yet (VB-Audio's names or extra cables still show). Windows asks for permission once.");
            tidyNow.Visible = false;
            tidyNow.Click += (s, e) => { var ctx = Program.Context; if (ctx != null) ctx.TidyNow(); tidyNow.Visible = false; };
            tidyToggle.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                core.S.HideVirtualDevices = tidyToggle.Checked; core.SaveSoon();
                if (!Elevation.IsElevated)
                {
                    // Renaming and switching devices needs admin rights: one Windows prompt for a helper that does just that.
                    bool on = tidyToggle.Checked;
                    new System.Threading.Thread(() =>
                    {
                        bool asked = DriverInstaller.RunElevated(Application.ExecutablePath, on ? "--name-devices" : "--untidy-devices");
                        if (!asked) try { BeginInvoke(new Action(() => DarkMessage.Show(this, "Windows' permission prompt was declined, so the sound devices stay as they are.", "Keep the device list tidy", MessageBoxButtons.OK, MessageBoxIcon.Information))); } catch { }
                    }) { IsBackground = true }.Start();
                    return;
                }
                if (tidyToggle.Checked) core.TidyDevices(); else core.UntidyDevices();
            };
            formatToggle = Place(devs, new ToggleSwitch(), 20, 110);
            Theme.Help(Theme.Label(devs, "Best working audio format for every device", 74, 110, Theme.Bold),
                "When StreamLite first sees a headset, mic or speakers, it sets the device's highest native bit depth at 48 kHz - the rate games, Discord, browsers and StreamLite all produce - and runs a short test stream to make sure the device really works at it (otherwise it keeps the old format).\nHigher rates like 192 or 384 kHz don't add anything here: Windows just resamples 48 kHz up, which costs CPU (also in Equalizer APO). Extra channels on a mic are empty and make apps that downmix hear you quieter.\nStreamLite - In (stereo, 48 kHz) and StreamLite - Out (7.1, 48 kHz) are always kept right. If you change a real device's format in Windows afterwards, StreamLite leaves it alone. Bluetooth devices are skipped (their codec decides).");
            Theme.Label(devs, "Highest bit depth at 48 kHz, tested with a real stream. Turning this on re-checks all devices.", 74, 132, Theme.Small, Theme.Muted);
            formatToggle.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                core.S.AutoFormats = formatToggle.Checked; core.SaveSoon();
                if (formatToggle.Checked) core.RecheckFormats();
            };
            formatInfo = Theme.Label(devs, "", 74, 154, Theme.Small, Theme.Muted, 600);
            formatInfo.AutoSize = false; formatInfo.Height = Theme.Px(34);
            duckToggle = Place(devs, new ToggleSwitch(), 20, 194);
            Theme.Help(Theme.Label(devs, "Keep game volume during calls", 74, 194, Theme.Bold),
                "Windows turns every other sound down by 80% while a call app (Discord, Teams, Zoom...) has a voice stream open - so your game gets quiet whenever a friend talks.\nOn: Windows leaves other sounds alone (Sound control panel > Communications > Do nothing). Off: Windows' own default.\nDiscord has a similar setting of its own: Voice & Video > Attenuation.");
            duckInfo = Theme.Label(devs, "", 74, 216, Theme.Small, Theme.Muted, 600);
            duckToggle.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                Ducking.Set(duckToggle.Checked);
                duckInfo.Text = Ducking.Describe(Ducking.Preference);
            };

            var share = NewCard(page, 910, 140);
            Theme.Label(share, "Your setup", 20, 16, Theme.Section);
            Theme.Help(Theme.Label(share, "Move it to another PC, keep a copy, or share it with a friend", 20, 44, Theme.Small, Theme.Muted),
                "A setup file (.streamlite) holds your mic chain (noise removal, compressor, voice EQ...), every microphone's and output's volume and EQ, your headphones' corrections and personal 3D, the 3D sound settings, game profiles with their apps, the apps kept out of 3D, hotkeys and the camera look.\nDevices are kept by name only - no device IDs, no file paths (a custom head model or background picture stays on this PC) - so it's safe to share. Importing shows everything first, matches each device by name on that PC and lets you pick another one or leave it out.");
            var export = Place(share, new FlatButton("Export setup..."), 20, 70);
            export.Click += (s, e) => ExportSetup();
            var import = Place(share, new FlatButton("Import setup..."), 0, 70);
            import.Left = export.Right + Theme.Px(10);
            import.Click += (s, e) => ImportSetup(null);
            undoImport = Place(share, new FlatButton("Undo import"), 0, 70);
            undoImport.Left = import.Right + Theme.Px(10);
            Responsive.Fixed(undoImport);
            Theme.Help(undoImport, "Puts every setting back exactly as it was before the last import.");
            undoImport.Click += (s, e) => UndoImport();
            var presets = Place(share, new FlatButton("Mic presets..."), 0, 70);
            presets.Left = undoImport.Right + Theme.Px(10);
            Responsive.Fixed(presets);
            Theme.Help(presets, "Each microphone's own settings: export one to take your mic to a friend's PC, import one for any mic (plugged in or not).");
            presets.Click += (s, e) => { using (var d = new MicPresetsDialog(core)) d.ShowDialog(this); LoadAll(); };
            setupStatus = Theme.Label(share, "", 20, 108, Theme.Small, Theme.Muted, 640);

            var upd = NewCard(page, 1066, 170);
            Theme.Label(upd, "Updates and bug reports", 20, 16, Theme.Section);
            updateToggle = Place(upd, new ToggleSwitch(), 20, 54);
            Theme.Help(Theme.Label(upd, "Check for updates when StreamLite starts", 74, 54, Theme.Bold),
                "When StreamLite starts (and every 6 hours while it runs) it asks GitHub for the newest release. If there is one, a prompt at the top of this window offers Update now or Skip. Update now downloads it, checks it (SHA-256), installs it the way you installed StreamLite (MSI or zip) and restarts - the mic drops out for a few seconds. Skip doesn't ask again until a newer version is out.\nWhile StreamLite is in the tray, a note says an update is waiting; the prompt is there when you open the window.");
            Theme.Label(upd, "Asks \"Update now or Skip\" in this window when a new version is out.", 74, 76, Theme.Small, Theme.Muted);
            updateToggle.CheckedChanged += (s, e) => { if (loading) return; core.S.UpdateOnStartup = updateToggle.Checked; core.SaveSoon(); };
            updateStatus = Theme.Label(upd, "", 74, 96, Theme.Small, Theme.Muted, 600);
            var checkNow = Place(upd, new FlatButton("Check now"), 20, 124);
            checkNow.Click += (s, e) => { Program.CheckForUpdates(); updateStatus.Text = UpdateStatusText(); };
            var report = Place(upd, new FlatButton("Report a bug", true), 0, 124);
            report.Left = checkNow.Right + Theme.Px(10);
            Theme.Help(report, "Opens the bug report form on GitHub with your StreamLite and Windows version filled in. Describe what happened and what you expected; attach error.log from the settings folder if there is one.");
            report.Click += (s, e) => Program.ReportBug();
            var details = Place(upd, new FlatButton("Copy details"), 0, 124);
            details.Left = report.Right + Theme.Px(10);
            Theme.Help(details, "Copies your StreamLite and Windows version, what StreamLite is doing and your audio devices with their formats - paste it into the bug report so device problems can be fixed. Nothing is sent anywhere by this button.");
            details.Click += (s, e) =>
            {
                try { Clipboard.SetText(Diagnostics.Report(core)); updateStatus.Text = "Details copied - paste them into the bug report (Ctrl+V)."; }
                catch (Exception ex) { updateStatus.Text = "Couldn't copy: " + ex.Message; }
            };
            var known = Place(upd, new FlatButton("Known issues"), 0, 124);
            known.Left = details.Right + Theme.Px(10);
            Responsive.Fixed(details); Responsive.Fixed(known);
            known.Click += (s, e) => Updater.OpenUrl(Updater.IssuesPage);
            var news = Place(upd, new FlatButton("What's new"), 0, 124);
            news.Left = known.Right + Theme.Px(10);
            Responsive.Fixed(news);
            news.Click += (s, e) => Updater.OpenUrl(Updater.ReleasesPage);

            var about = NewCard(page, 1250, 150);
            Theme.Label(about, "About", 20, 18, Theme.Section);
            var t = new Label
            {
                ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent,
                Location = new Point(Theme.Px(20), Theme.Px(46)), Size = new Size(Theme.Px(650), Theme.Px(56)),
                Text = "StreamLite " + new Version(Application.ProductVersion).ToString(2) + ". Without admin rights, Windows asks for permission only when you enable/disable a camera or set up Equalizer APO.\nSettings are stored in " + AppSettings.Folder
            };
            about.Controls.Add(t);
            var folder = Place(about, new FlatButton("Open settings folder"), 20, 106);
            folder.Click += (s, e) => { System.IO.Directory.CreateDirectory(AppSettings.Folder); Process.Start("explorer.exe", AppSettings.Folder); };
            var quit = Place(about, new FlatButton("Stop service && quit"), 0, 106);
            quit.Left = folder.Right + Theme.Px(10);
            quit.Click += (s, e) => Program.Exit();
            var lic = Place(about, new FlatButton("Open-source licenses"), 0, 106);
            lic.Left = quit.Right + Theme.Px(10);
            Theme.Help(lic, "AI voice isolation uses RNNoise (c) 2017 Jean-Marc Valin, Mozilla, Xiph.Org Foundation - BSD license.\n3D audio uses the SADIE II HRTF database (University of York, Apache-2.0) and per-app routing from EarTrumpet (MIT).");
            lic.Click += (s, e) => ShowLicenses();
            var uninstall = Place(about, new FlatButton("Uninstall..."), 0, 106);
            uninstall.Left = lic.Right + Theme.Px(10);
            Responsive.Fixed(uninstall);   // stays in the row of buttons at any width
            Theme.Help(uninstall, "Removes StreamLite and puts your sound devices back the way Windows had them (default devices, device names, the cable devices it switched off). Also in Windows Settings > Apps.");
            uninstall.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--uninstall") { UseShellExecute = true }); } catch { }
            };
            page.Height = Theme.Px(1416);
            return page;
        }

        // ---- AI quality: one choice for where the AI runs and how big its models are (voice isolation + AI camera)

        readonly List<FlatButton> tierButtons = new List<FlatButton>();
        Label tierInfo, tierNow;
        Card aiCard;

        void BuildAiQualityCard(Panel page, int y)
        {
            var c = aiCard = NewCard(page, y, 210);
            Theme.Help(Theme.Label(c, "AI quality", 20, 16, Theme.Section),
                "Where StreamLite's AI runs - voice isolation (always on with noise removal: the best noise remover this tier can run) and the AI camera - and how big its models are. Each step up sounds and looks better and uses more video memory.\n" +
                "Low VRAM: about 210 MB of video memory. Normal: about 430-490 MB. High VRAM: about 860-890 MB. Full CPU: no graphics card at all (the slowest; camera effects may switch off).");
            Theme.Label(c, "Where voice isolation and the AI camera run, and how big their models are.", 20, 42, Theme.Small, Theme.Muted);
            int x = 20;
            foreach (var tier in AiTiers.All)
            {
                string tt = tier;
                var b = Place(c, new FlatButton(AiTiers.Label(tier)), x, 66, 150);
                b.Click += (s, e) => { core.SetAiTier(tt); ShowTier(); };
                Theme.Help(b, AiTiers.Label(tier) + " - " + AiTiers.Budget(tier) + ".\n" + AiTiers.Describe(tier));
                tierButtons.Add(b);
                x += 160;
            }
            tierInfo = Theme.Label(c, "", 20, 108, Theme.Body, Theme.Text, 650);
            tierInfo.AutoSize = false; tierInfo.Height = Theme.Px(56);
            tierNow = Theme.Label(c, "", 20, 170, Theme.Small, Theme.Muted, 650);
            ShowTier();
        }

        /// <summary>The chosen tier lit, what it does, and what's running right now.</summary>
        void ShowTier()
        {
            if (tierButtons.Count == 0) return;
            string cur = core.S.AiTier ?? AiTiers.Normal;
            for (int i = 0; i < AiTiers.All.Length; i++)
            {
                bool on = AiTiers.All[i] == cur; var b = tierButtons[i];
                b.BackColor = on ? Theme.Accent : Theme.Field; b.ForeColor = on ? Color.FromArgb(10, 25, 14) : Theme.Text;
                b.FlatAppearance.MouseOverBackColor = on ? Color.FromArgb(120, 225, 148) : Theme.FieldHover;
                b.Font = on ? Theme.Bold : Theme.Body;
            }
            tierInfo.Text = AiTiers.Label(cur) + " - " + AiTiers.Budget(cur) + ". " + AiTiers.Describe(cur);
            string vram = core.Health.VramMb >= 0 ? core.Health.VramMb + " MB of video memory in use now" : "Video memory use not available";
            tierNow.Text = "Voice isolation: " + core.VoiceIsolationText() + ".  " + vram + ".";
        }

        FlatButton undoImport;
        Label setupStatus;

        void ExportSetup()
        {
            using (var dlg = new SaveFileDialog { Title = "Export your StreamLite setup", Filter = "StreamLite setup (*.streamlite)|*.streamlite", FileName = "StreamLite setup " + DateTime.Now.ToString("yyyy-MM-dd") + SetupExport.Extension })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var all = AudioDevices.List(EDataFlow.Render, 1 | 2 | 8).Concat(AudioDevices.List(EDataFlow.Capture, 1 | 2 | 8)).ToList();
                    Func<string, string> name = id => all.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefault();
                    var f = SetupExport.Export(core.S, name(core.ActiveMicId), name, Program.Version.ToString(3));
                    System.IO.File.WriteAllText(dlg.FileName, SetupExport.ToJson(f));
                    setupStatus.ForeColor = Theme.Muted;
                    setupStatus.Text = "Saved " + System.IO.Path.GetFileName(dlg.FileName) + " - " + string.Join(", ", SetupExport.Describe(f).Select(p => p.Key == "mic" ? "mic chain" : p.Key == "3d" ? "3D" : p.Key)) + ".";
                }
                catch (Exception ex) { setupStatus.ForeColor = Theme.Warn; setupStatus.Text = "Couldn't export: " + ex.Message; }
            }
        }

        /// <summary>Import a setup file (path = null: pick one), with the preview first.</summary>
        public void ImportSetup(string path)
        {
            if (path == null)
                using (var dlg = new OpenFileDialog { Title = "Import a StreamLite setup", Filter = "StreamLite setup (*.streamlite)|*.streamlite|All files (*.*)|*.*" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            SetupFile f;
            try { f = SetupExport.Parse(System.IO.File.ReadAllText(path)); }
            catch (Exception ex) { setupStatus.ForeColor = Theme.Warn; setupStatus.Text = "Couldn't import: " + ex.Message; return; }
            var inputs = core.MicChoices().Where(d => d.Id != core.ActiveMicId && !AudioDevices.IsCable(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
            var outputs = core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
            string phones = core.GamePhonesId ?? core.RealDefaultOutput();
            var auto = SetupExport.AutoPlan(f, inputs, outputs, core.ActiveMicId, phones);
            using (var d = new SetupImportDialog(f, auto, inputs, outputs))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SetupExport.Snapshot(core.S);
                    var names = inputs.Concat(outputs).ToList();
                    SetupExport.Apply(core.S, f, d.Plan, core.ActiveMicId, phones, id => names.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefault());
                    AfterSetupChange();
                    setupStatus.ForeColor = Theme.Accent;
                    setupStatus.Text = "Imported " + System.IO.Path.GetFileName(path) + ". Undo import puts your previous settings back.";
                }
                catch (Exception ex) { setupStatus.ForeColor = Theme.Warn; setupStatus.Text = "Couldn't import: " + ex.Message; }
            }
        }

        void UndoImport()
        {
            try
            {
                SetupExport.Undo(core.S);
                AfterSetupChange();
                setupStatus.ForeColor = Theme.Muted;
                setupStatus.Text = "Your settings from before the import are back.";
            }
            catch (Exception ex) { setupStatus.ForeColor = Theme.Warn; setupStatus.Text = "Couldn't undo: " + ex.Message; }
        }

        /// <summary>After an import or undo: every engine and page picks up the settings.</summary>
        void AfterSetupChange()
        {
            core.S.Save();
            core.PushEngineParams();
            core.ApplyExtraMics();
            core.RestartGameAudio();
            core.Profiles.Reapply();
            if (Hotkeys != null) Hotkeys.Reload();
            LoadAll();
            if (gamePage != null) gamePage.LoadAll();
            undoImport.Enabled = SetupExport.CanUndo;
        }

        /// <summary>Every license text StreamLite ships, in Notepad.</summary>
        void ShowLicenses()
        {
            string path = System.IO.Path.Combine(AppSettings.Folder, "THIRD-PARTY-NOTICES.txt");
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            System.IO.File.WriteAllText(path, StreamLite.Audio.Rnnoise.License + ThirdPartyNotices());
            Process.Start("notepad.exe", "\"" + path + "\"");
        }

        static string UpdateStatusText()
        {
            return Updater.Status ?? ("You have StreamLite " + Program.Version + "." + (Updater.InstallKind == null ? " This copy isn't installed by a StreamLite installer, so updates are only announced." : ""));
        }

        void SetRunAsAdmin(bool on)
        {
            Cursor = Cursors.WaitCursor;
            string err = on ? Elevation.CreateTask() : Elevation.DeleteTask();
            Cursor = Cursors.Default;
            if (err != null)
            {
                loading = true;
                adminToggle.Checked = !on;
                loading = false;
                using (var d = new ConfirmDialog("Run as administrator", err, "OK", "Close")) d.ShowDialog(this);
                return;
            }
            core.S.RunAsAdmin = on;
            core.S.Save();
            RefreshAdminStatus();
            if (on && !Elevation.IsElevated)
                using (var d = new ConfirmDialog("Run as administrator", "Done. Restart StreamLite now with administrator rights? Your mic drops out for a few seconds.", "Restart now", "Later"))
                    if (d.ShowDialog(this) == DialogResult.OK) Program.RestartElevated();
        }

        void RefreshAdminStatus()
        {
            bool elevated = Elevation.IsElevated;
            adminStatus.ForeColor = elevated ? Theme.Accent : Theme.Muted;
            adminStatus.Text = elevated ? "Running as administrator now."
                : core.S.RunAsAdmin ? "Running without admin rights - takes effect next time StreamLite starts."
                : "Running without administrator rights.";
        }

        string FormatInfoText()
        {
            string f = core.FormatSummary ?? "";
            if (f == "not checked yet") return "Checking your devices...";
            if (f == "as set") return "Every device already runs its best working format.";
            return "Last change " + f;
        }

        void LoadSettings()
        {
            formatInfo.Text = FormatInfoText();
            loading = true;
            startupToggle.Checked = StartupRegistration.IsEnabled;
            minimizedToggle.Checked = core.S.StartMinimized;
            tidyToggle.Checked = core.S.HideVirtualDevices;
            try { tidyNow.Visible = !Elevation.IsElevated && core.S.HideVirtualDevices && CableNames.NeedsTidying(true); } catch { tidyNow.Visible = false; }
            formatToggle.Checked = core.S.AutoFormats;
            updateToggle.Checked = core.S.UpdateOnStartup;
            undoImport.Enabled = SetupExport.CanUndo;
            int duck = Ducking.Preference;
            duckToggle.Checked = duck == 3;
            duckInfo.Text = Ducking.Describe(duck);
            updateStatus.Text = UpdateStatusText();
            adminToggle.Checked = core.S.RunAsAdmin && Elevation.TaskExists();
            string a = core.S.CloseAction ?? "ask";
            closeCombo.SelectedIndex = a == "background" ? 1 : a == "exit" ? 2 : 0;
            loading = false;
            RefreshAdminStatus();
        }

        // ------------------------------------------------------------------

        void LoadAll()
        {
            LoadMic();
            LoadOutput();
            LoadCameras();
            LoadSettings();
            if (hotkeysPage != null) hotkeysPage.Reload();
        }

        // ---- "Update available" prompt: inside the window, over the top of the page (no separate window)

        Panel updateBanner;
        Label updateText;
        FlatButton updateNow, updateSkip;
        Action onUpdateNow, onUpdateSkip;

        /// <summary>Shows the in-app update prompt (or changes its text, e.g. while downloading: buttons = false).</summary>
        public void ShowUpdatePrompt(string text, Action now, Action skip, bool buttons = true, string nowLabel = "Update now", string skipLabel = "Skip")
        {
            if (updateBanner == null)
            {
                updateBanner = new Panel { BackColor = Theme.Card, Height = Theme.Px(84) };
                updateBanner.Paint += (s, e) =>
                {
                    using (var pen = new Pen(Theme.Accent, Theme.Px(2))) e.Graphics.DrawRectangle(pen, 1, 1, updateBanner.Width - 3, updateBanner.Height - 3);
                };
                updateText = new Label { ForeColor = Theme.Text, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(16), Theme.Px(10)), AutoSize = false, UseMnemonic = false };
                updateNow = new FlatButton("Update now", true);
                updateSkip = new FlatButton("Skip");
                updateNow.Click += (s, e) => { var a = onUpdateNow; if (a != null) a(); };
                updateSkip.Click += (s, e) => { var a = onUpdateSkip; HideUpdatePrompt(); if (a != null) a(); };
                Theme.Help(updateSkip, "Skips this version: StreamLite asks again when a newer one comes out. Settings > Updates > Check now still installs it.");
                updateBanner.Controls.Add(updateText); updateBanner.Controls.Add(updateNow); updateBanner.Controls.Add(updateSkip);
                Controls.Add(updateBanner);
                Resize += (s, e) => PlaceUpdatePrompt();
            }
            onUpdateNow = now; onUpdateSkip = skip;
            updateNow.Text = nowLabel; updateSkip.Text = skipLabel;
            updateText.Text = text;
            updateNow.Visible = updateSkip.Visible = buttons;
            PlaceUpdatePrompt();
            updateBanner.Visible = true;
            updateBanner.BringToFront();
        }

        public void HideUpdatePrompt() { if (updateBanner != null) updateBanner.Visible = false; }
        public bool UpdatePromptVisible { get { return updateBanner != null && updateBanner.Visible; } }

        /// <summary>"Tune StreamLite to your voice" (also offered once after installing or updating), then the page shows what changed.</summary>
        public void OpenVoiceSetup()
        {
            using (var d = new VoiceSetupDialog(core)) d.ShowDialog(this);
            LoadMic();
        }

        void PlaceUpdatePrompt()
        {
            if (updateBanner == null) return;
            int w = Math.Min(content.Width - Theme.Px(40), Theme.Px(760));
            updateBanner.Width = Math.Max(Theme.Px(360), w);
            updateBanner.Location = new Point(content.Left + (content.Width - updateBanner.Width) / 2, content.Top + Theme.Px(10));
            updateSkip.Location = new Point(updateBanner.Width - Theme.Px(16) - updateSkip.Width, (updateBanner.Height - updateSkip.Height) / 2);
            updateNow.Location = new Point(updateSkip.Left - Theme.Px(10) - updateNow.Width, updateSkip.Top);
            updateText.Size = new Size((updateNow.Visible ? updateNow.Left : updateBanner.Width) - Theme.Px(28), updateBanner.Height - Theme.Px(20));
        }

        HealthPage healthPage;

        /// <summary>A setup-check fix (Health page).</summary>
        void Fix(string id)
        {
            switch (id)
            {
                case "install-vbcable": InstallVbCable(); break;
                case "install-hifi": if (gamePage != null) gamePage.InstallHiFiCable(); break;
                case "install-apo": InstallEqualizerApo(); break;
                case "apo-devices": EqualizerApo.OpenConfigurator(); break;
                case "mic-privacy": DeviceErrors.OpenMicPrivacy(); break;
                case "mic-volume": { var c = core.PairedCapture(core.ActiveOutId); if (c != null) AudioDevices.SetVolume(c.Id, 1f); break; }
                case "restart-audio": core.RestartAudio(); break;
                case "restart-3d": core.RestartGameAudio(); break;
                case "default-mic": core.S.MakeDefaultMic = true; core.ApplyDefaultMic(); core.SaveSoon(); break;
                case "sound-settings": try { System.Diagnostics.Process.Start("ms-settings:sound"); } catch { } break;
            }
        }

        /// <summary>"--setup-check": the full check, written to setup-check.txt next to the settings (support, tests).</summary>
        void CheckToFile()
        {
            new System.Threading.Thread(() =>
            {
                string text;
                try { text = string.Join("\r\n", SetupCheck.Run(core, true, _ => { }).Select(r => (r.State == CheckItem.Ok ? "OK   " : r.State == CheckItem.Warn ? "WARN " : r.State == CheckItem.Fail ? "FAIL " : "INFO ") + r.Title + ": " + r.Detail + (r.FixId != null ? "  [fix: " + r.FixLabel + "]" : ""))); }
                catch (Exception ex) { text = "FAIL setup check: " + ex.Message; }
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppSettings.Folder, "setup-check.txt"), "time: " + DateTime.Now.ToString("s") + "\r\n" + text + "\r\n"); } catch { }
            }) { IsBackground = true }.Start();
        }

        /// <summary>Opens the Health page and runs the setup check (e.g. from the after-update note).</summary>
        public void ShowSetupCheck()
        {
            ShowPage(pages.IndexOf(healthPage));
            healthPage.RunCheck();
        }

        /// <summary>The global hotkeys (set by the tray app once the window exists).</summary>
        public HotkeyManager Hotkeys;
        HotkeysPage hotkeysPage;

        /// <summary>A hotkey changed settings: show them (when the window is open).</summary>
        public void ReloadFromHotkey()
        {
            if (!Visible) return;
            LoadAll();
            if (gamePage != null) gamePage.LoadAll();
        }

        int slowTick;
        void UiTick()
        {
            var e = core.Engine;
            inMeter.Level = e.Running ? e.Gate.LevelDb : -100f;
            inMeter.ThresholdDb = core.S.Gate ? (float?)core.S.GateThreshold : null;
            inMeter.GateOpen = e.Gate.IsOpen;
            outMeter.Level = e.OutputLevelDb;
            if (pages[0].Visible && (slowTick % 3 == 0 || core.VoiceTest.Playing)) RefreshVoiceTest();
            if (pages[0].Visible) TickMics();
            if (outPage != null && outPage.Visible && slowTick % 5 == 0) TickOuts();
            if (++slowTick % 15 == 0)
            {
                if (pages[0].Visible) ShowVoiceIsolation();
                if (pages[pages.Count - 1].Visible) ShowTier();
                float g = e.Leveler.CurrentGainDb;
                autoLevelVal.Text = !core.S.AutoLevel ? "Off - your mic is sent at its natural level"
                    : "Keeps your voice loud and steady" + (e.Running ? "  -  now " + (g >= 0 ? "+" : "") + g.ToString("0.0") + " dB" : "");
                string mic = micCombo.SelectedItem != null ? micCombo.SelectedItem.ToString() : "-";
                // The mic was plugged back in and the engine reconnected: drop the "(unplugged)" entry.
                if (mic.EndsWith("(unplugged)") && e.Status.StartsWith("Running") && !micCombo.DroppedDown)
                {
                    bool was = loading; loading = true;
                    FillAudioCombo(micCombo, core.MicChoices(), core.ActiveMicId);
                    loading = was;
                }
                statusLabel.Text = (core.S.VirtualMicOn ? "Virtual mic: " + e.Status : "Virtual mic: off") + "\n" + mic;
            }
            if (slowTick % 60 == 0 && pages[CameraPage].Visible) RefreshAutoValues();
            if (slowTick % 60 == 15 && formatInfo != null && formatInfo.Visible) formatInfo.Text = FormatInfoText();
            if (slowTick % 30 == 20 && updateStatus != null && updateStatus.Visible) updateStatus.Text = UpdateStatusText();
            if (slowTick % 60 == 30 && pages[0].Visible) RefreshMicInfo();
            if (slowTick % 60 == 45 && pages[1].Visible && profNow != null && core.Profiles.DetectedExe != null) profNow.Text = ProfileNowText();   // routing status
            if (slowTick % 45 == 0 && preview != null && pages[CameraPage].Visible)
            {
                double fps = preview.SampleFps();
                if (fps >= 0)
                {
                    fpsInfo.Text = fpsResult + " - camera is really sending " + fps.ToString("0.0") + " fps.";
                    // In dim light, auto exposure lengthens each frame and caps the frame rate.
                    var c = formatCombo.SelectedItem as FormatChoice;
                    var exp = camDev != null ? camDev.Properties.FirstOrDefault(p => p.Name == "Exposure") : null;
                    int ev; bool auto;
                    if (c != null && fps < c.Fps * 0.8 && exp != null && camDev.Get(exp, out ev, out auto) && auto)
                        fpsInfo.Text += " Auto exposure is limiting it - untick Exposure \"Auto\" and set -6 or lower.";
                }
            }
        }

        void RefreshAutoValues()
        {
            // Values on "Auto" drift (exposure, white balance); keep the sliders honest.
            if (camDev != null && Control.MouseButtons == MouseButtons.None) RefreshCameraValues();
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}

