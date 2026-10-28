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

        /// <summary>A restart is needed to finish (Windows said so, or a cable's driver was still in use).</summary>
        public static bool RestartNeeded;

        /// <summary>
        /// The cables' devices themselves ("ROOT\MEDIA\0000"...): VB-Audio's setup creates them by hand, so deleting the
        /// driver package leaves them running on the loaded driver - still in every sound list until a restart (3.5.4).
        /// Found from their sound devices, any state.
        /// </summary>
        public static List<string> CableNodes()
        {
            var nodes = new List<string>();
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                foreach (var d in AudioDevices.List(flow, 1 | 2 | 4 | 8))
                {
                    string s = (d.Name + " " + d.Adapter).ToLowerInvariant();
                    bool ours = CableNames.IsMicCable(d) || CableNames.IsGameCable(d) || s.Contains("streamlite") || s.Contains("vb-audio virtual cable") || s.Contains("vb-audio hi-fi cable")
                        || s.Contains("cable in 16ch") || s.Contains("cable in 16 ch") || s.Contains("cable output") || s.Contains("hi-fi cable output");
                    if (!ours || s.Contains("voicemeeter")) continue;
                    string i = d.Instance ?? ""; int dot = i.IndexOf("}.", StringComparison.Ordinal);
                    string node = dot >= 0 ? i.Substring(dot + 2) : i;
                    if (node.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) && !nodes.Contains(node, StringComparer.OrdinalIgnoreCase)) nodes.Add(node);
                }
            return nodes;
        }

        /// <summary>Removes one device node (pnputil /remove-device). null = done, otherwise pnputil's words.</summary>
        public static string RemoveDevice(string instance)
        {
            string text;
            int code = Run("/remove-device \"" + instance + "\"", 120000, out text);
            if (code == 3010 || (text ?? "").IndexOf("reboot", StringComparison.OrdinalIgnoreCase) >= 0) RestartNeeded = true;
            return code == 0 || code == 3010 ? null : (text ?? "").Trim().Split('\n').LastOrDefault() ?? ("exit code " + code);
        }

        /// <summary>A cable still shows in the sound lists (it goes with the restart).</summary>
        public static bool CablesStillShowing() { return CableNodes().Count > 0; }

        /// <summary>Removes one driver package and every device using it. null = done, otherwise pnputil's words.</summary>
        public static string Remove(string published)
        {
            string text;
            int code = Run("/delete-driver " + published + " /uninstall /force", 120000, out text);
            // 3010: done, a restart finishes it.
            if (code == 3010 || (text ?? "").IndexOf("reboot", StringComparison.OrdinalIgnoreCase) >= 0) RestartNeeded = true;
            return code == 0 || code == 3010 ? null : (text ?? "").Trim().Split('\n').LastOrDefault() ?? ("exit code " + code);
        }

        /// <summary>Equalizer APO's own silent uninstaller (it takes itself off every device). False when it isn't there.</summary>
        public static bool RemoveEqualizerApo()
        {
            string dir = EqualizerApo.InstallPath;
            string un = dir != null ? Path.Combine(dir, "Uninstall.exe") : null;
            if (un == null || !File.Exists(un)) return false;
            using (var p = Process.Start(new ProcessStartInfo(un, "/S") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(120000);
            // Its uninstaller hands over to a copy of itself: wait for that, then see whether its DLL is still loaded in
            // Windows' audio engine (it goes with the restart).
            string dll = Path.Combine(dir, "EqualizerAPO.dll");
            for (int i = 0; i < 60 && File.Exists(un); i++) System.Threading.Thread.Sleep(500);
            if (File.Exists(dll)) RestartNeeded = true;
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
