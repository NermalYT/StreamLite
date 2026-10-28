using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite.UI
{
    /// <summary>
    /// StreamLite's guided setup, one step after the other: your mic and headphones; Equalizer APO switched on for those
    /// headphones by StreamLite itself (installed first when it isn't there - one Windows prompt, never its own device
    /// list); Tune to my voice from the raw mic; your headphones' EQ (found by themselves, or you pick the model); and
    /// Personalize 3D with that EQ on - all on the whole primary monitor, StreamLite's settings covered until it's over.
    /// Required (4.0.5): StreamLite now reads the mic raw, so everyone sets it up again - new installs, and updates from
    /// any version, whose voice and 3D settings start fresh first. It's done when Personalize 3D has finished; until
    /// then it starts again every time StreamLite opens.
    /// </summary>
    internal static class RetuneWizard
    {
        /// <summary>The version whose setup everyone needs (raw mic tuning, headphone EQ, 3D audio).</summary>
        public const string RequiredVersion = "4.0.5";
        public const string FirstTitle = "Setting up StreamLite", UpdateTitle = "Set up StreamLite again", Subtitle = "One step at a time - your settings are waiting behind this.";
        public const string UpdateText = "StreamLite now takes your mic's raw sound - none of the effects Windows or your mic's maker run first - and tunes everything from it. So everything is set up again, one step at a time (about 6 minutes): your mic, your voice, your headphones' EQ and 3D audio tuned to your ears. Your voice and 3D settings start fresh; your devices, profiles and hotkeys stay.";
        public const string FirstText = "Welcome to StreamLite! Let's set it up for you, one step at a time (about 6 minutes): your mic and headphones, your voice from your mic's raw sound, your headphones' EQ and 3D audio tuned to your ears.";

        /// <summary>The setup hasn't been done since it became required (4.0.5).</summary>
        public static bool Needed(AppSettings s)
        {
            Version v;
            return s.SetupDoneFor == null || !Version.TryParse(s.SetupDoneFor, out v) || v < new Version(RequiredVersion);
        }

        /// <summary>
        /// The required setup. updated = StreamLite was set up before (an older version): every voice and 3D setting
        /// starts fresh first (once - leaving half way and coming back doesn't reset again). "Later" leaves it for the
        /// next time StreamLite opens.
        /// </summary>
        public static void Required(MainForm form, AppCore core, bool updated)
        {
            form.BeginSetupScreen(updated ? UpdateTitle : FirstTitle, Subtitle);
            int done0 = StepsDone(core.S);
            string text = done0 > 0 ? "Pick up where you left off: " + done0 + " of 5 steps are done (" + string.Join(", ", SetupCover.Steps.Take(done0)) + "). Next: " + SetupCover.Steps[done0] + "." : updated ? UpdateText : FirstText;
            // 4.1: coming from 4.0.5 or later with your settings saved, you may keep them and skip the setup.
            bool canKeep = updated && HasSavedSettings(core.S);
            if (canKeep && done0 == 0) text += "\n\nHappy with how StreamLite sounds now? Keep your settings and skip the setup.";
            var choices = canKeep ? new[] { done0 > 0 ? "Carry on" : "Start", "Keep my settings", "Later" } : new[] { done0 > 0 ? "Carry on" : "Start", "Later" };
            using (var d = new ChoiceDialog(updated ? UpdateTitle : "Set up StreamLite", text, choices, 560))
            {
                d.ShowDialog(form);
                if (canKeep && d.Choice == 1)
                {
                    form.EndSetupScreen();
                    try { HealthMonitor.LogLine("Setup skipped: settings kept (" + RequiredVersion + ")"); } catch { }
                    Finished(core);
                    return;
                }
                if (d.Choice != 0) { form.EndSetupScreen(); return; }
            }
            if (updated && core.S.ResetDoneFor != RequiredVersion)
            {
                core.ResetTuning();
                core.S.ResetDoneFor = RequiredVersion; core.SaveSoon();
            }
            bool toPersonalize = false;
            try
            {
                toPersonalize = RunSteps(form, core, () =>
                {
                    form.EndSetupScreen();
                    if (!Personal3DView.LastRunCompleted) return;
                    Finished(core);
                });
            }
            finally { if (!toPersonalize) form.EndSetupScreen(); }
        }

        /// <summary>Settings worth keeping: the voice tuned, a 3D head picked or a headphone EQ found (an update from 4.0.5 or later).</summary>
        internal static bool HasSavedSettings(AppSettings s)
        {
            return s.VoiceSetupDone || (s.Game.PersonalHeads != null && s.Game.PersonalHeads.Count > 0) || (s.Profiles != null && s.Profiles.Corrections != null && s.Profiles.Corrections.Count > 0);
        }

        /// <summary>
        /// The setup is done: saved, and StreamLite starts again with administrator rights if Run as administrator's
        /// task was just made (so every setting works from now on - the mic drops out for a moment).
        /// </summary>
        internal static void Finished(AppCore core)
        {
            core.S.SetupDoneFor = RequiredVersion; core.S.Save();
            try { HealthMonitor.LogLine("Setup done (" + RequiredVersion + "): raw mic tuning, headphone EQ, 3D audio"); } catch { }
            if (core.S.RunAsAdmin && !Elevation.IsElevated && Elevation.TaskRuns(Application.ExecutablePath)) Program.RestartElevated();
        }

        /// <summary>Steps of this setup already done (leaving half way and coming back picks up there, 4.0.7).</summary>
        internal static int StepsDone(AppSettings s) { return s.SetupStepsFor == RequiredVersion ? Math.Max(0, Math.Min(4, s.SetupStepsDone)) : 0; }

        static void Did(AppCore core, int steps)
        {
            if (StepsDone(core.S) >= steps) return;
            core.S.SetupStepsFor = RequiredVersion; core.S.SetupStepsDone = steps; core.SaveSoon();
        }

        /// <summary>true = it went on to Personalize 3D; done runs when that closes (it ends the setup screen).</summary>
        static bool RunSteps(MainForm form, AppCore core, Action done)
        {
            int from = StepsDone(core.S);
            // 1. Your mic and headphones (picked already: kept).
            form.SetupStep(0);
            string phonesId = core.GamePhonesId ?? core.S.Game.HeadphonesId, micId = core.S.MicId;
            if (from < 1 || phonesId == null || core.Outputs().All(o => o.Id != phonesId))
            {
                using (var d = new DevicePickDialog(core))
                {
                    if (d.ShowDialog(form) != DialogResult.OK) return false;
                    phonesId = d.PhonesId; micId = d.MicId;
                }
                Did(core, 1);
            }
            // 2. Equalizer APO on those headphones (off the mic), by StreamLite - every time: nothing to do when it's on.
            form.SetupStep(1);
            SetUpApo(form, core, phonesId, micId);
            Did(core, 2);
            // 3. Tune to my voice, from the raw mic - all of it (leaving it ends the setup for now).
            form.SetupStep(2);
            if (from < 3)
            {
                if (!form.OpenVoiceSetup()) return false;
                Did(core, 3);
            }
            // 4. The headphones' EQ - straight on, no question in between - then 3D audio tuned to your ears.
            form.SetupStep(3);
            var phones = core.Outputs().FirstOrDefault(o => o.Id == phonesId);
            if (phones == null) return false;
            bool opened = false;
            Action to3D = () => { Did(core, 4); form.SetupStep(4); opened = true; form.OpenPersonalize3D(done); };
            // Coming back for 3D audio: the EQ picked last time stays (no second lookup, no second question).
            if (from >= 4 && core.S.Profiles.CorrectionFor(phones.Id) != null) to3D();
            else FindEq(form, core, phones, to3D);
            return opened;
        }

        /// <summary>
        /// Equalizer APO - the free add-on that runs the headphone EQ - switched on for the headphones you picked and off
        /// your mic (StreamLite cleans the mic itself), installed first when it isn't there: one Windows prompt for all
        /// of it, and its own device list never shows.
        /// </summary>
        static void SetUpApo(MainForm form, AppCore core, string phonesId, string micId)
        {
            bool installed = EqualizerApo.IsInstalled;
            string micOff = installed && micId != null && ApoDevices.IsAttached(micId) ? micId : null;
            // Run as administrator's startup task (4.1: on by default) comes with the same Windows prompt.
            bool task = core.S.RunAsAdmin && !Elevation.IsElevated && !Elevation.TaskExists();
            if (installed && ApoDevices.IsAttached(phonesId) && micOff == null && !task) return;
            string err = null;
            var wait = new WaitDialog("Your headphones", (installed ? "Switching on Equalizer APO for your headphones" : "Installing Equalizer APO (free) for your headphones' EQ") +
                (task ? " and giving StreamLite the admin rights some settings need" : "") + " - approve the Windows prompt. Your sound stops for a moment when it's done.");
            var t = new Thread(() =>
            {
                try { err = ApoDevices.Setup(installed ? null : DriverInstaller.FetchEqualizerApo(), phonesId, micOff, task); }
                catch (Exception ex) { err = ex.Message; }
                try { wait.BeginInvoke(new Action(() => { wait.DialogResult = DialogResult.OK; wait.Close(); })); } catch { }
            }) { IsBackground = true, Name = "Equalizer APO setup" };
            wait.Shown += (s, e) => t.Start();
            wait.ShowDialog(form);
            wait.Dispose();
            try { HealthMonitor.LogLine("Setup: Equalizer APO for " + phonesId + " - " + (err ?? "on")); } catch { }
            if (err != null && err != EqualizerApo.NotGranted)
                DarkMessage.Show(form, "Your headphone EQ isn't switched on yet: " + err + "\n\nThe setup carries on - you can switch it on later on the Headphones page.", "Your headphones");
        }

        /// <summary>
        /// The headphones' own EQ: looked up by every name Windows has for them; when the model isn't certain (a
        /// wireless dongle...), Find my headphones asks you. Then done().
        /// </summary>
        static void FindEq(MainForm form, AppCore core, AudioDeviceInfo phones, Action done)
        {
            var names = DeviceNames.For(phones);
            var wait = new WaitDialog("Your headphones", "Looking up the EQ for " + phones.Name + "...");
            AutoEq.Entry found = null; List<EqFilter> filters = null;
            var t = new Thread(() =>
            {
                try
                {
                    found = SquigLink.BestFor(AppSettings.Folder, names);   // AutoEq first, then squig.link
                    if (found != null) { double pre; filters = AutoEq.Download(found, out pre); }
                }
                catch { found = null; }
                try { wait.BeginInvoke(new Action(() => { wait.DialogResult = DialogResult.OK; wait.Close(); })); } catch { }
            }) { IsBackground = true, Name = "retune EQ" };
            wait.Shown += (s, e) => t.Start();
            bool cancelled = wait.ShowDialog(form) != DialogResult.OK;
            wait.Dispose();
            if (cancelled) return;
            string corrName = null;
            if (found != null && filters != null) corrName = HeadphoneFinder.Named(found);
            else
                using (var f = new HeadphoneFinder(phones.Name))
                    if (f.ShowDialog(form) == DialogResult.OK && f.ChosenFilters != null) { corrName = f.ChosenName; filters = f.ChosenFilters; }
            if (corrName != null)
            {
                core.S.Profiles.SetCorrection(phones.Id, corrName, filters);
                if (core.Profiles != null) core.Profiles.Reapply();
                core.SaveSoon();
                try { HealthMonitor.LogLine("Retune: headphone EQ for " + phones.Name + " - " + corrName); } catch { }
            }
            done();
        }
    }

    /// <summary>A note while something runs in the background (closed with OK when it's done; Cancel stops waiting).</summary>
    internal sealed class WaitDialog : DarkDialog
    {
        public WaitDialog(string title, string text) : base(title, 420, 150)
        {
            Text2(text, 22, 18, 376, 50, Theme.Body, Theme.Text);
            var cancel = new FlatButton("Cancel") { Location = new Point(Theme.Px(398) - Theme.Px(110), Theme.Px(92)), Width = Theme.Px(110) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancel); CancelButton = cancel;
        }
    }

    /// <summary>
    /// The setup's first step (4.1: tiles, not dropdowns): which mic you talk into and which headphones you listen on -
    /// each device a card with its icon; click one to pick it. The mic's card shows its level live while you talk.
    /// </summary>
    internal sealed class DevicePickDialog : DarkDialog
    {
        readonly AppCore core;
        readonly List<DeviceTile> micTiles = new List<DeviceTile>(), phoneTiles = new List<DeviceTile>();
        readonly System.Windows.Forms.Timer levels = new System.Windows.Forms.Timer { Interval = 50 };
        public string PhonesId, MicId;

        public DevicePickDialog(AppCore core) : base("Your mic and headphones", 760, 470)
        {
            this.core = core;
            Text2("Click the microphone you talk into and the headphones you listen on. Everything that follows is tuned for these two.", 22, 14, 716, 40, Theme.Body, Theme.Muted);
            var micList = core.MicChoices().Where(d => !AudioDevices.IsCable(d)).ToList();
            var outList = core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList();
            string curMic = core.ActiveMicId, curOut = core.GamePhonesId ?? core.S.Game.HeadphonesId ?? core.RealDefaultOutput();
            Column("Microphone", "", micList, micTiles, curMic, 22);
            Column("Headphones", "", outList, phoneTiles, curOut, 394);
            var ok = new FlatButton("Continue", true) { Location = new Point(Theme.Px(738) - Theme.Px(150), Theme.Px(412)), Width = Theme.Px(150) };
            var cancel = new FlatButton("Cancel") { Location = new Point(ok.Left - Theme.Px(10) - Theme.Px(110), Theme.Px(412)), Width = Theme.Px(110) };
            ok.Click += (s, e) => Use();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(ok); Body.Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            // The mic in use: its level on its card (the others aren't open).
            levels.Tick += (s, e) => { foreach (var t in micTiles) t.Level = t.Device.Id == core.ActiveMicId ? core.Engine.InputLevelDb : float.NaN; };
            levels.Start();
        }

        void Column(string title, string glyph, List<AudioDeviceInfo> list, List<DeviceTile> tiles, string current, int x)
        {
            Theme.Label(Body, title, x, 62, Theme.Section);
            int y = 92, h = list.Count > 4 ? 62 : 74;
            foreach (var d in list.Take(5))
            {
                var t = new DeviceTile(d, glyph) { Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(344), Theme.Px(h - 8)) };
                t.Click += (s, e) => { foreach (var o in tiles) o.Selected = o == t; };
                Body.Controls.Add(t); tiles.Add(t);
                y += h;
            }
            var pick = tiles.FirstOrDefault(t => t.Device.Id == current) ?? tiles.FirstOrDefault();
            if (pick != null) pick.Selected = true;
            if (list.Count == 0) Theme.Label(Body, "None found - plug one in.", x, 96, Theme.Body, Theme.Warn);
        }

        void Use()
        {
            var mic = micTiles.Where(t => t.Selected).Select(t => t.Device).FirstOrDefault();
            var ph = phoneTiles.Where(t => t.Selected).Select(t => t.Device).FirstOrDefault();
            if (mic == null || ph == null) { DarkMessage.Show(this, "Pick a microphone and headphones first.", Text); return; }
            if (mic.Id != core.ActiveMicId) { core.S.MicId = mic.Id; core.RestartAudio(); }
            PhonesId = ph.Id; MicId = mic.Id;
            if (core.S.Game.HeadphonesId != ph.Id) { core.S.Game.HeadphonesId = ph.Id; if (core.EngineOn) core.RestartGameAudio(); }
            core.SaveSoon();
            DialogResult = DialogResult.OK; Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { levels.Dispose(); base.OnFormClosed(e); }
    }

    /// <summary>A device as a card (the setup's device step): icon, name, what Windows calls it; green ring when picked, a level bar while it hears you.</summary>
    internal sealed class DeviceTile : Control
    {
        public readonly AudioDeviceInfo Device;
        readonly string glyph;
        bool selected, hover;
        float level = float.NaN;

        public DeviceTile(AudioDeviceInfo d, string glyph)
        {
            Device = d; this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }
        /// <summary>Its level (dBFS), NaN = no bar.</summary>
        public float Level { get { return level; } set { if (float.IsNaN(value) && float.IsNaN(level)) return; level = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        /// <summary>"Microphone (Yeti Stereo Microphone)" -> the part in brackets as the name, the rest below it.</summary>
        static void Split(string full, out string name, out string kind)
        {
            int a = full.IndexOf('('), b = full.LastIndexOf(')');
            if (a > 0 && b > a) { name = full.Substring(a + 1, b - a - 1).Trim(); kind = full.Substring(0, a).Trim(); }
            else { name = full; kind = ""; }
            name = System.Text.RegularExpressions.Regex.Replace(name, @"^\d+\s*-\s*", "");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            float rad = Theme.Px(10);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 180, 90); path.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 90);
                path.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90); path.AddArc(r.X, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90); path.CloseFigure();
                using (var b = new SolidBrush(selected ? Color.FromArgb(34, 52, 40) : hover ? Theme.FieldHover : Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(selected ? Theme.Accent : Theme.Track, selected ? 2f : 1f)) g.DrawPath(p, path);
            }
            float icon = Theme.Px(40);
            using (var f = new Font("Segoe MDL2 Assets", 15f))
                TextRenderer.DrawText(g, glyph, f, new Rectangle(Theme.Px(10), 0, (int)icon, Height), selected ? Theme.Accent : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            string name, kind; Split(Device.Name ?? "", out name, out kind);
            int tx = Theme.Px(58), tw = Width - tx - Theme.Px(36);
            TextRenderer.DrawText(g, name, Theme.Bold, new Rectangle(tx, Height / 2 - Theme.Px(20), tw, Theme.Px(22)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, kind, Theme.Small, new Rectangle(tx, Height / 2 + Theme.Px(1), tw, Theme.Px(18)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected)
                using (var f = new Font("Segoe MDL2 Assets", 11f))
                    TextRenderer.DrawText(g, "", f, new Rectangle(Width - Theme.Px(34), 0, Theme.Px(24), Height), Theme.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (!float.IsNaN(level))
            {
                float t = Math.Max(0, Math.Min(1, (level + 60) / 60f));
                var bar = new RectangleF(tx, Height - Theme.Px(9), tw * t, Theme.Px(3));
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, bar);
            }
        }
    }
}