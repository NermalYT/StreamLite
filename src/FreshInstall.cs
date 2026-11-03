using System;
using System.IO;
#if !LINUX
using Microsoft.Win32;
#endif

namespace StreamLite
{
    /// <summary>
    /// A new install starts with the setup (4.2.1). The installer marks an install that isn't an update - the MSI in
    /// HKLM\Software\StreamLite (FreshInstall), the Linux .sh in fresh-install.txt next to the program - and the first
    /// start after it on each account sets the settings there aside as a kept copy. StreamLite then starts as on a new
    /// PC (the thank-you, the whole setup), and Scan for settings in the setup's first step brings back the parts you
    /// pick. Updates (StreamLite's own, or an MSI over an older one) leave the settings as they are.
    /// </summary>
    internal static class FreshInstall
    {
        /// <summary>The kept copy's version ("4.2.0") when this start set settings aside, else null.</summary>
        public static string KeptFrom { get; private set; }

        /// <summary>The install's mark ("" = none: an update, or a copy no installer put there).</summary>
        public static string Mark()
        {
            try
            {
#if LINUX
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fresh-install.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
#else
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = b.OpenSubKey(@"Software\StreamLite"))
                    {
                        var v = k != null ? k.GetValue("FreshInstall") as string : null;
                        if (!string.IsNullOrEmpty(v)) return v.Trim();
                    }
                return "";
#endif
            }
            catch { return ""; }
        }

        /// <summary>At start, before the settings load: an install this account hasn't started yet sets them aside.</summary>
        public static void Check() { Check(Mark()); }

        /// <summary>true = settings were set aside (the mark is new to this account and there were settings).</summary>
        internal static bool Check(string mark)
        {
            if (string.IsNullOrEmpty(mark)) return false;
            try
            {
                string dir = AppSettings.Folder, seen = Path.Combine(dir, "install-seen.txt");
                if (File.Exists(seen) && File.ReadAllText(seen).Trim() == mark) return false;
                Directory.CreateDirectory(dir);
                string kept = SetAside(dir);
                File.WriteAllText(seen, mark);
                try { HealthMonitor.LogLine("New install (" + mark + "): " + (kept != null ? "your settings were set aside as " + Path.GetFileName(kept) + " - the setup starts fresh, and Scan for settings brings back what you pick" : "no settings to set aside - the setup starts")); } catch { }
                return kept != null;
            }
            catch { return false; }
        }

        /// <summary>settings.json (or its last good copy) becomes a kept copy Scan for settings lists; both go. Returns the kept file.</summary>
        static string SetAside(string dir)
        {
            string file = Path.Combine(dir, "settings.json"), good = file + ".lastgood";
            string from = file;
            var s = AppSettings.ReadFile(file);
            if (s == null) { from = good; s = AppSettings.ReadFile(good); }
            string kept = null;
            if (s != null)
            {
                // settings.kept-<when>~<version>.json: "Your settings from StreamLite <version>" in Scan for settings.
                kept = Path.Combine(dir, "settings.kept-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (s.LastVersion != null ? "~" + s.LastVersion : "") + ".json");
                File.Copy(from, kept, true);
                KeptFrom = s.LastVersion ?? "before";
            }
            foreach (var f in new[] { file, good }) if (File.Exists(f)) File.Delete(f);
            return kept;
        }
    }
}
