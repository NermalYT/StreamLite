using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StreamLite.Interop
{
    /// <summary>
    /// One blocking audio stream through libpulse-simple (PulseAudio, or PipeWire's pulse server - the default on
    /// current distros). 32-bit float, 48 kHz, interleaved.
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
        [DllImport("libpulse.so.0")] static extern IntPtr pa_strerror(int error);

        IntPtr s;
        public readonly int Channels;

        // PulseAudio channel positions: 1 FL, 2 FR, 3 FC, 5 rear L, 6 rear R, 7 LFE, 10 side L, 11 side R.
        public static readonly int[] Map71 = { 1, 2, 3, 7, 5, 6, 10, 11 };

        PulseStream(bool record, string device, int channels, string streamName, int latencyMs, int[] map)
        {
            Channels = channels;
            var ss = new SampleSpec { Format = PA_SAMPLE_FLOAT32LE, Rate = 48000, Channels = (byte)channels };
            uint bytes = (uint)(48 * latencyMs * channels * 4);
            var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = record ? uint.MaxValue : bytes, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = record ? bytes : uint.MaxValue };
            IntPtr mapPtr = IntPtr.Zero;
            int err;
            unsafe
            {
                ChannelMap cm = default;
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

        public double LatencyMs { get { int e; return pa_simple_get_latency(s, out e) / 1000.0; } }

        public void Dispose() { if (s != IntPtr.Zero) { pa_simple_free(s); s = IntPtr.Zero; } }

        /// <summary>True when libpulse-simple can be loaded (pulseaudio-libs / libpulse0 is installed).</summary>
        public static bool Available
        {
            get { IntPtr h; if (!NativeLibrary.TryLoad(Lib, out h)) return false; NativeLibrary.Free(h); return true; }
        }
    }

    /// <summary>A sink (output) or source (input) as PulseAudio / PipeWire lists it.</summary>
    internal sealed class PulseDevice
    {
        public string Name, Description;
        public int Channels;
        public bool IsMonitor;
        public override string ToString() { return Description; }
    }

    /// <summary>
    /// Device management through pactl (pulseaudio-utils, or pipewire-pulse's pactl): StreamLite's virtual devices,
    /// listing, the default output and moving apps between outputs.
    /// </summary>
    internal static class Pactl
    {
        public static string Run(params string[] args)
        {
            var psi = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C";
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0) throw new InvalidOperationException("pactl " + string.Join(" ", args) + ": " + e.Trim());
                return o;
            }
        }

        public static bool Available
        {
            get { try { Run("info"); return true; } catch { return false; } }
        }

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
                if (l.StartsWith("Sink #") || l.StartsWith("Source #")) { cur = new PulseDevice(); list.Add(cur); continue; }
                if (cur == null) continue;
                if (l.StartsWith("Name: ")) { cur.Name = l.Substring(6).Trim(); cur.IsMonitor = cur.Name.EndsWith(".monitor"); }
                else if (l.StartsWith("Description: ")) cur.Description = l.Substring(13).Trim();
                else if (l.StartsWith("Sample Specification: ")) { var m = Regex.Match(l, @"(\d+)ch"); if (m.Success) cur.Channels = int.Parse(m.Groups[1].Value); }
            }
            foreach (var d in list) if (string.IsNullOrEmpty(d.Description)) d.Description = d.Name;
            return list;
        }

        /// <summary>Real outputs (StreamLite's own virtual ones left out).</summary>
        public static List<PulseDevice> Sinks() { return List("sinks").Where(d => !Linux.LinuxAudio.IsOurs(d.Name)).ToList(); }
        /// <summary>Real inputs (no monitors, no StreamLite devices).</summary>
        public static List<PulseDevice> Sources() { return List("sources").Where(d => !d.IsMonitor && !Linux.LinuxAudio.IsOurs(d.Name)).ToList(); }
        public static List<PulseDevice> AllSinks() { return List("sinks"); }
        public static List<PulseDevice> AllSources() { return List("sources"); }

        public static string DefaultSink()
        {
            var m = Regex.Match(Run("info"), @"Default Sink:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public static string DefaultSource()
        {
            var m = Regex.Match(Run("info"), @"Default Source:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

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

        public sealed class SinkInput { public int Index; public string Binary, AppName, Sink; public bool Corked; }

        /// <summary>Apps currently playing (each playback stream).</summary>
        public static List<SinkInput> SinkInputs()
        {
            var list = new List<SinkInput>();
            SinkInput cur = null;
            foreach (var raw in Run("list", "sink-inputs").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("Sink Input #")) { cur = new SinkInput { Index = int.Parse(l.Substring(12)) }; list.Add(cur); continue; }
                if (cur == null) continue;
                if (l.StartsWith("Sink: ")) cur.Sink = l.Substring(6).Trim();
                else if (l.StartsWith("Corked: ")) cur.Corked = l.EndsWith("yes");
                else if (l.StartsWith("application.process.binary = ")) cur.Binary = Unquote(l.Substring(29));
                else if (l.StartsWith("application.name = ")) cur.AppName = Unquote(l.Substring(19));
            }
            return list;
        }

        public static void MoveSinkInput(int index, string sink) { try { Run("move-sink-input", index.ToString(), sink); } catch { } }

        static string Unquote(string s) { s = s.Trim(); return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s; }
    }
}
