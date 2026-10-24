using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StreamLite
{
    /// <summary>
    /// One-time move from the app's old name (Broadcast Lite): settings folder, startup entry, the elevated task,
    /// and the old install folder and shortcuts. Every step is safe to run again.
    /// </summary>
    internal static class Migration
    {
        const string OldName = "Broadcast Lite", OldFolder = "BroadcastLite", OldTask = "Broadcast Lite (Administrator)";

        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string NewInstallExe { get { return Path.Combine(Local, "Programs", "StreamLite", "StreamLite.exe"); } }
        static string OldInstallDir { get { return Path.Combine(Local, "Programs", OldFolder); } }

        /// <summary>Copies the old settings folder if StreamLite has none yet.</summary>
        public static void Settings()
        {
            try
            {
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldFolder);
                if (!Directory.Exists(oldDir) || File.Exists(Path.Combine(AppSettings.Folder, "settings.json"))) return;
                Directory.CreateDirectory(AppSettings.Folder);
                foreach (var f in Directory.GetFiles(oldDir)) File.Copy(f, Path.Combine(AppSettings.Folder, Path.GetFileName(f)), true);
            }
            catch { }
        }

        /// <summary>Replaces the old "launch at login" entry with the new one.</summary>
        public static void Startup()
        {
            try
            {
                bool had;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    had = k != null && k.GetValue(OldName) != null;
                    if (had) k.DeleteValue(OldName, false);
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true))
                    if (k != null) k.DeleteValue(OldName, false);
                if (had) StartupRegistration.Set(true);
            }
            catch { }
        }

        /// <summary>
        /// Elevated only: re-creates the admin task under the new name, pointing at the new install, and removes the old
        /// one. Returns true when this copy (started from the old location) launched the new one and should exit.
        /// </summary>
        public static bool Task(bool minimized)
        {
            if (!Elevation.IsElevated || !Elevation.TaskExists(OldTask)) return false;
            string target = File.Exists(NewInstallExe) ? NewInstallExe : Application.ExecutablePath;
            if (Elevation.CreateTask(target) != null) return false;
            Elevation.DeleteTask(OldTask);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
            Elevation.RestartElevatedAfterExit(minimized);   // starts after a short delay, once this copy has exited
            return true;
        }

        /// <summary>Deletes the old install folder and shortcuts once StreamLite runs from its own folder.</summary>
        public static void Cleanup()
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(NewInstallExe), StringComparison.OrdinalIgnoreCase)) return;
                foreach (var lnk in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), OldName + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldName + ".lnk")
                })
                    if (File.Exists(lnk)) File.Delete(lnk);
                if (Directory.Exists(OldInstallDir) && !Process.GetProcessesByName(OldFolder).Any())
                    Directory.Delete(OldInstallDir, true);
                // The old settings folder was copied on first run; once StreamLite has its own, the copy is redundant.
                string oldSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldFolder);
                if (Directory.Exists(oldSettings) && File.Exists(Path.Combine(AppSettings.Folder, "settings.json")))
                    Directory.Delete(oldSettings, true);
            }
            catch { }
        }
    }
}
