using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StreamLite.Linux;

namespace StreamLite.Interop
{
    // Linux versions of the Windows system helpers: the same classes and members, over PipeWire / PulseAudio, /proc and
    // the desktop's own tools. What only exists on Windows (virtual cable drivers, Equalizer APO, device formats set in
    // the registry) does nothing here - the pages that offer it aren't shown on Linux.

    /// <summary>Per-app output: each app's streams are moved where they should play (re-applied every few seconds, so new streams follow too).</summary>
    internal static class AppRouting
    {
        static readonly Dictionary<string, string> wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Points every stream of these programs at a device (null = the default, which is 3D audio while it's on). Real
        /// headphones are reached through "StreamLite Headphones (EQ)" while it runs, so the apps keep your EQ.
        /// </summary>
        public static int Route(IEnumerable<string> exeNames, string endpointId)
        {
            var names = new HashSet<string>(exeNames.Select(LinuxAudio.Bare).Where(x => x.Length > 0));
            if (names.Count == 0) return 0;
            string sink = endpointId == null ? AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Multimedia) : LinuxAudio.PlaybackFor(endpointId);
            if (sink == null) return 0;
            lock (wanted) foreach (var n in names) { if (endpointId == null) wanted.Remove(n); else wanted[n] = endpointId; }
            int moved = 0;
            foreach (var s in Pactl.Streams(true))
            {
                string bin = LinuxAudio.Bare(s.Binary ?? LinuxAudio.AppOf(s.Pid));
                if (!names.Contains(bin) || s.Device == sink || s.Pid == Process.GetCurrentProcess().Id) continue;
                Pactl.MoveSinkInput(s.Index, sink); moved++;
            }
            return moved;
        }

        public static bool IsRouted(string exeName, string endpointId)
        {
            string w; lock (wanted) return wanted.TryGetValue(LinuxAudio.Bare(exeName), out w) && w == endpointId;
        }

        public static string Current(int pid) { return null; }

        /// <summary>Every app StreamLite moved back to the default output.</summary>
        public static bool ClearAll() { ResetAll(); return true; }

        public static void ResetAll()
        {
            List<string> names; lock (wanted) { names = wanted.Keys.ToList(); wanted.Clear(); }
            if (names.Count > 0) try { Route(names, null); } catch { }
        }
    }

    /// <summary>Who plays and who records, per app.</summary>
    internal static class AudioSessions
    {
        public sealed class Session { public string DeviceId, Process; public bool Active; }

        /// <summary>Another app records from this input right now (Discord in a call records "StreamLite Mic").</summary>
        public static bool AnyoneRecording(string captureId)
        {
            if (captureId == null) return false;
            try
            {
                var nums = Pactl.DeviceNumbers(false);
                int me = System.Diagnostics.Process.GetCurrentProcess().Id;
                string src = captureId == LinuxAudio.MicFeed ? LinuxAudio.MicSource : captureId;
                return Pactl.Streams(false).Any(s => s.Pid != me && (s.Device == src || (nums.ContainsKey(s.Device ?? "") && nums[s.Device] == src)));
            }
            catch { return false; }
        }

        public static List<Session> All(EDataFlow flow)
        {
            var l = new List<Session>();
            try
            {
                var nums = Pactl.DeviceNumbers(flow != EDataFlow.Capture);
                foreach (var s in Pactl.Streams(flow != EDataFlow.Capture))
                {
                    string dev; if (s.Device == null || !nums.TryGetValue(s.Device, out dev)) dev = s.Device;
                    l.Add(new Session { DeviceId = dev, Process = LinuxAudio.Bare(s.Binary ?? LinuxAudio.AppOf(s.Pid) ?? s.AppName), Active = !s.Corked });
                }
            }
            catch { }
            return l;
        }

        /// <summary>Programs playing sound right now (process names).</summary>
        public static HashSet<string> Playing()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in All(EDataFlow.Render)) if (s.Active && !string.IsNullOrEmpty(s.Process)) h.Add(s.Process);
            return h;
        }
    }

    /// <summary>Apps with a stream on an output: process id -> program name.</summary>
    internal static class ProcessLoopback
    {
        public static Dictionary<int, string> Sessions(string deviceId, bool playingOnly = false)
        {
            var d = new Dictionary<int, string>();
            try
            {
                var nums = Pactl.DeviceNumbers(true);
                int me = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var s in Pactl.Streams(true))
                {
                    string dev; if (s.Device == null || !nums.TryGetValue(s.Device, out dev)) dev = s.Device;
                    if (dev != deviceId || s.Pid == me || s.Pid == 0 || (playingOnly && s.Corked) || d.ContainsKey(s.Pid)) continue;
                    d[s.Pid] = LinuxAudio.Bare(s.Binary ?? LinuxAudio.AppOf(s.Pid) ?? s.AppName);
                }
            }
            catch { }
            return d;
        }

        public static Dictionary<int, int> Parents()
        {
            var d = new Dictionary<int, int>();
            foreach (var dir in Directory.GetDirectories("/proc"))
            {
                int pid; if (!int.TryParse(Path.GetFileName(dir), out pid)) continue;
                try { var st = File.ReadAllText(dir + "/stat"); int close = st.LastIndexOf(')'); var f = st.Substring(close + 2).Split(' '); int pp; if (int.TryParse(f[1], out pp)) d[pid] = pp; } catch { }
            }
            return d;
        }
    }

    /// <summary>Windows' virtual cables: on Linux StreamLite's own devices take their place.</summary>
    internal static class CableNames
    {
        public static bool IsMicCable(AudioDeviceInfo d) { return d != null && (d.Id == LinuxAudio.MicFeed || d.Id == LinuxAudio.MicSource); }
        public static bool IsSpare16(string name) { return false; }
        public static bool IsGameCable(AudioDeviceInfo d) { return d != null && d.Id == LinuxAudio.Game; }
        public static List<string> Apply() { return new List<string>(); }
        public static List<AudioDeviceInfo> Clutter() { return new List<AudioDeviceInfo>(); }
        public static List<string> Tidy() { return new List<string>(); }
        public static List<string> Untidy() { return new List<string>(); }
        public static bool NeedsTidying(bool hide) { return false; }
        public static string OtherCableUninstaller() { return null; }
        public static bool Named() { return true; }
        public static List<string> ApplyWhenReady(int waitMs, bool mic, bool gameCable) { return new List<string>(); }
    }

    internal static class DeviceErrors
    {
        public const int E_ACCESSDENIED = unchecked((int)0x80070005), E_NOTFOUND = unchecked((int)0x80070490), DEVICE_INVALIDATED = unchecked((int)0x88890004),
            UNSUPPORTED_FORMAT = unchecked((int)0x88890008), DEVICE_IN_USE = unchecked((int)0x8889000A), ENDPOINT_CREATE_FAILED = unchecked((int)0x8889000F),
            SERVICE_NOT_RUNNING = unchecked((int)0x88890010), CPUUSAGE_EXCEEDED = unchecked((int)0x88890017), RESOURCES_INVALIDATED = unchecked((int)0x88890026);
        public static string Describe(int hr, bool mic) { return (mic ? "Microphone" : "Output") + " error " + hr; }
        /// <summary>Linux desktops don't block apps from the mic (only sandboxed apps ask, through the portal).</summary>
        public static bool MicBlocked() { return false; }
        public static void OpenMicPrivacy() { }
    }

    internal struct DeviceFormat
    {
        public int Channels, Mask, Rate, Container, Valid;
        public bool Float;
        public override string ToString() { return Rate + " Hz, " + Channels + " ch" + (Valid > 0 ? ", " + Valid + "-bit" : ""); }
        public bool SameAs(DeviceFormat o) { return Channels == o.Channels && Rate == o.Rate && Float == o.Float && Valid == o.Valid; }
    }

    /// <summary>Device formats: PipeWire picks and converts them itself; StreamLite only reads them.</summary>
    internal static class DeviceFormats
    {
        public static bool Get(string id, out DeviceFormat f)
        {
            f = new DeviceFormat();
            var d = AudioDevices.Find(id);
            if (d == null || d.Spec == null) return false;
            var m = System.Text.RegularExpressions.Regex.Match(d.Spec, @"^(\w+)\s+(\d+)ch\s+(\d+)Hz");
            if (!m.Success) return false;
            string fmt = m.Groups[1].Value;
            f.Channels = int.Parse(m.Groups[2].Value); f.Rate = int.Parse(m.Groups[3].Value);
            f.Float = fmt.StartsWith("float");
            f.Valid = fmt.Contains("32") ? 32 : fmt.Contains("24") ? 24 : 16; f.Container = f.Valid == 24 ? 32 : f.Valid;
            f.Mask = f.Channels == 8 ? 0x63F : f.Channels == 2 ? 3 : 4;
            return true;
        }
        public static bool Supported(string id, DeviceFormat f) { return true; }
        public static DeviceFormat? Best(string id, int channels, int mask) { return null; }
        public static int Set(string id, DeviceFormat f) { return 0; }
        public static string Verify(string id, bool capture, int rate, int ms = 500) { return null; }
        public static string Apply(string id, bool capture, DeviceFormat want, bool verify) { return null; }
        public static bool IsBluetooth(AudioDeviceInfo d) { return d != null && d.Id != null && d.Id.StartsWith("bluez_", StringComparison.Ordinal); }
    }

    /// <summary>The names a device goes by (what the desktop shows, the product the hardware reports).</summary>
    internal static class DeviceNames
    {
        public static List<string> For(AudioDeviceInfo d)
        {
            var l = new List<string>();
            if (d == null) return l;
            var p = AudioDevices.Find(d.Id);
            foreach (var n in new[] { d.Name, p != null ? p.Product : null, d.Adapter })
                if (!string.IsNullOrEmpty(n) && !l.Contains(n)) l.Add(n.Replace('_', ' '));
            return l;
        }
    }

    /// <summary>"Keep game volume during calls": PipeWire / PulseAudio turn other sound down for calls with a role module.</summary>
    internal static class Ducking
    {
        /// <summary>0 = other sound is turned down for calls (role ducking or corking loaded), 3 = nothing changes.</summary>
        public static int Preference
        {
            get
            {
                try { var m = Pactl.Run("list", "short", "modules"); return m.Contains("module-role-cork") || m.Contains("module-role-ducking") ? 0 : 3; }
                catch { return 3; }
            }
        }

        public static bool Set(bool keepVolume)
        {
            try
            {
                if (keepVolume) { foreach (var name in new[] { "module-role-cork", "module-role-ducking" }) foreach (int i in Pactl.ModulesMentioning("").Where(i => ModuleName(i) == name)) Pactl.UnloadModule(i); }
                else if (Preference != 0) Pactl.LoadModule("module-role-cork");
                return true;
            }
            catch { return false; }
        }

        static string ModuleName(int index)
        {
            foreach (var l in Pactl.Run("list", "short", "modules").Split('\n')) { var p = l.Split('\t'); if (p.Length >= 2 && p[0] == index.ToString()) return p[1]; }
            return null;
        }

        public static string Describe(int p) { return p == 3 ? "Calls don't change other sound" : "Other sound is turned down during calls (the sound server's call rule)"; }
    }

    /// <summary>"Lock in": every other app turned down while you play (and back up after).</summary>
    internal static class LockIn
    {
        static readonly object gate = new object();
        static Dictionary<int, float> lowered;
        public static bool Active { get { lock (gate) return lowered != null; } }
        public static string Game { get; private set; }

        public static string Toggle(int percent, string detectedExe)
        {
            if (Active) { Release(); return "Lock in off - every app back to its volume"; }
            if (detectedExe == null) return "Lock in: start a game first";
            Engage(detectedExe, percent);
            return "Locked in on " + detectedExe + " - other apps at " + percent + "%";
        }

        public static void Engage(string game, int percent)
        {
            lock (gate)
            {
                lowered = new Dictionary<int, float>();
                Game = game;
                foreach (var s in Pactl.Streams(true))
                {
                    if (LinuxAudio.Bare(s.Binary ?? LinuxAudio.AppOf(s.Pid)) == LinuxAudio.Bare(game) || s.Pid == Process.GetCurrentProcess().Id) continue;
                    lowered[s.Index] = 1f;
                    Pactl.SetSinkInputVolume(s.Index, percent / 100f);
                }
            }
        }

        public static void Release()
        {
            lock (gate)
            {
                if (lowered == null) return;
                foreach (var kv in lowered) Pactl.SetSinkInputVolume(kv.Key, kv.Value);
                lowered = null; Game = null;
            }
        }
    }

    /// <summary>Raw mic (Windows' effects skipped): Linux has no per-app effects in front of the mic, so there's nothing to skip.</summary>
    internal static class RawCapture
    {
        public static volatile bool Enabled = true;
        public static bool Supported(string deviceId) { return false; }
        public static bool Request(object client, string deviceId) { return false; }
    }

    internal static class UserProcess
    {
        public static bool IsSystem { get { return false; } }
        public static string SignedInUserSid() { return null; }
        public static bool RunElevated(string exe, string args, int timeoutMs, out int code, out string why) { code = -1; why = "not on Linux"; return false; }
    }

    internal static class VbTraces
    {
        public static void CleanUserStartMenu() { }
        public static void RestoreNames() { }
    }

    /// <summary>Default devices to put back after installing (Windows' installer step; nothing to do on Linux).</summary>
    internal sealed class DefaultDevices
    {
        public sealed class Change { public string Role, From, To; }
        public static DefaultDevices Take() { return new DefaultDevices(); }
        public List<string> Restore() { return new List<string>(); }
        public static List<Change> Plan(Dictionary<string, string> before, Dictionary<string, string> now, Func<string, bool> isCable, Func<string, bool> present) { return new List<Change>(); }
    }

    /// <summary>Windows-only installers (VB-CABLE, Hi-Fi Cable, Equalizer APO): Linux needs none of them.</summary>
    internal static class DriverInstaller
    {
        public static string VbCableZip { get { return null; } }
        public static string HiFiCableZip { get { return null; } }
        public static string FetchVbAudio(string zipUrl, string setupPattern, string name) { return null; }
        public static string FetchEqualizerApo() { return null; }
        public static bool RunElevated(string exe, string args) { return false; }
        public static string InstallVbCable(Func<bool> present, Action<string> progress) { return "not needed on Linux - StreamLite makes its own devices"; }
        public static string InstallEqualizerApo(string headphonesId, Action<string> progress) { return "not needed on Linux - StreamLite runs the headphone EQ itself"; }
    }

    internal static class DriverRemoval
    {
        public static List<KeyValuePair<string, string>> CablePackages() { return new List<KeyValuePair<string, string>>(); }
        public static bool RestartNeeded;
        public static List<string> CableNodes() { return new List<string>(); }
        public static string RemoveDevice(string instance) { return null; }
        public static bool CablesStillShowing() { return false; }
        public static string Remove(string published) { return null; }
        public static bool RemoveEqualizerApo() { return false; }
    }
    /// <summary>Media Foundation's camera list on Windows; on Linux the video devices (V4L2): name, device path.</summary>
    internal interface IMFActivate { }

    internal static class MF
    {
        public static List<Tuple<string, string, IMFActivate>> Cameras()
        {
            return VideoDevices.List().Select(v => Tuple.Create(v.Name, v.DevicePath, (IMFActivate)null)).ToList();
        }
    }
}
