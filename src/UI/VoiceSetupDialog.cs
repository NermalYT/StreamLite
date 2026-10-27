using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// "Tune StreamLite to your voice": offered once after installing or updating (it took the place of the Direct to
    /// headphones question), and any time from the Microphone page. Five short recordings (two can be skipped), about a
    /// minute in all, then StreamLite tunes itself to your voice (VoiceSetup) and offers Only my voice.
    /// </summary>
    internal sealed class VoiceSetupDialog : DarkDialog
    {
        enum Phase { Intro, Ready, Quiet, Talk, Tuning, Result }
        readonly AppCore core;
        readonly VoiceSetup.Step[] steps = VoiceSetup.Steps();
        int idx = -1;
        volatile Phase phase = Phase.Intro;
        volatile VoiceSetup.Step cur;
        DateTime quietUntil, talkSince;
        readonly Label head, say, line, progress;
        readonly LevelMeter meter;
        readonly FlatButton primary, secondary, cancel;
        readonly ToggleSwitch lockToggle;
        readonly Label lockLabel;
        readonly Timer timer = new Timer { Interval = 100 };
        VoiceSetup.Result result;
        bool begun, finished;

        public VoiceSetupDialog(AppCore core) : base("Tune StreamLite to your voice", 620, 560)
        {
            this.core = core;
            head = Text2("", 24, 16, 572, 30, Theme.Section, Theme.Text);
            say = Text2("", 24, 56, 572, 136, Theme.Body, Theme.Text);
            line = Text2("", 24, 198, 572, 96, Theme.Bold, Theme.Accent);
            meter = new LevelMeter { Location = new Point(Theme.Px(24), Theme.Px(300)), Size = new Size(Theme.Px(572), Theme.Px(10)) };
            Body.Controls.Add(meter);
            progress = Text2("", 24, 318, 572, 60, Theme.Small, Theme.Muted);
            lockToggle = new ToggleSwitch { Location = new Point(Theme.Px(24), Theme.Px(420)), Visible = false };
            Body.Controls.Add(lockToggle);
            lockLabel = Text2("Only my voice: turn down other people's voices in my room (a girlfriend, a brother, the TV)", 78, 420, 518, 40, Theme.Body, Theme.Text);
            lockLabel.Visible = false;
            primary = new FlatButton("Start", true) { Location = new Point(Theme.Px(24), Theme.Px(500)), Width = Theme.Px(150) };
            secondary = new FlatButton("Skip this one") { Location = new Point(Theme.Px(186), Theme.Px(500)), Width = Theme.Px(140) };
            cancel = new FlatButton("Not now") { Location = new Point(Theme.Px(476), Theme.Px(500)), Width = Theme.Px(120) };
            Body.Controls.Add(primary); Body.Controls.Add(secondary); Body.Controls.Add(cancel);
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Skip();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = primary;
            // The raw mic's levels for the step being recorded (the audio thread hands each packet over).
            core.Engine.RawTap = (b, n, t) => { var c = cur; var ph = phase; if (c != null && (ph == Phase.Quiet || ph == Phase.Talk)) c.FeedRaw(b, n, ph == Phase.Quiet); };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            ShowIntro();
        }

        void ShowIntro()
        {
            head.Text = "Tune StreamLite to your voice";
            say.Text = "Five short recordings, about a minute in all - two of them you can skip. Every voice setting starts from scratch for them (what you have now comes back if you stop half way):\n" +
                "   1. right at your mic, your normal voice\n   2. from further away (your bed, the sofa)\n   3. loud, like a callout\n   4. quiet, like late at night\n   5. your silly voice, if you put one on for fun (an Elmo voice...)";
            line.Text = "Then StreamLite tunes every voice setting to you: noise removal for your room, de-hum, low cut and de-esser for your voice, gate, compressor, auto level, Across the room, Studio voice, and your voiceprint for Only my voice.";
            line.ForeColor = Theme.Muted; line.Font = Theme.Body;
            progress.Text = "Use the mic you talk into every day, where you usually sit.";
            secondary.Visible = false;
            primary.Text = "Start";
        }

        void ShowStep()
        {
            cur = steps[idx];
            phase = Phase.Ready;
            head.Text = "Step " + (idx + 1) + " of " + steps.Length + " - " + cur.Title;
            say.Text = cur.Say;
            line.Text = cur.Line; line.ForeColor = Theme.Accent; line.Font = Theme.Bold;
            progress.Text = "Press Record when you're ready.";
            primary.Text = "Record"; primary.Enabled = true;
            secondary.Visible = cur.Optional; secondary.Text = "Skip this one";
        }

        void Primary()
        {
            if (phase == Phase.Intro) { core.BeginVoiceSetup(); begun = true; idx = 0; ShowStep(); return; }
            if (phase == Phase.Ready)
            {
                core.Lock.StartLearning();
                talkSince = DateTime.Now;
                if (cur.Kind == VoiceSetup.Kind.Normal) { phase = Phase.Quiet; quietUntil = DateTime.Now.AddSeconds(2); }
                else phase = Phase.Talk;
                primary.Text = "Done"; primary.Enabled = false; secondary.Visible = false;
                return;
            }
            if (phase == Phase.Talk) { FinishStep(); return; }
            if (phase == Phase.Result)
            {
                if (lockToggle.Visible && lockToggle.Checked != core.S.VoiceLockOn) { core.S.VoiceLockOn = lockToggle.Checked; core.PushEngineParams(); core.SaveSoon(); }
                DialogResult = DialogResult.OK; Close();
            }
        }

        void Skip()
        {
            if (phase != Phase.Ready || !cur.Optional) return;
            cur.Skipped = true;
            Next();
        }

        void FinishStep()
        {
            cur.Voice16 = core.Lock.TakeLearned();
            cur.Done = true;
            Next();
        }

        void Next()
        {
            if (idx + 1 < steps.Length) { idx++; ShowStep(); return; }
            cur = null; phase = Phase.Tuning;
            head.Text = "Tuning StreamLite to your voice...";
            say.Text = "A few seconds."; line.Text = ""; progress.Text = "";
            primary.Enabled = false; secondary.Visible = false; cancel.Enabled = false;
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
            say.Height = Theme.Px(330);
            line.Text = ""; progress.Text = ""; meter.Visible = false;
            bool canLock = r != null && r.Print != null;
            lockToggle.Visible = lockLabel.Visible = canLock;
            lockToggle.Location = new Point(Theme.Px(24), Theme.Px(400)); lockLabel.Location = new Point(Theme.Px(78), Theme.Px(400));
            lockToggle.SetSilently(core.S.VoiceLockOn);
            primary.Text = "Finish"; primary.Enabled = true; cancel.Visible = false;
        }

        void Tick()
        {
            if (phase == Phase.Result || phase == Phase.Tuning) return;
            meter.Level = core.Engine.InputLevelDb;
            if (phase == Phase.Quiet)
            {
                int left = Math.Max(1, (int)Math.Ceiling((quietUntil - DateTime.Now).TotalSeconds));
                progress.Text = "Stay quiet... " + left;
                if (DateTime.Now >= quietUntil) { phase = Phase.Talk; talkSince = DateTime.Now; }
                return;
            }
            if (phase != Phase.Talk) return;
            double got = core.Lock.LearnedSeconds;
            bool slow = (DateTime.Now - talkSince).TotalSeconds > 25;
            progress.Text = "Talk now - " + Math.Min(got, cur.Seconds).ToString("0") + " of " + cur.Seconds.ToString("0") + " s of voice" +
                (slow && got < cur.Seconds ? ". Keep going, or press Done to use what's there." : core.Engine.InputLevelDb < -70 && got < 1 ? ". Nothing heard yet - is it the right mic?" : "");
            primary.Enabled = got >= 2;
            if (got >= cur.Seconds) FinishStep();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            core.Engine.RawTap = null;
            if (core.Lock.Learning) core.Lock.CancelLearning();
            if (begun && !finished && phase != Phase.Tuning) core.CancelVoiceSetup();   // left half way: your settings as they were
            // Asked once: whether finished or not, it isn't offered by itself again (the Microphone page has it).
            if (!core.S.VoiceSetupDone) { core.S.VoiceSetupDone = true; core.SaveSoon(); }
            base.OnFormClosed(e);
        }
    }
}
