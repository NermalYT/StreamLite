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
    /// 4.0's fresh start, offered once after updating from an older StreamLite: every tuning setting back to the start,
    /// then - one step after the other - your mic and headphones, Tune to my voice from the raw mic, your headphones'
    /// EQ (found by themselves, or you pick the model), and Personalize 3D with that EQ on.
    /// </summary>
    internal static class RetuneWizard
    {
        public const string Question = "Reset settings for improved tuning technologies?";

        /// <summary>Asks once; on Yes runs the whole retune.</summary>
        public static void Offer(MainForm form, AppCore core)
        {
            core.S.RetuneAsked = true; core.SaveSoon();
            using (var d = new ConfirmDialog("StreamLite 4.0", Question + "\n\nStreamLite 4.0 tunes your mic from its raw sound, your headphones' EQ and 3D audio to your ears. Yes starts every tuning setting fresh and walks you through it (about 6 minutes). Your devices, profiles and hotkeys stay.", "Yes", "No", 520, 250))
                if (d.ShowDialog(form) != DialogResult.OK) return;
            Run(form, core);
        }

        public static void Run(MainForm form, AppCore core)
        {
            core.ResetTuning();
            // 1. Your mic and headphones.
            string phonesId;
            using (var d = new DevicePickDialog(core))
            {
                if (d.ShowDialog(form) != DialogResult.OK) return;
                phonesId = d.PhonesId;
            }
            // 2. Tune to my voice, from the raw mic.
            form.OpenVoiceSetup();
            // 3. The headphones: their EQ, then 3D audio tuned to your ears.
            var phones = core.Outputs().FirstOrDefault(o => o.Id == phonesId);
            string name = phones != null ? phones.Name : "your headphones";
            using (var d = new ConfirmDialog("Your headphones", "Your mic is tuned. Now retune 3D audio and the EQ for " + name + "?\n\nStreamLite finds their EQ, puts it on, then Personalize 3D (about 4 minutes) tunes 3D audio to your ears.", "Retune headphones", "Not now", 520, 230))
                if (d.ShowDialog(form) != DialogResult.OK) return;
            if (phones == null) return;
            FindEq(form, core, phones, () => form.OpenPersonalize3D());
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
        public string PhonesId;

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
            PhonesId = ph.Id;
            if (core.S.Game.HeadphonesId != ph.Id) { core.S.Game.HeadphonesId = ph.Id; if (core.S.Game.Enabled) core.RestartGameAudio(); }
            core.SaveSoon();
            DialogResult = DialogResult.OK; Close();
        }
    }
}
