using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// Health page: the audio paths right now (latency, dropouts, buffers, CPU / RAM / video memory, the noise engine
    /// really in use) and a plain-language log of what changed and why. Refreshes every second while it's shown.
    /// </summary>
    internal sealed class HealthPage : Panel
    {
        readonly AppCore core;
        readonly Card now, log;
        readonly List<Label> keys = new List<Label>(), values = new List<Label>();
        readonly Label events;
        readonly Timer timer = new Timer { Interval = 1000 };
        const int RowH = 26, Rows = 12;

        public HealthPage(AppCore core)
        {
            this.core = core;
            Location = new Point(Theme.Px(28), Theme.Px(18));
            Size = new Size(Theme.Px(690), Theme.Px(900));
            BackColor = Theme.Bg;
            Theme.Label(this, "Health", 0, 0, Theme.Title);
            now = new Card { Location = new Point(0, Theme.Px(54)), Size = new Size(Theme.Px(690), Theme.Px(56 + Rows * RowH)) };
            Controls.Add(now);
            Theme.Help(Theme.Label(now, "Right now", 20, 16, Theme.Section),
                "What StreamLite is doing this second. Latency is measured on the real path (mic to virtual cable, game device to your ears), dropouts are counted since StreamLite started, and the buffers grow by themselves after a dropout. CPU is StreamLite's share of your whole CPU; video memory is what it uses on the graphics card.");
            var copy = new FlatButton("Copy");
            copy.Location = new Point(Theme.Px(600), Theme.Px(12)); copy.Width = Theme.Px(70);
            Theme.Help(copy, "Copies this page and the log below - paste it into a bug report.");
            copy.Click += (s, e) => { try { Clipboard.SetText(Report()); } catch { } };
            now.Controls.Add(copy);
            Responsive.Fixed(copy);
            for (int i = 0; i < Rows; i++)
            {
                keys.Add(Theme.Label(now, "", 20, 52 + i * RowH, Theme.Bold, Theme.Text, 170));
                var v = Theme.Label(now, "", 196, 52 + i * RowH, Theme.Body, Theme.Text, 474);
                v.AutoEllipsis = true;
                values.Add(v);
            }
            log = new Card { Location = new Point(0, now.Bottom + Theme.Px(14)), Size = new Size(Theme.Px(690), Theme.Px(380)) };
            Controls.Add(log);
            Theme.Help(Theme.Label(log, "What happened", 20, 16, Theme.Section), "Every time StreamLite switched to a slower or less capable path - or back - with what it means. Newest first.");
            events = new Label { Location = new Point(Theme.Px(20), Theme.Px(48)), Size = new Size(Theme.Px(650), Theme.Px(320)), Font = Theme.Small, ForeColor = Theme.Text, BackColor = Color.Transparent, UseMnemonic = false };
            log.Controls.Add(events);
            Height = log.Bottom + Theme.Px(20);
            timer.Tick += (s, e) => Refresh2();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { Refresh2(); timer.Start(); } else timer.Stop();
        }

        static string Ms(double v) { return v.ToString("0.0") + " ms"; }

        /// <summary>(label, value, warning) rows for right now.</summary>
        List<Tuple<string, string, bool>> Lines()
        {
            var l = new List<Tuple<string, string, bool>>();
            var e = core.Engine; var g = core.Game; var S = core.S; var h = core.Health;
            Action<string, string, bool> add = (k, v, w) => l.Add(Tuple.Create(k, v, w));
            if (!S.VirtualMicOn) add("Microphone", "off (Virtual mic is switched off)", false);
            else
            {
                add("Microphone", e.Status + (e.Running ? (e.ExclusiveCapture ? " - exclusive, " + e.PacketMs.ToString("0") + " ms packets" : " - shared (" + (e.ExclusiveError ?? "Windows' 10 ms packets") + ")") : ""), !e.Running || !e.ExclusiveCapture);
                float nr = S.NoiseRemoval ? e.Suppressor.DelayMs : 0;
                if (!e.Running) add("Mic latency", "- (the mic isn't running)", true); else
                add("Mic latency", Ms(e.LatencyMs) + " to the cable (capture " + e.CaptureWaitMs.ToString("0.0") + " + buffer " + e.BufferMs.ToString("0.0") + " + cable " + e.CableLeadMs.ToString("0.0") + ")" + (nr > 0 ? " + " + nr.ToString("0") + " ms noise removal = " + Ms(e.LatencyMs + nr) : ""), false);
                add("Mic dropouts", e.Dropouts + " since start - " + (e.MarginFrames / 48.0).ToString("0.0") + " ms kept in reserve" + (e.CableCatchUps > 0 ? ", cable caught up " + e.CableCatchUps + "x" : ""), e.Dropouts > 0);
                var d = e.Suppressor.DfnEngine;
                string engine = !S.NoiseRemoval ? "off" : d != null && d.Ready && d.Error == null
                    ? "DeepFilterNet on the " + (d.Gpu ? "graphics card" + (d.LowVram ? " (Low VRAM)" : "") : "CPU") + " - " + d.AvgMs.ToString("0.0") + " ms per 10 ms of sound, " + d.Late + " late"
                    : NoiseEngines.IsDfn(S.NsEngine) && !e.Suppressor.DfnWanted ? "RNNoise standing in (DeepFilterNet rests while no app records your mic)"
                    : d != null && !d.Ready ? "DeepFilterNet loading - RNNoise meanwhile" : NoiseEngines.Label(S.NsEngine, true);
                add("Noise removal", engine + (e.Suppressor.FallbackNote != null ? " - " + e.Suppressor.FallbackNote : ""), e.Suppressor.FallbackNote != null);
                if (e.Mixer.Mics.Length > 0)
                    add("More mics", string.Join("; ", e.Mixer.Mics.Select((m, i) => (i + 2) + ": " + (m.Running ? "running" : m.Status) + ", " + m.Dropouts + " dropouts")), e.Mixer.Mics.Any(m => !m.Running));
            }
            if (!g.Running) add("Game 3D audio", S.Game.Enabled || core.MultiOut ? "not running" + (core.GameError != null ? " - " + core.GameError : "") : "off", S.Game.Enabled || core.MultiOut);
            else
            {
                add("Game sound", g.Status + " - " + (g.Plain ? "plain stereo" : g.Processor.Current + (g.Processor.Rendering360 ? ", true 360°" : "")) + " - CPU " + g.CpuPercent.ToString("0.0") + "% of one core", core.GameError != null);
                add("Game delay", Ms(g.LatencyMs) + " from the game device to your headphones; " + g.Dropouts + " dropouts, " + (g.LearnedMargin / 48.0).ToString("0.0") + " ms in reserve", g.Dropouts > 0);
                if (g.ExtraOutputs.Length > 0)
                    add("More outputs", string.Join("; ", g.ExtraOutputs.Select((o, i) => (i + 2) + ": " + (o.Playing ? "playing" : o.Status) + ", " + o.Dropouts + " dropouts" + (o.DelayFrames > 0 ? ", held " + (o.DelayFrames / 48) + " ms" : ""))), g.ExtraOutputs.Any(o => !o.Playing));
            }
            var c = core.CamFx;
            if (c != null) add("Camera", c.Status + (c.Fps > 0 ? " - " + c.Fps.ToString("0") + " fps, " + c.GpuMs.ToString("0.0") + " ms per frame" : "") + (c.Error != null ? " - " + c.Error : ""), c.Error != null);
            add("StreamLite uses", "CPU " + h.CpuPercent.ToString("0.0") + "% of the whole CPU, RAM " + h.RamMb + " MB, video memory " + (h.VramMb >= 0 ? h.VramMb + " MB" : "unknown"), h.CpuPercent > 20);
            add("Running since", h.Started.ToString("t") + " (" + Math.Max(0, (int)(DateTime.Now - h.Started).TotalMinutes) + " min)", false);
            return l;
        }

        void Refresh2()
        {
            var lines = Lines();
            for (int i = 0; i < Rows; i++)
            {
                bool has = i < lines.Count;
                keys[i].Text = has ? lines[i].Item1 : "";
                values[i].Text = has ? lines[i].Item2 : "";
                values[i].ForeColor = has && lines[i].Item3 ? Theme.Warn : Theme.Text;
                Theme.Help(values[i], has ? lines[i].Item2 : null);
            }
            var ev = core.Health.Events;
            events.Text = ev.Count == 0 ? "Nothing to report - everything has run on its best path since StreamLite started."
                : string.Join("\n\n", ev.AsEnumerable().Reverse().Take(10).Select(x => x.Time.ToString("T") + (x.Warning ? "  !  " : "  ") + x.Text));
        }

        string Report()
        {
            return "StreamLite " + Program.Version + " health\r\n" + string.Join("\r\n", Lines().Select(x => x.Item1 + ": " + x.Item2)) +
                "\r\n\r\nLog:\r\n" + string.Join("\r\n", core.Health.Events.Select(x => x.Time.ToString("T") + " " + x.Text));
        }
    }
}
