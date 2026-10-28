using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// "Tune StreamLite to your voice": part of the setup, and any time from the Microphone page. Five short recordings
    /// (two can be skipped), about a minute in all, then StreamLite tunes itself to your voice (VoiceSetup), switches on
    /// the best noise removal and Studio voice your PC runs, and offers Only my voice. Each recording starts from the big
    /// green button (it counts down from 5); then a teleprompter (4.1) shows the lines to read in big letters, every word
    /// turning green the moment StreamLite hears it (Windows' own offline speech recognizer, WordFollower), and the step
    /// ends by itself when the last word is read.
    /// </summary>
    internal sealed class VoiceSetupDialog : DarkDialog
    {
        enum Phase { Intro, Ready, Countdown, Quiet, Talk, Tuning, Result }
        const int CountdownSeconds = 5;
        readonly AppCore core;
        readonly VoiceSetup.Step[] steps = VoiceSetup.Steps();
        int idx = -1;
        volatile Phase phase = Phase.Intro;
        volatile VoiceSetup.Step cur;
        DateTime countEnd, quietUntil, talkSince, doneAt = DateTime.MaxValue;
        readonly Label head, say, progress;
        readonly Teleprompter prompter;
        readonly LevelMeter meter;
        readonly FlatButton primary, secondary, cancel;
        readonly RecordButton rec;
        readonly ToggleSwitch lockToggle;
        readonly Label lockLabel;
        readonly Timer timer = new Timer { Interval = 30 };
        volatile WordFollower follower;
        VoiceSetup.Result result;
        bool begun, finished;

        public VoiceSetupDialog(AppCore core) : base("Tune StreamLite to your voice", 900, 640)
        {
            this.core = core;
            head = Text2("", 24, 16, 852, 30, Theme.Section, Theme.Text);
            say = Text2("", 24, 54, 852, 60, Theme.Body, Theme.Muted);
            // The teleprompter: big, in the middle.
            prompter = new Teleprompter { Location = new Point(Theme.Px(24), Theme.Px(112)), Size = new Size(Theme.Px(852), Theme.Px(250)), Visible = false };
            Body.Controls.Add(prompter);
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
            primary = new FlatButton("Start", true) { Location = new Point(Theme.Px(24), Theme.Px(580)), Width = Theme.Px(160) };
            secondary = new FlatButton("Skip this one") { Location = new Point(Theme.Px(196), Theme.Px(580)), Width = Theme.Px(150) };
            cancel = new FlatButton("Not now") { Location = new Point(Theme.Px(756), Theme.Px(580)), Width = Theme.Px(120) };
            Body.Controls.Add(primary); Body.Controls.Add(secondary); Body.Controls.Add(cancel);
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Skip();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
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
            head.Text = "Tune StreamLite to your voice";
            say.Text = "Five short recordings, about a minute in all - two of them you can skip. You read a few lines out loud from big text, like a teleprompter: each word turns green as StreamLite hears it.";
            prompter.Visible = true; prompter.Script = "Right at your mic.  From further away.  Loud, like a callout.  Quiet, like late at night.  Your silly voice.";
            prompter.Heard = 0; prompter.Run(true);
            progress.Text = "Then StreamLite tunes every voice setting to you and switches on the best noise removal and Studio voice your PC can run. Use the mic you talk into every day, where you usually sit.";
            progress.Height = Theme.Px(44);
            secondary.Visible = false; rec.Visible = false;
            primary.Text = "Start";
        }

        void ShowStep()
        {
            cur = steps[idx];
            phase = Phase.Ready; doneAt = DateTime.MaxValue;
            head.Text = "Step " + (idx + 1) + " of " + steps.Length + " - " + cur.Title;
            say.Text = cur.Say;
            bool script = cur.Line.Length > 0;
            prompter.Script = script ? cur.Line : "Say anything in that voice.";
            prompter.Heard = 0; prompter.Visible = true; prompter.Run(true);
            progress.Text = "Press the green button when you're ready: it counts down from " + CountdownSeconds + ", then read the text out loud.";
            primary.Visible = false;
            rec.Visible = true; rec.Ready();
            secondary.Visible = cur.Optional; secondary.Text = "Skip this one";
            // The recognizer warms up during the countdown (about a second the first time).
            var old = follower; follower = null; if (old != null) old.Dispose();
            if (script) { var text = cur.Line; System.Threading.ThreadPool.QueueUserWorkItem(_ => { var f = new WordFollower(text); if (cur != null && cur.Line == text && phase != Phase.Tuning) follower = f; else f.Dispose(); }); }
        }

        void Primary()
        {
            if (phase == Phase.Intro) { core.BeginVoiceSetup(); begun = true; idx = 0; progress.Height = Theme.Px(24); ShowStep(); return; }
            if (phase == Phase.Result)
            {
                if (lockToggle.Visible && lockToggle.Checked != core.S.VoiceLockOn) { core.S.VoiceLockOn = lockToggle.Checked; core.PushEngineParams(); core.SaveSoon(); }
                DialogResult = DialogResult.OK; Close();
            }
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
            if (idx > 0) cur.SetRoom(RoomDb());
            talkSince = DateTime.Now;
            if (cur.Kind == VoiceSetup.Kind.Normal) { phase = Phase.Quiet; quietUntil = DateTime.Now.AddSeconds(2); }
            else phase = Phase.Talk;
            progress.Text = phase == Phase.Quiet ? "Stay quiet... 2" : "Read it out loud.";
            rec.Recording(false);
        }

        void Skip()
        {
            if (phase != Phase.Ready || !cur.Optional) return;
            cur.Skipped = true;
            Next();
        }

        void FinishStep()
        {
            cur.TakeVoice();
            cur.Done = true;
            Next();
        }

        void Next()
        {
            if (idx + 1 < steps.Length) { idx++; ShowStep(); return; }
            cur = null; phase = Phase.Tuning;
            var f = follower; follower = null; if (f != null) f.Dispose();
            head.Text = "Tuning StreamLite to your voice...";
            say.Text = "A few seconds."; progress.Text = ""; prompter.Run(false); prompter.Visible = false;
            primary.Visible = true; primary.Enabled = false; secondary.Visible = false; cancel.Enabled = false; rec.Visible = false;
            new System.Threading.Thread(() =>
            {
                VoiceSetup.Result r = null; string err = null;
                try { r = core.ApplyVoiceSetup(steps); } catch (Exception ex) { err = ex.Message; }
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
        }

        /// <summary>The room's level from the first step's quiet seconds (dBFS), NaN before it.</summary>
        double RoomDb()
        {
            List<float> l; lock (steps[0].RawDb) l = steps[0].QuietDb.OrderBy(d => d).ToList();
            return l.Count > 10 ? l[l.Count / 2] : double.NaN;
        }

        void Tick()
        {
            if (phase == Phase.Result || phase == Phase.Tuning) return;
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            prompter.Run(false);
            core.Engine.RawTap = null;
            var f = follower; follower = null; if (f != null) f.Dispose();
            if (begun && !finished && phase != Phase.Tuning) core.CancelVoiceSetup();   // left half way: your settings as they were
            if (begun) core.EndVoiceSetup();   // the virtual mic back off, if it was off
            // Asked once: whether finished or not, it isn't offered by itself again (the Microphone page has it).
            if (!core.S.VoiceSetupDone) { core.S.VoiceSetupDone = true; core.SaveSoon(); }
            base.OnFormClosed(e);
        }
    }
}
