using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace StreamLite
{
    /// <summary>
    /// Linux: StreamLite never needs root to run - PipeWire / PulseAudio let any app make devices and route sound - so
    /// there's no administrator task (Run as administrator is a Windows setting and isn't offered on Linux).
    /// </summary>
    internal static class Elevation
    {
        public const string TaskName = "StreamLite (Administrator)";
        public static bool IsElevated { get { return false; } }
        public static bool TaskExists() { return false; }
        public static bool TaskExists(string name) { return false; }
        public static bool TaskRuns(string exe) { return false; }
        public static void DeleteTask(string name) { }
        public static bool RunTask(string name, bool minimized) { return false; }
        public static string CreateTask(string exePath = null) { return CreateTask(exePath, null); }
        public static string CreateTask(string exePath, string sid) { return "not needed on Linux"; }
        public static string DeleteTask() { return null; }
        public static bool RunTask(bool minimized) { return false; }
        public static void RestartElevatedAfterExit(bool minimized = false) { }
        public static bool TakePendingMinimized() { return false; }
    }

    /// <summary>Linux: StreamLite makes its own devices (no virtual cable drivers to install).</summary>
    internal static class Prerequisites
    {
        public static bool VbCableInstalled { get { return true; } }
        public static bool HiFiCableInstalled { get { return true; } }
        public static int InstallAll(bool dryRun) { return 0; }
    }

    /// <summary>
    /// Uninstalling on Linux: the installer's own uninstall script (it removes the program, its menu entry and launch at
    /// login, and asks whether to keep your settings - keep them to bring them back later with Scan for settings).
    /// </summary>
    internal static class Uninstaller
    {
        public static void Register() { }

        public static void Start(string[] args)
        {
            string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            string script = Path.Combine(dir, "uninstall.sh");
            if (!File.Exists(script))
            {
                MessageBox.Show("This copy of StreamLite wasn't installed by its installer, so there's nothing to uninstall - delete its folder:\n" + dir, "Uninstall StreamLite");
                return;
            }
            // In a terminal so it can ask about your settings (and for your password, for a system-wide install).
            foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "kitty", "alacritty", "xterm" })
                if (Interop.Shell.Has(term))
                {
                    string a = term == "gnome-terminal" ? "-- bash " + Interop.Shell.Quote(script) : "-e bash " + Interop.Shell.Quote(script);
                    try { Process.Start(new ProcessStartInfo(term, a) { UseShellExecute = false }); Environment.Exit(0); } catch { }
                }
            MessageBox.Show("Run this in a terminal to uninstall StreamLite:\n\nbash " + script, "Uninstall StreamLite");
        }
    }
}
