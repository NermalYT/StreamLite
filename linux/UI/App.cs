using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using StreamLite.Linux;

namespace StreamLite.UI
{
    internal sealed class App : Application
    {
        public static bool StartMinimized;
        public static Core Core;
        public static WindowIcon Icon;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
            Resources["SystemAccentColor"] = Palette.Accent;
            using (var s = typeof(App).Assembly.GetManifestResourceStream("streamlite.png")) if (s != null) Icon = new WindowIcon(s);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var d = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (d != null)
            {
                Updater.Cleanup();
                Core = new Core();
                Core.Followed += msg => Notify("StreamLite", msg);
                Core.Start();
                var w = new MainWindow(Core) { Icon = Icon };
                Window = w; Lifetime = d;
                if (!StartMinimized) w.Show();
                Program.ListenForShow(() => Dispatcher.UIThread.Post(() => { w.Show(); w.WindowState = WindowState.Normal; w.Activate(); }));
                SetupTray(w, d);
                d.Exit += (s, e) => Core.Dispose();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Core.Dispose(); } catch { } };
                if (Core.S.UpdateOnStartup) Task.Delay(15000).ContinueWith(_ => CheckForUpdates(false));
            }
            base.OnFrameworkInitializationCompleted();
        }

        void SetupTray(MainWindow w, IClassicDesktopStyleApplicationLifetime d)
        {
            try
            {
                var menu = new NativeMenu();
                var open = new NativeMenuItem("Open StreamLite"); open.Click += (s, e) => { w.Show(); w.Activate(); };
                var update = new NativeMenuItem("Check for updates"); update.Click += (s, e) => { w.Show(); w.Activate(); CheckForUpdates(true); };
                var bug = new NativeMenuItem("Report a bug..."); bug.Click += (s, e) => ReportBug();
                var quit = new NativeMenuItem("Quit (stops the clean mic and 3D audio)"); quit.Click += (s, e) => d.Shutdown();
                menu.Items.Add(open); menu.Items.Add(new NativeMenuItemSeparator());
                menu.Items.Add(update); menu.Items.Add(bug); menu.Items.Add(quit);
                var tray = new TrayIcon { Icon = Icon, ToolTipText = "StreamLite", Menu = menu, IsVisible = true };
                tray.Clicked += (s, e) => { w.Show(); w.Activate(); };
                TrayIcon.SetIcons(this, new TrayIcons { tray });
            }
            catch { }   // no tray on this desktop: starting StreamLite again opens the window
        }

        /// <summary>Opens a web page or file with the desktop's default app.</summary>
        public static void Open(string url) { Updater.OpenUrl(url); }

        /// <summary>Opens GitHub's bug report form with the version and distro filled in.</summary>
        public static void ReportBug() { Open(Updater.BugReportUrl(Program.Version)); }

        static MainWindow Window;
        static IClassicDesktopStyleApplicationLifetime Lifetime;
        static int checking;

        /// <summary>A desktop notification (notify-send), if the desktop has one.</summary>
        static void Notify(string title, string text)
        {
            try { var p = new ProcessStartInfo("notify-send") { UseShellExecute = false }; foreach (var a in new[] { "-a", "StreamLite", title, text }) p.ArgumentList.Add(a); Process.Start(p); } catch { }
        }

        /// <summary>
        /// Asks GitHub for the newest release; a newer one is downloaded, checked and installed the way StreamLite was
        /// installed, then StreamLite restarts. manual = Settings or the tray: asks first and reports "up to date" too.
        /// </summary>
        public static void CheckForUpdates(bool manual)
        {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            Updater.Status = "Checking for updates...";
            Task.Run(async () =>
            {
                try
                {
                    var r = Updater.Latest();
                    if (!Updater.IsNewer(r, Updater.ParseVersion(Program.Version))) { Updater.Status = "StreamLite " + Program.Version + " is the newest version."; return; }
                    string kind = Updater.InstallKind, asset = kind == null ? null : Updater.AssetFor(r, kind);
                    if (asset == null)
                    {
                        Updater.Status = "StreamLite " + r.Version + " is out - " + (kind == "arch" ? "rebuild the package (PKGBUILD) to update." : "download it from GitHub.");
                        Notify("StreamLite " + r.Version + " is out", Updater.Status);
                        if (manual) Open(r.Page);
                        return;
                    }
                    if (!manual && Updater.WasDeclined(r.Version)) { Updater.Status = "StreamLite " + r.Version + " is out (Check now installs it)."; return; }
                    if (manual && !await Dispatcher.UIThread.InvokeAsync(() => Ask("Update StreamLite", "StreamLite " + r.Version + " is out (you have " + Program.Version + "). Install it now? StreamLite restarts by itself and your mic drops out for a few seconds." + (kind == "deb" || kind == "rpm" ? " Your system asks for your password to install the package." : ""), "Update now")))
                    { Updater.Status = "StreamLite " + r.Version + " is out."; return; }
                    Updater.Status = "Downloading StreamLite " + r.Version + "...";
                    Notify("Updating StreamLite to " + r.Version, "StreamLite restarts by itself in a moment.");
                    string file = Updater.Download(r, asset);
                    Updater.Status = "Installing StreamLite " + r.Version + "...";
                    bool show = await Dispatcher.UIThread.InvokeAsync(() => Window.IsVisible);
                    string msg;
                    if (Updater.ApplyLinux(kind, file, show, true, out msg)) Dispatcher.UIThread.Post(() => Lifetime.Shutdown());
                    else { Updater.Status = msg; Notify("StreamLite " + r.Version, msg); }
                }
                catch (Exception ex) { Updater.Status = "Couldn't check for updates: " + ex.Message; }
                finally { Interlocked.Exchange(ref checking, 0); }
            });
        }

        /// <summary>A small yes/no dialog over the main window.</summary>
        static async Task<bool> Ask(string title, string text, string ok)
        {
            bool result = false;
            var dlg = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Palette.Bg, Icon = Icon };
            var yes = new Button { Content = ok }; yes.Classes.Add("accent");
            var no = new Button { Content = "Later" };
            yes.Click += (s, e) => { result = true; dlg.Close(); };
            no.Click += (s, e) => dlg.Close();
            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            buttons.Children.Add(no); buttons.Children.Add(yes);
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text });
            body.Children.Add(buttons);
            dlg.Content = body;
            if (!Window.IsVisible) Window.Show();
            await dlg.ShowDialog(Window);
            return result;
        }
    }

    internal static class Palette
    {
        public static readonly Color Accent = Color.FromRgb(76, 194, 122);
        public static readonly IBrush Bg = new SolidColorBrush(Color.FromRgb(23, 26, 30));
        public static readonly IBrush Card = new SolidColorBrush(Color.FromRgb(30, 34, 39));
        public static readonly IBrush Text = new SolidColorBrush(Color.FromRgb(230, 233, 236));
        public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(140, 148, 156));
        public static readonly IBrush Good = new SolidColorBrush(Accent);
        public static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(240, 180, 80));
    }
}
