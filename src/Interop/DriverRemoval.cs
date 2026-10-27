using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamLite.Audio;

namespace StreamLite.Interop
{
    /// <summary>
    /// Uninstalling: the drivers StreamLite installed come off again - VB-CABLE (the mic cable) and VB-Audio Hi-Fi Cable
    /// (the 3D game device), with Windows' own pnputil (their devices and driver packages), and Equalizer APO with its own
    /// uninstaller. Voicemeeter and other VB-Audio products are different packages and stay. Needs admin rights.
    /// </summary>
    internal static class DriverRemoval
    {
        // The driver files' own names (pnputil's labels are translated, these aren't).
        static readonly Regex Ours = new Regex(@"\b(vbmmecable\w*\.inf|vbhfvaio\w*\.inf)\b", RegexOptions.IgnoreCase);
        static readonly Regex Oem = new Regex(@"\boem\d+\.inf\b", RegexOptions.IgnoreCase);

        static string PnpUtil { get { return Path.Combine(Environment.SystemDirectory, "pnputil.exe"); } }

        /// <summary>The cable driver packages: published name (oemNN.inf) -> original file (vbmmecable64_win10.inf...).</summary>
        public static List<KeyValuePair<string, string>> CablePackages()
        {
            var list = new List<KeyValuePair<string, string>>();
            string text;
            int code = Run("/enum-drivers", 60000, out text);
            if (code != 0 || string.IsNullOrEmpty(text)) return list;
            foreach (var block in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
            {
                var o = Oem.Match(block); var m = Ours.Match(block);
                if (o.Success && m.Success) list.Add(new KeyValuePair<string, string>(o.Value, m.Value));
            }
            return list;
        }

        /// <summary>Removes one driver package and every device using it. null = done, otherwise pnputil's words.</summary>
        public static string Remove(string published)
        {
            string text;
            int code = Run("/delete-driver " + published + " /uninstall /force", 120000, out text);
            // 3010: done, a restart finishes it.
            return code == 0 || code == 3010 ? null : (text ?? "").Trim().Split('\n').LastOrDefault() ?? ("exit code " + code);
        }

        /// <summary>Equalizer APO's own silent uninstaller (it takes itself off every device). False when it isn't there.</summary>
        public static bool RemoveEqualizerApo()
        {
            string dir = EqualizerApo.InstallPath;
            string un = dir != null ? Path.Combine(dir, "Uninstall.exe") : null;
            if (un == null || !File.Exists(un)) return false;
            using (var p = Process.Start(new ProcessStartInfo(un, "/S") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(120000);
            return true;
        }

        static int Run(string args, int timeoutMs, out string output)
        {
            output = null;
            try
            {
                var psi = new ProcessStartInfo(PnpUtil, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    output = read.Result;
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }
    }
}
