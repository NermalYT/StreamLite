using System;
using System.IO;
using System.Linq;
using System.Text;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>
    /// "Copy details for a bug report": the StreamLite and Windows version, what the engines are doing (status.txt) and
    /// every audio device with its format - what's needed to fix a device problem. Only copied to the clipboard; the
    /// user decides what to paste into the bug form.
    /// </summary>
    internal static class Diagnostics
    {
        public static string Report(AppCore core)
        {
            var sb = new StringBuilder();
            sb.AppendLine("StreamLite " + Program.Version + " - " + Updater.SystemDescription() + (Elevation.IsElevated ? ", running as administrator" : ""));
            var S = core.S;
            sb.AppendLine("virtual mic " + (S.VirtualMicOn ? "on" : "off") + ", noise removal " + (S.NoiseRemoval ? S.NsEngine : "off") + (S.LowVram ? " (low VRAM)" : "") + ", mic input " + (S.MicChannel ?? "auto") +
                ", 3D " + (S.Game.Enabled ? "on" : "off") + ", headphone EQ engine " + (EqualizerApo.IsInstalled ? "installed" : "not installed") +
                ", profile " + (core.Profiles != null ? core.Profiles.ActiveName : "-") + (S.Profiles.AutoSwitch ? " (auto)" : " (by hand)"));
            try
            {
                string status = File.ReadAllText(Path.Combine(AppSettings.Folder, "status.txt"));
                sb.AppendLine().AppendLine("Status:").AppendLine(status.Trim());
            }
            catch { }
            sb.AppendLine().AppendLine("Audio devices (format " + Os.TheSystem + " uses / mix format):");
            foreach (var flow in new[] { EDataFlow.Capture, EDataFlow.Render })
                foreach (var d in AudioDevices.List(flow, 1 | 2 | 8))
                {
                    int state = AudioDevices.State(d.Id);
                    DeviceFormat f; int ch, mask, rate;
                    string fmt = DeviceFormats.Get(d.Id, out f) ? f.ToString() + (f.Float ? " float" : "") : "?";
                    string mix = AudioDevices.MixFormat(d.Id, out ch, out mask, out rate) ? ch + " ch, " + rate + " Hz" : "?";
                    var tags = new[]
                    {
                        flow == EDataFlow.Capture ? "in" : "out",
                        state == 1 ? null : state == 2 ? "disabled" : state == 8 ? "unplugged" : "state " + state,
                        DeviceFormats.IsBluetooth(d) ? "Bluetooth" : null,
                        AudioDevices.LooksVirtual(d) ? "virtual" : null,
                        d.Id == core.ActiveMicId ? "StreamLite's mic" : null,
                        d.Id == core.GamePhonesId ? "3D headphones" : null,
                        d.Id == core.GameSinkId ? "game device" : null,
                        d.Id == AudioDevices.GetDefaultId(flow, ERole.Console) ? "default" : null,
                    }.Where(t => t != null);
                    sb.AppendLine("  " + d.Name + " - " + fmt + " / " + mix + " [" + string.Join(", ", tags) + "]");
                }
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
            // File paths carry the Windows user name: keep it out.
            string text = sb.ToString();
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length > 1) text = text.Replace("\\" + user + "\\", "\\<user>\\");
            return text;
        }
    }
}
