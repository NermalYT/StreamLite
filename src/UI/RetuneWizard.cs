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
            using (var d = new ConfirmDialog(updated ? UpdateTitle : "Set up StreamLite", updated ? UpdateText : FirstText, "Start", "Later", 520, updated ? 250 : 210))
                if (d.ShowDialog(form) != DialogResult.OK) { form.EndSetupScreen(); return; }
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
                    core.S.SetupDoneFor = RequiredVersion; core.SaveSoon();
                    try { HealthMonitor.LogLine("Setup done (" + RequiredVersion + "): raw mic tuning, headphone EQ, 3D audio"); } catch { }
                });
            }
            finally { if (!toPersonalize) form.EndSetupScreen(); }
        }

        /// <summary>true = it went on to Personalize 3D; done runs when that closes (it ends the setup screen).</summary>
        static bool RunSteps(MainForm form, AppCore core, Action done)
        {
            // 1. Your mic and headphones.
            form.SetupStep(0);
            string phonesId, micId;
            using (var d = new DevicePickDialog(core))
            {
                if (d.ShowDialog(form) != DialogResult.OK) return false;
                phonesId = d.PhonesId; micId = d.MicId;
            }
            // 2. Equalizer APO on those headphones (off the mic), by StreamLite.
            form.SetupStep(1);
            SetUpApo(form, core, phonesId, micId);
            // 3. Tune to my voice, from the raw mic - all of it (leaving it ends the setup for now).
            form.SetupStep(2);
            if (!form.OpenVoiceSetup()) return false;
            // 4. The headphones: their EQ, then 3D audio tuned to your ears.
            form.SetupStep(3);
            var phones = core.Outputs().FirstOrDefault(o => o.Id == phonesId);
            string name = phones != null ? phones.Name : "your headphones";
            using (var d = new ConfirmDialog("Your headphones", "Your mic is tuned. Now tune 3D audio and the EQ for " + name + ".\n\nStreamLite finds their EQ, puts it on, then Personalize 3D (about 4 minutes) tunes 3D audio to your ears.", "Tune headphones", "Later", 520, 230))
                if (d.ShowDialog(form) != DialogResult.OK) return false;
            if (phones == null) return false;
            bool opened = false;
            FindEq(form, core, phones, () => { form.SetupStep(4); opened = true; form.OpenPersonalize3D(done); });
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
            if (installed && ApoDevices.IsAttached(phonesId) && micOff == null) return;
            string err = null;
            var wait = new WaitDialog("Your headphones", (installed ? "Switching on Equalizer APO for your headphones" : "Installing Equalizer APO (free) for your headphones' EQ") +
                " - approve the Windows prompt. Your sound stops for a moment when it's done.");
            var t = new Thread(() =>
            {
                try { err = ApoDevices.Setup(installed ? null : DriverInstaller.FetchEqualizerApo(), phonesId, micOff); }
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
                    found = AutoEq.BestFor(AutoEq.Index(AppSettings.Folder), names);
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
            if (found != null && filters != null) corrName = found.Name + " to neutral (AutoEq, " + found.Source + ")";
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

    /// <summary>The retune's first step: which mic you talk into and which headphones you listen on.</summary>
    internal sealed class DevicePickDialog : DarkDialog
    {
        readonly AppCore core;
        readonly DarkCombo mics, phones;
        public string PhonesId, MicId;

        public DevicePickDialog(AppCore core) : base("Your mic and headphones", 520, 260)
        {
            this.core = core;
            Text2("Pick the microphone you talk into and the headphones you listen on. Everything that follows is tuned for these two.", 22, 16, 476, 44, Theme.Body, Theme.Text);
            Theme.Label(Body, "Microphone", 22, 76, Theme.Bold);
            mics = new DarkCombo { Location = new Point(Theme.Px(140), Theme.Px(72)), Width = Theme.Px(358) };
            Theme.Label(Body, "Headphones", 22, 118, Theme.Bold);
            phones = new DarkCombo { Location = new Point(Theme.Px(140), Theme.Px(114)), Width = Theme.Px(358) };
            Body.Controls.Add(mics); Body.Controls.Add(phones);
            var micList = core.MicChoices().Where(d => !AudioDevices.IsCable(d)).ToList();
            foreach (var d in micList) mics.Items.Add(d);
            mics.SelectedItem = micList.FirstOrDefault(d => d.Id == core.ActiveMicId) ?? micList.FirstOrDefault();
            var outList = core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList();
            foreach (var d in outList) phones.Items.Add(d);
            string cur = core.GamePhonesId ?? core.S.Game.HeadphonesId ?? core.RealDefaultOutput();
            phones.SelectedItem = outList.FirstOrDefault(d => d.Id == cur) ?? outList.FirstOrDefault();
            var ok = new FlatButton("Continue", true) { Location = new Point(Theme.Px(498) - Theme.Px(140), Theme.Px(200)), Width = Theme.Px(140) };
            var cancel = new FlatButton("Cancel") { Location = new Point(ok.Left - Theme.Px(10) - Theme.Px(110), Theme.Px(200)), Width = Theme.Px(110) };
            ok.Click += (s, e) => Use();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(ok); Body.Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        void Use()
        {
            var mic = mics.SelectedItem as AudioDeviceInfo; var ph = phones.SelectedItem as AudioDeviceInfo;
            if (mic == null || ph == null) { DarkMessage.Show(this, "Pick a microphone and headphones first.", Text); return; }
            if (mic.Id != core.ActiveMicId) { core.S.MicId = mic.Id; core.RestartAudio(); }
            PhonesId = ph.Id; MicId = mic.Id;
            if (core.S.Game.HeadphonesId != ph.Id) { core.S.Game.HeadphonesId = ph.Id; if (core.S.Game.Enabled) core.RestartGameAudio(); }
            core.SaveSoon();
            DialogResult = DialogResult.OK; Close();
        }
    }
}
