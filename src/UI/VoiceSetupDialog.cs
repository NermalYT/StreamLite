using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// "Set up your mic": part of the setup, and any time with Re-Setup Mic on the Microphone page. Three required
    /// recordings, about 30 seconds in all, then StreamLite tunes itself to your voice (VoiceSetup), switches on the best
    /// noise removal and Studio voice your PC runs, and offers Only my voice. After that, or any time later, optional
    /// sections aimed at one kind of sound each (4.4). Each recording starts from the big green button (it counts down
    /// from 5); then a teleprompter (4.1) shows the lines to read in big letters, every word turning green the moment
    /// StreamLite hears it (Windows' own offline speech recognizer, WordFollower), and the step ends by itself when the
    /// last word is read.
    /// </summary>
    internal sealed class VoiceSetupDialog : DarkDialog
    {
        enum Phase { Intro, Ready, Countdown, Quiet, Talk, Tuning, Result, Menu }
        const int CountdownSeconds = 5;
        readonly AppCore core;
        VoiceSetup.Step[] steps = VoiceSetup.Steps();
        int idx = -1;
        volatile Phase phase = Phase.Intro;
        volatile VoiceSetup.Step cur;
        DateTime countEnd, quietUntil, talkSince, doneAt = DateTime.MaxValue;
        readonly Label head, say, progress, plan;
        readonly Teleprompter prompter;
        readonly LevelMeter meter;
        readonly FlatButton primary, secondary, cancel;
        readonly RecordButton rec;
        readonly ToggleSwitch lockToggle;
        readonly Label lockLabel;
        // The result page (4.1.1): record a sample and hear it through the new settings, or tune again.
        readonly FlatButton hearRec, hearPlay;
        readonly ToggleSwitch hearOrig;
        readonly Label hearOrigLabel, hearInfo;
        readonly Timer timer = new Timer { Interval = 30 };
        volatile WordFollower follower;
        VoiceSetup.Result result;
        bool begun, finished, optionalOpened;
        // The optional sections' menu (4.4).
        readonly List<Control> menu = new List<Control>();
        readonly Dictionary<VoiceSetup.Kind, FlatButton> menuButtons = new Dictionary<VoiceSetup.Kind, FlatButton>();
        readonly Label menuLines;
        readonly FlatButton more;
        double normalDb = double.NaN;

        public VoiceSetupDialog(AppCore core) : base("Set up your mic", 900, 640)
        {
            this.core = core;
            head = Text2("", 24, 16, 852, 30, Theme.Section, Theme.Text);
            say = Text2("", 24, 54, 852, 60, Theme.Body, Theme.Muted);
            // The teleprompter: big, in the middle.
            prompter = new Teleprompter { Location = new Point(Theme.Px(24), Theme.Px(112)), Size = new Size(Theme.Px(852), Theme.Px(250)), Visible = false };
            Body.Controls.Add(prompter);
            // The first page: the five recordings as a plain list (the teleprompter comes once you start, 4.1.1).
            plan = Text2("", 24, 120, 852, 236, Theme.Section, Theme.Text);
            meter = new LevelMeter { Location = new Point(Theme.Px(24), Theme.Px(362)), Size = new Size(Theme.Px(852), Theme.Px(10)) };
            Body.Controls.Add(meter);
            progress = Text2("", 24, 380, 852, 24, Theme.Body, Theme.Muted);
            progress.TextAlign = ContentAlignment.MiddleCenter;
            // The record button, big and in the middle.
            rec = new RecordButton { Visible = false };
            rec.Location = new Point((Theme.Px(900) - rec.Width) / 2, Theme.Px(412));
            rec.Click += (s, e) => RecordClicked();
            Body.Controls.Add(rec);
            lockToggle = new ToggleSwitch { Location = new Point(Theme.Px(24), Theme.Px(500)), Visible = false };
            Body.Controls.Add(lockToggle);
            lockLabel = Text2("Only my voice: turn down other people's voices in my room (a girlfriend, a brother, the TV)", 78, 500, 798, 40, Theme.Body, Theme.Text);
            lockLabel.Visible = false;
            hearRec = new FlatButton("●  Record a sample") { Location = new Point(Theme.Px(24), Theme.Px(400)), Width = Theme.Px(180), Visible = false };
            hearPlay = new FlatButton("▶  Hear it tuned") { Location = new Point(Theme.Px(214), Theme.Px(400)), Width = Theme.Px(180), Visible = false };
            hearOrigLabel = Text2("Original", 412, 404, 70, 24, Theme.Body, Theme.Text); hearOrigLabel.Visible = false;
            hearOrig = new ToggleSwitch { Location = new Point(Theme.Px(484), Theme.Px(403)), Visible = false };
            hearInfo = Text2("", 24, 440, 852, 40, Theme.Body, Theme.Muted); hearInfo.Visible = false;
            Body.Controls.Add(hearRec); Body.Controls.Add(hearPlay); Body.Controls.Add(hearOrig);
            hearRec.Click += (s, e) => HearRecord();
            hearPlay.Click += (s, e) => { var vt = core.VoiceTest; if (vt.Playing) vt.StopPlaying(); else { vt.Original = hearOrig.Checked; vt.Play(core.GamePhonesId ?? core.RealDefaultOutput()); } };
            hearOrig.CheckedChanged += (s, e) => core.VoiceTest.Original = hearOrig.Checked;
            primary = new FlatButton("Start", true) { Location = new Point(Theme.Px(24), Theme.Px(580)), Width = Theme.Px(160) };
            secondary = new FlatButton("Skip this one") { Location = new Point(Theme.Px(196), Theme.Px(580)), Width = Theme.Px(150) };
            cancel = new FlatButton("Not now") { Location = new Point(Theme.Px(756), Theme.Px(580)), Width = Theme.Px(120) };
            more = new FlatButton("Optional sections...") { Location = new Point(Theme.Px(358), Theme.Px(580)), Width = Theme.Px(190), Visible = false };
            Body.Controls.Add(primary); Body.Controls.Add(secondary); Body.Controls.Add(cancel); Body.Controls.Add(more);
            more.Click += (s, e) => { KeepLockChoice(); ShowMenu(null); };
            // The optional sections: two columns of three, what each tunes under it.
            int row = 0;
            foreach (var st in steps.Where(x => x.Optional))
            {
                var kind = st.Kind;
                int x = row % 2 == 0 ? 24 : 462, y = 112 + row / 2 * 72;
                var b = new FlatButton(st.Title) { Location = new Point(Theme.Px(x), Theme.Px(y)), Width = Theme.Px(414), Visible = false };
                b.Click += (s, e) => StartOptional(kind);
                Body.Controls.Add(b); menu.Add(b); menuButtons[kind] = b;
                var l = Text2(st.Tunes, x, y + 34, 414, 36, Theme.Small, Theme.Muted); l.Visible = false; menu.Add(l);
                row++;
            }
            menuLines = Text2("", 24, 336, 852, 230, Theme.Body, Theme.Text); menuLines.Visible = false;
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Skip();
            // Tuning again and leaving it: the tuning made before stays, and counts as done (4.1.1).
            cancel.Click += (s, e) => { DialogResult = tunedBefore ? DialogResult.OK : DialogResult.Cancel; Close(); };
            AcceptButton = primary;
            // The raw mic for the step being recorded (the audio thread hands each packet over) - and the teleprompter's ears.
            core.Engine.RawTap = (b, n, t) =>
            {
                var c = cur; var ph = phase;
                if (c == null || (ph != Phase.Quiet && ph != Phase.Talk)) return;
                c.FeedRaw(b, n, ph == Phase.Quiet);
                var f = follower;
                if (f != null && ph == Phase.Talk) { double e = 0; for (int i = 0; i < n; i++) e += b[i] * b[i]; f.Feed(b, n, 10 * Math.Log10(e / Math.Max(1, n) + 1e-12) > -50); }
            };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            ShowIntro();
        }

        void ShowIntro()
        {
            head.Text = "Set up your mic";
            say.Text = "Three short recordings first, about 30 seconds in all. You read a few lines out loud from big text, like a teleprompter: each word turns green as StreamLite hears it.";
            prompter.Visible = false; meter.Visible = false;
            plan.Text = string.Join("\n", steps.Where(s => !s.Optional).Select((s, i) => (i + 1) + "   " + s.Title)) +
                "\n\nOptional, now or any time later:\n" + string.Join(",  ", steps.Where(s => s.Optional).Select(s => s.Title.ToLowerInvariant().Replace("s sounds", "S sounds")));
            plan.Visible = true;
            progress.Text = "Then StreamLite tunes every voice setting to you and switches on the best noise removal and Studio voice your PC can run. Use the mic you talk into every day, where you usually sit.";
            progress.Height = Theme.Px(44);
            // Set up before: the optional sections on their own, with everything else left as it is.
            secondary.Visible = core.S.VoiceSetupDone; secondary.Text = "Optional sections only"; secondary.Width = Theme.Px(200);
            rec.Visible = false;
            primary.Text = "Start";
        }

        void ShowStep()
        {
            cur = steps[idx];
            phase = Phase.Ready; doneAt = DateTime.MaxValue;
            head.Text = cur.Optional ? "Optional - " + cur.Title : "Step " + (idx + 1) + " of " + steps.Count(s => !s.Optional) + " - " + cur.Title;
            say.Text = cur.Say;
            bool script = cur.Line.Length > 0;
            prompter.Script = script ? cur.Line : "Say anything in that voice.";
            prompter.Heard = 0; prompter.Visible = true; prompter.Run(true);
            plan.Visible = false; meter.Visible = true;
            progress.Text = "Press the green button when you're ready: it counts down from " + CountdownSeconds + ", then read the text out loud.";
            primary.Visible = false;
            rec.Visible = true; rec.Ready();
            secondary.Visible = cur.Optional; secondary.Text = "Back";
            // The recognizer warms up during the countdown (about a second the first time).
            var old = follower; follower = null; if (old != null) old.Dispose();
            if (script && cur.Follow) { var text = cur.Line; System.Threading.ThreadPool.QueueUserWorkItem(_ => { var f = new WordFollower(text); if (cur != null && cur.Line == text && phase != Phase.Tuning) follower = f; else f.Dispose(); }); }
        }

        void Primary()
        {
            if (phase == Phase.Intro) { core.BeginVoiceSetup(); begun = true; idx = 0; progress.Height = Theme.Px(24); ShowStep(); return; }
            if (phase == Phase.Result) { KeepLockChoice(); DialogResult = DialogResult.OK; Close(); }
            if (phase == Phase.Menu) { DialogResult = DialogResult.OK; Close(); }
        }

        void KeepLockChoice()
        {
            if (lockToggle.Visible && lockToggle.Checked != core.S.VoiceLockOn) { core.S.VoiceLockOn = lockToggle.Checked; core.PushEngineParams(); core.SaveSoon(); }
        }

        // ------------------------------------------------------------------ the optional sections (4.4)

        /// <summary>The menu: every section with what it tunes, the ones done marked, what the last one did under them.</summary>
        void ShowMenu(string[] lastLines)
        {
            if (!optionalOpened && !begun) { core.BeginVoiceOptional(); }
            optionalOpened = true;
            cur = null; phase = Phase.Menu;
            var f = follower; follower = null; if (f != null) f.Dispose();
            var vt = core.VoiceTest; if (vt.Playing) vt.StopPlaying(); if (vt.Recording) vt.StopRecording();
            head.Text = "Optional sections";
            say.Text = "Each one reads a few lines aimed at one kind of sound and tunes StreamLite to how you make it. Do any of them, in any order - now, or any time later with Re-Setup Mic.";
            say.ForeColor = Theme.Muted; say.Height = Theme.Px(44);
            prompter.Run(false); prompter.Visible = false; plan.Visible = false; meter.Visible = false; rec.Visible = false;
            progress.Text = "";
            hearRec.Visible = hearPlay.Visible = hearOrig.Visible = hearOrigLabel.Visible = hearInfo.Visible = false;
            lockToggle.Visible = lockLabel.Visible = false;
            secondary.Visible = false; more.Visible = false; cancel.Visible = false;
            primary.Text = "Finish"; primary.Visible = true; primary.Enabled = true;
            foreach (var c in menu) c.Visible = true;
            foreach (var kv in menuButtons)
            {
                var st = steps.First(s => s.Kind == kv.Key);
                kv.Value.Text = (core.S.VoiceTests.ContainsKey(kv.Key.ToString()) ? "\u2713  " : "") + st.Title;
            }
            menuLines.Visible = true;
            menuLines.Text = lastLines != null ? string.Join("\n", lastLines.Select(l => "\u2022 " + l)) : "";
        }

        /// <summary>A section from the menu, from the start each time.</summary>
        void StartOptional(VoiceSetup.Kind kind)
        {
            int i = Array.FindIndex(steps, s => s.Kind == kind);
            if (i < 0) return;
            steps[i] = VoiceSetup.Steps().First(s => s.Kind == kind);
            foreach (var c in menu) c.Visible = false;
            menuLines.Visible = false; primary.Visible = false;
            say.Height = Theme.Px(60);
            cancel.Visible = false;
            idx = i;
            ShowStep();
        }

        /// <summary>A section is recorded: tuned, and back to the menu with what it did.</summary>
        void FinishOptional()
        {
            var st = cur;
            cur = null; phase = Phase.Tuning;
            var f = follower; follower = null; if (f != null) f.Dispose();
            head.Text = "Tuning..."; say.Text = "A moment."; progress.Text = ""; prompter.Run(false); prompter.Visible = false; rec.Visible = false; secondary.Visible = false;
            double normal = normalDb;
            new System.Threading.Thread(() =>
            {
                string[] lines;
                try { lines = core.ApplyVoiceOptional(st, normal).Lines.ToArray(); } catch (Exception ex) { lines = new[] { "Couldn't finish: " + ex.Message }; }
                try { BeginInvoke(new Action(() => ShowMenu(lines))); } catch { }
            }) { IsBackground = true, Name = "voice setup section" }.Start();
        }

        /// <summary>The big button: starts the countdown, and while recording (with enough voice) finishes the step.</summary>
        void RecordClicked()
        {
            if (phase == Phase.Ready)
            {
                phase = Phase.Countdown; countEnd = DateTime.Now.AddSeconds(CountdownSeconds);
                secondary.Visible = false;
                Tick();
            }
            else if (phase == Phase.Talk && cur.VoiceSeconds >= 2) FinishStep();
        }

        /// <summary>The countdown is over: recording, straight from the raw mic (RawTap).</summary>
        void StartRecording()
        {
            // The room level from the first step tells voice from room at once.
            double room = RoomDb();
            if (idx > 0) cur.SetRoom(room);
            talkSince = DateTime.Now;
            // A section on its own (no first step this run): it listens to the room itself first.
            if (cur.Kind == VoiceSetup.Kind.Normal || double.IsNaN(room)) { phase = Phase.Quiet; quietUntil = DateTime.Now.AddSeconds(2); }
            else phase = Phase.Talk;
            progress.Text = phase == Phase.Quiet ? "Stay quiet... 2" : "Read it out loud.";
            rec.Recording(false);
        }

        void Skip()
        {
            if (phase == Phase.Intro) { secondary.Width = Theme.Px(150); ShowMenu(null); return; }
            if (phase == Phase.Result) { TuneAgain(); return; }
            if (phase != Phase.Ready || !cur.Optional) return;
            ShowMenu(null);   // Back: the menu, nothing changed
        }

        void FinishStep()
        {
            cur.TakeVoice();
            cur.Done = true;
            if (cur.Optional) { FinishOptional(); return; }
            Next();
        }

        void Next()
        {
            // Required first, then the result (the optional sections are offered there).
            if (idx + 1 < steps.Length && !steps[idx + 1].Optional) { idx++; ShowStep(); return; }
            cur = null; phase = Phase.Tuning;
            var f = follower; follower = null; if (f != null) f.Dispose();
            head.Text = "Tuning StreamLite to your voice...";
            say.Text = "A few seconds."; progress.Text = ""; prompter.Run(false); prompter.Visible = false;
            primary.Visible = true; primary.Enabled = false; secondary.Visible = false; cancel.Enabled = false; rec.Visible = false;
            new System.Threading.Thread(() =>
            {
                VoiceSetup.Result r = null; string err = null;
                try { r = core.ApplyVoiceSetup(steps); normalDb = r.NormalDb; } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke(new Action(() => ShowResult(r, err))); } catch { }
            }) { IsBackground = true, Name = "voice setup" }.Start();
        }

        void ShowResult(VoiceSetup.Result r, string err)
        {
            result = r; phase = Phase.Result; finished = err == null;
            head.Text = err == null ? "Tuned to your voice" : "Couldn't finish";
            say.Text = err ?? string.Join("\n", r.Lines.Select(l => "• " + l));
            say.ForeColor = Theme.Text;
            say.Height = Theme.Px(420);
            progress.Text = ""; meter.Visible = false;
            bool canLock = r != null && r.Print != null;
            lockToggle.Visible = lockLabel.Visible = canLock;
            lockToggle.SetSilently(core.S.VoiceLockOn);
            primary.Text = "Finish"; primary.Visible = true; primary.Enabled = true; cancel.Visible = false; rec.Visible = false;
            // Hear it before you go on, or tune again (4.1.1).
            bool ok = err == null;
            say.Height = Theme.Px(ok ? 330 : 420);
            hearRec.Visible = hearPlay.Visible = hearOrig.Visible = hearOrigLabel.Visible = hearInfo.Visible = ok;
            secondary.Visible = true; secondary.Enabled = true; secondary.Text = "Tune again"; secondary.Width = Theme.Px(150);
            more.Visible = ok;
            if (lockToggle.Visible) { lockToggle.Top = Theme.Px(500); lockLabel.Top = Theme.Px(500); }
            RefreshHear();
        }

        /// <summary>Record a sample (about 8 seconds) of your raw mic; when it stops, it plays tuned on your headphones.</summary>
        void HearRecord()
        {
            var vt = core.VoiceTest;
            if (vt.Recording) { vt.StopRecording(); if (vt.HasClip) { vt.Original = hearOrig.Checked; vt.Play(core.GamePhonesId ?? core.RealDefaultOutput()); } }
            else { if (vt.Playing) vt.StopPlaying(); vt.StartRecording(); hearUntil = DateTime.Now.AddSeconds(8); }
            RefreshHear();
        }
        DateTime hearUntil;
        bool tunedBefore;

        void RefreshHear()
        {
            if (!hearRec.Visible) return;
            var vt = core.VoiceTest;
            if (vt.Recording && DateTime.Now >= hearUntil) { HearRecord(); return; }
            hearRec.Text = vt.Recording ? "■  Stop" : "●  Record a sample";
            hearPlay.Enabled = vt.HasClip && !vt.Recording;
            hearPlay.Text = vt.Playing ? "■  Stop" : "▶  Hear it tuned";
            hearInfo.Text = vt.Recording ? "Recording... " + vt.RecordedSeconds.ToString("0") + " of 8 s - say a few sentences"
                : vt.Playing ? (vt.Original ? "Your raw mic" : "Tuned") + " on your headphones - flip Original to compare. Not happy? Tune again."
                : vt.HasClip ? "Press Hear it tuned to listen on your headphones (flip Original to compare)."
                : "Hear what the tuning did: record a few sentences, then listen on your headphones. Not happy? Tune again.";
        }

        /// <summary>Not happy with it: the required recordings again (the tuning just made stays until the new one replaces it).</summary>
        void TuneAgain()
        {
            var vt = core.VoiceTest; if (vt.Playing) vt.StopPlaying(); if (vt.Recording) vt.StopRecording();
            core.BeginVoiceSetup(); begun = true; finished = false; tunedBefore = true;
            steps = VoiceSetup.Steps(); idx = 0; result = null;
            hearRec.Visible = hearPlay.Visible = hearOrig.Visible = hearOrigLabel.Visible = hearInfo.Visible = false;
            lockToggle.Visible = lockLabel.Visible = false; more.Visible = false;
            say.ForeColor = Theme.Muted; say.Height = Theme.Px(60); meter.Visible = true;
            cancel.Visible = true; cancel.Enabled = true;
            try { HealthMonitor.LogLine("Voice setup: tuning again"); } catch { }
            ShowStep();
        }

        /// <summary>The room's level from the first step's quiet seconds (dBFS), NaN before it.</summary>
        double RoomDb()
        {
            List<float> l; lock (steps[0].RawDb) l = steps[0].QuietDb.OrderBy(d => d).ToList();
            return l.Count > 10 ? l[l.Count / 2] : double.NaN;
        }

        void Tick()
        {
            if (phase == Phase.Result) { RefreshHear(); return; }
            if (phase == Phase.Tuning || phase == Phase.Menu) return;
            meter.Level = core.Engine.InputLevelDb;
            if (phase == Phase.Countdown)
            {
                double left = (countEnd - DateTime.Now).TotalSeconds;
                if (left <= 0) { StartRecording(); return; }
                rec.Countdown((int)Math.Ceiling(left), left - Math.Floor(left) == 0 ? 1 : left - Math.Floor(left));
                progress.Text = "Get ready to read it out loud...";
                return;
            }
            if (phase == Phase.Quiet)
            {
                int left = Math.Max(1, (int)Math.Ceiling((quietUntil - DateTime.Now).TotalSeconds));
                progress.Text = "Stay quiet... " + left;
                if (DateTime.Now >= quietUntil) { phase = Phase.Talk; talkSince = DateTime.Now; progress.Text = "Read it out loud."; }
                return;
            }
            if (phase != Phase.Talk) return;
            double got = cur.VoiceSeconds;
            var f = follower;
            bool script = cur.Line.Length > 0 && f != null;
            if (script)
            {
                // The teleprompter: every word heard turns green; the last one read ends the step (a moment after it).
                int heard = f.Heard;
                prompter.Heard = heard;
                if (heard >= f.Count && doneAt == DateTime.MaxValue) doneAt = DateTime.Now.AddMilliseconds(700);
                bool slow = (DateTime.Now - talkSince).TotalSeconds > 30;
                progress.Text = heard + " of " + f.Count + " words" + (f.Estimating ? " (counted by how long you talk - no speech recognizer on this PC)" : "") +
                    (slow && heard < f.Count ? ". Keep going, or press the button to use what's there." : core.Engine.InputLevelDb < -70 && got < 1 ? ". Nothing heard yet - is it the right mic?" : "");
                rec.Recording(got >= 2);
                if (DateTime.Now >= doneAt && got >= 2) FinishStep();
                // Read everything but the recognizer missed a word or two: once there's plenty of voice, that's enough.
                else if (got >= cur.Seconds * 1.6 && heard >= f.Count - 2 && (DateTime.Now - talkSince).TotalSeconds > cur.Seconds * 1.6) FinishStep();
                return;
            }
            bool slow2 = (DateTime.Now - talkSince).TotalSeconds > 25;
            progress.Text = Math.Min(got, cur.Seconds).ToString("0") + " of " + cur.Seconds.ToString("0") + " s of voice" +
                (slow2 && got < cur.Seconds ? ". Keep going, or press the button to use what's there." : core.Engine.InputLevelDb < -70 && got < 1 ? ". Nothing heard yet - is it the right mic?" : "");
            prompter.Heard = (int)Math.Min(prompter.WordCount, got / Math.Max(1, cur.Seconds) * prompter.WordCount);
            rec.Recording(got >= 2);
            if (got >= cur.Seconds) FinishStep();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (tunedBefore && DialogResult == DialogResult.Cancel && RetuneWizard.JumpTo < 0) DialogResult = DialogResult.OK;   // closed while tuning again: the earlier tuning stays
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            prompter.Run(false);
            try { if (core.VoiceTest.Playing) core.VoiceTest.StopPlaying(); if (core.VoiceTest.Recording) core.VoiceTest.StopRecording(); } catch { }
            core.Engine.RawTap = null;
            var f = follower; follower = null; if (f != null) f.Dispose();
            if (begun && !finished && phase != Phase.Tuning) core.CancelVoiceSetup();   // left half way: your settings as they were
            if (begun || optionalOpened) core.EndVoiceSetup();   // the virtual mic back off, if it was off
            // Asked once: whether finished or not, it isn't offered by itself again (the Microphone page has it).
            if (!core.S.VoiceSetupDone) { core.S.VoiceSetupDone = true; core.SaveSoon(); }
            base.OnFormClosed(e);
        }
    }
}
