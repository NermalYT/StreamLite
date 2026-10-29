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
        public static readonly string UpdateText = "StreamLite now takes your mic's raw sound - none of the effects " + Os.TheSystem + " or your mic's maker run first - and tunes everything from it. So everything is set up again, one step at a time (about 6 minutes): your mic, your voice, your headphones' EQ and 3D audio tuned to your ears. Your voice and 3D settings start fresh; your devices, profiles and hotkeys stay.";
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
            // 4.1.1: the developer's thank-you first, once (Continue once "I understand" is ticked).
            if (!core.S.WelcomeShown)
            {
                using (var w = new WelcomeDialog())
                    if (w.ShowDialog(form) != DialogResult.OK) { form.EndSetupScreen(); return; }
                core.S.WelcomeShown = true; core.SaveSoon(); justShownWelcome = true;
            }
            int done0 = StepsDone(core.S);
            wizForm = form; wizCore = core;
            // Everything up to 3D audio done: only the camera is left (4.1.1).
            if (done0 >= 5) { Flow(form, core, 5); return; }
            string text = done0 > 0 ? "Pick up where you left off: " + SetupCover.ShownNames(done0).Count() + " of " + SetupCover.ShownCount + " steps are done (" + string.Join(", ", SetupCover.ShownNames(done0)) + "). Next: " + SetupCover.Steps[done0] + "." : updated ? UpdateText : FirstText;
            // 4.1: coming from 4.0.5 or later with your settings saved, you may keep them and skip the setup.
            bool canKeep = updated && HasSavedSettings(core.S);
            if (canKeep && done0 == 0) text += "\n\nHappy with how StreamLite sounds now? Keep your settings and skip the setup.";
            var choices = canKeep ? new[] { done0 > 0 ? "Carry on" : "Start", "Keep my settings", "Later" } : new[] { done0 > 0 ? "Carry on" : "Start", "Later" };
            // A new install that just read the thank-you goes straight on to the first step (no second popup).
            bool justWelcomed = !updated && done0 == 0 && core.S.WelcomeShown && justShownWelcome;
            justShownWelcome = false;
            if (!justWelcomed)
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
                    if (d.Choice != 0) { if (JumpTo >= 0) Flow(form, core, JumpTo); else form.EndSetupScreen(); return; }
                }
            if (updated && core.S.ResetDoneFor != RequiredVersion)
            {
                SettingsTransfer.Keep(core.S);   // 4.1.3: what's started fresh can come back (Scan for settings)
                core.ResetTuning();
                core.S.ResetDoneFor = RequiredVersion; core.SaveSoon();
            }
            Flow(form, core, -1);
        }

        // ---- Going back (4.1.1): the steps along the bottom of the setup screen can be clicked - any step reached so far.
        static MainForm wizForm;
        static AppCore wizCore;
        /// <summary>The step clicked (0-based) while a step's window was open: it closes and the setup goes on from there.</summary>
        internal static int JumpTo = -1;

        /// <summary>A step clicked on the setup screen: whatever is open closes and the setup carries on from that step.</summary>
        internal static void Jump(int step)
        {
            if (wizForm == null || !wizForm.InSetupScreen) return;
            try { HealthMonitor.LogLine("Setup: back to step " + (step + 1) + " (" + SetupCover.Steps[step] + ")"); } catch { }
            var open = Application.OpenForms.OfType<DarkDialog>().Where(f => f.Visible).ToList();
            if (open.Count > 0)
            {
                JumpTo = step;
                foreach (var f in open) { try { f.DialogResult = DialogResult.Cancel; f.Close(); } catch { } }
                return;
            }
            // On the 3D audio page (no window open): it goes, and the setup runs from the step clicked.
            wizForm.EndPersonalizePage();
            var form = wizForm; var core = wizCore;
            form.BeginInvoke(new Action(() => Flow(form, core, step)));
        }

        /// <summary>The steps from startAt (-1 = where they're due), again from the step clicked when one is.</summary>
        static void Flow(MainForm form, AppCore core, int startAt)
        {
            while (true)
            {
                JumpTo = -1;
                bool toPersonalize = false;
                if (startAt == 5) { CameraThenDone(form, core, true); return; }
                try { toPersonalize = RunSteps(form, core, () => AfterPersonalize(form, core), startAt); }
                catch { }
                if (JumpTo >= 0) { startAt = JumpTo; continue; }
                if (!toPersonalize) form.EndSetupScreen();
                return;
            }
        }

        static void AfterPersonalize(MainForm form, AppCore core)
        {
            if (!Personal3DView.LastRunCompleted)
            {
                // Later / Back to StreamLite (4.1.4): Personalize 3D waits on the 3D Audio page and the setup carries on
                // with the camera (it used to end here and bring the camera step back only at the next start).
                try { HealthMonitor.LogLine("Setup: Personalize 3D left for later"); } catch { }
                CameraThenDone(form, core);
                return;
            }
            Did(core, 5);
            // Your head found (4.1.2): 3D audio goes on - for games (the other apps play as they are).
            if (!core.S.Game.Enabled)
            {
                core.S.Game.Enabled = true; core.SaveSoon(); core.RestartGameAudio();
                try { HealthMonitor.LogLine("Setup: 3D audio on (Personalize 3D done)"); } catch { }
            }
            CameraThenDone(form, core);
        }

        /// <summary>6. The camera (4.1.1), then the full app - or back to a step clicked meanwhile.</summary>
        static void CameraThenDone(MainForm form, AppCore core, bool clicked = false)
        {
            JumpTo = -1;
            form.EndPersonalizePage();   // the 3D page doesn't stay behind the camera step
            form.SetupStep(5);
            if (clicked || !Imported(core, 5)) CameraStep(form, core);
            if (JumpTo >= 0) { int to = JumpTo; Flow(form, core, to); return; }
            form.EndSetupScreen();
            Finished(core);
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
            core.S.SetupDoneFor = RequiredVersion; core.S.SetupKind = null; core.S.SetupImported.Clear(); core.S.Save();
            try { HealthMonitor.LogLine("Setup done (" + RequiredVersion + "): raw mic tuning, headphone EQ, 3D audio"); } catch { }
            if (!core.S.RunAsAdmin || Elevation.IsElevated || Os.Linux) return;   // Linux: nothing runs "as administrator"
            bool task = Elevation.TaskRuns(Application.ExecutablePath);
            try { HealthMonitor.LogLine("Setup done: restart with administrator rights - " + (task ? "yes" : "no, the admin task doesn't start " + Application.ExecutablePath)); } catch { }
            if (task) Program.RestartElevated();
        }

        /// <summary>A step your imported settings cover (4.1.3: 2 voice, 3 headphone EQ, 4 3D, 5 camera) - skipped unless you click it.</summary>
        internal static bool Imported(AppCore core, int step) { return core.S.SetupImported.Contains(step); }

        /// <summary>Which setup steps imported pieces cover, for the mic and headphones picked.</summary>
        internal static List<int> StepsCovered(IEnumerable<SetupPart> parts, Func<SetupPart, string> target, string phonesId)
        {
            var l = new List<int>();
            foreach (var p in parts)
            {
                if (p.Area == SetupParts.Mic && p.Id != "mic.options") l.Add(2);
                if (p.Id == "phones.correction" && target(p) == phonesId) l.Add(3);
                if (p.Id == "phones.head" && target(p) == phonesId) l.Add(4);
                if (p.Area == SetupParts.Camera) l.Add(5);
            }
            return l.Distinct().OrderBy(x => x).ToList();
        }

        /// <summary>Steps of this setup already done (leaving half way and coming back picks up there, 4.0.7).</summary>
        internal static int StepsDone(AppSettings s) { return s.SetupStepsFor == RequiredVersion ? Math.Max(0, Math.Min(5, s.SetupStepsDone)) : 0; }

        static bool justShownWelcome;

        /// <summary>The camera step (4.1.1): StreamLite Camera, AI tracking, a background and the hand signs - or not.</summary>
        static void CameraStep(MainForm form, AppCore core)
        {
            try
            {
                using (var d = new CameraSetupDialog(core)) d.ShowDialog(form);
                var C = core.S.CamFx;
                HealthMonitor.LogLine("Setup: camera " + (C.Enabled ? "on" : "off") + ", AI tracking " + (C.FaceTracking ? "on" : "off") + ", background " + C.Background +
                    ", gestures palm " + C.GesturePalm + " V " + C.GestureV + " (" + C.VAction + ") L " + C.GestureL + " point " + C.GesturePoint);
            }
            catch (Exception ex) { try { HealthMonitor.LogLine("Setup: camera step - " + ex.Message); } catch { } }
        }

        static void Did(AppCore core, int steps)
        {
            if (StepsDone(core.S) >= steps) return;
            core.S.SetupStepsFor = RequiredVersion; core.S.SetupStepsDone = steps; core.SaveSoon();
        }

        /// <summary>true = it went on to Personalize 3D; done runs when that closes (it ends the setup screen).</summary>
        static bool RunSteps(MainForm form, AppCore core, Action done, int startAt)
        {
            int from = StepsDone(core.S);
            Func<int, bool> again = k => startAt == k;   // the step clicked runs again even when it's done
            // 1. Your mic and headphones (picked already: kept).
            form.SetupStep(0);
            string phonesId = core.GamePhonesId ?? core.S.Game.HeadphonesId, micId = core.S.MicId;
            if (from < 1 || again(0) || phonesId == null || core.Outputs().All(o => o.Id != phonesId))
            {
                using (var d = new DevicePickDialog(core))
                {
                    bool ok = d.ShowDialog(form) == DialogResult.OK;
                    // Back at a step done before and left with Cancel: what you had stays, the setup goes on (4.1.1).
                    if (!ok && (JumpTo >= 0 || from < 1 || phonesId == null)) return false;
                    if (ok) { phonesId = d.PhonesId; micId = d.MicId; }
                }
                Did(core, 1);
            }
            // 2. Equalizer APO on those headphones (off the mic), by StreamLite - every time: nothing to do when it's on.
            form.SetupStep(1);
            SetUpApo(form, core, phonesId, micId);
            if (JumpTo >= 0) return false;
            Did(core, 2);
            // 3. Tune to my voice, from the raw mic - all of it (leaving it ends the setup for now).
            form.SetupStep(2);
            if ((from < 3 && !Imported(core, 2)) || again(2))
            {
                bool tuned = form.OpenVoiceSetup();
                if (JumpTo >= 0 || (!tuned && from < 3)) return false;   // back at it after it was done: Not now keeps the tuning
                Did(core, 3);
            }
            else if (from < 3) Did(core, 3);   // 4.1.3: your imported mic tuning (click the step to tune again)
            // 4. The headphones' EQ - straight on, no question in between - then 3D audio tuned to your ears.
            form.SetupStep(3);
            var phones = core.Outputs().FirstOrDefault(o => o.Id == phonesId);
            if (phones == null) return false;
            bool opened = false;
            Action to3D = () =>
            {
                Did(core, 4); form.SetupStep(4); opened = true;
                // 4.1.3: your imported personal 3D for these headphones - Personalize 3D is done already (click its step to do it again).
                if (Imported(core, 4) && !again(4)) { Personal3DView.LastRunCompleted = true; form.BeginInvoke(done); return; }
                form.OpenPersonalize3D(done);
            };
            // Coming back for 3D audio (or your imported headphone EQ, 4.1.3): the EQ stays (no second lookup, no second question).
            if ((from >= 4 || Imported(core, 3)) && !again(3) && core.S.Profiles.CorrectionFor(phones.Id) != null) to3D();
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
            if (Os.Linux) return;   // Linux: the headphone EQ runs inside StreamLite, there's no Equalizer APO
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
                    // A wireless dongle (4.1.1): its name isn't your headphones' - only AutoEq's quick list is tried (a dongle
                    // made for one headset carries its name), then Find my headphones asks which ones you have.
                    found = AutoEq.IsAdapter(phones.Name) ? AutoEq.BestFor(AutoEq.Index(AppSettings.Folder), names) : SquigLink.BestFor(AppSettings.Folder, names);   // AutoEq first, then squig.link
                    if (found != null) { double pre; filters = AutoEq.Download(found, out pre); }
                }
                catch { found = null; }
                try { wait.BeginInvoke(new Action(() => { wait.DialogResult = DialogResult.OK; wait.Close(); })); } catch { }
            }) { IsBackground = true, Name = "retune EQ" };
            wait.Shown += (s, e) => t.Start();
            bool cancelled = wait.ShowDialog(form) != DialogResult.OK;
            wait.Dispose();
            if (cancelled || JumpTo >= 0) return;
            string corrName = null;
            if (found != null && filters != null) corrName = HeadphoneFinder.Named(found);
            else
                using (var f = new HeadphoneFinder(phones.Name))
                    if (f.ShowDialog(form) == DialogResult.OK && f.ChosenFilters != null) { corrName = f.ChosenName; filters = f.ChosenFilters; }
            if (JumpTo >= 0) return;   // a step clicked meanwhile
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
            // 4.1.3: bring back settings you kept or exported - only the parts you pick; the setup skips the steps they cover.
            var scan = new FlatButton("Scan for settings") { Location = new Point(Theme.Px(22), Theme.Px(412)), Width = Theme.Px(150) };
            var import = new FlatButton("Import settings...") { Location = new Point(scan.Right + Theme.Px(8), Theme.Px(412)), Width = Theme.Px(150) };
            Theme.Help(scan, "Finds settings StreamLite kept on this PC (also after uninstalling) and settings files in Documents, Desktop and Downloads. Pick the parts to keep - your mic tuning and camera, say - and set up the rest here.");
            Theme.Help(import, "Open a settings file (yours or someone else's export) and pick the parts to bring in. The setup skips the steps they cover.");
            scan.Click += (s, e) => Bring(SettingsTransfer.ScanAndImport(this, core));
            import.Click += (s, e) => Bring(SettingsTransfer.ImportFile(this, core, null));
            Body.Controls.Add(scan); Body.Controls.Add(import);
            importNote = Text2("", 22, 446, 716, 20, Theme.Small, Theme.Accent);
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

        Label importNote;
        List<SetupPart> imported;
        Dictionary<SetupPart, string> importedTo;

        /// <summary>Settings brought in from a scan or a file: applied now; which steps they cover is decided once the headphones are picked.</summary>
        void Bring(List<SetupPart> parts)
        {
            if (parts == null) return;
            imported = parts;
            // Where each device piece went (the same matching the import showed).
            var names = SettingsTransfer.Names(core);
            importedTo = new Dictionary<SetupPart, string>();
            foreach (var p in parts.Where(x => x.DeviceKind != null))
            {
                var here = SettingsTransfer.DevicesHere(core, p.DeviceKind);
                var id = SetupExport.Match(p.Device, here, null); if (id != null) importedTo[p] = id;
            }
            core.PushEngineParams(); core.SaveSoon();
            var areas = parts.Select(p => p.Area).Distinct().ToList();
            importNote.Text = "Brought in: " + string.Join(", ", areas) + ". The setup skips the steps they cover (click a step to do it anyway).";
        }

        void Use()
        {
            var mic = micTiles.Where(t => t.Selected).Select(t => t.Device).FirstOrDefault();
            var ph = phoneTiles.Where(t => t.Selected).Select(t => t.Device).FirstOrDefault();
            if (mic == null || ph == null) { DarkMessage.Show(this, "Pick a microphone and headphones first.", Text); return; }
            if (mic.Id != core.ActiveMicId) { core.S.MicId = mic.Id; core.RestartAudio(); }
            PhonesId = ph.Id; MicId = mic.Id;
            if (core.S.Game.HeadphonesId != ph.Id) { core.S.Game.HeadphonesId = ph.Id; if (core.EngineOn) core.RestartGameAudio(); }
            if (imported != null)
            {
                core.S.SetupImported = RetuneWizard.StepsCovered(imported, p => { string id; return importedTo.TryGetValue(p, out id) ? id : null; }, ph.Id);
                try { HealthMonitor.LogLine("Setup: imported settings cover steps " + string.Join(", ", core.S.SetupImported.Select(x => x + 1))); } catch { }
            }
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
        void Split(string full, out string name, out string kind)
        {
#if LINUX
            // Linux names are the device's own ("Sennheiser MOMENTUM 5"); what it is comes from PipeWire.
            name = full;
            var d = Interop.AudioDevices.Find(Device.Id);
            string ff = d != null ? d.FormFactor : null;
            kind = ff == "headphone" || ff == "headset" ? "Headphones" : ff == "speaker" ? "Speakers" : ff == "microphone" || ff == "webcam" ? "Microphone"
                : Device.Id != null && Device.Id.StartsWith("alsa_input", StringComparison.Ordinal) ? "Microphone" : Device.Id != null && Device.Id.StartsWith("bluez", StringComparison.Ordinal) ? "Bluetooth" : "Sound device";
            return;
#endif
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
            Icons.Draw(g, glyph, 15f, new Rectangle(Theme.Px(10), 0, (int)icon, Height), selected ? Theme.Accent : Theme.Muted);
            string name, kind; Split(Device.Name ?? "", out name, out kind);
            int tx = Theme.Px(58), tw = Width - tx - Theme.Px(36);
            TextRenderer.DrawText(g, name, Theme.Bold, new Rectangle(tx, Height / 2 - Theme.Px(20), tw, Theme.Px(22)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, kind, Theme.Small, new Rectangle(tx, Height / 2 + Theme.Px(1), tw, Theme.Px(18)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected)
                Icons.Draw(g, "", 11f, new Rectangle(Width - Theme.Px(34), 0, Theme.Px(24), Height), Theme.Accent);
            if (!float.IsNaN(level))
            {
                float t = Math.Max(0, Math.Min(1, (level + 60) / 60f));
                var bar = new RectangleF(tx, Height - Theme.Px(9), tw * t, Theme.Px(3));
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, bar);
            }
        }
    }
}