using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// StreamLite's virtual devices on PipeWire / PulseAudio - the Linux side of what the virtual cables are on Windows:
    ///   "StreamLite Mic"             the clean microphone apps record from (a virtual source)
    ///   "StreamLite Mic Feed"        where StreamLite writes the clean mic (internal, feeds the source above)
    ///   "StreamLite Headphones (EQ)" plays through your headphone correction and profile EQ to your headphones
    ///   "StreamLite Game Audio"      the 7.1 output for games, turned into 3D sound for your headphones
    /// They exist only while StreamLite runs (unloaded on exit; stale ones are cleaned up at start).
    /// </summary>
    internal static class LinuxAudio
    {
        public const string MicFeed = "streamlite_mic_feed", MicSource = "streamlite_mic", Headphones = "streamlite_headphones", Game = "streamlite_game";

        public static bool IsOurs(string name) { return name != null && name.StartsWith("streamlite_", StringComparison.Ordinal); }

        static readonly Dictionary<string, List<int>> modules = new Dictionary<string, List<int>>();
        static readonly object gate = new object();

        /// <summary>Removes StreamLite modules left behind by a run that didn't exit cleanly.</summary>
        public static void CleanStale()
        {
            try
            {
                foreach (var n in new[] { MicSource, MicFeed, Headphones, Game })
                    foreach (int i in Pactl.ModulesMentioning(n + " ").Concat(Pactl.ModulesMentioning(n + "\t")).Concat(Pactl.ModulesMentioning("=" + n)).Distinct().OrderByDescending(x => x))
                        Pactl.UnloadModule(i);
            }
            catch { }
            AudioDevices.Invalidate();
        }

        static bool Exists(string name, bool source)
        {
            var list = source ? Pactl.AllSources() : Pactl.AllSinks();
            return list.Any(d => d.Name == name);
        }

        static void Track(string key, int module)
        {
            List<int> l;
            if (!modules.TryGetValue(key, out l)) modules[key] = l = new List<int>();
            l.Add(module);
        }

        /// <summary>The clean-mic pair: a feed sink StreamLite plays into and the "StreamLite Mic" source apps record.</summary>
        public static void CreateMic()
        {
            lock (gate)
            {
                if (!Exists(MicFeed, false))
                    Track("mic", Pactl.LoadModule("module-null-sink", "sink_name=" + MicFeed, "rate=48000", "channels=1", "channel_map=mono",
                        "sink_properties=device.description='StreamLite Mic Feed (internal)' node.hidden=true"));
                if (!Exists(MicSource, true))
                    Track("mic", Pactl.LoadModule("module-remap-source", "master=" + MicFeed + ".monitor", "source_name=" + MicSource,
                        "source_properties=device.description='StreamLite Mic'"));
                AudioDevices.Invalidate();
            }
        }

        public static void CreateHeadphones()
        {
            lock (gate)
            {
                if (!Exists(Headphones, false))
                    Track("hp", Pactl.LoadModule("module-null-sink", "sink_name=" + Headphones, "rate=48000", "channels=2", "channel_map=front-left,front-right",
                        "sink_properties=device.description='StreamLite Headphones (EQ)'"));
                AudioDevices.Invalidate();
            }
        }

        public static void CreateGame()
        {
            lock (gate)
            {
                if (!Exists(Game, false))
                    Track("game", Pactl.LoadModule("module-null-sink", "sink_name=" + Game, "rate=48000", "channels=8",
                        "channel_map=front-left,front-right,front-center,lfe,rear-left,rear-right,side-left,side-right",
                        "sink_properties=device.description='StreamLite - Out (Game Audio 7.1)'"));
                AudioDevices.Invalidate();
            }
        }

        public static void Remove(string key)
        {
            lock (gate)
            {
                List<int> l;
                if (!modules.TryGetValue(key, out l)) return;
                foreach (int i in l.AsEnumerable().Reverse()) Pactl.UnloadModule(i);
                modules.Remove(key);
                AudioDevices.Invalidate();
            }
        }

        public static void RemoveAll() { foreach (var k in modules.Keys.ToList()) Remove(k); }

        /// <summary>Where StreamLite's own sounds for a pair of headphones go: through the headphone path (with your EQ) while it runs.</summary>
        public static string PlaybackFor(string headphones)
        {
            try { if (Pactl.AllSinks().Any(d => d.Name == Headphones)) return Headphones; } catch { }
            return headphones;
        }

        /// <summary>Real outputs (StreamLite's own left out).</summary>
        public static List<PulseDevice> RealSinks() { return Pactl.AllSinks().Where(d => !IsOurs(d.Name)).ToList(); }
        /// <summary>Real inputs (no monitors, no StreamLite devices).</summary>
        public static List<PulseDevice> RealSources() { return Pactl.AllSources().Where(d => !d.IsMonitor && !IsOurs(d.Name)).ToList(); }

        /// <summary>Moves every playing stream of the given programs (process names, any case) to a sink.</summary>
        public static int MoveApps(IEnumerable<string> binaries, string sink)
        {
            var want = new HashSet<string>(binaries.Select(Bare).Where(b => b.Length > 0));
            int n = 0;
            foreach (var si in Pactl.Streams(true))
            {
                string bin = Bare(si.Binary ?? AppOf(si.Pid) ?? "");
                if (!want.Contains(bin) || si.Device == sink) continue;
                Pactl.MoveSinkInput(si.Index, sink); n++;
            }
            return n;
        }

        /// <summary>"Discord.exe", "/usr/bin/firefox" -> "discord", "firefox".</summary>
        public static string Bare(string b)
        {
            b = (b ?? "").Trim();
            int slash = b.LastIndexOf('/'); if (slash >= 0) b = b.Substring(slash + 1);
            if (b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - 4);
            return b.ToLowerInvariant();
        }

        /// <summary>A process's program name (/proc/pid/comm), or null.</summary>
        public static string AppOf(int pid)
        {
            if (pid <= 0) return null;
            try { return File.ReadAllText("/proc/" + pid + "/comm").Trim(); } catch { return null; }
        }
    }

    /// <summary>Where StreamLite lives on Linux and how it's started.</summary>
    internal static class LinuxApp
    {
        /// <summary>The command that starts StreamLite (the installer's launcher on the PATH, or mono and this program).</summary>
        public static string Launcher
        {
            get
            {
                string l = Environment.GetEnvironmentVariable("STREAMLITE_LAUNCHER");
                if (!string.IsNullOrEmpty(l)) return l;
                foreach (var p in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/streamlite"), "/usr/local/bin/streamlite", "/usr/bin/streamlite" })
                    if (File.Exists(p)) return p;
                return "mono \"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"";
            }
        }

        /// <summary>Opens a file, folder or web page with the desktop's own app.</summary>
        public static void Open(string what)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", Shell.Quote(what)) { UseShellExecute = false }); } catch { }
        }
    }
}
