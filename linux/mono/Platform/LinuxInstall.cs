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
    /// Uninstalling on Linux: StreamLite asks in its own windows (as on Windows), then the installer's uninstall script
    /// removes the program, its menu entry and launch at login - and your settings only if you said so (kept, Scan for
    /// settings brings them back). A system-wide install asks for your password through the desktop.
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
                UI.DarkMessage.Show("This copy of StreamLite wasn't installed by its installer, so there's nothing to uninstall - delete its folder:\n" + dir, "Uninstall StreamLite");
                return;
            }
            if (UI.DarkMessage.Show("Remove StreamLite?\n\nIts sound devices go and your default devices come back. Its menu entry and launch at login go too.",
                    "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            // 4.4: keep, pick and save, or delete (the script keeps them; what goes goes here, after it).
            var settings = UI.UninstallSettings.Ask(_ => { });
            bool wipe = settings.Delete;
            string output = "";
            int code = -1;
            try
            {
                string err;
                output = Interop.Shell.Run("bash", new[] { script, "--keep-settings" }, 180000, out code, out err) + err;
            }
            catch (Exception ex) { output = ex.Message; }
            if (code == 0) UI.UninstallSettings.Finish(settings, _ => { }, f => { try { Directory.Delete(f, true); } catch { } });
            if (code == 0 && !Directory.Exists(dir))
            {
                // 4.3.2: a part that still needs the password, what came with it from the distro, and the commands for both.
                int more = output.IndexOf("One part needs", StringComparison.Ordinal);
                if (more < 0) more = output.IndexOf("These came with it", StringComparison.Ordinal);
                UI.DarkMessage.Show("StreamLite was removed." + (settings.Saved != null ? "\n\nThe settings you picked are in " + settings.Saved + "." : wipe ? "" : "\n\nYour settings are kept in ~/.config/StreamLite.") + (more >= 0 ? "\n\n" + output.Substring(more).Trim() : ""), "Uninstall StreamLite");
            }
            else
                UI.DarkMessage.Show("StreamLite couldn't be removed completely:\n\n" + output.Trim() + "\n\nRun this in a terminal to finish:\nbash " + script, "Uninstall StreamLite");
        }
    }
}
