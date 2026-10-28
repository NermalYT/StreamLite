using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Report a bug, all inside StreamLite and no account needed (4.1.2): what kind of problem, which part, what happened,
    /// how to make it happen, screenshots (checked, then shown on the report), "I have a video" (it's never uploaded - the
    /// developer asks for it on Discord) and - if you like - your Discord name, which only the developer sees. Your
    /// version, system and StreamLite's details are added; Send posts it on StreamLite's issue list.
    /// </summary>
    internal sealed class BugReportDialog : DarkDialog
    {
        static readonly string[] Kinds = { "Bug", "Broken feature", "Missing device / headphones", "Hardware incompatibility", "Idea to improve StreamLite" };
        static readonly string[] Areas = { "Microphone / noise removal", "Headphone EQ / correction / profiles", "3D game audio", "Camera (Windows)", "Installing / updating / setup", "Something else" };
        const string NoPictures = "Screenshots of StreamLite or an error help a lot. They're checked, then shown on the public report - leave out anything personal.";
        const long MaxFileBytes = 40L * 1024 * 1024;   // before it's made smaller (BugReports.Prepare)
        readonly AppCore core;
        readonly DarkCombo kind, area;
        readonly TextBox title, what, steps, discord;
        readonly DarkCheck details, video;
        readonly Label pics, status;
        readonly FlatButton send;
        readonly List<string> images = new List<string>();

        public BugReportDialog(AppCore core) : base("Report a bug", 720, 690)
        {
            this.core = core;
            Text2("Thank you! It goes straight onto StreamLite's issue list, where the developer reads every one - no account needed. Your version and system are added for you.", 24, 14, 672, 40, Theme.Body, Theme.Muted);
            Text2("Kind", 24, 66, 120, 24, Theme.Bold, Theme.Text);
            kind = new DarkCombo { Location = new Point(Theme.Px(150), Theme.Px(62)), Width = Theme.Px(260) }; kind.Items.AddRange(Kinds); kind.SelectedIndex = 0; Body.Controls.Add(kind);
            Text2("Part", 430, 66, 60, 24, Theme.Bold, Theme.Text);
            area = new DarkCombo { Location = new Point(Theme.Px(490), Theme.Px(62)), Width = Theme.Px(206) }; area.Items.AddRange(Areas); area.SelectedIndex = Areas.Length - 1; Body.Controls.Add(area);
            Text2("Title", 24, 106, 120, 24, Theme.Bold, Theme.Text);
            title = Box(150, 102, 546, 28, false, "In a few words - e.g. \"My friends hear robotic audio with DeepFilterNet on\"");
            Text2("What happened?", 24, 144, 300, 24, Theme.Bold, Theme.Text);
            what = Box(24, 170, 672, 110, true, "What you saw or heard, and what you expected instead.");
            Text2("How can it be made to happen? (if you know)", 24, 292, 400, 24, Theme.Bold, Theme.Text);
            steps = Box(24, 318, 672, 80, true, "1. ...\r\n2. ...");
            var add = new FlatButton("Add screenshots...") { Location = new Point(Theme.Px(24), Theme.Px(410)), Width = Theme.Px(160) };
            add.Click += (s, e) => AddPictures();
            Body.Controls.Add(add);
            pics = Text2(NoPictures, 196, 408, 500, 40, Theme.Small, Theme.Muted);
            // A way to reach you, seen by the developer only.
            Text2("Discord name (optional)", 24, 460, 190, 24, Theme.Bold, Theme.Text);
            discord = Box(220, 456, 230, 28, false, "Your Discord username (under your profile picture), e.g. coolname");
            Text2("Only the developer sees it - it's never on the public report. He may DM you on Discord about your report.", 220, 488, 476, 36, Theme.Small, Theme.Muted);
            video = new DarkCheck("I have a video of it (don't attach it - the developer will ask you for it on Discord)") { Location = new Point(Theme.Px(24), Theme.Px(528)), Font = Theme.Body, ForeColor = Theme.Text };
            Body.Controls.Add(video);
            details = new DarkCheck("Add StreamLite's details (versions, sound devices, settings - nothing personal)") { Location = new Point(Theme.Px(24), Theme.Px(560)), Checked = true, Font = Theme.Body, ForeColor = Theme.Text };
            Body.Controls.Add(details);
            status = Text2("", 24, 594, 380, 40, Theme.Small, Theme.Muted);
            send = new FlatButton("Send report", true) { Location = new Point(Theme.Px(696) - Theme.Px(170), Theme.Px(634)), Width = Theme.Px(170) };
            send.Click += (s, e) => Send();
            var cancel = new FlatButton("Cancel") { Location = new Point(send.Left - Theme.Px(10) - Theme.Px(110), Theme.Px(634)), Width = Theme.Px(110) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(send); Body.Controls.Add(cancel);
        }

        TextBox Box(int x, int y, int w, int h, bool multi, string hint)
        {
            var t = new TextBox { Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(w), Theme.Px(h)), Multiline = multi, ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None,
                BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
            Theme.Help(t, hint);
            Body.Controls.Add(t);
            return t;
        }

        void AddPictures()
        {
            // Screenshots only - no videos or GIFs (tick "I have a video" instead).
            using (var d = new OpenFileDialog { Title = "Screenshots for the report", Filter = "Screenshots (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK)
                    foreach (var f in d.FileNames)
                    {
                        if (images.Contains(f)) continue;
                        if (images.Count >= BugReports.MaxPictures) { status.Text = BugReports.MaxPictures + " screenshots at most."; break; }
                        if (new FileInfo(f).Length > MaxFileBytes) { status.Text = Path.GetFileName(f) + " is too big for a screenshot."; continue; }
                        images.Add(f);
                    }
            pics.Text = images.Count == 0 ? NoPictures : images.Count + " screenshot" + (images.Count == 1 ? "" : "s") + " (checked, then shown on the report): " + string.Join(", ", images.Select(Path.GetFileName));
        }

        void Send()
        {
            if (title.Text.Trim().Length < 4 || what.Text.Trim().Length < 4) { status.Text = "A title and what happened, please."; return; }
            if (!BugReports.DiscordOk(discord.Text)) { status.Text = "That Discord name doesn't look right - it's the username under your profile picture, like coolname or cool.name."; return; }
            if (video.Checked && discord.Text.Trim().Length == 0) { status.Text = "Add your Discord name so the developer can ask you for the video."; discord.Focus(); return; }
            var r = new BugReports.Report { Kind = Kinds[kind.SelectedIndex], Area = Areas[area.SelectedIndex], Title = title.Text.Trim(), What = what.Text.Trim(), Steps = steps.Text.Trim(), Discord = discord.Text.Trim(), Video = video.Checked };
            r.Pictures.AddRange(images);
            if (details.Checked) try { r.Details = Diagnostics.Report(core); } catch { }
            send.Enabled = false; status.Text = "Sending...";
            new System.Threading.Thread(() =>
            {
                var res = BugReports.Send(r);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        send.Enabled = true;
                        if (res.Error != null) { status.Text = res.Error; return; }
                        try { HealthMonitor.LogLine("Bug report sent: #" + res.Number); } catch { }
                        using (var d = new ChoiceDialog("Report a bug", "Sent - thank you! It's report #" + res.Number + " on StreamLite's issue list." +
                            (res.LeftOut == 0 ? "" : "\n\n" + (res.LeftOut == 1 ? "1 picture was" : res.LeftOut + " pictures were") + " left out - only family-friendly screenshots of an app or an error are shown.") +
                            (r.Video ? "\n\nThe developer will ask you for the video on Discord." : string.IsNullOrEmpty(r.Discord) ? "" : "\n\nThe developer may DM you on Discord about it."), new[] { "Done", "Open the report" }, 460))
                        {
                            d.ShowDialog(this);
                            if (d.Choice == 1 && res.Url != null) Updater.OpenUrl(res.Url);
                        }
                        DialogResult = DialogResult.OK; Close();
                    }));
                }
                catch { }
            }) { IsBackground = true, Name = "bug report" }.Start();
        }
    }
}
