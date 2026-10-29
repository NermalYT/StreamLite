using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// "Find my headphones": searches AutoEq's list of measured headphones - and, behind it (4.0.7), squig.link's
    /// reviewers' measurements, whose EQ StreamLite makes itself - and gets the correction for the one you pick. Starts
    /// with a search made from your output device's name. A model nobody has measured can be reported in one click.
    /// </summary>
    internal sealed class HeadphoneFinder : DarkDialog
    {
        readonly TextBox search;
        readonly ListBox results;
        readonly Label status;
        readonly FlatButton use;
        readonly string deviceName;
        List<AutoEq.Entry> index;
        bool squigLoading = true;
        public string ChosenName;
        public List<EqFilter> ChosenFilters;

        public HeadphoneFinder(string deviceName) : base("Find my headphones", 560, 430)
        {
            this.deviceName = deviceName;
            bool adapter = AutoEq.IsAdapter(deviceName);
            Text2((adapter ? Os.TheSystemCap + " only sees your wireless dongle (" + AutoEq.GuessQuery(deviceName) + "), not the headphones on it - type their model (e.g. \"cloud iii\"). " : "Type your headphones' or earbuds' model. ") +
                "EQs come from AutoEq (thousands of measured models) and squig.link's reviewers (thousands more, gaming headsets too - StreamLite makes their EQ). Every game, music and chat profile goes on top.",
                22, 14, 516, 54, Theme.Small, Theme.Muted);
            search = new TextBox
            {
                Location = new Point(Theme.Px(22), Theme.Px(74)), Width = Theme.Px(516), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = adapter ? "" : AutoEq.GuessQuery(deviceName)
            };
            Body.Controls.Add(search);
            results = new ListBox
            {
                Location = new Point(Theme.Px(22), Theme.Px(108)), Size = new Size(Theme.Px(516), Theme.Px(230)), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(44)
            };
            // Each result as a row (4.1): the model in bold, where its EQ comes from below it, the picked one in green.
            results.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var it = results.Items[e.Index] as AutoEq.Entry;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Color.FromArgb(34, 52, 40) : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + Theme.Px(6), Theme.Px(3), e.Bounds.Height - Theme.Px(12));
                var r = new Rectangle(e.Bounds.X + Theme.Px(14), e.Bounds.Y + Theme.Px(4), e.Bounds.Width - Theme.Px(20), Theme.Px(20));
                TextRenderer.DrawText(e.Graphics, it != null ? it.Name : results.Items[e.Index].ToString(), Theme.Bold, r, sel ? Theme.Accent : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                r.Offset(0, Theme.Px(19));
                TextRenderer.DrawText(e.Graphics, it != null ? it.Source : "", Theme.Small, r, Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (var p = new Pen(Theme.Track)) e.Graphics.DrawLine(p, e.Bounds.X + Theme.Px(10), e.Bounds.Bottom - 1, e.Bounds.Right - Theme.Px(10), e.Bounds.Bottom - 1);
            };
            Body.Controls.Add(results);
            status = Text2("Loading the list of headphones...", 22, 346, 516, 36, Theme.Small, Theme.Muted);
            use = new FlatButton("Use this EQ", true) { Enabled = false };
            use.Location = new Point(Theme.Px(538) - use.Width, Theme.Px(388));
            Body.Controls.Add(use);
            var cancel = new FlatButton("Cancel");
            cancel.Location = new Point(use.Left - Theme.Px(10) - cancel.Width, Theme.Px(388));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancel);
            CancelButton = cancel;
            // Not in either list: one click opens a ready-made report (you send it - nothing goes anywhere by itself).
            var report = new FlatButton("Missing? Report it") { Location = new Point(Theme.Px(22), Theme.Px(388)) };
            report.Width = Theme.Px(150);
            Theme.Help(report, "Opens a ready-to-send report on StreamLite's GitHub page with the model you typed and the name " + Os.TheSystem + " shows for your headphones, so it can be added. You send it yourself.");
            report.Click += (s, e) => Report();
            Body.Controls.Add(report);

            var debounce = new System.Windows.Forms.Timer { Interval = 250 };
            debounce.Tick += (s, e) => { debounce.Stop(); Filter(); };
            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            results.SelectedIndexChanged += (s, e) => use.Enabled = results.SelectedItem is AutoEq.Entry;
            results.DoubleClick += (s, e) => { if (use.Enabled) Pick(); };
            use.Click += (s, e) => Pick();
            Shown += (s, e) => LoadIndex();
        }

        /// <summary>The address of a ready-made report for a missing model (GitHub's new-issue page, filled in).</summary>
        internal static string ReportUrl(string model, string deviceName)
        {
            string title = "Missing headphones: " + (string.IsNullOrWhiteSpace(model) ? deviceName : model.Trim());
            string body = "Model: " + (string.IsNullOrWhiteSpace(model) ? "(please type it here)" : model.Trim()) + "\n" +
                Os.TheSystemCap + " calls them: " + (deviceName ?? "") + "\n" +
                "StreamLite " + Program.Version + "\n\n" +
                "Not in AutoEq's or squig.link's lists. A link to a measurement (or a review with a frequency response graph) helps.";
            return "https://github.com/" + Updater.Repo + "/issues/new?labels=missing-headphones&title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
        }

        void Report()
        {
            try { Process.Start(new ProcessStartInfo(ReportUrl(search.Text, deviceName)) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Couldn't open your browser: " + ex.Message; status.ForeColor = Theme.Warn; }
        }

        void LoadIndex()
        {
            new Thread(() =>
            {
                string err = null; List<AutoEq.Entry> list = null;
                try { list = AutoEq.Index(AppSettings.Folder); }
                catch (Exception ex) { err = ex.Message; list = new List<AutoEq.Entry>(AutoEq.Builtins()); }
                if (IsDisposed) return;
                BeginInvoke(new Action(() => { index = list; Filter(); if (err != null) { status.Text = "AutoEq's list didn't download (" + err + ") - trying squig.link..."; status.ForeColor = Theme.Warn; } }));
                // squig.link's measurements after it (a few seconds the first time, then cached for two weeks).
                List<AutoEq.Entry> extra = null;
                try { extra = SquigLink.Extra(SquigLink.Index(AppSettings.Folder), list, AppSettings.Folder); } catch { }
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    squigLoading = false;
                    if (extra != null) index = list.Concat(extra).ToList();
                    else if (err != null) { status.Text = "Couldn't download the lists (" + err + "). Check your internet connection and try again."; status.ForeColor = Theme.Warn; return; }
                    Filter();
                }));
            }) { IsBackground = true }.Start();
        }

        void Filter()
        {
            if (index == null) return;
            string used; bool relaxed;
            var found = AutoEq.SearchBest(index, search.Text, out used, out relaxed);
            var keep = results.SelectedItem as AutoEq.Entry;
            results.BeginUpdate();
            results.Items.Clear();
            foreach (var e in found) results.Items.Add(e);
            // A model nobody has measured yet can still be yours: the closest measured ones are above, and this plays it
            // as it is (every game profile still applies on top).
            string q = search.Text.Trim();
            if (q.Length > 0 && (relaxed || found.Count == 0))
                results.Items.Add(new AutoEq.Entry { Name = q, Source = "not measured yet - use it without a correction", NoCorrection = true });
            results.EndUpdate();
            int again = keep != null ? found.FindIndex(e => e.Name == keep.Name && e.Source == keep.Source) : -1;
            if (again >= 0) results.SelectedIndex = again; else if (found.Count > 0) results.SelectedIndex = 0;
            status.ForeColor = Theme.Muted;
            string more = squigLoading ? "  Also searching squig.link's measurements..." : "";
            status.Text = (search.Text.Trim().Length == 0 ? index.Count + " models. Type a brand or model, e.g. \"HyperX Cloud III\" or \"AirPods Pro\"."
                : found.Count == 0 ? "Not measured yet - pick it without a correction (last line), or report it (bottom left)."
                : (relaxed ? "Your exact model isn't measured yet - the closest (\"" + used + "\") are listed; pick yours without a correction (last line) or report it. " : found.Count + " found. ") +
                  "The first one uses the most trusted measurement.") + more;
        }

        void Pick()
        {
            var e = results.SelectedItem as AutoEq.Entry;
            if (e == null) return;
            if (e.NoCorrection || e.Builtin != null)
            {
                ChosenName = e.NoCorrection ? e.Name + " (not measured yet - no correction)" : e.Name + " to neutral (" + e.Source + ")";
                double p0; ChosenFilters = AutoEq.Download(e, out p0);
                DialogResult = DialogResult.OK; Close();
                return;
            }
            use.Enabled = false; status.Text = e.Fetch != null ? "Downloading " + e.Name + "'s measurement and making its EQ..." : "Downloading the EQ for " + e.Name + "...";
            new Thread(() =>
            {
                string err = null; List<EqFilter> filters = null; double preamp;
                try { filters = AutoEq.Download(e, out preamp); }
                catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (err != null) { status.Text = "Download failed: " + err; status.ForeColor = Theme.Warn; use.Enabled = true; return; }
                    ChosenName = Named(e);
                    ChosenFilters = filters;
                    DialogResult = DialogResult.OK; Close();
                }));
            }) { IsBackground = true }.Start();
        }

        /// <summary>How a picked EQ is named on the Headphones page.</summary>
        internal static string Named(AutoEq.Entry e)
        {
            return e.Fetch != null ? e.Name + " to neutral (" + e.Source + ")" : e.Name + " to neutral (AutoEq, " + e.Source + ")";
        }
    }
}
