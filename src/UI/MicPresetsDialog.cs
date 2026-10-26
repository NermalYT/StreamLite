using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.UI
{
    /// <summary>
    /// Settings > Mic presets: every mic's own settings - export one to a file, import one (from another PC, a friend)
    /// for that mic whenever it's plugged in or for any other mic, plugged in or not, and switch "each mic keeps its own
    /// settings" on or off.
    /// </summary>
    internal sealed class MicPresetsDialog : DarkDialog
    {
        readonly AppCore core;
        readonly ListBox list;
        readonly Label status;
        List<MicPreset> shown = new List<MicPreset>();
        List<AudioDeviceInfo> mics = new List<AudioDeviceInfo>();
        readonly Dictionary<string, int> states = new Dictionary<string, int>();

        public MicPresetsDialog(AppCore core) : base("Mic presets", 640, 470)
        {
            this.core = core;
            core.KeepMicPreset();   // the mic in use, as it is right now
            Text2("Each microphone keeps its own settings: noise removal, gate, compressor, voice EQ, volume and what Studio voice learned about it. " +
                "Export one to take your mic to a friend's PC (or share your sound); import it there and it's used whenever that mic is plugged in and picked - or put it on any other mic, plugged in or not.",
                20, 12, 600, 56, Theme.Small, Theme.Muted);
            list = new ListBox
            {
                Location = new Point(Theme.Px(20), Theme.Px(74)), Size = new Size(Theme.Px(430), Theme.Px(270)), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(44)
            };
            list.DrawItem += DrawRow;
            Body.Controls.Add(list);
            var export = Button("Export...", 462, 74); var import = Button("Import...", 462, 112);
            var assign = Button("Use for another mic...", 462, 162); var use = Button("Use now", 462, 200); var del = Button("Delete", 462, 250);
            Theme.Help(export, "Saves the selected mic's settings as a file (.slmic) - no device ids or paths, safe to share.");
            Theme.Help(import, "Loads a .slmic file: for the mic it was made for (whenever it's plugged in), or for one you pick.");
            Theme.Help(assign, "Puts the selected settings on another microphone - also one that isn't plugged in right now.");
            Theme.Help(use, "Uses the selected settings for the mic in use right now.");
            export.Click += (s, e) => Export();
            import.Click += (s, e) => Import(null);
            assign.Click += (s, e) => { var p = Selected(); if (p != null) Assign(p, "Use these settings for"); };
            use.Click += (s, e) =>
            {
                var p = Selected(); if (p == null) return;
                var d = core.AllMics().FirstOrDefault(x => x.Id == core.ActiveMicId);
                if (d == null) { Say("No microphone is in use right now.", true); return; }
                var copy = Copy(p); copy.Key = MicPresets.KeyOf(d); copy.Device = d.Name; copy.Note = "From " + (p.Device ?? "another mic");
                MicPresets.Keep(core.S, copy); core.UseMicPresetNow(copy); core.SaveSoon(); Fill(copy);
                Say("Now in use on " + d.Name + ".", false);
            };
            del.Click += (s, e) =>
            {
                var p = Selected(); if (p == null) return;
                core.S.MicPresets.Remove(p); core.SaveSoon(); Fill(null);
                Say("Deleted. The mic in use keeps its current settings.", false);
            };
            list.DoubleClick += (s, e) => use.PerformClick();

            var follow = new ToggleSwitch { Checked = core.S.MicPresetsFollow, Location = new Point(Theme.Px(20), Theme.Px(358)) };
            Body.Controls.Add(follow);
            Theme.Help(Text2("Each mic keeps its own settings", 74, 356, 380, 22, Theme.Bold, Theme.Text),
                "On: picking another microphone (or plugging in one that StreamLite switches to) loads that mic's settings, and the last mic's are kept for next time. Off: one set of settings for every mic, as before.");
            follow.CheckedChanged += (s, e) => { core.S.MicPresetsFollow = follow.Checked; core.SaveSoon(); };
            status = Text2("", 20, 390, 600, 36, Theme.Small, Theme.Muted);
            var done = new FlatButton("Done", true);
            Body.Controls.Add(done);
            done.Location = new Point(Theme.Px(620) - done.Width, Theme.Px(430));
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            CancelButton = done;
            Fill(null);
        }

        FlatButton Button(string text, int x, int y)
        {
            var b = new FlatButton(text) { Location = new Point(Theme.Px(x), Theme.Px(y)), Width = Theme.Px(158) };
            Body.Controls.Add(b);
            return b;
        }

        MicPreset Selected() { int i = list.SelectedIndex; return i >= 0 && i < shown.Count ? shown[i] : null; }

        void Say(string text, bool warn) { status.ForeColor = warn ? Theme.Warn : Theme.Muted; status.Text = text; }

        void Fill(MicPreset select)
        {
            mics = core.AllMics();
            states.Clear(); foreach (var d in mics) states[d.Id] = AudioDevices.State(d.Id);
            var active = mics.FirstOrDefault(x => x.Id == core.ActiveMicId);
            string activeKey = active != null ? MicPresets.KeyOf(active) : null;
            shown = core.S.MicPresets.OrderByDescending(p => string.Equals(p.Key, activeKey, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Device).ToList();
            list.Items.Clear();
            foreach (var p in shown) list.Items.Add(p.Device ?? "Microphone");
            int i = select != null ? shown.IndexOf(select) : 0;
            if (shown.Count > 0) list.SelectedIndex = Math.Max(0, i);
            if (shown.Count == 0) Say("No mic presets yet - the mic in use gets one as soon as it's running.", false);
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            var p = shown[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Field : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            var here = mics.Where(d => string.Equals(MicPresets.KeyOf(d), p.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            bool inUse = here.Any(d => d.Id == core.ActiveMicId), plugged = here.Any(d => states.ContainsKey(d.Id) && states[d.Id] == 1);
            string state = inUse ? "in use" : plugged ? "plugged in" : "not plugged in";
            string sub = state + " - " + (p.Note ?? "kept " + (p.Updated ?? "")) + (p.Key != null && p.Key.StartsWith("usb:") ? "" : " (matched by name)");
            TextRenderer.DrawText(e.Graphics, p.Device ?? "Microphone", Theme.Body, new Rectangle(e.Bounds.X + Theme.Px(8), e.Bounds.Y + Theme.Px(4), e.Bounds.Width - Theme.Px(12), Theme.Px(20)), inUse ? Theme.Accent : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, sub, Theme.Small, new Rectangle(e.Bounds.X + Theme.Px(8), e.Bounds.Y + Theme.Px(24), e.Bounds.Width - Theme.Px(12), Theme.Px(18)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void Export()
        {
            var p = Selected(); if (p == null) return;
            using (var dlg = new SaveFileDialog { Title = "Export mic preset", Filter = "StreamLite mic preset (*.slmic)|*.slmic", FileName = MicPresets.FileName(p) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var copy = Copy(p); copy.Note = null;
                    File.WriteAllText(dlg.FileName, MicPresets.ToJson(copy, Program.Version.ToString(3)));
                    Say("Saved " + Path.GetFileName(dlg.FileName) + ". On another PC: Settings > Mic presets > Import.", false);
                }
                catch (Exception ex) { Say("Couldn't export: " + ex.Message, true); }
            }
        }

        /// <summary>Imports a .slmic file (path = null: pick one) and asks which mic it's for.</summary>
        public void Import(string path)
        {
            if (path == null)
                using (var dlg = new OpenFileDialog { Title = "Import mic preset", Filter = "StreamLite mic preset (*.slmic)|*.slmic|All files|*.*" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            MicPreset p;
            try { p = MicPresets.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Say("Couldn't import: " + ex.Message, true); return; }
            p.Note = "Imported from " + Path.GetFileName(path);
            Assign(p, "Use the settings from " + Path.GetFileName(path) + " for");
        }

        /// <summary>Asks which mic a preset goes on (the one it was made for, or any mic Windows knows), keeps it there and uses it if that mic is in use.</summary>
        void Assign(MicPreset p, string question)
        {
            var mics = core.AllMics();
            // One entry per mic (a mic plugged into different ports shows up more than once).
            var choices = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(p.Key, (p.Device ?? "the mic it was made for") + "  (whenever it's plugged in)") };
            foreach (var d in mics.OrderBy(d => AudioDevices.State(d.Id) == 1 ? 0 : 1).ThenBy(d => d.Name))
            {
                if (AudioDevices.LooksVirtual(d)) continue;
                string key = MicPresets.KeyOf(d);
                if (choices.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
                choices.Add(new KeyValuePair<string, string>(key, d.Name + (d.Id == core.ActiveMicId ? "  (in use)" : AudioDevices.State(d.Id) == 1 ? "" : "  (not plugged in)")));
            }
            using (var f = new DarkDialog("Mic preset", 520, 150))
            {
                var label = new Label { Text = question + ":", Font = Theme.Body, ForeColor = Theme.Text, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(16)), Size = new Size(Theme.Px(480), Theme.Px(22)), UseMnemonic = false };
                var combo = new DarkCombo { Location = new Point(Theme.Px(20), Theme.Px(46)), Width = Theme.Px(480) };
                foreach (var c in choices) combo.Items.Add(c.Value);
                combo.SelectedIndex = 0;
                var ok = new FlatButton("Use", true); ok.Location = new Point(Theme.Px(500) - ok.Width, Theme.Px(100));
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                var body = f.Controls.OfType<Panel>().First(x => x.Dock == DockStyle.Fill);
                body.Controls.Add(label); body.Controls.Add(combo); body.Controls.Add(ok);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var pick = choices[Math.Max(0, combo.SelectedIndex)];
                var copy = Copy(p);
                copy.Key = pick.Key;
                if (combo.SelectedIndex > 0) copy.Device = pick.Value.Replace("  (in use)", "").Replace("  (not plugged in)", "");
                if (copy.Updated == null) copy.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                MicPresets.Keep(core.S, copy);
                core.UseMicPresetNow(copy);
                core.SaveSoon();
                Fill(core.S.MicPresets.FirstOrDefault(x => x.Key == copy.Key));
                Say("Kept for " + copy.Device + (MicPresets.PluggedIn(copy, mics.Where(d => d.Id == core.ActiveMicId)) != null ? " - in use now." : " - used whenever it's plugged in and picked."), false);
            }
        }

        static MicPreset Copy(MicPreset p)
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return js.Deserialize<MicPreset>(js.Serialize(p));
        }
    }
}
