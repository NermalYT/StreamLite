using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Avalonia;
using StreamLite.Audio;
using StreamLite.Interop;
using StreamLite.Linux;

namespace StreamLite
{
    internal static class Program
    {
        public const string Version = "2.7.0";
        const string PipeName = "streamlite-show";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--version")) { Console.WriteLine("StreamLite " + Version); return 0; }
            if (args.Contains("--selftest")) return SelfTest.Run(args);
            if (args.Contains("--check-3d")) return Diag.Check3D();
            if (args.Contains("--update")) return UpdateFromTerminal();
            if (args.Contains("--details")) { Console.Write(Diagnostics.Report(null)); return 0; }
            if (args.Contains("--report-bug")) { Console.WriteLine(Updater.BugReportUrl(Version)); Updater.OpenUrl(Updater.BugReportUrl(Version)); return 0; }
            if (args.Contains("--render")) return UI.Render.Pages(args[Array.IndexOf(args, "--render") + 1]);
            if (args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine("StreamLite " + Version + " - clean mic, 3D game audio and headphone EQ for PipeWire / PulseAudio\n\n" +
                    "  streamlite              open StreamLite\n  streamlite --minimized  start in the background (tray)\n" +
                    "  streamlite --selftest   check the audio system and StreamLite's devices\n" +
                    "  streamlite --update     install the newest version from GitHub\n  streamlite --report-bug open the bug report form\n" +
                    "  streamlite --details    your audio devices and setup, to paste into a bug report\n  streamlite --version");
                return 0;
            }
            // One StreamLite at a time: a second start just shows the running one.
            if (TellRunningToShow()) return 0;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            UI.App.StartMinimized = args.Contains("--minimized");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<UI.App>().UsePlatformDetect().WithInterFont().With(BundledFont).LogToTrace();
        }

        /// <summary>The bundled Inter is the default font, so StreamLite also starts on systems without any fonts installed.</summary>
        public static readonly Avalonia.Media.FontManagerOptions BundledFont = new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" };

        /// <summary>Crashes go to ~/.config/StreamLite/error.log (the bug report form asks for it).</summary>
        public static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), DateTime.Now + "  " + Version + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>"streamlite --update": the same update as at startup, from a terminal (a running StreamLite keeps the old version until it restarts).</summary>
        static int UpdateFromTerminal()
        {
            try
            {
                var r = Updater.Latest();
                if (!Updater.IsNewer(r, Updater.ParseVersion(Version))) { Console.WriteLine("StreamLite " + Version + " is the newest version."); return 0; }
                string kind = Updater.InstallKind, asset = kind == null ? null : Updater.AssetFor(r, kind);
                Console.WriteLine("StreamLite " + r.Version + " is out: " + r.Page);
                if (asset == null) { Console.WriteLine(kind == "arch" ? "Rebuild the package (PKGBUILD) to update." : "This copy wasn't installed by a StreamLite package: download the new version from the page above."); return 0; }
                Console.WriteLine("Downloading " + asset + "...");
                string file = Updater.Download(r, asset), msg;
                if (!Updater.ApplyLinux(kind, file, false, false, out msg)) { Console.WriteLine(msg); return 1; }
                Console.WriteLine("Updated to " + r.Version + ". Restart StreamLite to use it.");
                return 0;
            }
            catch (Exception e) { Console.WriteLine("Update failed: " + e.Message); return 1; }
        }

        static bool TellRunningToShow()
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(300);
                    c.WriteByte(1);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Listens for "show yourself" from a second start.</summary>
        public static void ListenForShow(Action show)
        {
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            if (s.ReadByte() >= 0) show();
                        }
                    }
                    catch { Thread.Sleep(1000); }
                }
            }) { IsBackground = true, Name = "show" }.Start();
        }
    }
}
