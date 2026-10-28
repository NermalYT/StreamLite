using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Report a bug, inside StreamLite (4.1.1): what kind of problem, what happened, how to make it happen and pictures;
    /// your version, system and StreamLite's details are added for you. Signed in with GitHub (once), it goes straight onto
    /// StreamLite's issue list; GitHub doesn't let apps upload pictures, so those are added on the report's page after.
    /// Without the sign-in set up, Send opens GitHub's form with everything filled in.
    /// </summary>
    internal sealed class BugReportDialog : DarkDialog
    {
        static readonly string[] Kinds = { "Bug", "Broken feature", "Missing device / headphones", "Hardware incompatibility", "Idea to improve StreamLite" };
        static readonly string[] Areas = { "Microphone / noise removal", "Headphone EQ / correction / profiles", "3D game audio", "Camera (Windows)", "Installing / updating / setup", "Something else" };
        readonly AppCore core;
        readonly DarkCombo kind, area;
        readonly TextBox title, what, steps;
        readonly DarkCheck details;
        readonly Label pics, account, status;
        readonly FlatButton signIn, send;
        readonly List<string> images = new List<string>();
        string token, login;

        public BugReportDialog(AppCore core) : base("Report a bug", 720, 640)
        {
            this.core = core;
            Text2("Thank you! It goes onto StreamLite's issue list on GitHub, where the developer reads every one. Your version and system are added for you.", 24, 14, 672, 40, Theme.Body, Theme.Muted);
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
            var add = new FlatButton("Add pictures...") { Location = new Point(Theme.Px(24), Theme.Px(410)), Width = Theme.Px(150) };
            add.Click += (s, e) => AddPictures();
            Body.Controls.Add(add);
            pics = Text2("No pictures - screenshots help a lot.", 186, 414, 510, 24, Theme.Small, Theme.Muted);
            details = new DarkCheck("Add StreamLite's details (versions, sound devices, settings - nothing personal)") { Location = new Point(Theme.Px(24), Theme.Px(452)), Checked = true, Font = Theme.Body, ForeColor = Theme.Text };
            Body.Controls.Add(details);
            account = Text2("", 24, 494, 470, 40, Theme.Body, Theme.Muted);
            signIn = new FlatButton("Sign in with GitHub") { Location = new Point(Theme.Px(696) - Theme.Px(190), Theme.Px(494)), Width = Theme.Px(190) };
            signIn.Click += (s, e) => SignIn();
            Body.Controls.Add(signIn);
            status = Text2("", 24, 540, 470, 40, Theme.Small, Theme.Muted);
            send = new FlatButton("Send report", true) { Location = new Point(Theme.Px(696) - Theme.Px(170), Theme.Px(584)), Width = Theme.Px(170) };
            send.Click += (s, e) => Send();
            var cancel = new FlatButton("Cancel") { Location = new Point(send.Left - Theme.Px(10) - Theme.Px(110), Theme.Px(584)), Width = Theme.Px(110) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(send); Body.Controls.Add(cancel);
            token = GitHubReporter.CanSignIn ? GitHubReporter.Token : null;
            ShowAccount();
            if (token != null) new System.Threading.Thread(() => { string l = GitHubReporter.Login(token); try { BeginInvoke(new Action(() => { login = l; if (l == null) token = null; ShowAccount(); })); } catch { } }) { IsBackground = true }.Start();
        }

        TextBox Box(int x, int y, int w, int h, bool multi, string hint)
        {
            var t = new TextBox { Location = new Point(Theme.Px(x), Theme.Px(y)), Size = new Size(Theme.Px(w), Theme.Px(h)), Multiline = multi, ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None,
                BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
            Theme.Help(t, hint);
            Body.Controls.Add(t);
            return t;
        }

        void ShowAccount()
        {
            if (!GitHubReporter.CanSignIn) { account.Text = "Send opens GitHub's report form in your browser with all of this filled in (a free GitHub account)."; signIn.Visible = false; return; }
            account.Text = token == null ? "Sign in with your GitHub account (free) once - the report goes up from here, under your name." : "Signed in as " + (login ?? "...") + " on GitHub.";
            signIn.Text = token == null ? "Sign in with GitHub" : "Sign out";
        }

        void AddPictures()
        {
            using (var d = new OpenFileDialog { Title = "Pictures for the report", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) if (!images.Contains(f)) images.Add(f);
            pics.Text = images.Count == 0 ? "No pictures - screenshots help a lot." : images.Count + " picture" + (images.Count == 1 ? "" : "s") + ": " + string.Join(", ", images.Select(Path.GetFileName));
        }

        volatile bool waiting;

        void SignIn()
        {
            if (token != null) { GitHubReporter.SignOut(); token = login = null; ShowAccount(); return; }
            if (waiting) return;
            GitHubReporter.DeviceCode c;
            try { c = GitHubReporter.Start(); }
            catch (Exception ex) { status.Text = "Couldn't reach GitHub: " + ex.Message; return; }
            waiting = true;
            try { Clipboard.SetText(c.Code); } catch { }
            status.Text = "On the GitHub page that opened, enter the code " + c.Code + " (it's copied) and allow StreamLite. Waiting...";
            Updater.OpenUrl(c.Url);
            new System.Threading.Thread(() =>
            {
                string t = GitHubReporter.Wait(c, () => IsDisposed);
                string l = t != null ? GitHubReporter.Login(t) : null;
                try { BeginInvoke(new Action(() => { waiting = false; token = t; login = l; status.Text = t == null ? "Not signed in (cancelled or the code ran out) - try again." : ""; ShowAccount(); Activate(); })); } catch { }
            }) { IsBackground = true, Name = "GitHub sign-in" }.Start();
        }

        string Body1()
        {
            var b = new System.Text.StringBuilder();
            b.AppendLine("### Kind").AppendLine(Kinds[kind.SelectedIndex]).AppendLine();
            b.AppendLine("### Which part?").AppendLine(Areas[area.SelectedIndex]).AppendLine();
            b.AppendLine("### What happened?").AppendLine(what.Text.Trim()).AppendLine();
            if (steps.Text.Trim().Length > 0) b.AppendLine("### How can we make it happen?").AppendLine(steps.Text.Trim()).AppendLine();
            b.AppendLine("### StreamLite version").AppendLine(Program.Version.ToString()).AppendLine();
            b.AppendLine("### System").AppendLine(Updater.SystemDescription()).AppendLine();
            if (details.Checked)
            {
                string d = null; try { d = Diagnostics.Report(core); } catch { }
                if (!string.IsNullOrEmpty(d)) b.AppendLine("### Details from StreamLite").AppendLine("```").AppendLine(d.Length > 50000 ? d.Substring(0, 50000) : d).AppendLine("```").AppendLine();
            }
            if (images.Count > 0) b.AppendLine("_" + images.Count + " picture(s) to come in a comment._");
            b.AppendLine().AppendLine("<sub>Sent from StreamLite's Report a bug.</sub>");
            return b.ToString();
        }

        void Send()
        {
            if (title.Text.Trim().Length < 4 || what.Text.Trim().Length < 4) { status.Text = "A title and what happened, please."; return; }
            string t = (kind.SelectedIndex == 4 ? "[Idea] " : "[Bug] ") + title.Text.Trim();
            if (token == null)
            {
                if (GitHubReporter.CanSignIn) { status.Text = "Sign in with GitHub first (or the report form opens in your browser if you'd rather)."; if (!signIn.Focused) signIn.Focus(); return; }
                // GitHub's form, every field filled in from here (its fields: area, what, steps); pictures are dragged in there.
                Updater.OpenUrl(Updater.BugReportUrl(Program.Version.ToString()) + "&title=" + Uri.EscapeDataString(t) + "&area=" + Uri.EscapeDataString(Areas[area.SelectedIndex]) +
                    "&what=" + Uri.EscapeDataString("(" + Kinds[kind.SelectedIndex] + ") " + what.Text.Trim()) + (steps.Text.Trim().Length > 0 ? "&steps=" + Uri.EscapeDataString(steps.Text.Trim()) : ""));
                if (images.Count > 0) try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + images[0] + "\""); } catch { }
                DialogResult = DialogResult.OK; Close(); return;
            }
            send.Enabled = false; status.Text = "Sending...";
            string body = Body1(), label = kind.SelectedIndex == 4 ? "enhancement" : "bug", tok = token;
            new System.Threading.Thread(() =>
            {
                string url = null, err = null;
                try { url = GitHubReporter.Post(tok, t, body, label); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        send.Enabled = true;
                        if (err != null) { status.Text = "GitHub said: " + err + (GitHubReporter.Token == null ? " - sign in again." : ""); token = GitHubReporter.Token; ShowAccount(); return; }
                        try { HealthMonitor.LogLine("Bug report sent: " + url); } catch { }
                        string msg = "Sent - thank you! It's " + url + ".";
                        if (images.Count > 0) msg += "\n\nGitHub doesn't let apps upload pictures: the report opens in your browser, and the folder with your pictures too - drag them into a comment there.";
                        DarkMessage.Show(this, msg, "Report a bug", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        if (images.Count > 0) { Updater.OpenUrl(url + "#issuecomment-new"); try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + images[0] + "\""); } catch { } }
                        DialogResult = DialogResult.OK; Close();
                    }));
                }
                catch { }
            }) { IsBackground = true, Name = "bug report" }.Start();
        }
    }
}
