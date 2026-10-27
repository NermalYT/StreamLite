using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Auto EQ priorities: when apps of several profiles are in use, the profile higher in the list wins. Move profiles
    /// up and down, go back to the standard order (games, music, chat), and choose whether every profile plays equally loud.
    /// </summary>
    internal sealed class ProfilePriorityDialog : DarkDialog
    {
        readonly ProfileSettings ps;
        readonly ListBox list;
        public bool Changed;

        sealed class Item
        {
            public GameProfile P;
            public override string ToString() { return P.Name + "   - " + (P.WhilePlaying ? "while it plays" : P.ChatApp ? "while it's open" : "while it runs"); }
        }

        public ProfilePriorityDialog(ProfileSettings ps) : base("When several apps are in use", 560, 470)
        {
            this.ps = ps;
            Text2("Auto EQ plays one profile at a time. When apps of several profiles are in use, the one higher in this list wins: a game while it runs, " +
                "music while it plays, a chat app while it's open. A switch happens once the new profile wins two checks in a row (3-6 seconds), so a launcher " +
                "or an updater that's open for a moment doesn't flip your EQ.", 20, 12, 520, 72, Theme.Small, Theme.Muted);
            list = new ListBox
            {
                Location = new Point(Theme.Px(20), Theme.Px(90)), Size = new Size(Theme.Px(390), Theme.Px(270)), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false
            };
            Body.Controls.Add(list);
            var up = Button("Move up", 422, 90); var down = Button("Move down", 422, 128); var reset = Button("Standard order", 422, 184);
            Theme.Help(reset, "Games first (in the order of the profile list), then music while it plays, then chat apps.");
            up.Click += (s, e) => MoveBy(-1);
            down.Click += (s, e) => MoveBy(1);
            reset.Click += (s, e) => { ps.Priority.Clear(); Changed = true; Fill(null); };
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Up) { MoveBy(-1); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.Down) { MoveBy(1); e.Handled = true; }
            };

            var match = new ToggleSwitch { Checked = ps.MatchLoudness, Location = new Point(Theme.Px(20), Theme.Px(378)) };
            Body.Controls.Add(match);
            Theme.Help(Text2("Same loudness for every profile", 74, 376, 400, 22, Theme.Bold, Theme.Text),
                "Every profile's EQ changes how loud things are a little (a bass lift adds loudness, a cut takes it away), so switching from a game to music could jump in volume. With this on, StreamLite measures each profile with your headphones' correction under it (pink noise, weighted the way loudness meters do) and turns the louder ones down to match - never up, so nothing can clip.");
            Text2("A switch changes the sound, not the volume (louder ones go down, never up).", 74, 398, 466, 20, Theme.Small, Theme.Muted);
            match.CheckedChanged += (s, e) => { ps.MatchLoudness = match.Checked; Changed = true; };

            var done = new FlatButton("Done", true);
            Body.Controls.Add(done);
            done.Location = new Point(Theme.Px(540) - done.Width, Theme.Px(428));
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            CancelButton = done;
            Fill(null);
        }

        FlatButton Button(string text, int x, int y)
        {
            var b = new FlatButton(text) { Location = new Point(Theme.Px(x), Theme.Px(y)), Width = Theme.Px(118) };
            Body.Controls.Add(b);
            return b;
        }

        void Fill(GameProfile select)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in GameProfiles.PriorityOrder(ps)) list.Items.Add(new Item { P = p });
            list.EndUpdate();
            int at = select == null ? 0 : list.Items.Cast<Item>().ToList().FindIndex(i => i.P == select);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, at);
        }

        void MoveBy(int by)
        {
            int i = list.SelectedIndex, j = i + by;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var items = list.Items.Cast<Item>().ToList();
            var it = items[i]; items.RemoveAt(i); items.Insert(j, it);
            ps.Priority = items.Select(x => x.P.Name).ToList();   // the whole order, so nothing moves on its own later
            Changed = true;
            Fill(it.P);
        }
    }
}
