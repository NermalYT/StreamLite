using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Interop
{
    // Linux: the same device API the Windows app uses (CoreAudio.cs there), over PipeWire / PulseAudio. A device's id is
    // its PulseAudio name (alsa_output.usb-..., streamlite_game); its name is the description the desktop shows.

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid f, int p) { fmtid = f; pid = p; }
    }

    internal static class CoreAudioConst
    {
        public const int DEVICE_STATE_ACTIVE = 1;
        public static PropertyKey PKEY_Device_FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        public static PropertyKey PKEY_DeviceInterface_FriendlyName = new PropertyKey(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    }

    internal class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public string Adapter;
        /// <summary>Linux: the bus the device hangs off ("usb", "pci", "bluetooth"; empty for a software device).</summary>
        public string Instance = "";
        public override string ToString() { return Name; }
    }

    internal static class AudioDevices
    {
        static List<PulseDevice> sinks, sources;
        static DateTime listed;

        /// <summary>The server's lists, at most once every 300 ms (pages ask often).</summary>
        static void Refresh(bool force)
        {
            if (!force && sinks != null && (DateTime.UtcNow - listed).TotalMilliseconds < 300) return;
            try { sinks = Pactl.AllSinks(); sources = Pactl.AllSources(); }
            catch { sinks = sinks ?? new List<PulseDevice>(); sources = sources ?? new List<PulseDevice>(); }
            listed = DateTime.UtcNow;
        }

        public static void Invalidate() { listed = DateTime.MinValue; }

        static AudioDeviceInfo Info(PulseDevice d)
        {
            return new AudioDeviceInfo { Id = d.Name, Name = d.Description, Adapter = d.Product ?? d.Description, Instance = Linux.LinuxAudio.IsOurs(d.Name) ? "" : (d.Bus ?? "") };
        }

        internal static PulseDevice Find(string id)
        {
            if (id == null) return null;
            Refresh(false);
            return sinks.Concat(sources).FirstOrDefault(d => d.Name == id);
        }

        public static List<AudioDeviceInfo> List(EDataFlow flow) { return List(flow, CoreAudioConst.DEVICE_STATE_ACTIVE); }

        /// <summary>Devices of a direction (the state mask doesn't apply: PipeWire lists only what's there).</summary>
        public static List<AudioDeviceInfo> List(EDataFlow flow, int stateMask)
        {
            Refresh(false);
            var l = new List<AudioDeviceInfo>();
            // "StreamLite - Out (no 3D)" is the engine's own way in (apps are moved there): never a device to pick.
            if (flow != EDataFlow.Capture) l.AddRange(sinks.Where(d => d.Name != Linux.LinuxAudio.Plain).Select(Info));
            if (flow != EDataFlow.Render) l.AddRange(sources.Where(d => !d.IsMonitor).Select(Info));
            l.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return l;
        }

        public static string GetDefaultId(EDataFlow flow, ERole role)
        {
            try { return flow == EDataFlow.Capture ? Pactl.DefaultSource() : Pactl.DefaultSink(); }
            catch { return null; }
        }

        /// <summary>The default output. Linux has one default for every role.</summary>
        public static bool SetDefaultPlayback(string id) { return SetDefault(id); }

        public static bool SetDefaultRole(string id, ERole role)
        {
            // Calls follow the default output on Linux; the "communications" role changes nothing on its own.
            if (role == ERole.Communications) return true;
            return SetDefault(id);
        }

        public static bool SetDefault(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                var d = Find(id);
                bool source = d != null && sources.Contains(d);
                if (source) Pactl.SetDefaultSource(id); else Pactl.SetDefaultSink(id);
                Invalidate();
                return true;
            }
            catch { return false; }
        }

        public static bool MixFormat(string id, out int channels, out int mask, out int rate)
        {
            var d = Find(id);
            channels = d != null && d.Channels > 0 ? d.Channels : 2;
            mask = channels == 8 ? 0x63F : channels == 6 ? 0x3F : 3;
            rate = 48000;
            if (d != null && d.Spec != null) { var m = System.Text.RegularExpressions.Regex.Match(d.Spec, @"(\d+)Hz"); if (m.Success) rate = int.Parse(m.Groups[1].Value); }
            return d != null;
        }

        /// <summary>Linux: StreamLite's game output is 7.1 from the start; real devices keep their own layout.</summary>
        public static string SetSurround71(string id) { return null; }

        /// <summary>1 = there (PipeWire lists only devices that are there).</summary>
        public static int State(string id) { return Find(id) != null ? 1 : 0; }

        /// <summary>Devices can't be switched off from an app on Linux (the desktop's sound settings can).</summary>
        public static bool SetEnabled(string id, bool enabled) { return false; }

        public static string GetString(string id, PropertyKey key) { var d = Find(id); return d != null ? d.Description : null; }
        public static string KsFilterPath(string id) { return null; }
        public static bool RenameAdapter(string id, string adapter) { return false; }
        public static string AdapterName(string id) { var d = Find(id); return d != null ? (d.Product ?? d.Description) : null; }
        public static bool Rename(string id, string description) { return false; }

        public static bool GetVolume(string id, out float level, out float db)
        {
            var d = Find(id);
            level = d != null ? d.Volume : 1f;
            db = (float)(20 * Math.Log10(Math.Max(1e-4, Math.Pow(level, 3))));   // PulseAudio's cubic volume curve
            return d != null;
        }

        public static bool SetVolume(string id, float level)
        {
            var d = Find(id); if (d == null) return false;
            try { Pactl.SetVolume(sinks.Contains(d), id, level); Invalidate(); return true; } catch { return false; }
        }

        public static int SpatialObjects(string id) { return 0; }

        public static bool? GetMute(string id) { var d = Find(id); return d != null ? (bool?)d.Muted : null; }
        public static bool? GetBool(string id, PropertyKey key) { return null; }

        public static bool SetMute(string id, bool mute)
        {
            var d = Find(id); if (d == null) return false;
            try { Pactl.SetMute(sinks.Contains(d), id, mute); Invalidate(); return true; } catch { return false; }
        }

        /// <summary>A software device (StreamLite's own, a virtual sink, a loopback), not real headphones / speakers / mics.</summary>
        public static bool LooksVirtual(AudioDeviceInfo d)
        {
            if (d == null) return false;
            if (Linux.LinuxAudio.IsOurs(d.Id)) return true;
            string s = (d.Name + " " + d.Id).ToLowerInvariant();
            if (s.Contains("streamlite") || s.Contains("null") || s.Contains("virtual") || s.Contains("loopback") || s.Contains("easyeffects") || s.Contains("echo-cancel")) return true;
            var p = Find(d.Id);
            return p != null && string.IsNullOrEmpty(p.Bus) && !d.Id.StartsWith("alsa_", StringComparison.Ordinal) && !d.Id.StartsWith("bluez_", StringComparison.Ordinal);
        }

        /// <summary>StreamLite's own devices and other apps' virtual cables: never a mic you talk into.</summary>
        public static bool IsCable(AudioDeviceInfo d)
        {
            if (d == null) return false;
            string s = (d.Name + " " + d.Id).ToLowerInvariant();
            return Linux.LinuxAudio.IsOurs(d.Id) || s.Contains("streamlite") || s.Contains("null") || s.Contains("loopback");
        }

        /// <summary>Same physical (or virtual) device: a headset's output and its mic, "StreamLite - Mic Feed" and "StreamLite - In".</summary>
        public static bool SameDevice(AudioDeviceInfo a, AudioDeviceInfo b) { return a != null && b != null && DeviceKey(a) == DeviceKey(b); }

        /// <summary>
        /// The card a device belongs to: "alsa_output.usb-HyperX_Cloud-00.analog-stereo" and "alsa_input.usb-HyperX_Cloud-00.mono-fallback"
        /// share "usb-HyperX_Cloud-00" (the part between the kind and the profile), Bluetooth ends share the address.
        /// </summary>
        public static string DeviceKey(AudioDeviceInfo d)
        {
            string id = d.Id ?? "";
            if (id == Linux.LinuxAudio.MicFeed || id == Linux.LinuxAudio.MicSource) return Linux.LinuxAudio.MicSource;
            foreach (var p in new[] { "alsa_output.", "alsa_input.", "bluez_output.", "bluez_input." })
            {
                if (!id.StartsWith(p, StringComparison.Ordinal)) continue;
                string card = id.Substring(p.Length);
                int dot = card.LastIndexOf('.');
                return p.Substring(0, p.IndexOf('_')) + ":" + (dot > 0 ? card.Substring(0, dot) : card);
            }
            return id;
        }
    }
}
