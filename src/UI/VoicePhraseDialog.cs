using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// A voice hotkey's phrase (4.4): three takes of a word or sentence, then try it before saving. Only numbers are kept
    /// (VoiceWords): the sound itself is gone as soon as a take is measured.
    /// </summary>
    internal sealed class VoicePhraseDialog : DarkDialog
    {
        enum State { Idle, Listening, Talking }
        const int Needed = 3, MaxTakes = 6, MaxSeconds = 6;

        readonly AppCore core;
        readonly TextBox phraseBox;
        readonly LevelMeter meter;
        readonly FlatButton rec, again, save, cancel, remove, looser, stricter;
        readonly Label status, tryLabel, senseLabel;
        readonly Timer timer = new Timer { Interval = 30 };
        readonly VoiceWords trial = new VoiceWords();
        readonly List<float[][]> takes = new List<float[][]>();
        readonly float[] buf = new float[VoiceWords.Fs * MaxSeconds];
        volatile State state = State.Idle;
        volatile int fill, quietMs, talkMs;
        volatile string heardNote;
        DateTime heardUntil;
        float tapDb = -100;
        int sense = 100;

        /// <summary>The phrase as you typed it, its takes as settings text, how easily it triggers (null phrase: removed).</summary>
        public string Phrase { get; private set; }
        public List<string> Takes { get; private set; }
        public int Sense { get { return sense; } }

        public VoicePhraseDialog(AppCore core, string phrase, List<string> existing, int existingSense) : base("Say it to trigger it", 640, 470)
        {
            this.core = core;
            sense = existingSense <= 0 ? 100 : existingSense;
            Text2("What you'll say: a word or a whole sentence.", 22, 14, 596, 24, Theme.Bold, Theme.Text);
            phraseBox = new TextBox { Location = new Point(Theme.Px(22), Theme.Px(42)), Width = Theme.Px(596), BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Body, Text = phrase ?? "" };
            phraseBox.TextChanged += (s, e) => Refresh2();
            Body.Controls.Add(phraseBox);
            Text2("Say it three times, the way you'd say it in a game. It only keeps numbers about how the sound moves, never the sound, and listens only while a voice hotkey is set.", 22, 78, 596, 40, Theme.Small, Theme.Muted);
            rec = new FlatButton("", true) { Location = new Point(Theme.Px(22), Theme.Px(124)), Width = Theme.Px(250) };
            rec.Click += (s, e) => StartTake();
            Body.Controls.Add(rec);
            again = new FlatButton("Start over") { Location = new Point(Theme.Px(282), Theme.Px(124)), Width = Theme.Px(120) };
            again.Click += (s, e) => { takes.Clear(); trial.Set(new VoiceWords.Phrase[0]); Refresh2(); };
            Body.Controls.Add(again);
            meter = new LevelMeter { Location = new Point(Theme.Px(22), Theme.Px(170)), Size = new Size(Theme.Px(596), Theme.Px(10)) };
            Body.Controls.Add(meter);
            status = Text2("", 22, 188, 596, 44, Theme.Body, Theme.Text);
            tryLabel = Text2("", 22, 240, 596, 64, Theme.Section, Theme.Muted);
            tryLabel.TextAlign = ContentAlignment.MiddleCenter;
            senseLabel = Text2("", 22, 318, 300, 24, Theme.Body, Theme.Text);
            stricter = new FlatButton("Harder to trigger") { Location = new Point(Theme.Px(250), Theme.Px(312)), Width = Theme.Px(180) };
            looser = new FlatButton("Easier to trigger") { Location = new Point(Theme.Px(438), Theme.Px(312)), Width = Theme.Px(180) };
            stricter.Click += (s, e) => { sense = Math.Max(60, sense - 10); Listen(); Refresh2(); };
            looser.Click += (s, e) => { sense = Math.Min(160, sense + 10); Listen(); Refresh2(); };
            Theme.Help(stricter, "Triggers when other things you say set it off by mistake: it has to sound closer to your takes.");
            Theme.Help(looser, "When it misses you: it may sound further from your takes.");
            Body.Controls.Add(stricter); Body.Controls.Add(looser);
            save = new FlatButton("Save", true) { Location = new Point(Theme.Px(498), Theme.Px(420)), Width = Theme.Px(120) };
            save.Click += (s, e) => { Phrase = phraseBox.Text.Trim(); Takes = takes.Select(VoiceWords.Pack).ToList(); DialogResult = DialogResult.OK; Close(); };
            cancel = new FlatButton("Cancel") { Location = new Point(Theme.Px(378), Theme.Px(420)), Width = Theme.Px(110) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            remove = new FlatButton("Remove the phrase") { Location = new Point(Theme.Px(22), Theme.Px(420)), Width = Theme.Px(180), Visible = existing != null && existing.Count > 0 };
            remove.Click += (s, e) => { Phrase = null; Takes = null; DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(save); Body.Controls.Add(cancel); Body.Controls.Add(remove);
            CancelButton = cancel;
            if (existing != null) foreach (var t in existing) { var f = VoiceWords.Unpack(t); if (f != null) takes.Add(f); }
            // Your hotkeys wait while this is open: trying a phrase mustn't fire it.
            core.Engine.VoiceTap = null;
            core.Engine.RawTap = Tap;
            trial.Heard += (p, score) => { heardNote = "Heard it  (" + (int)Math.Round(100 * (1 - 0.5 * score)) + "% match)"; try { BeginInvoke(new Action(() => heardUntil = DateTime.Now.AddSeconds(2))); } catch { } };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Listen();
            Refresh2();
        }

        void Tap(float[] b, int n, long t)
        {
            double e = 0; for (int i = 0; i < n; i++) e += b[i] * (double)b[i];
            float db = (float)(10 * Math.Log10(e / Math.Max(1, n) + 1e-12));
            tapDb = db;
            var st = state;
            if (st == State.Idle) { trial.Feed(b, n, t); return; }
            int f = fill, room = Math.Min(n, buf.Length - f);
            if (room > 0) { Array.Copy(b, 0, buf, f, room); fill = f + room; }
            int ms = n * 1000 / VoiceWords.Fs;
            if (st == State.Listening) { if (db > -50) { state = State.Talking; quietMs = 0; talkMs = 0; } return; }
            talkMs += ms;
            quietMs = db > -52 ? 0 : quietMs + ms;
        }

        void StartTake()
        {
            if (state != State.Idle) return;
            fill = 0; quietMs = 0; talkMs = 0; state = State.Listening; heardNote = null;
            Refresh2();
        }

        void Tick()
        {
            meter.Level = core.Engine.InputLevelDb;
            var st = state;
            if (st != State.Idle)
            {
                bool done = (st == State.Talking && quietMs >= 500 && talkMs > 300) || fill >= buf.Length;
                if (done)
                {
                    state = State.Idle;
                    var take = VoiceWords.TakeOf(buf, fill);
                    if (take == null) status.Text = "That didn't come through clearly. Try that take again, a little closer to your mic.";
                    else { takes.Add(take); Listen(); }
                    Refresh2();
                }
                return;
            }
            var note = heardNote;
            if (note != null && DateTime.Now < heardUntil) { tryLabel.Text = note; tryLabel.ForeColor = Theme.Accent; }
            else if (takes.Count >= Needed) { tryLabel.Text = "Try it: say it now"; tryLabel.ForeColor = Theme.Muted; heardNote = null; }
            else tryLabel.Text = "";
        }

        /// <summary>The takes so far, listening for them (once there are enough).</summary>
        void Listen()
        {
            if (takes.Count < Needed) { trial.Set(new VoiceWords.Phrase[0]); return; }
            trial.Set(new[] { new VoiceWords.Phrase { Text = phraseBox.Text, Takes = takes, Limit = VoiceWords.LimitFor(takes, sense / 100.0) } });
        }

        void Refresh2()
        {
            var st = state;
            bool enough = takes.Count >= Needed;
            rec.Enabled = st == State.Idle && takes.Count < MaxTakes;
            rec.Text = st != State.Idle ? "Listening..." : enough ? "Add another take" : "Say it (take " + (takes.Count + 1) + " of " + Needed + ")";
            again.Visible = takes.Count > 0;
            if (st != State.Idle) status.Text = "Say it now.";
            else if (!core.Engine.Running) status.Text = "The mic isn't running: turn the virtual mic on (Microphone page) to record.";
            else if (enough) status.Text = takes.Count + " takes. Say it again to see whether it's heard, and say other things to see that they aren't.";
            else if (takes.Count > 0) status.Text = takes.Count + " of " + Needed + " takes.";
            else status.Text = "Press the button, then say it.";
            senseLabel.Visible = stricter.Visible = looser.Visible = enough;
            senseLabel.Text = "Sensitivity: " + sense + "%";
            save.Enabled = enough && phraseBox.Text.Trim().Length > 0;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            core.Engine.RawTap = null;
            trial.Dispose();
            base.OnFormClosed(e);
        }
    }
}
