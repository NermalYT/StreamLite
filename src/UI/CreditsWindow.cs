using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Credits: every project, AI model and dataset StreamLite uses, with links to its source and model files, for this
    /// version or any earlier release (what each release used, and what was new in it). From src\Credits.cs.
    /// </summary>
    internal sealed class CreditsWindow : DarkDialog
    {
        const string GitHubList = "https://github.com/NermalYT/StreamLite/blob/main/CREDITS.md";
        readonly Panel list;
        readonly ScrollHost listHost;
        readonly Label change;

        /// <param name="licenses">Opens the full license texts (Settings > About > Open-source licenses).</param>
        public CreditsWindow(Action licenses) : base("Credits", 700, 640)
        {
            Text2("StreamLite stands on a lot of freely shared work. Thank you to everyone below!", 22, 16, 660, 22, Theme.Section, Theme.Text);
            Text2("Credits for", 22, 52, 90, 22, Theme.Bold, Theme.Text);
            var pick = new DarkCombo { Location = new Point(Theme.Px(112), Theme.Px(48)), Width = Theme.Px(220) };
            string mine = Credits.Release(Program.Version.ToString());
            var versions = Enumerable.Reverse(Credits.Versions).ToArray();
            foreach (var v in versions) pick.Items.Add("StreamLite " + v + (v == mine ? " (this version)" : ""));
            Body.Controls.Add(pick);
            change = Text2("", 22, 82, 660, 54, Theme.Small, Theme.Muted);

            list = ScrollLists.Make(Body, new Rectangle(Theme.Px(12), Theme.Px(140), Theme.Px(680), Theme.Px(436)), Theme.Bg, out listHost);

            var lic = new FlatButton("Full license texts") { Location = new Point(Theme.Px(22), Theme.Px(590)), Width = Theme.Px(160) };
            lic.Click += (s, e) => licenses();
            Body.Controls.Add(lic);
            var web = new FlatButton("Credits on GitHub") { Location = new Point(Theme.Px(194), Theme.Px(590)), Width = Theme.Px(160) };
            web.Click += (s, e) => Updater.OpenUrl(GitHubList);
            Body.Controls.Add(web);
            var close = new FlatButton("Close", true) { Location = new Point(Theme.Px(580), Theme.Px(590)), Width = Theme.Px(100) };
            close.Click += (s, e) => Close();
            Body.Controls.Add(close);
            CancelButton = close;

            pick.SelectedIndexChanged += (s, e) => Fill(versions[Math.Max(0, pick.SelectedIndex)]);
            pick.SelectedIndex = Math.Max(0, Array.IndexOf(versions, mine));
        }

        void Fill(string version)
        {
            string note;
            change.Text = Credits.Changes.TryGetValue(version, out note) ? "What changed in " + version + ": " + note : "";
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
            list.Controls.Clear();
            int y = 4, w = Theme.Px(640);
            var items = Credits.For(version);
            foreach (var g in Credits.Groups)
            {
                var group = items.Where(c => c.Group == g).ToList();
                if (group.Count == 0) continue;
                list.Controls.Add(new Label { Text = g, Font = Theme.Section, ForeColor = Theme.Accent, BackColor = Color.Transparent, AutoSize = true, Location = new Point(Theme.Px(10), y), UseMnemonic = false });
                y += Theme.Px(30);
                foreach (var c in group)
                {
                    int x = Theme.Px(10);
                    var name = Link(c.Name, c.Url, Theme.Bold, x, y);
                    x = name.Right + Theme.Px(8);
                    if (c.Platform != null) { var p = Tag(c.Platform, Theme.Muted, x, y); x = p.Right + Theme.Px(6); }
                    if (Credits.NewIn(c, version)) { var n = Tag("New in " + version, Theme.Accent, x, y); x = n.Right + Theme.Px(6); }
                    if (c.File != null) Link("Model / data file", c.File, Theme.Small, x, y + Theme.Px(2));
                    y += Theme.Px(22);
                    string text = c.What + "\n" + c.Authors + "  -  " + c.License;
                    var size = TextRenderer.MeasureText(text, Theme.Small, new Size(w, 0), TextFormatFlags.WordBreak);
                    list.Controls.Add(new Label { Text = text, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, Location = new Point(Theme.Px(10), y), Size = new Size(w, size.Height + Theme.Px(2)), UseMnemonic = false });
                    y += size.Height + Theme.Px(12);
                }
                y += Theme.Px(8);
            }
            list.ResumeLayout();
            ScrollLists.Fit(listHost);
        }

        LinkLabel Link(string text, string url, Font font, int x, int y)
        {
            var l = new LinkLabel
            {
                Text = text, Font = font, AutoSize = true, Location = new Point(x, y), BackColor = Color.Transparent, UseMnemonic = false,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline
            };
            l.LinkClicked += (s, e) => Updater.OpenUrl(url);
            Theme.Help(l, url);
            list.Controls.Add(l);
            return l;
        }

        Label Tag(string text, Color color, int x, int y)
        {
            var l = new Label { Text = text, Font = Theme.Small, ForeColor = color, BackColor = Color.Transparent, AutoSize = true, Location = new Point(x, y + Theme.Px(2)), UseMnemonic = false };
            list.Controls.Add(l);
            return l;
        }
    }
}
