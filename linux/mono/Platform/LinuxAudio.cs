using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// StreamLite's virtual devices on PipeWire / PulseAudio - the Linux side of the virtual cables on Windows, same names:
    ///   "StreamLite - In"        the clean microphone apps record from (a virtual source)
    ///   "StreamLite - Mic Feed"  where StreamLite writes the clean mic. On PipeWire it's hidden, as on Windows (4.1.4):
    ///                            "StreamLite - In" is a virtual source StreamLite writes into directly (PwFeed), so no
    ///                            app sees a "Mic Feed" output. On PulseAudio it's a sink feeding the source above.
    ///   "StreamLite - Out"       the 7.1 output everything plays into, turned into EQ'd (and 3D) sound for your headphones
    ///   "StreamLite - Out (no 3D)" where apps kept out of 3D play: mixed in after the 3D, with your headphone EQ (what
    ///                            Equalizer APO does for them on Windows)
    /// They exist only while StreamLite runs (made at start, unloaded on exit; stale ones are cleaned up at start).
    /// </summary>
    internal static class LinuxAudio
    {
        public const string MicFeed = "streamlite_mic_feed", MicSource = "streamlite_mic", Game = "streamlite_game", Plain = "streamlite_plain";
        public const string PlainName = AppCore.GameOutName + " (no 3D)";

        public static bool IsOurs(string name) { return name != null && name.StartsWith("streamlite_", StringComparison.Ordinal); }

        static readonly Dictionary<string, List<int>> modules = new Dictionary<string, List<int>>();
        static readonly object gate = new object();

        /// <summary>Removes StreamLite modules left behind by a run that didn't exit cleanly.</summary>
        public static void CleanStale()
        {
            try
            {
                foreach (var n in new[] { MicSource, MicFeed, Game, Plain })
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

        /// <summary>Makes all of StreamLite's devices (what installing the cable drivers does on Windows).</summary>
        public static void CreateAll() { CreateMic(); CreateGame(); CreatePlain(); }

        // Properties in double quotes, the name in single quotes: the only form PulseAudio and PipeWire both read whole.
        // (No node.hidden: PipeWire won't play into a hidden sink and quietly sends the stream to the default instead.)
        static string Props(string description) { return "\"device.description='" + description + "' device.icon_name=audio-card\""; }

        /// <summary>
        /// The mic feed is hidden (PipeWire, with its pw-cat and pw-link tools): "StreamLite - In" is a virtual source that
        /// StreamLite writes into itself - no "StreamLite - Mic Feed" output for apps to pick or screen shares to record.
        /// </summary>
        public static bool HiddenFeed { get; private set; }

        /// <summary>PipeWire runs (pipewire-pulse) with the tools to feed a virtual source directly.</summary>
        static bool CanHideFeed()
        {
            try { return Shell.Has("pw-cat") && Shell.Has("pw-link") && Pactl.ServerName().IndexOf("PipeWire", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        /// <summary>
        /// "StreamLite - In": on PipeWire a virtual source StreamLite writes into (hidden feed); otherwise a feed sink
        /// StreamLite plays into and a source made from its monitor.
        /// </summary>
        public static void CreateMic()
        {
            lock (gate)
            {
                if (CanHideFeed() && Environment.GetEnvironmentVariable("STREAMLITE_VISIBLE_FEED") != "1")
                {
                    if (!Exists(MicSource, true))
                        Track("mic", Pactl.LoadModule("module-null-sink", "media.class=Audio/Source/Virtual", "sink_name=" + MicSource, "rate=48000", "channels=1", "channel_map=mono",
                            "sink_properties=" + Props(AppCore.MicName)));
                    HiddenFeed = true;
                    AudioDevices.Invalidate();
                    return;
                }
                HiddenFeed = false;
                if (!Exists(MicFeed, false))
                    Track("mic", Pactl.LoadModule("module-null-sink", "sink_name=" + MicFeed, "rate=48000", "channels=1", "channel_map=mono",
                        "sink_properties=" + Props(AppCore.MicFeedName)));
                if (!Exists(MicSource, true))
                    Track("mic", Pactl.LoadModule("module-remap-source", "master=" + MicFeed + ".monitor", "source_name=" + MicSource,
                        "source_properties=" + Props(AppCore.MicName)));
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
                        "sink_properties=" + Props(AppCore.GameOutName)));
                AudioDevices.Invalidate();
            }
        }

        public static void CreatePlain()
        {
            lock (gate)
            {
                if (!Exists(Plain, false))
                    Track("plain", Pactl.LoadModule("module-null-sink", "sink_name=" + Plain, "rate=48000", "channels=2", "channel_map=front-left,front-right",
                        "sink_properties=" + Props(PlainName)));
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

        /// <summary>The headphones 3D audio plays on right now (set by the engine), or null.</summary>
        public static volatile string EngineHeadphones;

        /// <summary>
        /// Where sound for these headphones goes: while 3D audio plays on them, into "StreamLite - Out (no 3D)" - mixed in
        /// with the EQ and never in 3D, as Equalizer APO gives every app on Windows; otherwise straight to them.
        /// </summary>
        public static string PlaybackFor(string headphones)
        {
            return headphones != null && headphones == EngineHeadphones ? Plain : headphones;
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

        /// <summary>
        /// A note on the desktop (notify-send: GNOME, KDE, Xfce... all show it), where Windows shows a tray balloon.
        /// False when there's no notify-send (the tray balloon is used then).
        /// </summary>
        public static bool Notify(string title, string text, int ms, bool warning)
        {
            if (!Shell.Has("notify-send")) return false;
            try
            {
                var args = new[] { "--app-name=StreamLite", "--icon=streamlite", "--expire-time=" + ms, "--urgency=" + (warning ? "critical" : "normal"), title, text };
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notify-send", string.Join(" ", args.Select(Shell.Quote))) { UseShellExecute = false }).Dispose();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Opens a file, folder or web page with the desktop's own app.</summary>
        public static void Open(string what)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", Shell.Quote(what)) { UseShellExecute = false }); } catch { }
        }
    }
}
