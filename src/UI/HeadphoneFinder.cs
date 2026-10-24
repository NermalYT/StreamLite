using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// "Find my headphones": searches AutoEq's list of measured headphones and downloads the correction for the one
    /// you pick. Starts with a search made from your output device's name.
    /// </summary>
    internal sealed class HeadphoneFinder : DarkDialog
    {
        readonly TextBox search;
        readonly ListBox results;
        readonly Label status;
        readonly FlatButton use;
        List<AutoEq.Entry> index;
        public string ChosenName;
        public List<EqFilter> ChosenFilters;

        public HeadphoneFinder(string deviceName) : base("Find my headphones", 560, 430)
        {
            Text2("Type your headphones' or earbuds' model. Corrections come from AutoEq: real measurements of thousands of models, each with an EQ that makes it sound neutral. Every game, music and chat profile is then layered on top.",
                22, 14, 516, 54, Theme.Small, Theme.Muted);
            search = new TextBox
            {
                Location = new Point(Theme.Px(22), Theme.Px(74)), Width = Theme.Px(516), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = AutoEq.GuessQuery(deviceName)
            };
            Body.Controls.Add(search);
            results = new ListBox
            {
                Location = new Point(Theme.Px(22), Theme.Px(108)), Size = new Size(Theme.Px(516), Theme.Px(230)), Font = Theme.Body,
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false
            };
            Body.Controls.Add(results);
            status = Text2("Loading the list of headphones...", 22, 346, 516, 36, Theme.Small, Theme.Muted);
            use = new FlatButton("Use this correction", true) { Enabled = false };
            use.Location = new Point(Theme.Px(538) - use.Width, Theme.Px(388));
            Body.Controls.Add(use);
            var cancel = new FlatButton("Cancel");
            cancel.Location = new Point(use.Left - Theme.Px(10) - cancel.Width, Theme.Px(388));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancel);
            CancelButton = cancel;

            var debounce = new System.Windows.Forms.Timer { Interval = 250 };
            debounce.Tick += (s, e) => { debounce.Stop(); Filter(); };
            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            results.SelectedIndexChanged += (s, e) => use.Enabled = results.SelectedItem is AutoEq.Entry;
            results.DoubleClick += (s, e) => { if (use.Enabled) Pick(); };
            use.Click += (s, e) => Pick();
            Shown += (s, e) => LoadIndex();
        }

        void LoadIndex()
        {
            new Thread(() =>
            {
                string err = null; List<AutoEq.Entry> list = null;
                try { list = AutoEq.Index(AppSettings.Folder); }
                catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (err != null) { status.Text = "Couldn't download the list (" + err + "). Check your internet connection and try again."; status.ForeColor = Theme.Warn; return; }
                    index = list;
                    Filter();
                }));
            }) { IsBackground = true }.Start();
        }

        void Filter()
        {
            if (index == null) return;
            string used; bool relaxed;
            var found = AutoEq.SearchBest(index, search.Text, out used, out relaxed);
            results.BeginUpdate();
            results.Items.Clear();
            foreach (var e in found) results.Items.Add(e);
            results.EndUpdate();
            if (found.Count > 0) results.SelectedIndex = 0;
            status.ForeColor = Theme.Muted;
            status.Text = search.Text.Trim().Length == 0 ? index.Count + " models. Type a brand or model, e.g. \"HyperX Cloud II\" or \"AirPods Pro\"."
                : found.Count == 0 ? "Nothing found - try just the model (e.g. \"cloud ii\"). Not every model is measured; a similar one from the same brand is usually close."
                : (relaxed ? "Your exact model isn't measured - showing \"" + used + "\". " : found.Count + " found. ") +
                  "The first one uses the most trusted measurement; pick another if your exact version is listed.";
        }

        void Pick()
        {
            var e = results.SelectedItem as AutoEq.Entry;
            if (e == null) return;
            use.Enabled = false; status.Text = "Downloading the correction for " + e.Name + "...";
            new Thread(() =>
            {
                string err = null; List<EqFilter> filters = null; double preamp;
                try { filters = AutoEq.Download(e, out preamp); }
                catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (err != null) { status.Text = "Download failed: " + err; status.ForeColor = Theme.Warn; use.Enabled = true; return; }
                    ChosenName = e.Name + " to neutral (AutoEq, " + e.Source + ")";
                    ChosenFilters = filters;
                    DialogResult = DialogResult.OK; Close();
                }));
            }) { IsBackground = true }.Start();
        }
    }
}
