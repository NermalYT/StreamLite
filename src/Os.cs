using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace StreamLite
{
    /// <summary>
    /// The few things that differ between Windows and Linux outside the system layer: which one this is (for words and
    /// for the cards only Windows needs), and opening folders, files, sound settings and StreamLite itself.
    /// </summary>
    internal static class Os
    {
#if LINUX
        public static readonly bool Linux = true;
        public const string Name = "Linux";
#else
        public static readonly bool Linux = false;
        public const string Name = "Windows";
#endif

        /// <summary>A folder in the file manager.</summary>
        public static void OpenFolder(string path)
        {
#if LINUX
            StreamLite.Linux.LinuxApp.Open(path);
#else
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
#endif
        }

        /// <summary>A text file in the text editor.</summary>
        public static void OpenText(string path)
        {
#if LINUX
            StreamLite.Linux.LinuxApp.Open(path);
#else
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
#endif
        }

        /// <summary>The system's sound settings (Linux: the desktop's own, or pavucontrol).</summary>
        public static void OpenSoundSettings()
        {
#if LINUX
            foreach (var c in new[] { new[] { "pavucontrol" }, new[] { "gnome-control-center", "sound" }, new[] { "systemsettings", "kcm_pulseaudio" }, new[] { "xfce4-pulseaudio-plugin" } })
            {
                if (!StreamLite.Interop.Shell.Has(c[0])) continue;
                try { Process.Start(new ProcessStartInfo(c[0], c.Length > 1 ? c[1] : "") { UseShellExecute = false }).Dispose(); return; } catch { }
            }
#else
            try { Process.Start("ms-settings:sound"); } catch { }
#endif
        }

        /// <summary>Starts another StreamLite with these arguments ("--uninstall").</summary>
        public static void StartSelf(string args)
        {
#if LINUX
            try { Process.Start(new ProcessStartInfo("sh", "-c " + StreamLite.Interop.Shell.Quote(StreamLite.Linux.LinuxApp.Launcher + " " + args)) { UseShellExecute = false }).Dispose(); } catch { }
#else
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true }); } catch { }
#endif
        }
    }
}
