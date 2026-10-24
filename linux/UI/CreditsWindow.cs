using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace StreamLite.UI
{
    /// <summary>
    /// Credits: every project, AI model and dataset StreamLite uses, with links to its source and model files, for this
    /// version or any earlier release (from ../src/Credits.cs, shared with Windows).
    /// </summary>
    internal sealed class CreditsWindow : Window
    {
        readonly StackPanel list = new StackPanel { Spacing = 4, Margin = new Thickness(20, 0, 20, 12) };
        readonly TextBlock change = new TextBlock { Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        public CreditsWindow()
        {
            Title = "Credits - StreamLite";
            Width = 720; Height = 680; MinWidth = 520; MinHeight = 400;
            Background = Palette.Bg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            string mine = Credits.Release(Program.Version);
            var versions = Enumerable.Reverse(Credits.Versions).ToArray();
            var pick = new ComboBox { Width = 260 };
            foreach (var v in versions) pick.Items.Add("StreamLite " + v + (v == mine ? " (this version)" : ""));
            pick.SelectionChanged += (s, e) => Fill(versions[Math.Max(0, pick.SelectedIndex)]);

            var top = new StackPanel { Margin = new Thickness(20, 18, 20, 8), Spacing = 10 };
            top.Children.Add(new TextBlock { Text = "StreamLite stands on a lot of freely shared work. Thank you to everyone below!", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = "Credits for", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text });
            row.Children.Add(pick);
            top.Children.Add(row);
            top.Children.Add(change);

            var github = new Button { Content = "Credits on GitHub" };
            github.Click += (s, e) => App.Open("https://github.com/NermalYT/StreamLite/blob/main/CREDITS.md");
            var close = new Button { Content = "Close" }; close.Classes.Add("accent");
            close.Click += (s, e) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 16) };
            buttons.Children.Add(github); buttons.Children.Add(close);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = list });
            Content = root;
            pick.SelectedIndex = Math.Max(0, Array.IndexOf(versions, mine));
        }

        void Fill(string version)
        {
            string note;
            change.Text = Credits.Changes.TryGetValue(version, out note) ? "What changed in " + version + ": " + note : "";
            list.Children.Clear();
            var items = Credits.For(version);
            foreach (var g in Credits.Groups)
            {
                var group = items.Where(c => c.Group == g).ToList();
                if (group.Count == 0) continue;
                list.Children.Add(new TextBlock { Text = g, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Good, Margin = new Thickness(0, 12, 0, 2) });
                foreach (var c in group)
                {
                    var head = new WrapPanel { Orientation = Orientation.Horizontal };
                    head.Children.Add(Link(c.Name, c.Url, FontWeight.SemiBold));
                    if (c.Platform != null) head.Children.Add(new TextBlock { Text = "  " + c.Platform, Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                    if (Credits.NewIn(c, version)) head.Children.Add(new TextBlock { Text = "  New in " + version, Foreground = Palette.Good, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                    if (c.File != null) { head.Children.Add(new TextBlock { Text = "   " }); head.Children.Add(Link("Model / data file", c.File, FontWeight.Normal)); }
                    list.Children.Add(head);
                    list.Children.Add(new TextBlock { Text = c.What + "\n" + c.Authors + "  -  " + c.License, Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
                }
            }
        }

        static Control Link(string text, string url, FontWeight weight)
        {
            var t = new TextBlock { Text = text, Foreground = Palette.Good, FontWeight = weight, Cursor = new Cursor(StandardCursorType.Hand), TextDecorations = TextDecorations.Underline };
            ToolTip.SetTip(t, url);
            t.PointerPressed += (s, e) => App.Open(url);
            return t;
        }
    }
}
