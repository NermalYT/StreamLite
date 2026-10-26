using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Camera;

namespace StreamLite.UI
{
    /// <summary>Borderless dark dialog with our own title bar, matching the main window.</summary>
    internal class DarkDialog : Form
    {
        protected readonly Panel Body;

        public DarkDialog(string title, int width, int height)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Theme.Track;            // 1px border
            Padding = new Padding(1);
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Text = title;
            ClientSize = new Size(Theme.Px(width) + 2, Theme.Px(height) + Theme.Px(40) + 2);
            Body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bar = new TitleBar(title, false, false);
            bar.CloseClicked += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(Body);
            Controls.Add(bar);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.StyleWindow(Handle);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected Label Text2(string text, int x, int y, int w, int h, Font font, Color color)
        {
            var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(w), Theme.Px(h)), UseMnemonic = false };
            Body.Controls.Add(l);
            return l;
        }
    }

    /// <summary>Asked when the window is closed: exit the app (service keeps running) or stop the service.</summary>
    internal class CloseDialog : DarkDialog
    {
        public string Choice = "background";
        readonly DarkCheck remember;
        public bool Remember { get { return remember.Checked; } }

        public CloseDialog() : base("Close StreamLite", 470, 300)
        {
            Text2("Do you want to keep the service running?", 22, 18, 430, 24, Theme.Section, Theme.Text);

            var keep = new FlatButton("Exit app", true) { Location = new Point(Theme.Px(22), Theme.Px(60)), Width = Theme.Px(150) };
            keep.Click += (s, e) => { Choice = "background"; DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(keep);
            Text2("Closes the window. The service keeps running in the tray - clean mic, auto level and camera lock stay on.", 186, 60, 270, 48, Theme.Small, Theme.Muted);

            var stop = new FlatButton("Stop service") { Location = new Point(Theme.Px(22), Theme.Px(126)), Width = Theme.Px(150) };
            stop.Click += (s, e) => { Choice = "exit"; DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(stop);
            Text2("Quits completely. Your virtual mic goes silent (Discord hears nothing) and camera lock stops. Headphone EQ keeps working.", 186, 126, 270, 48, Theme.Small, Theme.Muted);

            remember = new DarkCheck("Remember my choice (change it later in Settings)") { Location = new Point(Theme.Px(22), Theme.Px(200)) };
            Body.Controls.Add(remember);

            var cancel = new FlatButton("Cancel") { Location = new Point(Theme.Px(362), Theme.Px(244)) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancel);
            AcceptButton = keep;
            CancelButton = cancel;
        }
    }

    internal class ConfirmDialog : DarkDialog
    {
        public ConfirmDialog(string title, string message, string ok, string cancel) : base(title, 440, 170)
        {
            Text2(message, 22, 18, 400, 70, Theme.Body, Theme.Text);
            var okButton = new FlatButton(ok.Replace("&", "&&"), true);   // "&" is not a shortcut key here
            okButton.Location = new Point(Theme.Px(418) - okButton.Width, Theme.Px(116));
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(okButton);
            var cancelButton = new FlatButton(cancel.Replace("&", "&&"));
            cancelButton.Location = new Point(okButton.Left - Theme.Px(10) - cancelButton.Width, Theme.Px(116));
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancelButton);
            CancelButton = cancelButton;
        }
    }

    /// <summary>Mic "Tuning & EQ" - shown inline on the Microphone page when expanded.</summary>
    internal class MicTuningPanel : Panel
    {
        const int TabTop = 44;
        readonly AppCore core;
        readonly List<Card> tabs = new List<Card>();
        readonly List<FlatButton> tabButtons = new List<FlatButton>();
        readonly EqEditor eq;
        readonly Spectrogram before = new Spectrogram { Caption = "Before - your raw mic" };
        readonly Spectrogram after = new Spectrogram { Caption = "After noise removal + gate - what's left" };
        readonly NoiseSpectrumGraph noiseGraph = new NoiseSpectrumGraph();
        readonly LevelHistoryGraph gateGraph = new LevelHistoryGraph(), outGraph = new LevelHistoryGraph { ShowOutput = true }, voiceGraph = new LevelHistoryGraph { ShowOutput = true };
        readonly Label noiseStats, gateStats, outStats, voiceStats;
        readonly FlatButton useSuggestion;
        readonly Timer timer = new Timer { Interval = 40 };
        readonly List<Action> loaders = new List<Action>();
        int current, ticks;
        float suggestedThreshold;
        bool loading;

        // Auto-EQ capture
        int autoTicks = -1, autoFrames;
        double[] autoSum;

        /// <summary>Raised when a setting shown on the main page (volume, auto level...) changes here.</summary>
        public event EventHandler Changed;

        public MicTuningPanel(AppCore core)
        {
            this.core = core;
            BackColor = Theme.Bg;
            Width = Theme.Px(690);

            string[] names = { "Equalizer", "Noise removal", "Noise gate", "Voice", "Output" };
            int[] heights = { 580, 1450, 760, 690, 590 };
            string[] tips =
            {
                "Shape the tone of your voice: 10-band, precise 31-point, or fully parametric EQ.",
                "Removes steady background noise (fans, PC hum, AC, hiss) while keeping your voice natural. Live graphs show exactly what it removes.",
                "Mutes or turns down the mic between words so remaining room noise and keyboard clicks don't come through.",
                "Pro vocal tools: compressor (even, radio-ready level) and de-esser (tames harsh S sounds).",
                "Final volume, auto level and the limiter that keeps you from distorting."
            };
            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new FlatButton(names[i]) { Location = new Point(x, 0) };
                b.Click += (s, e) => ShowTab(idx);
                Theme.Help(b, tips[i]);
                Controls.Add(b);
                tabButtons.Add(b);
                Responsive.Fixed(b);
                x = b.Right + Theme.Px(8);
                var c = new Card { Location = new Point(0, Theme.Px(TabTop)), Size = new Size(Theme.Px(690), Theme.Px(heights[i])), Visible = false };
                Controls.Add(c);
                tabs.Add(c);
            }

            // ---- Equalizer
            eq = new EqEditor(EqEditor.MicPresets(), true, true, true) { Location = new Point(Theme.Px(5), Theme.Px(14)) };
            eq.Changed += (s, e) => core.PushEngineParams();
            eq.AutoEqButton.Click += (s, e) => StartAutoEq();
            HookCompare(eq.CompareButton);
            tabs[0].Controls.Add(eq);
            Theme.Label(tabs[0], "Grey fill = your raw mic.  Green fill = what apps hear.  Hover any button or name for an explanation.", 10, 548, Theme.Small, Theme.Muted);

            // ---- Noise removal
            var nr = tabs[1];
            Theme.Label(nr, "Talk, then pause - watch the noise vanish in \"After\".", 20, 16, Theme.Small, Theme.Muted);
            HookCompare(Place(nr, new FlatButton("Hold: hear original"), 380, 8, 150));
            AddMonitorToggle(nr, 548, 12);
            Place(nr, before, 20, 44, 650).Height = Theme.Px(110);
            Place(nr, after, 20, 160, 650).Height = Theme.Px(110);
            Theme.Help(before, "Spectrogram of your raw microphone: time runs left to right, low pitches at the bottom, brighter = louder.\nSteady background noise shows up as constant horizontal haze.");
            Theme.Help(after, "The same view after noise removal and the gate. The steady haze should be gone while your voice stays bright.");
            Place(nr, noiseGraph, 20, 280, 650).Height = Theme.Px(170);
            Theme.Help(noiseGraph, "Grey: everything your mic hears.  Green: what's left after noise removal.\nOrange dashes: the background noise the remover has learned. The further green drops below grey in the gaps, the more noise was removed.");
            noiseStats = new Label { ForeColor = Theme.Text, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(458)), Size = new Size(Theme.Px(650), Theme.Px(44)), UseMnemonic = false };
            nr.Controls.Add(noiseStats);
            var nrOn = Place(nr, new ToggleSwitch(), 20, 510);
            Theme.Help(Theme.Label(nr, "Noise removal on", 74, 510, Theme.Bold), "Turns the noise remover on or off. Off = your mic goes through untouched by this stage.");
            loaders.Add(() => nrOn.SetSilently(core.S.NoiseRemoval));
            nrOn.CheckedChanged += (s, e) => { if (loading) return; core.S.NoiseRemoval = nrOn.Checked; core.PushEngineParams(); FireChanged(); };
            Theme.Help(Theme.Label(nr, "Voice isolation", 270, 512, Theme.Bold), NoiseEngines.Help);
            var engine = Theme.Label(nr, "", 390, 514, Theme.Small, Theme.Muted, 280);
            loaders.Add(() => engine.Text = core.VoiceIsolationText());
            Theme.Help(Theme.Label(nr, "Delay", 74, 550, Theme.Bold), "Your voice goes through the noise remover itself - its own window and look-ahead. That's what keeps a voice clean and natural, and the fan out of every word.");
            var rtInfo = Theme.Label(nr, "", 260, 552, Theme.Small, Theme.Muted, 410);
            loaders.Add(() => rtInfo.Text = "Noise removal adds " + (core.S.NsEngine == NoiseEngines.Classic ? "21" : NoiseEngines.IsDfn(core.S.NsEngine) ? "50" : "20") + " ms (its own path)");
            Row(nr, 590, "Strength", 0, 100, () => core.S.Strength, v => core.S.Strength = v, v => v + "%", "How far background noise is turned down.",
                "Below 30% it limits how far noise is turned down (12-30 dB: gentler). From 30% on the AI removes everything it calls noise, and the strength sets how much quieter your pauses get on top (up to 40 dB more). 50-80% is a good range; go up next to loud machines.");
            Row(nr, 660, "Voice protection", 0, 100, () => core.S.NsVoiceProtect, v => core.S.NsVoiceProtect = v, v => v + "%", "Keeps your voice full and natural while you talk.",
                "While you're talking, the remover backs off in the voice range so your voice doesn't get thin, robotic or watery.\nHigher = more natural voice; a little noise may remain under your words (it's masked by your voice anyway).");
            Row(nr, 730, "Smoothing", 0, 100, () => core.S.NsSmoothing, v => core.S.NsSmoothing = v, v => v + "%", "Fewer watery, chirpy, 'underwater' artifacts.",
                "Smooths the remover's decisions over time. Prevents the bubbly/chirpy 'musical noise' sound and choppy word endings.\nToo high lets a little noise trail after words. 50% matches stock RNNoise.");
            Row(nr, 800, "Low cut", 0, 200, () => core.S.HighPassHz, v => core.S.HighPassHz = v, v => v < 20 ? "Off" : v + " Hz", "Removes rumble below this pitch (desk bumps, traffic).",
                "High-pass filter: removes everything below this frequency - desk thumps, footsteps, traffic rumble, mic stand noise.\n70-100 Hz suits most voices; deep voices may prefer 60 Hz.");
            Row(nr, 870, "High cut", 5000, 20000, () => core.S.HighCutHz <= 0 ? 20000 : core.S.HighCutHz, v => core.S.HighCutHz = v >= 20000 ? 0 : v, v => v >= 20000 ? "Off" : (v / 1000.0).ToString("0.#") + " kHz", null,
                "Low-pass filter: removes hiss and squeaks above this frequency. Off keeps full clarity; 12-16 kHz can tame hiss from noisy mics.");
            var humLbl = Theme.Help(Theme.Label(nr, "De-hum", 20, 910, Theme.Bold),
                "Removes electrical mains hum (a steady low buzz from cables, chargers, lights). Use 60 Hz in the US/Canada, 50 Hz in Europe/most other countries.");
            var hum = Place(nr, new DarkCombo(), 150, 906, 160);
            hum.Items.AddRange(new object[] { "Off", "50 Hz", "60 Hz" });
            loaders.Add(() => hum.SelectedIndex = core.S.DeHumHz == 50 ? 1 : core.S.DeHumHz == 60 ? 2 : 0);
            hum.SelectionChangeCommitted += (s, e) => { core.S.DeHumHz = hum.SelectedIndex == 1 ? 50 : hum.SelectedIndex == 2 ? 60 : 0; core.PushEngineParams(); };

            // Which inputs of a multi-channel mic (audio interfaces, array mics) carry the voice.
            Theme.Help(Theme.Label(nr, "Mic input", 20, 964, Theme.Bold),
                "For mics with more than one channel: audio interfaces (XLR mic on input 1 or 2), array and laptop mics, some USB mics.\nAuto uses only the inputs that carry your voice - an interface's empty second input would otherwise make you 6 dB quieter and add its noise, and an out-of-phase channel would cancel your voice.\nMix averages every input (what Windows does). Input 1, 2... use just that one.");
            var chan = Place(nr, new DarkCombo(), 150, 960, 230);
            var chanInfo = Theme.Label(nr, "", 400, 964, Theme.Small, Theme.Muted, 270);
            chanInfo.AutoSize = false; chanInfo.Height = Theme.Px(40);
            Action fillChan = () =>
            {
                int n = Math.Max(2, core.Engine.InputChannelCount);
                chan.Items.Clear();
                chan.Items.Add("Auto (inputs with your voice)"); chan.Items.Add("Mix all inputs");
                for (int i = 1; i <= n; i++) chan.Items.Add("Input " + i);
                string m = core.S.MicChannel ?? InputChannels.Auto;
                int k; chan.SelectedIndex = m == InputChannels.Mix ? 1 : int.TryParse(m, out k) && k >= 1 && k <= n ? k + 1 : 0;
                chan.Enabled = core.Engine.InputChannelCount > 1 || chan.SelectedIndex > 1;
            };
            loaders.Add(fillChan);
            chan.SelectionChangeCommitted += (s, e) =>
            {
                core.S.MicChannel = chan.SelectedIndex <= 0 ? InputChannels.Auto : chan.SelectedIndex == 1 ? InputChannels.Mix : (chan.SelectedIndex - 1).ToString();
                core.PushEngineParams();
            };
            var chanTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            chanTimer.Tick += (s, e) =>
            {
                if (!nr.Visible) return;
                string d = core.Engine.InputChannelCount > 1 ? core.Engine.Fx.Channels.Describe() : "Your mic sends one channel.";
                if (chanInfo.Text != d) chanInfo.Text = d;
                if (chan.Enabled != (core.Engine.InputChannelCount > 1) && !chan.DroppedDown) fillChan();
            };
            chanTimer.Start();
            Disposed += (s, e) => chanTimer.Dispose();

            // "Record a sound to remove": a TV, kids, music... that the noise remover keeps (it keeps every voice).
            Theme.Help(Theme.Label(nr, "Remove a specific sound", 20, 1026, Theme.Bold),
                "For a sound the noise remover keeps - a TV or radio, kids, music, a dishwasher. Voices and music are exactly what it's built to let through, so it can't know they aren't you.\n" +
                "Let the sound play, stay quiet and press Record: for 8 seconds StreamLite listens to what the noise remover leaves of it and learns its patterns. From then on, every moment of your mic is split into that sound and your voice, and the sound is taken out.\n" +
                "Your own voice is learned in the background while you talk normally (about a minute of talking), so it isn't mistaken for the sound. Adds 21 ms while it's on. Steady noise like fans needs none of this: the noise remover already takes it out (Record tells you if so).");
            var npInfo = Theme.Label(nr, "", 20, 1054, Theme.Small, Theme.Muted, 650);
            npInfo.AutoSize = false; npInfo.Height = Theme.Px(48);
            var npRec = Place(nr, new FlatButton("Record the sound (8 s)"), 20, 1108, 200);
            var npOn = Place(nr, new ToggleSwitch(), 240, 1112);
            Theme.Label(nr, "On", 294, 1112, Theme.Bold);
            var npForget = Place(nr, new FlatButton("Forget it"), 560, 1108, 110);
            Theme.Help(npForget, "Forgets the recorded sound (your learned voice stays).");
            Row(nr, 1156, "Sound removal", 0, 100, () => core.S.NoisePrintStrength, v => core.S.NoisePrintStrength = v, v => v + "%", null,
                "How hard the recorded sound is pushed down where it's alone. While you talk it always stays gentle on your voice.");
            NoisePrintRecording npRecording = null; bool npLearning = false; string npResult = null;
            Action npText = () =>
            {
                var pr = core.Engine.Suppressor.Print;
                string voice = pr.OwnVoice ? "Your voice: learned." : "Your voice: still learning - just talk normally for a minute or so.";
                string t;
                if (npRecording != null) t = "Recording - stay quiet and let the sound play... " + Math.Ceiling(NoisePrintRecording.Seconds * (1 - npRecording.Progress)) + " s";
                else if (npLearning) t = "Learning the sound (a few seconds)...";
                else if (npResult != null) t = npResult + " " + voice;
                else if (pr.HasPrint) t = "Learned: " + (core.S.NoisePrintAbout ?? "a sound") + "." + (core.S.NoisePrintOn ? " Taking out " + Math.Max(0, pr.RemovedDb).ToString("0") + " dB right now." : " Off.") + " " + voice;
                else t = "Let the sound play (TV, kids, music...), stay quiet, press Record. " + voice;
                if (npInfo.Text != t) npInfo.Text = t;
                npRec.Text = npRecording != null ? "Stop" : npLearning ? "Learning..." : pr.HasPrint ? "Record again (8 s)" : "Record the sound (8 s)";
                npRec.Enabled = !npLearning && core.S.NoiseRemoval;
                npOn.Enabled = pr.HasPrint; npForget.Enabled = pr.HasPrint && npRecording == null;
            };
            loaders.Add(() => { npOn.SetSilently(core.S.NoisePrintOn && core.Engine.Suppressor.Print.HasPrint); npText(); });
            npOn.CheckedChanged += (s, e) => { if (loading) return; core.S.NoisePrintOn = npOn.Checked; npResult = null; core.PushEngineParams(); npText(); FireChanged(); };
            npForget.Click += (s, e) => { core.ForgetNoisePrint(); npResult = null; npOn.SetSilently(false); npText(); FireChanged(); };
            npRec.Click += (s, e) =>
            {
                if (npRecording != null) { core.CancelNoisePrint(); npRecording = null; npText(); return; }
                npResult = null; npRecording = core.StartNoisePrint(); npText();
            };
            var npTimer = new System.Windows.Forms.Timer { Interval = 200 };
            npTimer.Tick += (s, e) =>
            {
                if (npRecording != null && npRecording.Full)
                {
                    var r = npRecording; npRecording = null; npLearning = true;
                    core.FinishNoisePrint(r, l =>
                    {
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                npLearning = false;
                                core.ApplyNoisePrint(l);
                                npResult = l.AlreadyGone
                                    ? "Nothing to take out: the noise remover already removes that sound (" + l.BeforeDb.ToString("0") + " dB before it, " + l.AfterDb.ToString("0") + " dB after)."
                                    : "Learned: " + l.Character + ". It's on.";
                                npOn.SetSilently(core.S.NoisePrintOn && core.Engine.Suppressor.Print.HasPrint);
                                npText(); FireChanged();
                            }));
                        }
                        catch { }
                    });
                }
                if (nr.Visible) npText();
            };
            npTimer.Start();
            Disposed += (s, e) => { npTimer.Dispose(); if (npRecording != null) core.CancelNoisePrint(); };

            // "Only my voice": another person's voice (a girlfriend, a brother, the TV host) turned down, by who it is.
            Theme.Help(Theme.Label(nr, "Only my voice", 20, 1216, Theme.Bold),
                "For someone else talking in your room - the noise remover keeps every voice, so it can't tell yours from theirs. A speaker-recognition model (3D-Speaker's CAM++) learns what your voice sounds like from 15 seconds of you talking. While it's on, the last second of voice on your mic is compared with yours ten times a second, and when it's clearly someone else, the mic is turned down until it's you again.\n" +
                "When you both talk at once you mostly stay on. After a pause the mic starts open, so your first word is never cut - which also means someone else gets through for about half a second at the start of their sentence.\n" +
                "If their voice is a lot like yours (two guys, two girls), also learn theirs: then it decides by which of the two a voice is closer to, which tells similar voices apart much better. Runs on the CPU (about a tenth of one core while someone talks).");
            var vlInfo = Theme.Label(nr, "", 20, 1244, Theme.Small, Theme.Muted, 650);
            vlInfo.AutoSize = false; vlInfo.Height = Theme.Px(66);
            var vlLearn = Place(nr, new FlatButton("Learn my voice (15 s)"), 20, 1318, 200);
            var vlOn = Place(nr, new ToggleSwitch(), 240, 1322);
            Theme.Label(nr, "On", 294, 1322, Theme.Bold);
            var vlThem = Place(nr, new FlatButton("Learn their voice"), 350, 1318, 170);
            Theme.Help(vlThem, "Optional, for voices like yours: let them talk for 15 seconds (you stay quiet) - their voice is learned as your mic hears it, and kept out.");
            var vlForget = Place(nr, new FlatButton("Forget"), 560, 1318, 110);
            Theme.Help(vlForget, "Forgets your voiceprint and theirs and switches Only my voice off.");
            var vlLive = Theme.Label(nr, "", 20, 1366, Theme.Small, Theme.Muted, 650);
            int vlLearning = 0;   // 0 = no, 1 = yours, 2 = theirs
            bool vlBusy = false; string vlResult = null;
            const double VlSeconds = 15;
            Action vlText = () =>
            {
                var lk = core.Lock; string t;
                if (!VoiceLock.ModelInstalled) t = "The voice model isn't installed with this copy of StreamLite.";
                else if (vlLearning != 0) t = (vlLearning == 1 ? "Talk normally - read anything aloud, or tell a story about your day. Only you, nobody else talking. " : "Now only THEY talk, normally, for 15 seconds - you stay quiet. ") +
                    lk.LearnedSeconds.ToString("0") + " of " + VlSeconds.ToString("0") + " s of voice";
                else if (vlBusy) t = "Learning the voice (a few seconds)...";
                else if (vlResult != null) t = vlResult;
                else if (lk.HasPrint) t = "Your voice: learned from " + core.LockYouSeconds.ToString("0") + " s." + (lk.HasBlockPrint ? " Their voice: learned too (kept out by which of you a voice is closer to)." : " Their voice: not learned (optional - for a voice like yours).") +
                    (core.S.VoiceLockOn ? "" : " Off.");
                else t = "Learn your voice first: press the button and talk normally for 15 seconds. Do it with your usual mic, where you usually sit.";
                if (vlInfo.Text != t) vlInfo.Text = t;
                string live = "";
                if (core.S.VoiceLockOn && lk.HasPrint && vlLearning == 0)
                    live = lk.Error != null ? "Problem: " + lk.Error : float.IsNaN(lk.LastSimilarity) ? "Listening..." : (lk.Blocking ? "Someone else - turned down" : "You - on") + "   (how much it sounds like you: " + (lk.LastSimilarity * 100).ToString("0") + "%)";
                if (vlLive.Text != live) { vlLive.Text = live; vlLive.ForeColor = lk.Blocking ? Theme.Warn : Theme.Muted; }
                vlLearn.Text = vlLearning == 1 ? "Stop" : lk.HasPrint ? "Learn my voice again" : "Learn my voice (15 s)";
                vlThem.Text = vlLearning == 2 ? "Stop" : lk.HasBlockPrint ? "Learn theirs again" : "Learn their voice";
                vlLearn.Enabled = !vlBusy && vlLearning != 2 && VoiceLock.ModelInstalled;
                vlThem.Enabled = !vlBusy && vlLearning != 1 && lk.HasPrint;
                vlOn.Enabled = lk.HasPrint; vlForget.Enabled = lk.HasPrint && vlLearning == 0 && !vlBusy;
            };
            Action<bool> vlFinish = them =>
            {
                vlLearning = 0; vlBusy = true; vlText();
                core.FinishLearnVoice(them, (p, secs, cons) =>
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            vlBusy = false;
                            if (p == null) vlResult = core.Lock.Error != null ? "Couldn't learn it: " + core.Lock.Error : "Not enough voice heard (" + secs.ToString("0") + " s) - try again and keep talking.";
                            else
                            {
                                vlResult = (them ? "Their voice" : "Your voice") + " learned from " + secs.ToString("0") + " s" + (cons < 0.75 ? " - but it varied a lot: if someone else talked meanwhile, learn it again." : ".");
                                if (!them && !core.S.VoiceLockOn) { core.S.VoiceLockOn = true; core.PushEngineParams(); vlOn.SetSilently(true); vlResult += " It's on."; }
                            }
                            core.SaveSoon(); vlText(); FireChanged();
                        }));
                    }
                    catch { }
                });
            };
            loaders.Add(() => { vlOn.SetSilently(core.S.VoiceLockOn && core.Lock.HasPrint); vlText(); });
            vlOn.CheckedChanged += (s, e) => { if (loading) return; core.S.VoiceLockOn = vlOn.Checked; vlResult = null; core.PushEngineParams(); core.SaveSoon(); vlText(); FireChanged(); };
            vlForget.Click += (s, e) => { core.ForgetVoiceLock(); vlResult = null; vlOn.SetSilently(false); vlText(); FireChanged(); };
            vlLearn.Click += (s, e) =>
            {
                if (vlLearning == 1) { vlFinish(false); return; }
                vlResult = null; vlLearning = 1; core.StartLearnVoice(); vlText();
            };
            vlThem.Click += (s, e) =>
            {
                if (vlLearning == 2) { vlFinish(true); return; }
                vlResult = null; vlLearning = 2; core.StartLearnVoice(); vlText();
            };
            var vlTimer = new System.Windows.Forms.Timer { Interval = 200 };
            vlTimer.Tick += (s, e) =>
            {
                if (vlLearning != 0 && core.Lock.LearnedSeconds >= VlSeconds) vlFinish(vlLearning == 2);
                if (nr.Visible) vlText();
            };
            vlTimer.Start();
            Disposed += (s, e) => { vlTimer.Dispose(); if (vlLearning != 0) core.CancelLearnVoice(); };

            // ---- Noise gate / expander
            var g = tabs[2];
            Theme.Label(g, "Red = turned down. Keep the dotted threshold between the orange noise line and your voice.", 20, 16, Theme.Small, Theme.Muted);
            Place(g, gateGraph, 20, 44, 650).Height = Theme.Px(200);
            gateGraph.RawLabel = "Your mic after noise removal";
            Theme.Help(gateGraph, "Last 12 seconds. Grey: what the gate hears (after noise removal).  Green: what comes out.\nDotted line: your threshold.  Orange: background noise level.  Red: moments the gate turned the mic down.");
            gateStats = new Label { ForeColor = Theme.Text, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(254)), Size = new Size(Theme.Px(480), Theme.Px(40)), UseMnemonic = false };
            g.Controls.Add(gateStats);
            useSuggestion = Place(g, new FlatButton("Use suggested threshold"), 500, 254, 170);
            Theme.Help(useSuggestion, "Sets the threshold between your measured background noise and your voice. Talk for a few seconds first.");
            useSuggestion.Click += (s, e) =>
            {
                core.S.GateThreshold = (int)Math.Round(suggestedThreshold);
                core.S.Gate = true;
                core.PushEngineParams();
                Reload();
                FireChanged();
            };
            var gateOn = Place(g, new ToggleSwitch(), 20, 302);
            Theme.Help(Theme.Label(g, "Gate on", 74, 302, Theme.Bold), "Turns the gate/expander on or off.");
            loaders.Add(() => gateOn.SetSilently(core.S.Gate));
            gateOn.CheckedChanged += (s, e) => { if (loading) return; core.S.Gate = gateOn.Checked; core.PushEngineParams(); FireChanged(); };
            Theme.Help(Theme.Label(g, "Mode", 200, 302, Theme.Bold),
                "Gate: snaps the mic down whenever you're below the threshold (very clean, can sound choppy).\nExpander: turns quiet sounds down gradually - smoother and more natural, the way broadcast mics are usually set up.");
            var mode = Place(g, new DarkCombo(), 260, 298, 180);
            mode.Items.AddRange(new object[] { "Expander (smooth)", "Gate (hard)" });
            loaders.Add(() => mode.SelectedIndex = core.S.GateExpander ? 0 : 1);
            mode.SelectionChangeCommitted += (s, e) => { core.S.GateExpander = mode.SelectedIndex == 0; core.PushEngineParams(); };
            Row(g, 340, "Threshold", -80, -10, () => core.S.GateThreshold, v => core.S.GateThreshold = v, v => v + " dB", null,
                "Anything quieter than this gets turned down. Set it above your background noise (orange line) and below your voice.");
            Row(g, 390, "Expander ratio", 12, 60, () => core.S.ExpanderRatio, v => core.S.ExpanderRatio = v, v => "1:" + (v / 10.0).ToString("0.0"), "Expander only: how steeply quiet sounds are turned down.",
                "Expander mode: for every 1 dB you are below the threshold, the output drops by this many dB. 1:2 is gentle, 1:4 is strong.");
            Row(g, 460, "Attack", 0, 50, () => core.S.GateAttack, v => core.S.GateAttack = v, v => v + " ms", "How fast it opens when you start talking.",
                "How quickly the mic comes back up when you start talking. 0-5 ms keeps the first syllable; slower softens clicks.");
            Row(g, 530, "Hold", 0, 1000, () => core.S.GateHold, v => core.S.GateHold = v, v => v + " ms", "Stays open after you stop, so word endings aren't cut.",
                "How long it stays open after your voice drops below the threshold. Prevents chopping the ends of words and quick pauses.");
            Row(g, 600, "Release", 10, 1000, () => core.S.GateRelease, v => core.S.GateRelease = v, v => v + " ms", "How smoothly it fades down.",
                "How long the fade-down takes once it closes. Longer = smoother and less noticeable.");
            Row(g, 670, "Range", -80, -6, () => core.S.GateRange, v => core.S.GateRange = v, v => v <= -80 ? "Full mute" : v + " dB", "How far down it goes when closed.",
                "The most it turns the mic down. Full mute is cleanest but can sound unnatural; -20 to -30 dB keeps a little room sound and sounds smoother.");

            // ---- Voice tools
            var vt = tabs[3];
            Theme.Label(vt, "Grey = your mic as it comes in.  Green = after all processing.", 20, 16, Theme.Small, Theme.Muted);
            Place(vt, voiceGraph, 20, 40, 650).Height = Theme.Px(130);
            voiceStats = new Label { ForeColor = Theme.Accent, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(176)), Size = new Size(Theme.Px(650), Theme.Px(22)), UseMnemonic = false };
            vt.Controls.Add(voiceStats);
            var compOn = Place(vt, new ToggleSwitch(), 20, 208);
            Theme.Help(Theme.Label(vt, "Compressor", 74, 208, Theme.Section),
                "Evens out your level: loud words are turned down a bit so quiet and loud parts sit closer together.\nThis is the main thing that makes a voice sound 'produced' and easy to hear in a busy Discord call.");
            loaders.Add(() => compOn.SetSilently(core.S.Compressor));
            compOn.CheckedChanged += (s, e) => { if (loading) return; core.S.Compressor = compOn.Checked; core.PushEngineParams(); };
            Row(vt, 244, "Threshold", -50, 0, () => core.S.CompThreshold, v => core.S.CompThreshold = v, v => v + " dB", null,
                "Level where compression starts. Lower = more of your voice gets evened out. Around -20 to -30 dB for most voices.");
            Row(vt, 284, "Ratio", 10, 80, () => core.S.CompRatio, v => core.S.CompRatio = v, v => (v / 10.0).ToString("0.0") + ":1", null,
                "How strongly loud parts are reduced. 2:1 is subtle, 3:1-4:1 is broadcast style, 6:1+ is heavy.");
            Row(vt, 324, "Attack", 1, 50, () => core.S.CompAttack, v => core.S.CompAttack = v, v => v + " ms", null,
                "How quickly it reacts to loud sounds. Faster catches sudden shouts; slower (10-20 ms) keeps speech punchier.");
            Row(vt, 364, "Release", 20, 500, () => core.S.CompRelease, v => core.S.CompRelease = v, v => v + " ms", null,
                "How quickly it lets go after a loud part. 100-200 ms sounds natural for speech.");
            Row(vt, 404, "Makeup gain", 0, 18, () => core.S.CompMakeup, v => core.S.CompMakeup = v, v => "+" + v + " dB", null,
                "Adds volume back after compression. Usually leave at 0 - Auto level already sets your final loudness.");
            var essOn = Place(vt, new ToggleSwitch(), 20, 456);
            Theme.Help(Theme.Label(vt, "De-esser", 74, 456, Theme.Section),
                "Turns down harsh 'S', 'SH' and 'T' sounds only at the moment they happen, so they don't hiss or pierce through headphones.\nUseful with bright condenser mics (Blue Yeti, HyperX QuadCast, Rode NT-USB...) or after a presence boost in the EQ.");
            loaders.Add(() => essOn.SetSilently(core.S.DeEsser));
            essOn.CheckedChanged += (s, e) => { if (loading) return; core.S.DeEsser = essOn.Checked; core.PushEngineParams(); };
            Row(vt, 492, "Frequency", 3000, 10000, () => core.S.DeEssFreq, v => core.S.DeEssFreq = v, v => (v / 1000.0).ToString("0.0") + " kHz", null,
                "Where your 'S' sounds are. Most voices: 5-8 kHz. Lower voices lower, higher voices higher.");
            Row(vt, 532, "Amount", 0, 12, () => core.S.DeEssAmount, v => core.S.DeEssAmount = v, v => "-" + v + " dB", null,
                "Maximum reduction on S sounds. 4-6 dB is usually enough; too much makes you sound lispy.");
            Theme.Label(vt, "Tip: while your voice test plays, hold \"Hold: hear original\" (Equalizer or Noise removal tab) to compare.", 20, 600, Theme.Small, Theme.Muted);

            // ---- Output
            var o = tabs[4];
            Theme.Label(o, "Grey = your mic as it comes in. Green = what Discord and other apps actually receive.", 20, 16, Theme.Small, Theme.Muted);
            Place(o, outGraph, 20, 40, 650).Height = Theme.Px(150);
            outStats = new Label { ForeColor = Theme.Text, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(196)), Size = new Size(Theme.Px(650), Theme.Px(22)), UseMnemonic = false };
            o.Controls.Add(outStats);
            Row(o, 226, "Volume", -20, 30, () => core.S.VolumeDb, v => core.S.VolumeDb = v, v => (v > 0 ? "+" : "") + v + " dB", "Boost or cut your mic after all effects.",
                "A fixed boost or cut applied after everything else.");
            var al = Place(o, new ToggleSwitch(), 20, 296);
            Theme.Help(Theme.Label(o, "Auto level (lifts your voice to a steady loudness, leaves silence alone)", 74, 296, Theme.Bold),
                "Automatically raises a quiet voice toward the loudness target while you talk. It doesn't change in silence, so background noise isn't pumped up.");
            loaders.Add(() => al.SetSilently(core.S.AutoLevel));
            al.CheckedChanged += (s, e) => { if (loading) return; core.S.AutoLevel = al.Checked; core.PushEngineParams(); FireChanged(); };
            Row(o, 334, "Loudness target", -30, -12, () => core.S.AutoLevelTarget, v => core.S.AutoLevelTarget = v, v => v + " dB", "Higher = louder voice. -20 dB suits Discord and most streams.",
                "How loud Auto level aims to make your voice. -20 dB is a typical comfortable level for voice chat.");
            Row(o, 404, "Max boost", 0, 30, () => core.S.AutoLevelMaxBoost, v => core.S.AutoLevelMaxBoost = v, v => "+" + v + " dB", "Upper limit for how much a quiet voice gets raised.",
                "The most Auto level will turn you up. Lower values keep room noise from being boosted along with a quiet voice.");
            var lim = Place(o, new ToggleSwitch(), 20, 476);
            Theme.Help(Theme.Label(o, "Limiter (prevents clipping/distortion when you shout)", 74, 476, Theme.Bold),
                "A safety ceiling at -1 dB: sudden loud peaks (laughing, shouting) are caught instantly instead of distorting.");
            loaders.Add(() => lim.SetSilently(core.S.Limiter));
            lim.CheckedChanged += (s, e) => { if (loading) return; core.S.Limiter = lim.Checked; core.PushEngineParams(); };
            var rst = Place(o, new FlatButton("Reset all tuning to defaults"), 20, 526, 0);
            Theme.Help(rst, "Puts noise removal, gate, voice tools, volume and auto level back to their recommended defaults. Your EQ is not changed.\nYour current tuning is kept: pick Voice style > \"" + VoiceStyles.Mine + "\" to get it back.");
            rst.Click += (s, e) =>
            {
                var d = new AppSettings();
                var s2 = core.S;
                VoiceStyles.RememberMine(s2);
                s2.Strength = d.Strength; s2.AdaptSpeed = d.AdaptSpeed; s2.HighPassHz = d.HighPassHz; s2.HighCutHz = d.HighCutHz; s2.DeHumHz = d.DeHumHz;
                s2.NsSmoothing = d.NsSmoothing; s2.NsVoiceProtect = d.NsVoiceProtect; s2.NsEngine = d.NsEngine;
                s2.GateThreshold = d.GateThreshold; s2.GateAttack = d.GateAttack; s2.GateHold = d.GateHold; s2.GateRelease = d.GateRelease; s2.GateRange = d.GateRange;
                s2.GateExpander = d.GateExpander; s2.ExpanderRatio = d.ExpanderRatio;
                s2.Compressor = d.Compressor; s2.CompThreshold = d.CompThreshold; s2.CompRatio = d.CompRatio; s2.CompAttack = d.CompAttack; s2.CompRelease = d.CompRelease; s2.CompMakeup = d.CompMakeup;
                s2.DeEsser = d.DeEsser; s2.DeEssFreq = d.DeEssFreq; s2.DeEssAmount = d.DeEssAmount;
                s2.VolumeDb = d.VolumeDb; s2.Limiter = d.Limiter;
                s2.AutoLevel = d.AutoLevel; s2.AutoLevelTarget = d.AutoLevelTarget; s2.AutoLevelMaxBoost = d.AutoLevelMaxBoost;
                core.PushEngineParams();
                Reload();
                FireChanged();
            };

            timer.Tick += (s, e) => Tick();
            timer.Start();
            Reload();
            ShowTab(0);
        }

        static T Place<T>(Control parent, T c, int x, int y, int w = 0) where T : Control
        {
            c.Location = new Point(Theme.Px(x), Theme.Px(y));
            if (w > 0) c.Width = Theme.Px(w);
            parent.Controls.Add(c);
            return c;
        }

        void FireChanged() { if (Changed != null) Changed(this, EventArgs.Empty); }

        /// <summary>
        /// Press and hold to hear the untouched mic (A/B compare); release to go back. While the voice test is playing it
        /// compares on your recording, so the mic your friends hear is never touched; otherwise it bypasses the live mic.
        /// </summary>
        void HookCompare(Control button)
        {
            if (button == null) return;
            bool held = false, onTest = false, wasOriginal = false;
            Action release = () =>
            {
                if (!held) return;
                held = false;
                if (onTest) core.VoiceTest.Original = wasOriginal; else core.Engine.Bypass = false;
                button.BackColor = Theme.Field;
            };
            button.MouseDown += (s, e) =>
            {
                if (held) return;
                held = true;
                onTest = core.VoiceTest.Playing;
                if (onTest) { wasOriginal = core.VoiceTest.Original; core.VoiceTest.Original = true; }
                else core.Engine.Bypass = true;
                button.BackColor = Theme.Warn;
            };
            button.MouseUp += (s, e) => release();
            button.MouseCaptureChanged += (s, e) => { if (Control.MouseButtons == MouseButtons.None) release(); };   // e.g. Alt+Tab while holding
            button.MouseLeave += (s, e) => { if (Control.MouseButtons == MouseButtons.None) release(); };
        }

        void AddMonitorToggle(Control parent, int x, int y)
        {
            var t = Place(parent, new ToggleSwitch(), x, y);
            var l = Theme.Label(parent, "Hear me", 0, 0, Theme.Small, Theme.Muted);
            l.Location = new Point(t.Right + Theme.Px(4), t.Top + Theme.Px(3));
            Theme.Help(l, "Plays your processed mic in your headphones so you can hear exactly what your friends hear (with a tiny delay).");
            loaders.Add(() => t.SetSilently(core.S.Monitor));
            t.CheckedChanged += (s, e) => { if (loading) return; core.S.Monitor = t.Checked; core.RestartAudio(); FireChanged(); };
        }


        FlatSlider Row(Control parent, int y, string label, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string hint, string help)
        {
            var name = Theme.Label(parent, label, 20, y + 2, Theme.Bold);
            if (help != null) Theme.Help(name, help);
            var sl = new FlatSlider { Minimum = min, Maximum = max, Location = new Point(Theme.Px(150), Theme.Px(y)), Size = new Size(Theme.Px(430), Theme.Px(24)) };
            sl.DefaultValue = get();
            parent.Controls.Add(sl);
            var val = Theme.Label(parent, "", 590, y + 2, Theme.Body, Theme.Text, 90);
            sl.ValueChanged += (s, e) =>
            {
                val.Text = fmt(sl.Value);
                if (loading) return;
                set(sl.Value);
                core.PushEngineParams();
                FireChanged();
            };
            if (hint != null) Theme.Label(parent, hint, 150, y + 30, Theme.Small, Theme.Muted);
            loaders.Add(() => { sl.SetSilently(get()); val.Text = fmt(get()); });
            return sl;
        }

        public void Reload()
        {
            loading = true;
            foreach (var l in loaders) l();
            eq.SetProfile(core.S.MicEq);
            loading = false;
        }

        void ShowTab(int idx)
        {
            current = idx;
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].Visible = i == idx;
                tabButtons[i].BackColor = i == idx ? Theme.AccentDim : Theme.Field;
            }
            Height = Theme.Px(TabTop) + tabs[idx].Height + Theme.Px(6);
        }

        readonly float[] hr = new float[LevelHistory.Length], hc = new float[LevelHistory.Length], ho = new float[LevelHistory.Length];
        readonly bool[] hg = new bool[LevelHistory.Length];

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }

        void Tick()
        {
            if (IsDisposed || !Visible) return;
            var en = core.Engine;
            ticks++;
            core.KeepDfnLoaded();   // you're tuning: hear and see the real noise removal, not the idle stand-in
            if (autoTicks >= 0) AutoEqTick();
            switch (current)
            {
                case 0:
                    eq.Graph.RawSpectrum = en.Running ? en.RawSpectrum.Snapshot() : null;
                    eq.Graph.OutSpectrum = en.Running ? en.OutSpectrum.Snapshot() : null;
                    eq.Graph.Invalidate();
                    break;
                case 1:
                    if (!en.Running) { noiseStats.Text = "Turn on the virtual mic (top of the page) to see live graphs."; break; }
                    before.Push(en.RawSpectrum.Snapshot());
                    after.Push(en.CleanSpectrum.Snapshot());
                    noiseGraph.Input = en.RawSpectrum.Snapshot();
                    noiseGraph.Clean = en.CleanSpectrum.Snapshot();
                    noiseGraph.Noise = en.NoiseSpectrum.Snapshot();
                    noiseGraph.Invalidate();
                    if (ticks % 10 == 0) UpdateNoiseStats();
                    break;
                case 2:
                    if (ticks % 2 != 0) break;
                    en.History.Snapshot(gateGraph.Raw, gateGraph.Clean, gateGraph.Output, gateGraph.GateOpen);
                    en.History.SnapshotGateInput(gateGraph.Raw);   // the gate listens after noise removal
                    gateGraph.Threshold = core.S.Gate ? (float?)core.S.GateThreshold : null;
                    float floor = LevelHistoryGraph.Percentile(gateGraph.Raw, 0.1), voice = LevelHistoryGraph.Percentile(gateGraph.Raw, 0.95);
                    gateGraph.NoiseFloor = floor;
                    gateGraph.Invalidate();
                    if (ticks % 10 == 0)
                    {
                        bool heard = voice - floor > 12;
                        suggestedThreshold = heard ? Math.Max(-80, Math.Min(-10, floor + Math.Min(10, (voice - floor) / 3))) : floor + 8;
                        gateStats.Text = "Background noise " + floor.ToString("0") + " dB" + (heard ? "   Your voice " + voice.ToString("0") + " dB" : "   (talk to measure your voice)") +
                            "\nSuggested threshold: " + suggestedThreshold.ToString("0") + " dB";
                        useSuggestion.Enabled = en.Running;
                    }
                    break;
                case 3:
                    if (ticks % 2 != 0) break;
                    en.History.Snapshot(voiceGraph.Raw, voiceGraph.Clean, voiceGraph.Output, voiceGraph.GateOpen);
                    voiceGraph.Invalidate();
                    if (ticks % 5 == 0)
                        voiceStats.Text = (core.S.Compressor ? "Compressor: " + en.Comp.GainReductionDb.ToString("0.0") + " dB" : "Compressor off") +
                            "      " + (core.S.DeEsser ? "De-esser: " + en.Ess.ReductionDb.ToString("0.0") + " dB" : "De-esser off") +
                            "      Auto level: " + (en.Leveler.CurrentGainDb >= 0 ? "+" : "") + en.Leveler.CurrentGainDb.ToString("0.0") + " dB";
                    break;
                case 4:
                    if (ticks % 2 != 0) break;
                    en.History.Snapshot(outGraph.Raw, outGraph.Clean, outGraph.Output, outGraph.GateOpen);
                    outGraph.Invalidate();
                    if (ticks % 10 == 0)
                    {
                        float vin = LevelHistoryGraph.Percentile(outGraph.Raw, 0.95), vout = LevelHistoryGraph.Percentile(outGraph.Output, 0.95);
                        outStats.Text = "Your voice comes in around " + vin.ToString("0") + " dB and goes out around " + vout.ToString("0") + " dB (" + (vout - vin >= 0 ? "+" : "") + (vout - vin).ToString("0") + " dB).";
                    }
                    break;
            }
        }

        void UpdateNoiseStats()
        {
            core.Engine.History.Snapshot(hr, hc, ho, hg);
            float rawFloor = LevelHistoryGraph.Percentile(hr, 0.1), cleanFloor = LevelHistoryGraph.Percentile(hc, 0.1), voice = LevelHistoryGraph.Percentile(hr, 0.95);
            string afterTxt = cleanFloor < -95 ? "silent (gate closed)" : cleanFloor.ToString("0") + " dB";
            string line1 = "Background noise:  " + rawFloor.ToString("0") + " dB  ->  " + afterTxt +
                (cleanFloor >= -95 ? "   (" + (rawFloor - cleanFloor).ToString("0") + " dB quieter)" : "") +
                "     Voice detected: " + (core.Engine.Suppressor.SpeechPresence * 100).ToString("0") + "%";
            string line2 = voice - rawFloor > 12
                ? "Your voice: " + voice.ToString("0") + " dB.   Voice above noise: " + (voice - rawFloor).ToString("0") + " dB before, " + (cleanFloor < -95 ? "fully clean" : (voice - cleanFloor).ToString("0") + " dB") + " after."
                : "Talk for a few seconds to measure your voice against the noise.";
            if (!core.S.NoiseRemoval) line2 = "Noise removal is OFF - turn it on below to see the difference.";
            var dfn = core.Engine.Suppressor.DfnEngine;
            if (core.S.NoiseRemoval && NoiseEngines.IsDfn(core.S.NsEngine))
            {
                if (dfn == null || (!dfn.Ready && dfn.Error == null)) line2 = "DeepFilterNet is loading (a few seconds - RNNoise covers until it's ready). It sleeps while no app records your mic, to keep video memory free.";
                else if (dfn.Error != null) line2 = "DeepFilterNet couldn't start (" + dfn.Error + ") - RNNoise is running instead.";
                else line2 += "   DeepFilterNet " + (dfn.Gpu ? "GPU" : "CPU") + ": " + dfn.AvgMs.ToString("0.0") + " ms per 10 ms" + (dfn.Late > 0 ? ", " + dfn.Late + " late frames" : "");
            }
            noiseStats.Text = line1 + "\n" + line2;
        }

        // ---- Auto-EQ: listen to the voice, compare with a pleasant broadcast-voice curve, suggest graphic bands.

        void StartAutoEq()
        {
            if (!core.Engine.Running) { DarkMessage.Show(FindForm(), "Turn on the virtual mic first.", "Auto-EQ", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            autoTicks = 0;
            autoFrames = 0;
            autoSum = new double[SpectrumAnalyzer.Points];
            eq.AutoEqButton.Enabled = false;
        }

        void AutoEqTick()
        {
            const int total = 200; // 8 s at 40 ms
            autoTicks++;
            core.Engine.History.Snapshot(hr, hc, ho, hg);
            float floor = LevelHistoryGraph.Percentile(hr, 0.1);
            if (hr[hr.Length - 1] > floor + 12)            // only frames where you're talking
            {
                var spec = core.Engine.RawSpectrum.Snapshot();
                for (int i = 0; i < spec.Length; i++) autoSum[i] += Math.Pow(10, spec[i] / 10);
                autoFrames++;
            }
            eq.AutoEqButton.Text = "Talk normally... " + (int)Math.Ceiling((total - autoTicks) * 0.04) + "s";
            if (autoTicks < total) return;
            autoTicks = -1;
            eq.AutoEqButton.Enabled = true;
            eq.AutoEqButton.Text = "Auto-EQ my voice";
            if (autoFrames < 30)
            {
                DarkMessage.Show(FindForm(), "Couldn't hear enough talking. Try again and speak normally for the full 8 seconds.", "Auto-EQ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            VoiceStyles.RememberMine(core.S);   // "My settings" can bring the previous EQ back
            eq.ApplyBands(SuggestBands(autoSum, autoFrames));
        }

        static double[] SuggestBands(double[] sum, int frames)
        {
            var m = new double[sum.Length];
            for (int i = 0; i < m.Length; i++) m[i] = 10 * Math.Log10(sum[i] / frames + 1e-20);
            Func<double, double, double> avg = (lo, hi) =>
            {
                double s = 0; int c = 0;
                for (int i = 0; i < m.Length; i++) { double f = SpectrumAnalyzer.PointFreq(i); if (f >= lo && f <= hi) { s += m[i]; c++; } }
                return c > 0 ? s / c : 0;
            };
            double refLevel = avg(800, 1250);
            double[] tf = { 63, 100, 200, 400, 1000, 2000, 4000, 8000, 16000 };
            double[] tv = { -10, -1, 5, 3, 0, -6, -11, -18, -30 };
            Func<double, double> target = f =>
            {
                if (f <= tf[0]) return tv[0];
                for (int i = 1; i < tf.Length; i++)
                    if (f <= tf[i]) { double t = Math.Log(f / tf[i - 1]) / Math.Log(tf[i] / tf[i - 1]); return tv[i - 1] + t * (tv[i] - tv[i - 1]); }
                return tv[tv.Length - 1];
            };
            var bands = new double[10];
            for (int b = 0; b < 10; b++)
            {
                double f = EqProfile.BandFreqs[b];
                double measured = avg(f / 1.41, f * 1.41) - refLevel;
                double limit = f >= 8000 ? 3 : 6;
                bands[b] = Math.Max(-limit, Math.Min(limit, target(f) - measured));
            }
            bands[0] = Math.Min(bands[0], 0) - 4;
            bands[1] = Math.Min(bands[1], 2);
            double mean = 0; for (int b = 2; b < 9; b++) mean += bands[b]; mean /= 7;
            for (int b = 0; b < 10; b++) bands[b] = Math.Max(-12, Math.Min(12, Math.Round((bands[b] - (b >= 2 ? mean : 0)) * 0.6 * 2) / 2));
            return bands;
        }
    }

    /// <summary>Headphone / speaker EQ editor shown inline on the Headphones page (applied through Equalizer APO).</summary>
    internal class OutputEqPanel : Panel
    {
        readonly AppCore core;
        readonly EqEditor eq;
        public event EventHandler Changed;

        public OutputEqPanel(AppCore core)
        {
            this.core = core;
            BackColor = Theme.Bg;
            Size = new Size(Theme.Px(690), Theme.Px(590));
            var card = new Card { Location = new Point(0, 0), Size = new Size(Theme.Px(690), Theme.Px(586)) };
            Controls.Add(card);
            eq = new EqEditor(EqEditor.HeadphonePresets(), true, false, false) { Location = new Point(Theme.Px(5), Theme.Px(14)) };
            // Redraw live while you drag, but only rewrite Equalizer APO's config once you let go:
            // every rewrite makes it reload, which is what caused the pops.
            eq.Changed += (s, e) => { if (Changed != null) Changed(this, EventArgs.Empty); };
            eq.Committed += (s, e) => core.OutputEqChanged();
            card.Controls.Add(eq);
            Theme.Label(card, "Changes apply when you let go of a control. Import accepts Equalizer APO / AutoEq \"ParametricEQ.txt\" files.", 10, 556, Theme.Small, Theme.Muted);
        }

        public void SetDevice(string id)
        {
            eq.SetProfile(core.OutputProfile(id));
        }
    }

    internal class ManageCamerasForm : DarkDialog
    {
        readonly Panel list;
        readonly ScrollHost listHost;
        readonly Label status;

        public ManageCamerasForm() : base("Enable / disable cameras", 520, 380)
        {
            Text2("Disabled cameras are hidden from every app, so apps pick the one you leave on.", 20, 16, 480, 20, Theme.Small, Theme.Muted);
            Text2("Windows asks for administrator permission for each change (unless the app runs as administrator).", 20, 36, 480, 20, Theme.Small, Theme.Muted);
            list = ScrollLists.Make(Body, new Rectangle(Theme.Px(20), Theme.Px(66), Theme.Px(480), Theme.Px(250)), Theme.Card, out listHost);
            status = Text2("", 20, 330, 380, 40, Theme.Small, Theme.Warn);
            var close = new FlatButton("Close") { Location = new Point(Theme.Px(410), Theme.Px(336)) };
            close.Click += (s, e) => Close();
            Body.Controls.Add(close);
            Fill();
        }

        void Fill()
        {
            list.Controls.Clear();
            var cams = CameraPnp.List();
            int y = 16;
            if (cams.Count == 0) Theme.Label(list, "No cameras found.", 16, y, Theme.Body, Theme.Muted);
            foreach (var c in cams)
            {
                var cam = c;
                var t = new ToggleSwitch { Location = new Point(Theme.Px(16), Theme.Px(y)) };
                t.SetSilently(cam.Enabled);
                list.Controls.Add(t);
                Theme.Label(list, cam.Name, 70, y, Theme.Bold, Theme.Text, 390);
                t.CheckedChanged += (s, e) =>
                {
                    Cursor = Cursors.WaitCursor;
                    string err = CameraPnp.SetEnabled(cam.InstanceId, t.Checked);
                    Cursor = Cursors.Default;
                    status.Text = err ?? "";
                    Fill();
                };
                y += 44;
            }
            ScrollLists.Fit(listHost);
        }
    }
}


