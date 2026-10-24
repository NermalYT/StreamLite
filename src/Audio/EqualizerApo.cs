using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StreamLite.Audio
{
    /// <summary>
    /// System-wide output EQ goes through Equalizer APO (an open-source audio driver add-on).
    /// Windows gives normal apps no way to change other apps' sound, so this app writes the EQ
    /// into Equalizer APO's config, which applies it to the chosen headphones/speakers.
    /// </summary>
    internal static class EqualizerApo
    {
        public const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/";
        const string IncludeFile = "StreamLite.txt";
        const string ApoClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";

        static RegistryKey OpenKey()
        {
            try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\EqualizerAPO"); }
            catch { return null; }
        }

        public static string InstallPath
        {
            get
            {
                using (var k = OpenKey())
                {
                    string p = k != null ? k.GetValue("InstallPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                }
                string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
                return Directory.Exists(def) ? def : null;
            }
        }

        public static string ConfigPath
        {
            get
            {
                using (var k = OpenKey())
                {
                    string p = k != null ? k.GetValue("ConfigPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                }
                string ip = InstallPath;
                return ip == null ? null : Path.Combine(ip, "config");
            }
        }

        public static bool IsInstalled { get { return ConfigPath != null && Directory.Exists(ConfigPath); } }

        public static string EndpointGuid(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            int i = deviceId.LastIndexOf('.');
            return i >= 0 ? deviceId.Substring(i + 1) : deviceId;
        }

        /// <summary>true = Equalizer APO is attached to this device, false = not, null = unknown.</summary>
        public static bool? IsEnabledForDevice(string deviceId)
        {
            try
            {
                string guid = EndpointGuid(deviceId);
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid + @"\FxProperties"))
                {
                    if (k == null) return false;
                    foreach (var name in k.GetValueNames())
                    {
                        var v = k.GetValue(name);
                        string s = v as string;
                        var arr = v as string[];
                        if (s != null && s.IndexOf(ApoClsid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (arr != null) foreach (var a in arr) if (a.IndexOf(ApoClsid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                    return false;
                }
            }
            catch { return null; }
        }

        public static string BuildConfig(Dictionary<string, EqProfile> profiles, Func<string, string> nameOf)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Written by StreamLite. Change the EQ in the app - edits here are overwritten.");
            foreach (var kv in profiles)
            {
                var p = kv.Value;
                if (p == null || !p.Enabled) continue;
                var filters = p.AllFilters();
                double pre = p.EffectivePreamp(48000);
                if (filters.Count == 0 && Math.Abs(pre) < 0.01) continue;
                sb.AppendLine();
                sb.AppendLine("# " + (nameOf(kv.Key) ?? kv.Key));
                sb.AppendLine("Device: " + EndpointGuid(kv.Key));
                sb.AppendLine("Preamp: " + F(pre) + " dB");
                foreach (var f in filters)
                {
                    string t = (f.Type ?? "PK").ToUpperInvariant();
                    if (t == "LS") t = "LSC"; else if (t == "HS") t = "HSC"; else if (t == "LPQ") t = "LP"; else if (t == "HPQ") t = "HP";
                    if (t == "LP" || t == "HP")
                        sb.AppendLine("Filter: ON " + t + "Q Fc " + F(f.Freq) + " Hz Q " + F(f.Q));
                    else
                        sb.AppendLine("Filter: ON " + t + " Fc " + F(f.Freq) + " Hz Gain " + F(f.Gain) + " dB Q " + F(f.Q));
                }
            }
            sb.AppendLine();
            sb.AppendLine("Device: all");
            return sb.ToString();
        }

        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>Writes the EQ file and makes sure config.txt includes it. Returns null or an error message.</summary>
        public static string Apply(string configText, bool allowElevation)
        {
            string dir = ConfigPath;
            if (dir == null) return "Equalizer APO is not installed.";
            try
            {
                WriteFiles(dir, configText);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                if (!allowElevation) return "No permission to write Equalizer APO's config folder.";
                if (!GrantConfigAccess(dir)) return "Permission was not granted.";
                try { WriteFiles(dir, configText); return null; }
                catch (Exception ex) { return ex.Message; }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static void WriteFiles(string dir, string configText)
        {
            File.WriteAllText(Path.Combine(dir, IncludeFile), configText, new UTF8Encoding(false));
            string main = Path.Combine(dir, "config.txt");
            string existing = File.Exists(main) ? File.ReadAllText(main) : "";
            // The app used to be called Broadcast Lite: drop its old include so the EQ isn't applied twice.
            const string legacy = "BroadcastLite.txt";
            if (existing.IndexOf("Include: " + legacy, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var lines = existing.Replace("\r\n", "\n").Split('\n');
                existing = string.Join(Environment.NewLine, Array.FindAll(lines, l => l.Trim().IndexOf("Include: " + legacy, StringComparison.OrdinalIgnoreCase) < 0));
                File.WriteAllText(main, existing, new UTF8Encoding(false));
                try { File.Delete(Path.Combine(dir, legacy)); } catch { }
            }
            if (existing.IndexOf("Include: " + IncludeFile, StringComparison.OrdinalIgnoreCase) < 0)
            {
                string add = (existing.Length > 0 && !existing.EndsWith("\n") ? Environment.NewLine : "") + "Include: " + IncludeFile + Environment.NewLine;
                File.AppendAllText(main, add, new UTF8Encoding(false));
            }
        }

        /// <summary>Takes StreamLite's EQ out of Equalizer APO (include line and file). Used when uninstalling.</summary>
        public static void Remove()
        {
            string dir = ConfigPath;
            if (dir == null) return;
            string main = Path.Combine(dir, "config.txt");
            if (File.Exists(main))
            {
                var lines = File.ReadAllText(main).Replace("\r\n", "\n").Split('\n');
                var keep = Array.FindAll(lines, l => l.Trim().IndexOf("Include: " + IncludeFile, StringComparison.OrdinalIgnoreCase) < 0);
                if (keep.Length != lines.Length) File.WriteAllText(main, string.Join(Environment.NewLine, keep), new UTF8Encoding(false));
            }
            string inc = Path.Combine(dir, IncludeFile);
            if (File.Exists(inc)) File.Delete(inc);
        }

        static bool GrantConfigAccess(string dir)
        {
            try
            {
                var psi = new ProcessStartInfo("icacls.exe", "\"" + dir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T /Q")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi)) { p.WaitForExit(30000); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        public static void OpenConfigurator()
        {
            string ip = InstallPath;
            if (ip == null) return;
            // 1.4+ calls it DeviceSelector; older versions Configurator. A silent install can leave Qt
            // unable to find its plugins, so launch through cmd with QT_PLUGIN_PATH set.
            string exe = Path.Combine(ip, "DeviceSelector.exe");
            if (!File.Exists(exe)) exe = Path.Combine(ip, "Configurator.exe");
            if (!File.Exists(exe)) return;
            string args = "/c set \"QT_PLUGIN_PATH=" + Path.Combine(ip, "qt") + "\" && cd /d \"" + ip + "\" && start \"\" \"" + Path.GetFileName(exe) + "\"";
            try { Process.Start(new ProcessStartInfo("cmd.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }
        }

        static readonly Regex FilterRx = new Regex(@"^\s*Filter\s*\d*\s*:\s*(ON|OFF)\s+([A-Z]+)\s+Fc\s+([\d.]+)\s*Hz(?:\s+Gain\s+(-?[\d.]+)\s*dB)?(?:\s+Q\s+([\d.]+))?", RegexOptions.IgnoreCase);
        static readonly Regex PreampRx = new Regex(@"^\s*Preamp\s*:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase);

        /// <summary>Parses an Equalizer APO / AutoEq "ParametricEQ.txt" file.</summary>
        public static List<EqFilter> ParseParametric(string text, out double preamp)
        {
            preamp = 0;
            var list = new List<EqFilter>();
            foreach (var raw in text.Split('\n'))
            {
                var m = PreampRx.Match(raw);
                if (m.Success) { preamp = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                m = FilterRx.Match(raw);
                if (!m.Success) continue;
                var f = new EqFilter
                {
                    On = m.Groups[1].Value.ToUpperInvariant() == "ON",
                    Type = m.Groups[2].Value.ToUpperInvariant(),
                    Freq = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    Gain = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0,
                    Q = m.Groups[5].Success ? double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0.707
                };
                if (f.Type == "LS") f.Type = "LSC";
                if (f.Type == "HS") f.Type = "HSC";
                list.Add(f);
            }
            return list;
        }
    }
}
