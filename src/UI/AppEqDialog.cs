using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite.UI
{
    /// <summary>
    /// App EQ (4.4), on the Headphones page: any app gets an EQ of its own on top of your headphones' EQ, from a preset or
    /// made by hand, and your own curves can be saved as presets for other apps. Each app's sound is EQ'd on its own while
    /// StreamLite plays to your headphones directly; apps playing through 3D audio share one stream, so there the EQ of the
    /// app playing applies to that stream.
    /// </summary>
    internal sealed class AppEqDialog : DarkDialog
    {
        readonly AppCore core;
        readonly ListBox apps;
        readonly TextBox input;
        readonly DarkCombo running, presets;
        readonly ToggleSwitch on;
        readonly Label onLabel, note, presetLabel;
        readonly EqGraph graph;
        readonly FlatButton adjust, savePreset, deletePreset, remove;
        bool loading;

        sealed class Item { public string Exe; public AppEqSetting S; public override string ToString() { return Exe + (S.On ? "" : "  (off)"); } }
        sealed class RunningItem { public string Exe, What; public override string ToString() { return Exe + (string.IsNullOrEmpty(What) ? "" : "  - " + What); } }

        public AppEqDialog(AppCore core) : base("App EQ", 720, 520)
        {
            this.core = core;
            Text2("Give any app an EQ of its own, on top of your headphones' EQ: more bass in Spotify, clearer voices in Discord. Pick a preset or make your own.", 22, 12, 676, 38, Theme.Small, Theme.Muted);
            apps = new ListBox { Location = new Point(Theme.Px(22), Theme.Px(58)), Size = new Size(Theme.Px(250), Theme.Px(290)), Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            DarkList.Style(apps);
            Body.Controls.Add(apps);
            apps.SelectedIndexChanged += (s, e) => ShowApp();
            input = new TextBox { Location = new Point(Theme.Px(22), Theme.Px(358)), Width = Theme.Px(180), Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            Body.Controls.Add(input);
            Theme.Help(input, "A program name, with or without .exe (for example spotify, discord or chrome).");
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddTyped(); e.SuppressKeyPress = true; } };
            var add = new FlatButton("Add") { Location = new Point(Theme.Px(208), Theme.Px(355)), Width = Theme.Px(64) };
            add.Click += (s, e) => AddTyped();
            Body.Controls.Add(add);
            Text2("Or an app that's running now:", 22, 394, 250, 20, Theme.Small, Theme.Muted);
            running = new DarkCombo { Location = new Point(Theme.Px(22), Theme.Px(414)), Width = Theme.Px(250), DropDownWidth = Theme.Px(380) };
            running.DropDown += (s, e) => FillRunning();
            running.SelectionChangeCommitted += (s, e) => { var r = running.SelectedItem as RunningItem; if (r != null) Add(r.Exe); };
            Body.Controls.Add(running);

            on = new ToggleSwitch { Location = new Point(Theme.Px(290), Theme.Px(58)) };
            on.CheckedChanged += (s, e) =>
            {
                var it = Current; if (it == null || loading) return;
                it.S.On = on.Checked; Changed();
                int i = apps.SelectedIndex; apps.Items[i] = it;
            };
            Body.Controls.Add(on);
            onLabel = Text2("EQ for this app", 344, 60, 200, 24, Theme.Bold, Theme.Text);
            remove = new FlatButton("Remove") { Location = new Point(Theme.Px(598), Theme.Px(54)), Width = Theme.Px(100) };
            remove.Click += (s, e) => RemoveCurrent();
            Body.Controls.Add(remove);
            presetLabel = Text2("Preset", 290, 102, 60, 24, Theme.Bold, Theme.Text);
            presets = new DarkCombo { Location = new Point(Theme.Px(350), Theme.Px(98)), Width = Theme.Px(348) };
            presets.SelectionChangeCommitted += (s, e) => UsePreset(presets.SelectedItem as string);
            Body.Controls.Add(presets);
            graph = new EqGraph { Interactive = false, Location = new Point(Theme.Px(290), Theme.Px(138)), Size = new Size(Theme.Px(408), Theme.Px(172)) };
            graph.Click += (s, e) => AdjustCurrent();
            Body.Controls.Add(graph);
            adjust = new FlatButton("Adjust EQ...", true) { Location = new Point(Theme.Px(290), Theme.Px(320)), Width = Theme.Px(130) };
            adjust.Click += (s, e) => AdjustCurrent();
            Body.Controls.Add(adjust);
            savePreset = new FlatButton("Save as preset...") { Location = new Point(Theme.Px(428), Theme.Px(320)), Width = Theme.Px(150) };
            savePreset.Click += (s, e) => SaveAsPreset();
            Body.Controls.Add(savePreset);
            deletePreset = new FlatButton("Delete preset") { Location = new Point(Theme.Px(586), Theme.Px(320)), Width = Theme.Px(112) };
            deletePreset.Click += (s, e) => DeletePreset();
            Body.Controls.Add(deletePreset);
            note = Text2("", 290, 364, 408, 90, Theme.Small, Theme.Muted);
            var done = new FlatButton("Done", true) { Location = new Point(Theme.Px(598), Theme.Px(470)), Width = Theme.Px(100) };
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(done);
            CancelButton = done;
            foreach (var kv in core.S.AppEq.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) apps.Items.Add(new Item { Exe = kv.Key, S = kv.Value });
            FillPresets();
            if (apps.Items.Count > 0) apps.SelectedIndex = 0; else ShowApp();
        }

        Item Current { get { return apps.SelectedItem as Item; } }

        /// <summary>Your presets and the ready-made ones.</summary>
        void FillPresets()
        {
            presets.Items.Clear();
            presets.Items.Add("Choose a preset...");
            foreach (var k in core.S.AppEqPresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) presets.Items.Add(k);
            foreach (var k in EqEditor.HeadphonePresets().Keys) if (!core.S.AppEqPresets.ContainsKey(k)) presets.Items.Add(k);
            presets.SelectedIndex = 0;
        }

        void UsePreset(string name)
        {
            var it = Current;
            if (it == null || name == null || presets.SelectedIndex <= 0) return;
            EqProfile mine;
            if (core.S.AppEqPresets.TryGetValue(name, out mine)) it.S.Eq = mine.Clone();
            else
            {
                double[] bands;
                if (!EqEditor.HeadphonePresets().TryGetValue(name, out bands)) return;
                it.S.Eq = new EqProfile { Mode = "graphic", Bands = (double[])bands.Clone() };
            }
            it.S.Preset = name;
            it.S.Eq.Repair();
            Changed();
            ShowApp();
        }

        void AdjustCurrent()
        {
            var it = Current; if (it == null) return;
            using (var d = new DeviceEqDialog(it.Exe + " - App EQ", "Plays on top of your headphones' EQ, on this app's sound.", it.S.Eq, EqEditor.HeadphonePresets(), () => { it.S.Preset = null; Changed(); graph.Invalidate(); }))
                d.ShowDialog(this);
            ShowApp();
        }

        void SaveAsPreset()
        {
            var it = Current; if (it == null) return;
            using (var d = new AskNameDialog("Save as preset", "A name for this EQ:", it.S.Preset ?? it.Exe))
            {
                if (d.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(d.Value)) return;
                string name = d.Value.Trim();
                core.S.AppEqPresets[name] = it.S.Eq.Clone();
                it.S.Preset = name;
                core.SaveSoon();
            }
            FillPresets();
            ShowApp();
        }

        void DeletePreset()
        {
            var it = Current; if (it == null || it.S.Preset == null) return;
            if (core.S.AppEqPresets.Remove(it.S.Preset)) { it.S.Preset = null; core.SaveSoon(); FillPresets(); ShowApp(); }
        }

        void Add(string text)
        {
            string exe = GameProfiles.NormalizeExe(text);
            if (exe == null) return;
            foreach (var i in apps.Items.OfType<Item>()) if (string.Equals(i.Exe, exe, StringComparison.OrdinalIgnoreCase)) { apps.SelectedItem = i; return; }
            var it = new Item { Exe = exe, S = new AppEqSetting() };
            core.S.AppEq[exe] = it.S;
            apps.Items.Add(it);
            apps.SelectedItem = it;
            Changed();
        }

        void AddTyped()
        {
            foreach (var p in (input.Text ?? "").Split(',')) if (p.Trim().Length > 0) Add(p.Trim());
            input.Text = "";
        }

        void RemoveCurrent()
        {
            var it = Current; if (it == null) return;
            core.S.AppEq.Remove(it.Exe);
            int i = apps.SelectedIndex;
            apps.Items.RemoveAt(i);
            if (apps.Items.Count > 0) apps.SelectedIndex = Math.Min(i, apps.Items.Count - 1); else ShowApp();
            Changed();
        }

        void FillRunning()
        {
            running.Items.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outs = new List<string>();
            if (core.GameSinkId != null) outs.Add(core.GameSinkId);
            if (core.Game.Mixer.DeviceId != null) outs.Add(core.Game.Mixer.DeviceId);
            foreach (var id in outs)
                try { foreach (var kv in ProcessLoopback.Sessions(id, true)) { string e = GameProfiles.NormalizeExe(kv.Value); if (e != null && seen.Add(e)) running.Items.Add(new RunningItem { Exe = e, What = "playing sound" }); } }
                catch { }
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                    string e = GameProfiles.NormalizeExe(p.ProcessName);
                    if (e != null && seen.Add(e)) running.Items.Add(new RunningItem { Exe = e, What = p.MainWindowTitle });
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        void ShowApp()
        {
            var it = Current;
            bool any = it != null;
            loading = true;
            on.Visible = onLabel.Visible = remove.Visible = presetLabel.Visible = presets.Visible = graph.Visible = adjust.Visible = savePreset.Visible = any;
            deletePreset.Visible = any && it.S.Preset != null && core.S.AppEqPresets.ContainsKey(it.S.Preset);
            if (any)
            {
                on.Checked = it.S.On;
                graph.Profile = it.S.Eq;
                graph.Invalidate();
                presets.SelectedIndex = 0;
                if (it.S.Preset != null) { int i = presets.Items.IndexOf(it.S.Preset); if (i > 0) presets.SelectedIndex = i; }
            }
            note.Text = !any ? "Add an app on the left. Nothing changes for apps you don't add."
                : core.Game.ExclusiveActive ? "Apps kept out of 3D get their EQ on their own sound. Apps playing through 3D audio share one stream, so there the EQ of the app playing applies to that stream (if two with an EQ play, the first one found)."
                : "Plays while StreamLite holds your headphones (Direct to headphones). Right now Windows is playing to them, so app EQ waits.";
            loading = false;
        }

        void Changed() { core.PushAppEq(true); core.SaveSoon(); }
    }

    /// <summary>A name to type.</summary>
    internal sealed class AskNameDialog : DarkDialog
    {
        readonly TextBox box;
        public string Value { get { return box.Text; } }

        public AskNameDialog(string title, string prompt, string start) : base(title, 420, 130)
        {
            Text2(prompt, 22, 14, 376, 24, Theme.Body, Theme.Text);
            box = new TextBox { Location = new Point(Theme.Px(22), Theme.Px(42)), Width = Theme.Px(376), Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = start ?? "" };
            Body.Controls.Add(box);
            var ok = new FlatButton("Save", true) { Location = new Point(Theme.Px(298), Theme.Px(84)), Width = Theme.Px(100) };
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var no = new FlatButton("Cancel") { Location = new Point(Theme.Px(188), Theme.Px(84)), Width = Theme.Px(100) };
            no.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(ok); Body.Controls.Add(no);
            AcceptButton = ok; CancelButton = no;
        }
    }
}
