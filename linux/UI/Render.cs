using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StreamLite.Linux;

namespace StreamLite.UI
{
    /// <summary>
    /// "streamlite --render &lt;dir&gt;": draws every page off-screen (no window, no audio) to PNGs - for checking the
    /// layout and for the manual's screenshots. Uses a throwaway settings folder.
    /// </summary>
    internal static class Render
    {
        public static int Pages(string dir)
        {
            Environment.SetEnvironmentVariable("STREAMLITE_DATA", Path.Combine(Path.GetTempPath(), "streamlite-render"));
            Directory.CreateDirectory(dir);
            AppBuilder.Configure<HeadlessApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
            var core = new Core();                 // not started: no devices are created
            var w = new MainWindow(core) { Width = 1000, Height = 780 };
            w.Show();
            var root = (DockPanel)w.Content;
            var nav = System.Linq.Enumerable.First(System.Linq.Enumerable.OfType<ListBox>(((DockPanel)root.Children[0]).Children));
            int i = 0;
            foreach (var kv in w.Pages)
            {
                nav.SelectedIndex = i++;           // shows the page and highlights it in the sidebar
                Dispatcher.UIThread.RunJobs();
                var frame = w.CaptureRenderedFrame();
                string file = Path.Combine(dir, kv.Key.Replace(" ", "").ToLowerInvariant() + ".png");
                frame.Save(file);
                Console.WriteLine("  wrote " + file);
            }
            w.QuitOnClose = true;
            return 0;
        }

        sealed class HeadlessApp : Application
        {
            public override void Initialize()
            {
                Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                Resources["SystemAccentColor"] = Palette.Accent;
            }
        }
    }
}
