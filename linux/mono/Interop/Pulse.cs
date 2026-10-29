using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamLite.Interop
{
    /// <summary>
    /// One blocking audio stream through libpulse-simple (PulseAudio, or PipeWire's pulse server - the default on current
    /// distros). 32-bit float, 48 kHz, interleaved.
    /// </summary>
    internal sealed class PulseStream : IDisposable
    {
        const string Lib = "libpulse-simple.so.0";
        const int PA_STREAM_PLAYBACK = 1, PA_STREAM_RECORD = 2, PA_SAMPLE_FLOAT32LE = 5;

        [StructLayout(LayoutKind.Sequential)] struct SampleSpec { public int Format; public uint Rate; public byte Channels; }
        [StructLayout(LayoutKind.Sequential)] struct BufferAttr { public uint MaxLength, TLength, PreBuf, MinReq, FragSize; }
        [StructLayout(LayoutKind.Sequential)] unsafe struct ChannelMap { public byte Channels; public fixed int Map[32]; }

        [DllImport(Lib)] static extern IntPtr pa_simple_new(string server, string name, int dir, string dev, string streamName, ref SampleSpec ss, IntPtr map, ref BufferAttr attr, out int error);
        [DllImport(Lib)] static extern int pa_simple_write(IntPtr s, float[] data, UIntPtr bytes, out int error);
        [DllImport(Lib)] static extern int pa_simple_read(IntPtr s, float[] data, UIntPtr bytes, out int error);
        [DllImport(Lib)] static extern void pa_simple_free(IntPtr s);
        [DllImport(Lib)] static extern ulong pa_simple_get_latency(IntPtr s, out int error);
        [DllImport(Lib)] static extern int pa_simple_flush(IntPtr s, out int error);
        [DllImport("libpulse.so.0")] static extern IntPtr pa_strerror(int error);

        IntPtr s;
        public readonly int Channels;

        // PulseAudio channel positions: 1 FL, 2 FR, 3 FC, 5 rear L, 6 rear R, 7 LFE, 10 side L, 11 side R.
        public static readonly int[] Map71 = { 1, 2, 3, 7, 5, 6, 10, 11 };

        PulseStream(bool record, string device, int channels, string streamName, int latencyMs, int[] map)
        {
            Channels = channels;
            var ss = new SampleSpec { Format = PA_SAMPLE_FLOAT32LE, Rate = 48000, Channels = (byte)channels };
            uint bytes = (uint)(48 * Math.Max(1, latencyMs) * channels * 4);
            var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = record ? uint.MaxValue : bytes, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = record ? bytes : uint.MaxValue };
            int err;
            unsafe
            {
                var cm = new ChannelMap();
                IntPtr mapPtr = IntPtr.Zero;
                if (map != null)
                {
                    cm.Channels = (byte)map.Length;
                    for (int i = 0; i < map.Length; i++) cm.Map[i] = map[i];
                    mapPtr = (IntPtr)(&cm);
                }
                else if (channels == 2) { cm.Channels = 2; cm.Map[0] = 1; cm.Map[1] = 2; mapPtr = (IntPtr)(&cm); }
                s = pa_simple_new(null, "StreamLite", record ? PA_STREAM_RECORD : PA_STREAM_PLAYBACK, device, streamName, ref ss, mapPtr, ref attr, out err);
            }
            if (s == IntPtr.Zero) throw new InvalidOperationException("Audio stream failed (" + Error(err) + ")" + (device != null ? ": " + device : ""));
        }

        public static PulseStream Record(string device, int channels, string name, int latencyMs = 10, int[] map = null) { return new PulseStream(true, device, channels, name, latencyMs, map); }
        public static PulseStream Play(string device, int channels, string name, int latencyMs = 20, int[] map = null) { return new PulseStream(false, device, channels, name, latencyMs, map); }

        static string Error(int e) { try { return Marshal.PtrToStringAnsi(pa_strerror(e)); } catch { return "error " + e; } }

        public void Read(float[] buf, int frames)
        {
            int err;
            if (pa_simple_read(s, buf, (UIntPtr)(frames * Channels * 4), out err) < 0) throw new InvalidOperationException("Recording stopped (" + Error(err) + ")");
        }

        public void Write(float[] buf, int frames)
        {
            int err;
            if (pa_simple_write(s, buf, (UIntPtr)(frames * Channels * 4), out err) < 0) throw new InvalidOperationException("Playback stopped (" + Error(err) + ")");
        }

        public void Flush() { int e; try { pa_simple_flush(s, out e); } catch { } }

        public double LatencyMs { get { int e; return pa_simple_get_latency(s, out e) / 1000.0; } }

        public void Dispose() { if (s != IntPtr.Zero) { pa_simple_free(s); s = IntPtr.Zero; } }

        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
        [DllImport("libdl.so.2")] static extern int dlclose(IntPtr h);

        /// <summary>True when libpulse-simple can be loaded (pulseaudio-libs / libpulse0 is installed).</summary>
        public static bool Available
        {
            get
            {
                try { IntPtr h = dlopen(Lib, 2); if (h == IntPtr.Zero) return false; dlclose(h); return true; }
                catch { return false; }
            }
        }
    }

    /// <summary>A sink (output) or source (input) as PulseAudio / PipeWire lists it.</summary>
    internal sealed class PulseDevice
    {
        public string Name, Description, Spec, State, FormFactor, Bus, Product;
        public int Index, Channels;
        /// <summary>PulseAudio channel positions in the device's own order (null = not known).</summary>
        public int[] Map;
        public bool IsMonitor, Muted;
        public float Volume = 1f;
        public override string ToString() { return Description; }
    }

    /// <summary>Running commands (pactl, pw-metadata...) with their arguments passed as they are.</summary>
    internal static class Shell
    {
        /// <summary>One argument as the runtime will hand it to the program (POSIX-style quoting).</summary>
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_./=:,@%+".IndexOf(c) >= 0)) return a;
            var sb = new StringBuilder("\"");
            foreach (char c in a) { if (c == '"' || c == '\\' || c == '$' || c == '`') sb.Append('\\'); sb.Append(c); }
            return sb.Append('"').ToString();
        }

        /// <summary>Runs a program; returns its output, or throws with its error text.</summary>
        public static string Run(string program, params string[] args)
        {
            int code; string err;
            string o = Run(program, args, 8000, out code, out err);
            if (code != 0) throw new InvalidOperationException(program + " " + string.Join(" ", args) + ": " + err.Trim());
            return o;
        }

        public static string Run(string program, string[] args, int timeoutMs, out int exitCode, out string error)
        {
            var psi = new ProcessStartInfo(program, string.Join(" ", args.Select(Quote))) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["LC_ALL"] = "C";
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                error = err.Wait(1000) ? err.Result : "";
                exitCode = p.HasExited ? p.ExitCode : -1;
                return o;
            }
        }

        /// <summary>Whether a program is on the PATH.</summary>
        public static bool Has(string program)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
                try { if (System.IO.File.Exists(System.IO.Path.Combine(dir, program))) return true; } catch { }
            return false;
        }
    }

    /// <summary>
    /// Device management through pactl (pulseaudio-utils, or pipewire-pulse's pactl): StreamLite's virtual devices,
    /// listing, defaults, volumes and moving apps between outputs.
    /// </summary>
    internal static class Pactl
    {
        public static string Run(params string[] args) { return Shell.Run("pactl", args); }

        public static bool Available { get { try { Run("info"); return true; } catch { return false; } } }

        /// <summary>Server name ("PulseAudio (on PipeWire 1.2.7)" or "pulseaudio").</summary>
        public static string ServerName()
        {
            var m = Regex.Match(Run("info"), @"Server Name:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : "unknown";
        }

        static List<PulseDevice> List(string kind)
        {
            var list = new List<PulseDevice>();
            PulseDevice cur = null;
            foreach (var raw in Run("list", kind).Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("Sink #") || l.StartsWith("Source #")) { cur = new PulseDevice(); int ix; if (int.TryParse(l.Substring(l.IndexOf('#') + 1), out ix)) cur.Index = ix; list.Add(cur); continue; }
                if (cur == null) continue;
                if (l.StartsWith("Name: ")) { cur.Name = l.Substring(6).Trim(); cur.IsMonitor = cur.Name.EndsWith(".monitor"); }
                else if (l.StartsWith("Description: ")) cur.Description = l.Substring(13).Trim();
                else if (l.StartsWith("State: ")) cur.State = l.Substring(7).Trim();
                else if (l.StartsWith("Mute: ")) cur.Muted = l.EndsWith("yes");
                else if (l.StartsWith("Volume: ")) { var m = Regex.Match(l, @"(\d+)%"); if (m.Success) cur.Volume = int.Parse(m.Groups[1].Value) / 100f; }
                else if (l.StartsWith("Sample Specification: ")) { cur.Spec = l.Substring(22).Trim(); var m = Regex.Match(l, @"(\d+)ch"); if (m.Success) cur.Channels = int.Parse(m.Groups[1].Value); }
                else if (l.StartsWith("Channel Map: ")) cur.Map = ParseMap(l.Substring(13));
                else if (l.StartsWith("device.form_factor = ")) cur.FormFactor = Unquote(l.Substring(21));
                else if (l.StartsWith("device.bus = ")) cur.Bus = Unquote(l.Substring(13));
                else if (l.StartsWith("device.product.name = ")) cur.Product = Unquote(l.Substring(22));
            }
            foreach (var d in list) if (string.IsNullOrEmpty(d.Description)) d.Description = d.Name;
            return list;
        }

        static readonly string[] Positions = { "mono", "front-left", "front-right", "front-center", "rear-center", "rear-left", "rear-right", "lfe",
            "front-left-of-center", "front-right-of-center", "side-left", "side-right" };

        /// <summary>"front-left,front-right" or "aux0,aux1,..." -> PulseAudio position numbers (null if a name is unknown).</summary>
        public static int[] ParseMap(string text)
        {
            var names = text.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            var map = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i] == "subwoofer" ? "lfe" : names[i];
                int k = Array.IndexOf(Positions, n);
                if (k < 0 && n.StartsWith("aux") && int.TryParse(n.Substring(3), out k) && k >= 0 && k < 32) k += 12;
                else if (k < 0) return null;
                map[i] = k;
            }
            return map.Length > 0 ? map : null;
        }

        public static List<PulseDevice> AllSinks() { return List("sinks"); }
        public static List<PulseDevice> AllSources() { return List("sources"); }

        public static string DefaultSink() { var m = Regex.Match(Run("info"), @"Default Sink:\s*(.+)"); return m.Success ? m.Groups[1].Value.Trim() : null; }
        public static string DefaultSource() { var m = Regex.Match(Run("info"), @"Default Source:\s*(.+)"); return m.Success ? m.Groups[1].Value.Trim() : null; }
        public static void SetDefaultSink(string name) { Run("set-default-sink", name); }
        public static void SetDefaultSource(string name) { Run("set-default-source", name); }

        /// <summary>Loads a module and returns its index.</summary>
        public static int LoadModule(string module, params string[] args)
        {
            var a = new List<string> { "load-module", module };
            a.AddRange(args);
            return int.Parse(Run(a.ToArray()).Trim());
        }

        public static void UnloadModule(int index) { try { Run("unload-module", index.ToString()); } catch { } }

        /// <summary>Modules whose arguments mention a name (to clean up after a crash).</summary>
        public static List<int> ModulesMentioning(string text)
        {
            var list = new List<int>();
            foreach (var l in Run("list", "short", "modules").Split('\n'))
            {
                var parts = l.Split('\t');
                if (parts.Length >= 3 && parts[2].Contains(text)) { int i; if (int.TryParse(parts[0], out i)) list.Add(i); }
            }
            return list;
        }

        public sealed class Stream { public int Index, Pid; public string Binary, AppName, Device, Media, Role; public bool Corked; }

        /// <summary>Apps playing (sink-inputs) or recording (source-outputs), each stream with its program.</summary>
        public static List<Stream> Streams(bool playing)
        {
            var list = new List<Stream>();
            Stream cur = null;
            string head = playing ? "Sink Input #" : "Source Output #", dev = playing ? "Sink: " : "Source: ";
            foreach (var raw in Run("list", playing ? "sink-inputs" : "source-outputs").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith(head)) { cur = new Stream { Index = int.Parse(l.Substring(head.Length)) }; list.Add(cur); continue; }
                if (cur == null) continue;
                if (l.StartsWith(dev)) cur.Device = l.Substring(dev.Length).Trim();
                else if (l.StartsWith("Corked: ")) cur.Corked = l.EndsWith("yes");
                else if (l.StartsWith("application.process.binary = ")) cur.Binary = Unquote(l.Substring(29));
                else if (l.StartsWith("application.process.id = ")) { int pid; if (int.TryParse(Unquote(l.Substring(25)), out pid)) cur.Pid = pid; }
                else if (l.StartsWith("application.name = ")) cur.AppName = Unquote(l.Substring(19));
                else if (l.StartsWith("media.name = ")) cur.Media = Unquote(l.Substring(13));
                else if (l.StartsWith("media.role = ")) cur.Role = Unquote(l.Substring(13));
            }
            return list;
        }

        /// <summary>Sink and source numbers by name (pactl lists streams by device number).</summary>
        public static Dictionary<string, string> DeviceNumbers(bool sinks)
        {
            var d = new Dictionary<string, string>();
            foreach (var l in Run("list", "short", sinks ? "sinks" : "sources").Split('\n'))
            {
                var p = l.Split('\t');
                if (p.Length >= 2) d[p[0].Trim()] = p[1].Trim();
            }
            return d;
        }

        public static void MoveSinkInput(int index, string sink) { try { Run("move-sink-input", index.ToString(), sink); } catch { } }
        public static void SetSinkInputVolume(int index, float level) { try { Run("set-sink-input-volume", index.ToString(), ((int)Math.Round(level * 100)) + "%"); } catch { } }

        public static void SetVolume(bool sink, string name, float level) { Run(sink ? "set-sink-volume" : "set-source-volume", name, ((int)Math.Round(level * 100)) + "%"); }
        public static void SetMute(bool sink, string name, bool mute) { Run(sink ? "set-sink-mute" : "set-source-mute", name, mute ? "1" : "0"); }

        /// <summary>
        /// "pactl subscribe": the sound server's events ("Event 'remove' on sink #5") as they happen, until the returned
        /// process is killed. Lets StreamLite react to a headset being unplugged within milliseconds.
        /// </summary>
        public static Process Subscribe(Action<string> onEvent)
        {
            var psi = new ProcessStartInfo("pactl", "subscribe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["LC_ALL"] = "C";
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) try { onEvent(e.Data); } catch { } };
            p.Start();
            p.BeginOutputReadLine();
            return p;
        }

        static string Unquote(string s) { s = s.Trim(); return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s; }
    }
}
