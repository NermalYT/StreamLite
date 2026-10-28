using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Auto EQ: which apps switch your headphones to which profile. Pick a profile, then add apps (typed, or from the
    /// ones running now) or remove them. An app belongs to one profile; adding it somewhere else moves it.
    /// </summary>
    internal sealed class AutoEqAppsDialog : DarkDialog
    {
        readonly ProfileSettings ps;
        readonly ListBox profiles, apps;
        readonly TextBox input;
        readonly DarkCombo running, fallback;
        readonly Label when, note;
        public bool Changed;

        sealed class ProfileItem
        {
            public GameProfile P;
            public override string ToString() { return P.Name + (P.Exes.Count > 0 ? "  (" + P.Exes.Count + ")" : ""); }
        }

        sealed class RunningItem
        {
            public string Exe, Title; public bool Playing;
            public override string ToString() { return Exe + (Playing ? "  - playing sound" : !string.IsNullOrEmpty(Title) ? "  - " + Title : ""); }
        }

        public AutoEqAppsDialog(ProfileSettings ps, string select) : base("Auto EQ apps", 660, 500)
        {
            this.ps = ps;
            Text2("Auto EQ switches your headphone EQ to the profile of the app you're using: a game first, then music while it plays, then an open voice chat app. Pick a profile and add or remove its apps. An app belongs to one profile - adding it here moves it.",
                22, 12, 616, 50, Theme.Small, Theme.Muted);
            profiles = List(22, 70, 230, 344);
            apps = List(266, 92, 372, 196);
            when = Text2("", 266, 70, 372, 20, Theme.Small, Theme.Accent);

            input = new TextBox
            {
                Location = new Point(Theme.Px(266), Theme.Px(300)), Width = Theme.Px(214), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle
            };
            Body.Controls.Add(input);
            var add = Button("Add", 488, 297, 64, true);
            var remove = Button("Remove", 558, 297, 80, false);
            Theme.Help(input, "A program name, with or without .exe (for example vlc, chrome or Spotify.exe). Several at once: separate them with commas.");

            Text2("Or add an app that's running now:", 266, 338, 372, 20, Theme.Small, Theme.Muted);
            running = new DarkCombo { Location = new Point(Theme.Px(266), Theme.Px(358)), Width = Theme.Px(372) };
            Body.Controls.Add(running);
            running.DropDown += (s, e) => FillRunning();
            running.SelectionChangeCommitted += (s, e) => { var r = running.SelectedItem as RunningItem; if (r != null) Add(r.Exe); };

            Text2("When no listed app is in use:", 22, 428, 230, 20, Theme.Small, Theme.Muted);
            fallback = new DarkCombo { Location = new Point(Theme.Px(22), Theme.Px(448)), Width = Theme.Px(230) };
            Body.Controls.Add(fallback);
            foreach (var p in ps.Profiles) fallback.Items.Add(p.Name);
            fallback.SelectedItem = ps.Profiles.Any(p => p.Name == ps.DefaultProfile) ? ps.DefaultProfile : GameProfiles.Movies;
            fallback.SelectionChangeCommitted += (s, e) => { var n = fallback.SelectedItem as string; if (n != null) { ps.DefaultProfile = n; Changed = true; } };

            note = Text2("", 266, 396, 372, 40, Theme.Small, Theme.Muted);
            var done = Button("Done", 0, 448, 0, true);
            done.Left = Theme.Px(638) - done.Width;
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            AcceptButton = null;   // Enter adds the typed app instead
            CancelButton = done;

            foreach (var p in ps.Profiles) profiles.Items.Add(new ProfileItem { P = p });
            profiles.SelectedIndexChanged += (s, e) => ShowApps();
            apps.SelectedIndexChanged += (s, e) => remove.Enabled = apps.SelectedItem != null;
            apps.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            add.Click += (s, e) => AddTyped();
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddTyped(); e.SuppressKeyPress = true; } };
            remove.Click += (s, e) => RemoveSelected();
            remove.Enabled = false;
            int at = ps.Profiles.FindIndex(p => p.Name == select);
            profiles.SelectedIndex = at >= 0 ? at : 0;
        }

        ListBox List(int x, int y, int w, int h)
        {
            var l = new ListBox
            {
                Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(w), Theme.Px(h)), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false
            };
            DarkList.Style(l);
            Body.Controls.Add(l);
            return l;
        }

        FlatButton Button(string text, int x, int y, int w, bool accent)
        {
            var b = new FlatButton(text, accent) { Location = new Point(Theme.Px(x), Theme.Px(y)) };
            if (w > 0) b.Width = Theme.Px(w);
            Body.Controls.Add(b);
            return b;
        }

        GameProfile Current { get { var i = profiles.SelectedItem as ProfileItem; return i != null ? i.P : null; } }

        void ShowApps()
        {
            var p = Current;
            apps.BeginUpdate();
            apps.Items.Clear();
            if (p != null) foreach (var x in p.Exes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) apps.Items.Add(x);
            apps.EndUpdate();
            when.Text = p == null ? "" : p.Exes.Count == 0 && p.Name == ps.DefaultProfile ? "Used when no listed app is in use" : "Used " + GameProfiles.When(p);
        }

        void AddTyped()
        {
            foreach (var part in input.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) Add(part);
            input.Text = "";
        }

        void Add(string exe)
        {
            var p = Current;
            string name = GameProfiles.NormalizeExe(exe);
            if (p == null || name == null) return;
            if (p.Exes.Contains(name, StringComparer.OrdinalIgnoreCase)) { note.Text = name + " is already in " + p.Name + "."; return; }
            var from = GameProfiles.AddApp(ps, p, name);
            Changed = true;
            note.Text = from != null ? name + " moved here from " + from.Name + "." : name + " added. Its EQ switches within 3 seconds of the app starting.";
            Refresh2();
            int i = apps.Items.IndexOf(name); if (i >= 0) apps.SelectedIndex = i;
        }

        void RemoveSelected()
        {
            var p = Current; var name = apps.SelectedItem as string;
            if (p == null || name == null) return;
            GameProfiles.RemoveApp(p, name);
            Changed = true;
            note.Text = name + " removed: it now uses whatever else is in use (or \"" + ps.DefaultProfile + "\").";
            Refresh2();
        }

        void Refresh2()
        {
            // Counts in the profile list change when an app moves.
            int sel = profiles.SelectedIndex;
            profiles.BeginUpdate();
            for (int i = 0; i < profiles.Items.Count; i++) profiles.Items[i] = profiles.Items[i];
            profiles.EndUpdate();
            profiles.SelectedIndex = sel;
            ShowApps();
        }

        /// <summary>Apps with a window or with sound right now (not StreamLite itself or parts of Windows).</summary>
        void FillRunning()
        {
            HashSet<string> playing;
            try { playing = Interop.AudioSessions.Playing(); } catch { playing = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
                "StartMenuExperienceHost", "SearchHost", "LockApp", "dwm", "csrss", "svchost", "audiodg", "Idle", "System", Process.GetCurrentProcess().ProcessName };
            var found = new Dictionary<string, RunningItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in Process.GetProcesses())
            {
                try
                {
                    string n = pr.ProcessName;
                    if (skip.Contains(n)) continue;
                    bool snd = playing.Contains(n);
                    if (!snd && pr.MainWindowHandle == IntPtr.Zero) continue;
                    RunningItem have;
                    if (found.TryGetValue(n, out have)) { have.Playing |= snd; continue; }
                    found[n] = new RunningItem { Exe = n, Playing = snd, Title = pr.MainWindowTitle };
                }
                catch { }
                finally { pr.Dispose(); }
            }
            running.BeginUpdate();
            running.Items.Clear();
            foreach (var r in found.Values.OrderByDescending(r => r.Playing).ThenBy(r => r.Exe, StringComparer.OrdinalIgnoreCase))
            {
                if (r.Title != null && r.Title.Length > 40) r.Title = r.Title.Substring(0, 40) + "...";
                running.Items.Add(r);
            }
            running.EndUpdate();
        }
    }
}
