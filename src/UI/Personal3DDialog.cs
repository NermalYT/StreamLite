using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// Personalize 3D: a blind listening test. Footsteps play at a random spot with a random setting (no 3D, the KU100
    /// or KEMAR head, cue sharpening off / 35% / 70%); you click where you heard them. Shows how well you place sounds
    /// with each one - and with no 3D at all - and saves the best for these headphones.
    /// </summary>
    internal sealed class Personal3DDialog : DarkDialog
    {
        readonly AppCore core;
        readonly string phones;
        readonly Label step, result;
        readonly List<FlatButton> answers = new List<FlatButton>();
        readonly FlatButton start, again, apply, close;
        readonly Random rnd = new Random();
        List<Personal3D.Variant> round1, round2;
        List<Personal3D.Trial> trials;
        int index, round;
        bool playing;
        Personal3D.Variant winner;

        public Personal3DDialog(AppCore core) : base("Personalize 3D", 620, 500)
        {
            this.core = core;
            phones = core.GamePhonesId ?? core.RealDefaultOutput();
            Text2("Put your headphones on. Footsteps play from somewhere around you - in front, behind, left, right, above or below - each time with a different setting, in random order. Click where you heard them; guess when unsure. About 2 minutes. StreamLite then shows which head model and cue sharpening let you place sounds best - and how you do with no 3D at all - and saves the best for these headphones.",
                20, 12, 580, 74, Theme.Small, Theme.Muted);
            step = Text2("Ready when you are.", 20, 94, 580, 28, Theme.Section, Theme.Text);
            // Where it came from: above on top, below at the bottom, the compass in between.
            var pos = new[] { new Point(250, 140), new Point(250, 186), new Point(250, 278), new Point(90, 232), new Point(410, 232), new Point(250, 324) };
            int[] order = { 4, 0, 1, 2, 3, 5 };   // Above, Front, Behind, Left, Right, Below
            for (int k = 0; k < order.Length; k++)
            {
                int place = order[k];
                var b = new FlatButton(Personal3D.Places[place]) { Location = new Point(Theme.Px(pos[k].X), Theme.Px(pos[k].Y)), Width = Theme.Px(120), Enabled = false };
                b.Click += (s, e) => Answer(place);
                Body.Controls.Add(b); answers.Add(b);
            }
            again = new FlatButton("Play again") { Location = new Point(Theme.Px(250), Theme.Px(232)), Width = Theme.Px(120), Enabled = false };
            again.Click += (s, e) => PlayCurrent();
            Body.Controls.Add(again);
            result = Text2("", 20, 140, 580, 300, Theme.Body, Theme.Text);
            result.Visible = false;
            start = new FlatButton("Start", true);
            apply = new FlatButton("Use it for these headphones", true) { Visible = false };
            close = new FlatButton("Close");
            foreach (var b in new[] { start, apply, close }) Body.Controls.Add(b);
            close.Location = new Point(Theme.Px(600) - close.Width, Theme.Px(454));
            start.Location = new Point(close.Left - Theme.Px(10) - start.Width, Theme.Px(454));
            apply.Location = new Point(close.Left - Theme.Px(10) - apply.Width, Theme.Px(454));
            start.Click += (s, e) => Begin();
            apply.Click += (s, e) =>
            {
                core.SavePersonal3D(phones, winner.Hrtf, winner.Cue);
                DialogResult = DialogResult.OK; Close();
            };
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            CancelButton = close;
            if (phones == null) { start.Enabled = false; step.Text = "No headphones found."; }
            else
            {
                var d = core.Outputs().FirstOrDefault(o => o.Id == phones);
                string mine = core.Personal3D(phones);
                Text2("Headphones: " + (d != null ? d.Name : "your default output") + (mine != null ? "  -  tested before: " + mine.Replace("|", ", cue ") + "%" : ""), 20, 420, 580, 22, Theme.Small, Theme.Muted);
            }
        }

        void Begin()
        {
            round = 1;
            round1 = Personal3D.Round1();
            trials = Personal3D.Trials(round1, rnd);
            index = 0;
            start.Visible = false; result.Visible = false;
            foreach (var b in answers) b.Visible = true;
            again.Visible = true;
            PlayCurrent();
        }

        void PlayCurrent()
        {
            if (playing || trials == null || index >= trials.Count) return;
            playing = true;
            var t = trials[index];
            step.Text = "Round " + round + " - " + (index + 1) + " of " + trials.Count + ": listen...";
            foreach (var b in answers) b.Enabled = false;
            again.Enabled = false;
            var baseCfg = core.S.Game.ToConfig();
            new System.Threading.Thread(() =>
            {
                string err = null;
                try { Personal3D.Play(phones, Personal3D.Render(t.V, t.Az, t.El, baseCfg)); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        playing = false;
                        step.Text = err != null ? "Couldn't play: " + err : "Round " + round + " - " + (index + 1) + " of " + trials.Count + ": where did the footsteps come from?";
                        foreach (var b in answers) b.Enabled = err == null;
                        again.Enabled = true;
                    }));
                }
                catch { }
            }) { IsBackground = true, Name = "3D listening test" }.Start();
        }

        void Answer(int place)
        {
            if (playing) return;
            Personal3D.Answer(trials[index], place);
            index++;
            if (index < trials.Count) { PlayCurrent(); return; }
            if (round == 1)
            {
                var best = Personal3D.Best(round1, core.S.Game.Hrtf, core.S.Game.CueSharpen);
                if (best.Bypass) { Finish(round1); return; }
                round = 2;
                round2 = Personal3D.Round2(best);
                round2.Add(best);   // its 35% result from round 1 stays in the race
                trials = Personal3D.Trials(round2.Where(v => v != best).ToList(), rnd);
                index = 0;
                step.Text = "Round 2: the same head with more or less cue sharpening...";
                PlayCurrent();
                return;
            }
            Finish(round1.Concat(round2.Where(v => !round1.Contains(v))).ToList());
        }

        void Finish(List<Personal3D.Variant> all)
        {
            foreach (var b in answers) b.Visible = false;
            again.Visible = false;
            var with3d = all.Where(v => !v.Bypass).ToList();
            var none = all.First(v => v.Bypass);
            winner = Personal3D.Best(with3d, core.S.Game.Hrtf, core.S.Game.CueSharpen);
            var lines = new List<string>();
            foreach (var v in all.OrderByDescending(v => v.Score))
                lines.Add((v == winner ? "> " : "   ") + v.Name + (v.Bypass ? "" : v.Cue == 35 && v.Name.IndexOf("cue") < 0 ? ", cue sharpening 35%" : "") + ":  " + v.Right + " of " + v.Total + " right" +
                    (v.FrontBack > 0 ? ", front/behind mixed up " + v.FrontBack + "x" : "") + (v.UpDown > 0 ? ", above/below mixed up " + v.UpDown + "x" : ""));
            lines.Add("");
            double gain = winner.Score - none.Score;
            lines.Add(gain > 0.15 ? "3D helps you: you placed " + (winner.Score * 100).ToString("0") + "% of the footsteps right with it, " + (none.Score * 100).ToString("0") + "% without."
                : gain > 0 ? "3D helps you a little (" + (winner.Score * 100).ToString("0") + "% vs " + (none.Score * 100).ToString("0") + "% without). Try the test again in a quiet moment - it gets easier with practice."
                : "With these headphones you placed sounds as well without 3D (" + (none.Score * 100).ToString("0") + "%). Many people need a few days with a head model; for games with their own HRTF, use theirs.");
            lines.Add("Best for you: " + winner.Name + (winner.Name.IndexOf("cue") < 0 ? ", cue sharpening " + winner.Cue + "%" : "") + ".");
            result.Text = string.Join("\n", lines);
            result.Visible = true;
            step.Text = "Done";
            apply.Visible = true;
            start.Text = "Again"; start.Visible = true; start.Left = apply.Left - Theme.Px(10) - start.Width;
            close.Text = "Close";
        }
    }
}
