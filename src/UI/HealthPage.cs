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
        readonly Card now, log, check;
        readonly FlatButton runCheck;
        readonly Label checkStatus;
        readonly List<Control> checkRows = new List<Control>();
        readonly Action<string> fix;
        bool checking;
        readonly List<Label> keys = new List<Label>(), values = new List<Label>();
        readonly Label events;
        readonly Timer timer = new Timer { Interval = 1000 };
        const int RowH = 26, Rows = 12;

        public HealthPage(AppCore core, Action<string> fix)
        {
            this.core = core; this.fix = fix;
            Location = new Point(Theme.Px(28), Theme.Px(18));
            Size = new Size(Theme.Px(690), Theme.Px(900));
            BackColor = Theme.Bg;
            Theme.Label(this, "Health", 0, 0, Theme.Title);
            check = new Card { Location = new Point(0, Theme.Px(54)), Size = new Size(Theme.Px(690), Theme.Px(90)) };
            Controls.Add(check);
            Theme.Help(Theme.Label(check, "Setup check", 20, 16, Theme.Section),
                "Checks everything between your mic and your friends and between your games and your ears: the drivers, the physical mic, the virtual mic end to end (a short test tone recorded back the way Discord hears it - only when no app is recording your mic), your headphones end to end (a quiet tone), Windows' default devices, which mic Discord and OBS really use, and the camera. Each problem comes with a fix.");
            runCheck = new FlatButton("Run setup check", true);
            runCheck.Location = new Point(Theme.Px(520), Theme.Px(12)); runCheck.Width = Theme.Px(150);
            check.Controls.Add(runCheck);
            Responsive.Fixed(runCheck);
            runCheck.Click += (s, e) => RunCheck();
            checkStatus = Theme.Label(check, "Takes about 5 seconds. You may hear a short, quiet tone in your headphones.", 20, 52, Theme.Small, Theme.Muted, 650);
            now = new Card { Location = new Point(0, check.Bottom + Theme.Px(14)), Size = new Size(Theme.Px(690), Theme.Px(56 + Rows * RowH)) };
            Controls.Add(now);
            Theme.Help(Theme.Label(now, "Right now", 20, 16, Theme.Section),
                "What StreamLite is doing this second. Latency is measured on the real path (mic to virtual cable, game device to your ears), short gaps (a packet later than the buffer could bridge, smoothed over with a soft fade) are counted since StreamLite started. A gap while a game loads or the PC is busy is normal; a row turns yellow only when gaps keep coming without a reason. The buffers cover a gap at once and give the extra back within minutes; only gaps that keep recurring keep them bigger. Everything is also written to health.log next to the settings. CPU is StreamLite's share of your whole CPU; video memory is what it uses on the graphics card.");
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

        /// <summary>Runs the setup check in the background and lists the results with their fixes.</summary>
        public void RunCheck()
        {
            if (checking) return;
            checking = true; runCheck.Enabled = false;
            checkStatus.Text = "Checking...";
            var t = new System.Threading.Thread(() =>
            {
                List<CheckItem> res;
                try { res = SetupCheck.Run(core, true, msg => { try { BeginInvoke(new Action(() => checkStatus.Text = msg)); } catch { } }); }
                catch (Exception ex) { res = new List<CheckItem> { new CheckItem { Title = "Setup check", State = CheckItem.Fail, Detail = "The check itself failed: " + ex.Message } }; }
                try { BeginInvoke(new Action(() => ShowResults(res))); } catch { }
            }) { IsBackground = true, Name = "setup check" };
            t.Start();
        }

        void ShowResults(List<CheckItem> res)
        {
            checking = false; runCheck.Enabled = true; runCheck.Text = "Check again";
            foreach (var c in checkRows) { check.Controls.Remove(c); c.Dispose(); }
            checkRows.Clear();
            int fails = res.Count(r => r.State == CheckItem.Fail), warns = res.Count(r => r.State == CheckItem.Warn);
            checkStatus.Text = fails + warns == 0 ? "All good - " + res.Count(r => r.State == CheckItem.Ok) + " checks passed." : fails + " problem" + (fails == 1 ? "" : "s") + ", " + warns + " warning" + (warns == 1 ? "" : "s") + " - fixes below.";
            checkStatus.ForeColor = fails > 0 ? Theme.Warn : Theme.Muted;
            int y = 80;
            foreach (var r in res.OrderBy(r => r.State == CheckItem.Fail ? 0 : r.State == CheckItem.Warn ? 1 : r.State == CheckItem.Ok ? 2 : 3))
            {
                var item = r;
                var mark = Theme.Label(check, item.State == CheckItem.Ok ? "✓" : item.State == CheckItem.Info ? "i" : "!", 20, y, Theme.Bold,
                    item.State == CheckItem.Ok ? Theme.Accent : item.State == CheckItem.Info ? Theme.Muted : Theme.Warn, 18);
                var title = Theme.Label(check, item.Title, 42, y, Theme.Bold, Theme.Text, 190);
                var detail = new Label { Text = item.Detail, Location = new Point(Theme.Px(236), Theme.Px(y)), Size = new Size(Theme.Px(item.FixId != null ? 290 : 434), Theme.Px(34)), Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, UseMnemonic = false };
                // As tall as its words (4.1.1: a long note next to a button was cut off).
                int need = detail.GetPreferredSize(new Size(detail.Width, 0)).Height + Theme.Px(2);
                if (need > detail.Height) detail.Height = need;
                Theme.Help(detail, item.Detail);
                check.Controls.Add(detail);
                checkRows.Add(mark); checkRows.Add(title); checkRows.Add(detail);
                if (item.FixId != null)
                {
                    var b = new FlatButton(item.FixLabel ?? "Fix") { Location = new Point(Theme.Px(532), Theme.Px(y - 4)), Width = Theme.Px(138) };
                    b.Click += (s, e) => { fix(item.FixId); b.Enabled = false; b.Text = "Done - check again"; };
                    check.Controls.Add(b); checkRows.Add(b);
                    Responsive.Fixed(b);
                }
                y += Math.Max(40, (int)Math.Ceiling(detail.Height / (double)Theme.Px(1)) + 8);
            }
            int h = y + 8;
            int delta = Theme.Px(h) - check.Height;
            check.Height = Theme.Px(h);
            now.Top += delta; log.Top += delta;
            Height = log.Bottom + Theme.Px(20);
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
                add("Microphone", e.Status + (e.Running ? (Os.Linux ? " - shared with other apps (PipeWire's own small steps - the normal way on Linux)"
                    : e.ExclusiveCapture ? " - exclusive, " + e.PacketMs.ToString("0") + " ms packets" : (S.ShareMic && e.ExclusiveError == null ? " - shared with other apps (Windows' 10 ms packets, as set)" : " - shared (" + (e.ExclusiveError ?? "Windows' 10 ms packets") + ")")) : ""),
                    !e.Running || (!Os.Linux && !e.ExclusiveCapture && !(S.ShareMic && e.ExclusiveError == null)));   // shared by choice (or on Linux) isn't a warning
                float nr = S.NoiseRemoval ? e.Suppressor.DelayMs : 0;
                if (!e.Running) add("Mic latency", "- (the mic isn't running)", true); else
                add("Mic latency", Ms(e.LatencyMs) + " to the cable (capture " + e.CaptureWaitMs.ToString("0.0") + " + buffer " + e.BufferMs.ToString("0.0") + " + cable " + e.CableLeadMs.ToString("0.0") + ")" + (nr > 0 ? " + " + nr.ToString("0") + " ms noise removal = " + Ms(e.LatencyMs + nr) : ""), false);
                add("Mic gaps", h.MicGaps.Describe() + "; " + (e.MarginFrames / 48.0).ToString("0.0") + " ms kept in reserve", h.MicGaps.Troubled(DateTime.Now));
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
                add("Game delay", Ms(g.LatencyMs) + " from the game device to your headphones, " + (g.LearnedMargin / 48.0).ToString("0.0") + " ms in reserve", false);
                add("Game sound gaps", h.GameGaps.Describe(), h.GameGaps.Troubled(DateTime.Now));
                if (S.Game.Exclusive && !Os.Linux) add("Direct to headphones", core.DirectStatus(), g.ExclusiveError != null);   // Linux: PipeWire already plays at the device's own small steps
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
