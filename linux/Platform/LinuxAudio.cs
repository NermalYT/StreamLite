using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// StreamLite's virtual devices on PipeWire / PulseAudio:
    ///   "StreamLite Mic"            - the clean microphone apps record from (a remapped source)
    ///   "StreamLite Mic Feed"       - where StreamLite writes the clean mic (internal; feeds the source above)
    ///   "StreamLite Headphones (EQ)" - plays through your headphone correction + profile EQ
    ///   "StreamLite Game Audio"      - 7.1 output for games, turned into 3D sound for your headphones
    /// They exist only while StreamLite runs (modules are unloaded on exit, and stale ones cleaned at start).
    /// </summary>
    internal static class LinuxAudio
    {
        public const string MicFeed = "streamlite_mic_feed", MicSource = "streamlite_mic", Headphones = "streamlite_headphones", Game = "streamlite_game";

        public static bool IsOurs(string name) { return name != null && name.StartsWith("streamlite_", StringComparison.Ordinal); }

        static readonly Dictionary<string, List<int>> modules = new Dictionary<string, List<int>>();

        /// <summary>Removes StreamLite modules left behind by a previous run that didn't exit cleanly.</summary>
        public static void CleanStale()
        {
            foreach (var n in new[] { MicSource, MicFeed, Headphones, Game })
                foreach (int i in Pactl.ModulesMentioning(n + " ").Concat(Pactl.ModulesMentioning(n + "\t")).Concat(Pactl.ModulesMentioning("=" + n)).Distinct().OrderByDescending(x => x))
                    Pactl.UnloadModule(i);
        }

        static bool Exists(string sinkOrSource, bool source)
        {
            var list = source ? Pactl.AllSources() : Pactl.AllSinks();
            return list.Any(d => d.Name == sinkOrSource);
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
            if (!Exists(MicFeed, false))
                Track("mic", Pactl.LoadModule("module-null-sink", "sink_name=" + MicFeed, "rate=48000", "channels=1", "channel_map=mono",
                    "sink_properties='device.description=\"StreamLite Mic Feed (internal)\"'"));
            if (!Exists(MicSource, true))
                Track("mic", Pactl.LoadModule("module-remap-source", "master=" + MicFeed + ".monitor", "source_name=" + MicSource,
                    "source_properties='device.description=\"StreamLite Mic\"'"));
        }

        public static void CreateHeadphones()
        {
            if (!Exists(Headphones, false))
                Track("hp", Pactl.LoadModule("module-null-sink", "sink_name=" + Headphones, "rate=48000", "channels=2", "channel_map=front-left,front-right",
                    "sink_properties='device.description=\"StreamLite Headphones (EQ)\"'"));
        }

        public static void CreateGame()
        {
            if (!Exists(Game, false))
                Track("game", Pactl.LoadModule("module-null-sink", "sink_name=" + Game, "rate=48000", "channels=8",
                    "channel_map=front-left,front-right,front-center,lfe,rear-left,rear-right,side-left,side-right",
                    "sink_properties='device.description=\"StreamLite Game Audio (7.1)\"'"));
        }

        public static void Remove(string key)
        {
            List<int> l;
            if (!modules.TryGetValue(key, out l)) return;
            foreach (int i in l.AsEnumerable().Reverse()) Pactl.UnloadModule(i);
            modules.Remove(key);
        }

        public static void RemoveAll() { foreach (var k in modules.Keys.ToList()) Remove(k); }

        /// <summary>A real output to send processed sound to: the remembered one, else the default, else the first.</summary>
        public static string PickOutput(string wanted)
        {
            var sinks = Pactl.Sinks();
            if (wanted != null && sinks.Any(s => s.Name == wanted)) return wanted;
            string def = Pactl.DefaultSink();
            if (def != null && !IsOurs(def) && sinks.Any(s => s.Name == def)) return def;
            return sinks.Count > 0 ? sinks[0].Name : null;
        }

        public static string PickInput(string wanted)
        {
            var src = Pactl.Sources();
            if (wanted != null && src.Any(s => s.Name == wanted)) return wanted;
            string def = Pactl.DefaultSource();
            if (def != null && !IsOurs(def) && src.Any(s => s.Name == def)) return def;
            return src.Count > 0 ? src[0].Name : null;
        }

        /// <summary>Moves every playing stream of the given programs (process names, any case) to a sink.</summary>
        public static int MoveApps(IEnumerable<string> binaries, string sink)
        {
            var want = new HashSet<string>(binaries.Select(b => b.Trim().ToLowerInvariant()).Where(b => b.Length > 0));
            int n = 0;
            foreach (var si in Pactl.SinkInputs())
            {
                string bin = (si.Binary ?? "").ToLowerInvariant();
                if (bin.EndsWith(".exe")) bin = bin.Substring(0, bin.Length - 4);
                if (!want.Contains(bin) || si.Sink == sink) continue;
                Pactl.MoveSinkInput(si.Index, sink); n++;
            }
            return n;
        }
    }
}
