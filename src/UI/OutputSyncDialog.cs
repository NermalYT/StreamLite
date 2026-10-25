using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// Sync outputs: measure each output's delay with the microphone (headphones held against it), line them all up to
    /// the slowest one, or set the delays by ear with test clicks on every output at once. Saved per device.
    /// </summary>
    internal sealed class OutputSyncDialog : DarkDialog
    {
        readonly AppCore core;
        readonly List<string> ids = new List<string>(), names = new List<string>();
        readonly List<Label> measured = new List<Label>();
        readonly List<FlatSlider> sliders = new List<FlatSlider>();
        readonly List<FlatButton> measureButtons = new List<FlatButton>();
        readonly double?[] baseDelay;
        readonly Label status;
        readonly FlatButton clicks, align;
        readonly Timer timer = new Timer { Interval = 40 };
        readonly OutputSyncMeter meter = new OutputSyncMeter();
        int measuring = -1, clicksSent, lastPlayed, nextAt;
        readonly List<long> clickTimes = new List<long>();
        bool clicking;
        const int Clicks = 5, Spacing = 500;

        public OutputSyncDialog(AppCore core) : base("Sync outputs", 640, 150 + 52 * (core.S.ExtraOutputs.Count + 1) + 110)
        {
            this.core = core;
            ids.Add(core.GamePhonesId);
            names.Add("1 - " + (core.GamePhonesName ?? "Headphones"));
            for (int i = 0; i < core.S.ExtraOutputs.Count; i++) { ids.Add(core.S.ExtraOutputs[i].Id); names.Add((i + 2) + " - " + core.S.ExtraOutputs[i].Name); }
            baseDelay = new double?[ids.Count];
            Text2("Every device has its own delay - Bluetooth headphones are often 150-250 ms behind wired ones. Hold each output's headphones (one earcup) right against your microphone and click Measure: StreamLite plays a few clicks on that output only and times them with the mic. Then Line up holds the faster outputs back so everyone hears the same moment. Or play the test clicks on every output and set the delays by ear.",
                20, 12, 600, 84, Theme.Small, Theme.Muted);
            int y = 104;
            for (int i = 0; i < ids.Count; i++)
            {
                int idx = i;
                Text2(names[i], 20, y + 4, 230, 22, Theme.Body, Theme.Text).AutoEllipsis = true;
                var m = new FlatButton("Measure") { Location = new Point(Theme.Px(256), Theme.Px(y)), Width = Theme.Px(84) };
                m.Click += (s, e) => StartMeasure(idx);
                Body.Controls.Add(m); measureButtons.Add(m);
                measured.Add(Text2("", 346, y + 4, 110, 22, Theme.Small, Theme.Muted));
                var sl = new FlatSlider { Minimum = 0, Maximum = 400, DefaultValue = 0, Location = new Point(Theme.Px(460), Theme.Px(y + 2)), Width = Theme.Px(110) };
                var val = Text2("", 574, y + 4, 56, 22, Theme.Small, Theme.Text);
                sl.Value = core.OutputDelay(ids[i]);
                val.Text = sl.Value + " ms";
                sl.ValueChanged += (s, e) => { val.Text = sl.Value + " ms"; core.SetOutputDelay(ids[idx], sl.Value); core.SaveSoon(); };
                Theme.Help(sl, "How much this output is held back, so it plays in sync with slower ones. Double-click for 0.");
                Body.Controls.Add(sl); sliders.Add(sl);
                y += 52;
            }
            status = Text2("", 20, y, 600, 44, Theme.Body, Theme.Text);
            clicks = new FlatButton("Play test clicks");
            align = new FlatButton("Line them up", true) { Enabled = false };
            var done = new FlatButton("Done");
            Theme.Help(clicks, "A click on every output at once, twice a second. Listen to two outputs together (one earcup of each) and move the sliders until you hear a single click.");
            Theme.Help(align, "Sets the delays from the measurements: every output waits for the slowest one.");
            foreach (var b in new[] { clicks, align, done }) Body.Controls.Add(b);
            int by = y + 54;
            done.Location = new Point(Theme.Px(620) - done.Width, Theme.Px(by));
            align.Location = new Point(done.Left - Theme.Px(10) - align.Width, Theme.Px(by));
            clicks.Location = new Point(Theme.Px(20), Theme.Px(by));
            clicks.Click += (s, e) => { clicking = !clicking; clicks.Text = clicking ? "Stop clicks" : "Play test clicks"; nextAt = Environment.TickCount; };
            align.Click += (s, e) => LineUp();
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            CancelButton = done;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            if (ids.Count < 2 || ids[0] == null || !core.Game.Running)
            {
                status.Text = ids.Count < 2 ? "Add a second output first (Outputs > + Add output)." : "The outputs aren't playing yet - check the 3D Audio page's status (game device and headphones).";
                foreach (var b in measureButtons) b.Enabled = false;
                clicks.Enabled = false;
            }
            else if (!core.Engine.Running) { status.Text = "Measuring needs the microphone running (virtual mic on). The test clicks and sliders still work."; foreach (var b in measureButtons) b.Enabled = false; }
        }

        void StartMeasure(int i)
        {
            if (measuring >= 0) return;
            clicking = false; clicks.Text = "Play test clicks";
            measuring = i; clicksSent = 0; clickTimes.Clear();
            meter.Clear();
            core.Engine.RawTap = meter.Feed;
            lastPlayed = core.Game.ClicksPlayed;
            nextAt = Environment.TickCount + 1200;   // a moment of quiet first: the room's level
            status.Text = "Hold output " + names[i] + " against your microphone... clicks in a second.";
            measured[i].Text = "listening...";
            foreach (var b in measureButtons) b.Enabled = false;
        }

        void Tick()
        {
            int now = Environment.TickCount;
            if (core.Game.ClicksPlayed != lastPlayed) { lastPlayed = core.Game.ClicksPlayed; if (measuring >= 0) clickTimes.Add(core.Game.ClickTicks); }
            if (clicking) core.Game.KeepAwake();
            if (clicking && now - nextAt >= 0) { core.Game.Click(GameAudioEngine.AllOutputs); nextAt = now + Spacing; }
            if (measuring < 0) return;
            core.Game.KeepAwake();
            if (clicksSent < Clicks && now - nextAt >= 0) { core.Game.Click(measuring); clicksSent++; nextAt = now + Spacing; }
            else if (clicksSent >= Clicks && now - nextAt >= 300) Finish();
        }

        void Finish()
        {
            int i = measuring; measuring = -1;
            core.Engine.RawTap = null;
            var r = meter.Measure(clickTimes);
            if (r.DelayMs.HasValue)
            {
                // What this output adds by itself: the measurement minus the delay it's held back by right now.
                baseDelay[i] = r.DelayMs.Value - sliders[i].Value;
                measured[i].Text = baseDelay[i].Value.ToString("0") + " ms";
                measured[i].ForeColor = r.Problem == null ? Theme.Accent : Theme.Warn;
            }
            else { measured[i].Text = "not heard"; measured[i].ForeColor = Theme.Warn; }
            status.Text = r.Problem ?? ("Output " + names[i] + ": heard " + r.Heard + " of " + r.Played + " clicks, " + r.DelayMs.Value.ToString("0") + " ms from StreamLite to your ears" + (r.SpreadMs > 0 ? " (within " + r.SpreadMs.ToString("0") + " ms)" : "") + ".");
            status.ForeColor = r.Problem == null ? Theme.Text : Theme.Warn;
            foreach (var b in measureButtons) b.Enabled = true;
            align.Enabled = baseDelay.Count(d => d.HasValue) >= 2;
        }

        void LineUp()
        {
            var delays = OutputSyncMeter.Align(baseDelay);
            for (int i = 0; i < ids.Count; i++) if (baseDelay[i].HasValue) sliders[i].Value = Math.Min(400, delays[i]);
            status.Text = "Lined up: every output now plays with the slowest one (" + baseDelay.Where(d => d.HasValue).Max(d => d.Value).ToString("0") + " ms). Test it with the clicks.";
            status.ForeColor = Theme.Text;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            core.Engine.RawTap = null;
            base.OnFormClosed(e);
        }
    }
}
