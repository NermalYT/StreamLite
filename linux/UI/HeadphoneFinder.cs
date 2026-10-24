using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>"Find my headphones": search AutoEq's measured headphones and download the correction for yours.</summary>
    internal sealed class HeadphoneFinder : Window
    {
        readonly TextBox search;
        readonly ListBox results = new ListBox { Height = 260 };
        readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted };
        readonly Button use = new Button { Content = "Use this correction", IsEnabled = false };
        List<AutoEq.Entry> index;
        public string ChosenName;
        public List<EqFilter> ChosenFilters;

        public HeadphoneFinder(string deviceName)
        {
            Title = "Find my headphones"; Width = 560; Height = 470; Background = Palette.Bg; Icon = App.Icon;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            search = new TextBox { Text = AutoEq.GuessQuery(deviceName), Watermark = "Brand or model, e.g. HyperX Cloud II" };
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (s, e) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel); buttons.Children.Add(use);
            var p = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
            p.Children.Add(new TextBlock { Text = "Type your headphones' or earbuds' model. Corrections come from AutoEq: real measurements of thousands of models, each with an EQ that makes it sound neutral.", TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted });
            p.Children.Add(search); p.Children.Add(results); p.Children.Add(status); p.Children.Add(buttons);
            Content = p;
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += (s, e) => { debounce.Stop(); Filter(); };
            search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            results.SelectionChanged += (s, e) => use.IsEnabled = results.SelectedItem is AutoEq.Entry;
            results.DoubleTapped += (s, e) => { if (use.IsEnabled) Pick(); };
            use.Click += (s, e) => Pick();
            Opened += async (s, e) =>
            {
                status.Text = "Loading the list of headphones...";
                try { index = await Task.Run(() => AutoEq.Index(AppSettings.Folder)); Filter(); }
                catch (Exception ex) { status.Text = "Couldn't download the list (" + ex.Message + "). Check your internet connection."; status.Foreground = Palette.Warn; }
            };
        }

        void Filter()
        {
            if (index == null) return;
            var found = AutoEq.Search(index, search.Text ?? "");
            results.ItemsSource = found;
            if (found.Count > 0) results.SelectedIndex = 0;
            status.Foreground = Palette.Muted;
            status.Text = (search.Text ?? "").Trim().Length == 0 ? index.Count + " models. Type a brand or model."
                : found.Count == 0 ? "Nothing found - try fewer words (just the model)."
                : found.Count + " found. The first one uses the most trusted measurement.";
        }

        async void Pick()
        {
            var e = results.SelectedItem as AutoEq.Entry;
            if (e == null) return;
            use.IsEnabled = false; status.Text = "Downloading the correction for " + e.Name + "...";
            try
            {
                double pre;
                ChosenFilters = await Task.Run(() => AutoEq.Download(e, out pre));
                ChosenName = e.Name + " to neutral (AutoEq, " + e.Source + ")";
                Close();
            }
            catch (Exception ex) { status.Text = "Download failed: " + ex.Message; status.Foreground = Palette.Warn; use.IsEnabled = true; }
        }
    }
}
