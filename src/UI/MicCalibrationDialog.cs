using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// Calibrate microphones: quiet for 3 seconds, then each person talks for 6 seconds on their own mic. Shows what was
    /// measured (voice levels, how much each mic hears the others, doubling before / after) and applies it on request.
    /// </summary>
    internal sealed class MicCalibrationDialog : DarkDialog
    {
        const int QuietMs = 3000, TalkMs = 6000, GapMs = 1800;
        readonly AppCore core;
        readonly string[] ids, names;
        readonly Label step, detail, result;
        readonly Panel bar;
        readonly List<LevelMeter> meters = new List<LevelMeter>();
        readonly FlatButton start, apply, close, reset;
        readonly Timer timer = new Timer { Interval = 50 };
        MicCalibration cal;
        MicCalibration.Result res;
        int stage, stageStart;        // 0 quiet, then gap/talk per mic; -1 idle; 99 done
        bool talking;

        public MicCalibrationDialog(AppCore core) : base("Calibrate microphones", 620, 470)
        {
            this.core = core;
            ids = core.MixerMicIds();
            names = ids.Select((id, i) =>
            {
                var d = core.Mics().FirstOrDefault(m => m.Id == id);
                return "Microphone " + (i + 1) + (d != null ? " - " + d.Name : "");
            }).ToArray();
            Text2("Everyone stays quiet for 3 seconds, then each person talks for 6 seconds on their own microphone, one after the other. StreamLite measures how much each mic hears the other people and sets the volumes and auto mix so one voice isn't heard twice - while two people talking at once both stay in.",
                20, 12, 580, 56, Theme.Small, Theme.Muted);
            step = Text2("Ready when you are.", 20, 76, 580, 28, Theme.Section, Theme.Text);
            detail = Text2("", 20, 106, 580, 40, Theme.Body, Theme.Muted);
            bar = new Panel { Location = new Point(Theme.Px(20), Theme.Px(150)), Size = new Size(Theme.Px(580), Theme.Px(6)), BackColor = Theme.Track };
            bar.Paint += (s, e) =>
            {
                float f = Progress();
                using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, 0, bar.Width * f, bar.Height);
            };
            Body.Controls.Add(bar);
            for (int i = 0; i < ids.Length; i++)
            {
                Text2(names[i], 20, 168 + i * 26, 250, 22, Theme.Small, Theme.Text).AutoEllipsis = true;
                var m = new LevelMeter { Location = new Point(Theme.Px(280), Theme.Px(176 + i * 26)), Width = Theme.Px(320) };
                Body.Controls.Add(m); meters.Add(m);
            }
            int ry = 176 + ids.Length * 26 + 8;
            result = Text2("", 20, ry, 580, 420 - ry, Theme.Small, Theme.Text);
            start = new FlatButton("Start", true);
            apply = new FlatButton("Use these settings", true) { Visible = false };
            close = new FlatButton("Cancel");
            reset = new FlatButton("Remove calibration") { Visible = core.S.MicBleed.Count > 0 };
            Theme.Help(reset, "Back to the plain auto mix (each mic by its level) - your volumes stay as they are.");
            foreach (var b in new[] { reset, close, apply, start }) Body.Controls.Add(b);
            close.Location = new Point(Theme.Px(600) - close.Width, Theme.Px(424));
            start.Location = apply.Location = new Point(close.Left - Theme.Px(10) - start.Width, Theme.Px(424));
            apply.Left = close.Left - Theme.Px(10) - apply.Width;
            reset.Location = new Point(Theme.Px(20), Theme.Px(424));
            start.Click += (s, e) => Begin();
            apply.Click += (s, e) => { core.ApplyMicCalibration(res, ids); DialogResult = DialogResult.OK; Close(); };
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            reset.Click += (s, e) => { core.ClearMicCalibration(); reset.Visible = false; detail.Text = "Calibration removed: auto mix goes by each mic's level again."; };
            CancelButton = close;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            stage = -1;
            if (!core.Engine.Running || ids.Length < 2 || ids.Any(i => i == null))
            {
                start.Enabled = false;
                detail.Text = ids.Length < 2 ? "Add a second microphone first (Microphones > + Add microphone)." : "The microphones aren't running yet - check that the virtual mic is on.";
            }
        }

        float Progress()
        {
            if (stage < 0) return 0;
            if (stage == 99) return 1;
            int total = QuietMs + ids.Length * (GapMs + TalkMs);
            int done = stage == 0 ? 0 : QuietMs + (stage - 1) * (GapMs + TalkMs) + (talking ? GapMs : 0);
            done += Environment.TickCount - stageStart;
            return Math.Min(1f, (float)done / total);
        }

        void Begin()
        {
            cal = new MicCalibration(ids.Length);
            core.Engine.Mixer.PowerTap = cal.Add;
            start.Visible = false; apply.Visible = false; result.Text = "";
            stage = 0; talking = true; stageStart = Environment.TickCount; cal.Phase = 0;
            step.Text = "Everyone quiet, please...";
            detail.Text = "Measuring the room's background noise on every mic.";
        }

        void Tick()
        {
            // Live levels of every mic.
            var live = core.Engine.Mixer.Mics;
            for (int i = 0; i < meters.Count; i++)
            {
                float db = i == 0 ? core.Engine.InputLevelDb : (i - 1 < live.Length ? live[i - 1].LevelDb : -100f);
                meters[i].Level = db;
                meters[i].GateOpen = stage < 1 || !talking || stage == i + 1;
            }
            bar.Invalidate();
            if (stage < 0 || stage == 99 || cal == null) return;
            int t = Environment.TickCount - stageStart;
            if (stage == 0 && t >= QuietMs) { Next(1); return; }
            if (stage >= 1)
            {
                if (!talking && t >= GapMs) { talking = true; stageStart = Environment.TickCount; cal.Phase = stage; step.Text = "Person on " + names[stage - 1] + ": talk now"; return; }
                if (talking)
                {
                    int left = (TalkMs - t + 999) / 1000;
                    detail.Text = "Talk normally - count to ten, or read: \"The quick brown fox jumps over the lazy dog.\"  " + Math.Max(0, left) + " s";
                    if (t >= TalkMs) { if (stage < ids.Length) Next(stage + 1); else Finish(); }
                }
            }
        }

        void Next(int k)
        {
            cal.Phase = -1; stage = k; talking = false; stageStart = Environment.TickCount;
            step.Text = "Next: the person on " + names[k - 1];
            detail.Text = "Everyone else stays quiet. Get ready...";
        }

        void Finish()
        {
            cal.Phase = -1; stage = 99;
            core.Engine.Mixer.PowerTap = null;
            res = cal.Analyze();
            step.Text = res.Problems.Count == 0 ? "Done - here's what was measured" : "Done - with a few things to check";
            detail.Text = "";
            var lines = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                string heard = string.Join(", ", Enumerable.Range(0, ids.Length).Where(j => j != i).Select(j => "mic " + (j + 1) + " hears them " + (-res.BleedDb[i, j]).ToString("0") + " dB quieter"));
                lines.Add((i + 1) + ". voice " + res.VoiceDb[i].ToString("0") + " dB, room " + res.NoiseDb[i].ToString("0") + " dB, volume " + (res.GainDb[i] > 0 ? "+" : "") + res.GainDb[i].ToString("0") + " dB  -  " + heard);
            }
            lines.Add("");
            lines.Add("One person talking, the copy from the other mics: " + res.DoublingBeforeDb.ToString("0") + " dB before, " + res.DoublingAfterDb.ToString("0") + " dB calibrated (lower = less doubled).");
            lines.Add("Two people at once: each keeps about " + (res.TogetherShare * 100).ToString("0") + "% of the mix - nobody is cut off.");
            foreach (var p in res.Problems) lines.Add("! " + p);
            lines.Add("You can still change every volume afterwards in the Microphones list.");
            result.Text = string.Join("\n", lines);
            result.ForeColor = res.Problems.Count == 0 ? Theme.Text : Theme.Warn;
            apply.Visible = true;
            start.Text = "Again"; start.Visible = true; start.Left = apply.Left - Theme.Px(10) - start.Width;
            close.Text = "Close";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            core.Engine.Mixer.PowerTap = null;
            base.OnFormClosed(e);
        }
    }
}
