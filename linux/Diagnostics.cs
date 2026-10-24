using System;
using System.IO;
using System.Linq;
using System.Text;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// "streamlite --details" / Settings > Copy details: version, distro and sound server, what StreamLite is doing and
    /// every audio device with its format - what's needed to fix a device problem. Printed or copied, never sent.
    /// </summary>
    internal static class Diagnostics
    {
        public static string Report(Core core)
        {
            var sb = new StringBuilder();
            sb.AppendLine("StreamLite " + Program.Version + " - " + Updater.SystemDescription());
            var S = core != null ? core.S : AppSettings.Load();
            sb.AppendLine("virtual mic " + (S.VirtualMicOn ? "on" : "off") + ", noise removal " + (S.NoiseRemoval ? S.NsEngine : "off") + ", mic input " + (S.MicChannel ?? "auto") +
                ", headphone EQ " + (S.HeadphoneEq ? "on" : "off") + ", 3D " + (S.Game.Enabled ? "on" : "off"));
            if (core != null)
            {
                sb.AppendLine("mic: " + core.Mic.Status + " (" + core.Mic.Source + ", " + core.Mic.InputChannelCount + " ch" +
                    (core.Mic.InputChannelCount > 1 ? " - " + core.Mic.Chain.Channels.Describe() : "") + ", dropouts " + core.Mic.Dropouts + ")");
                sb.AppendLine("headphone EQ: " + core.Eq.Status + " -> " + core.Eq.Output);
                sb.AppendLine("3D audio: " + core.Game.Status + " -> " + core.Game.Output + (core.Game.Processor.Rendering360 ? " (true 360)" : ""));
                sb.AppendLine("profile: " + core.ActiveProfile + (core.DetectedExe != null ? " (" + core.DetectedExe + ")" : ""));
                if (core.Error != null) sb.AppendLine("error: " + core.Error);
            }
            try
            {
                sb.AppendLine("default output: " + Pactl.DefaultSink() + ", default input: " + Pactl.DefaultSource());
                sb.AppendLine().AppendLine("Inputs:");
                foreach (var d in Pactl.AllSources()) sb.AppendLine("  " + d.Description + " (" + d.Name + ") - " + d.Spec + (d.IsMonitor ? " [monitor]" : "") + (LinuxAudio.IsOurs(d.Name) ? " [StreamLite]" : ""));
                sb.AppendLine("Outputs:");
                foreach (var d in Pactl.AllSinks()) sb.AppendLine("  " + d.Description + " (" + d.Name + ") - " + d.Spec + (LinuxAudio.IsOurs(d.Name) ? " [StreamLite]" : ""));
            }
            catch (Exception ex) { sb.AppendLine("pactl: " + ex.Message); }
            try
            {
                string err = Path.Combine(AppSettings.Folder, "error.log");
                if (File.Exists(err))
                {
                    var lines = File.ReadAllLines(err);
                    sb.AppendLine().AppendLine("Last errors:");
                    foreach (var l in lines.Skip(Math.Max(0, lines.Length - 12))) sb.AppendLine("  " + l);
                }
            }
            catch { }
            // Paths carry the user name: keep it out.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string text = sb.ToString();
            return home.Length > 1 ? text.Replace(home, "~") : text;
        }
    }
}
