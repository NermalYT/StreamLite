using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace StreamLite.Audio
{
    /// <summary>
    /// Headphone corrections from AutoEq (github.com/jaakkopasanen/AutoEq, MIT): measured responses of thousands of
    /// headphones and earbuds, each with a parametric EQ that takes it to a neutral target. StreamLite downloads the
    /// index once (cached for two weeks) and the one correction you pick - nothing is bundled.
    /// </summary>
    public static class AutoEq
    {
        const string Base = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";

        public sealed class Entry
        {
            public string Name, Path, Source;
            public override string ToString() { return Name + "   (" + Source + ")"; }
        }

        static readonly Regex Line = new Regex(@"^- \[(.+?)\]\(\./(.+?)\) by (.+)$");

        static WebClient Client()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StreamLite";
            wc.Encoding = System.Text.Encoding.UTF8;
            return wc;
        }

        /// <summary>All headphones AutoEq has a correction for (downloads the index if the cache is missing or old).</summary>
        public static List<Entry> Index(string cacheDir)
        {
            string cache = System.IO.Path.Combine(cacheDir, "autoeq-index.md");
            string text = null;
            bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(14);
            if (!fresh)
            {
                try
                {
                    using (var wc = Client()) text = wc.DownloadString(Base + "INDEX.md");
                    Directory.CreateDirectory(cacheDir);
                    File.WriteAllText(cache, text);
                }
                catch { if (!File.Exists(cache)) throw; }
            }
            if (text == null) text = File.ReadAllText(cache);
            return Parse(text);
        }

        public static List<Entry> Parse(string indexText)
        {
            var list = new List<Entry>();
            foreach (var raw in indexText.Split('\n'))
            {
                var m = Line.Match(raw.TrimEnd('\r'));
                if (m.Success) list.Add(new Entry { Name = m.Groups[1].Value, Path = m.Groups[2].Value, Source = m.Groups[3].Value });
            }
            return list;
        }

        // Measurement sources in order of preference (calibrated rigs first).
        static readonly string[] Preferred = { "oratory1990", "crinacle", "Rtings", "Innerfidelity", "HypetheSonics", "Headphone.com Legacy" };

        static int SourceRank(Entry e)
        {
            for (int i = 0; i < Preferred.Length; i++) if (e.Source.StartsWith(Preferred[i], StringComparison.OrdinalIgnoreCase)) return i;
            return Preferred.Length;
        }

        /// <summary>Entries whose name contains every word of the query, best matches first.</summary>
        public static List<Entry> Search(List<Entry> index, string query)
        {
            var words = Words(query);
            if (words.Length == 0) return new List<Entry>();
            return index.Where(e =>
                {
                    string n = e.Name.ToLowerInvariant(), compact = Compact(n);
                    return words.All(w => Has(n, compact, w));
                })
                .OrderBy(e => e.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.Name.Length).ThenBy(SourceRank).Take(200).ToList();
        }

        /// <summary>
        /// Search, and when the exact model isn't measured, drop words from the end until something is found
        /// ("hyperx cloud alpha wireless" -> "hyperx cloud alpha"). used = the words that matched ("" = nothing),
        /// relaxed = some words had to go.
        /// </summary>
        public static List<Entry> SearchBest(List<Entry> index, string query, out string used, out bool relaxed)
        {
            var words = Words(query).ToList();
            int all = words.Count;
            while (words.Count > 0)
            {
                used = string.Join(" ", words);
                var found = Search(index, used);
                relaxed = words.Count < all;
                if (found.Count > 0) return found;
                words.RemoveAt(words.Count - 1);
            }
            used = ""; relaxed = false;
            return new List<Entry>();
        }

        static string Compact(string s) { return Regex.Replace(s, @"[\s\-_/.,()]+", ""); }

        // "Buds2" matches "Buds 2" (compact form), "2" matches "II" and the other way round.
        static readonly Dictionary<string, string> Roman = new Dictionary<string, string> { { "ii", "2" }, { "iii", "3" }, { "iv", "4" }, { "2", "ii" }, { "3", "iii" }, { "4", "iv" } };

        static bool Has(string name, string compact, string w)
        {
            if (name.Contains(w) || compact.Contains(Compact(w))) return true;
            string alt;
            return Roman.TryGetValue(w, out alt) && Regex.IsMatch(name, @"\b" + alt + @"\b");
        }

        static string[] Words(string s)
        {
            return (s ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_', '(', ')', ',', '/', '’', '"' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static readonly HashSet<string> Generic = new HashSet<string>
        {
            // what Windows, PipeWire and Bluetooth add to a device name
            "headphones", "headphone", "headset", "earphones", "earphone", "earbuds", "speakers", "speaker", "usb", "audio", "device",
            "stereo", "mono", "hands-free", "handsfree", "hands", "free", "ag", "hfp", "hsp", "a2dp", "sink", "source", "bluetooth", "bt",
            "digital", "analog", "analogue", "output", "input", "iec958", "multichannel", "surround", "virtual", "7.1", "5.1",
            "high", "definition", "realtek", "r", "tm", "for", "gaming", "game", "chat", "voice", "microphone", "mic",
            // a few other Windows languages
            "kopfhörer", "lautsprecher", "casque", "écouteurs", "haut-parleurs", "auriculares", "altavoces", "cuffie", "altoparlanti",
            "fones", "alto-falantes", "słuchawki", "głośniki", "наушники", "динамики", "freisprechfunktion", "mains-libres", "manos",
        };

        /// <summary>
        /// A search to start from, made from a Windows / PipeWire device name: "Headphones (2- HyperX Cloud II Wireless)"
        /// -> "hyperx cloud ii wireless", "Headphones (Sam's AirPods Pro)" -> "airpods pro". Generic words and the owner's
        /// name that Bluetooth devices carry are dropped; model numbers stay.
        /// </summary>
        public static string GuessQuery(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return "";
            string s = Regex.Replace(deviceName, @"\(R\)|\(TM\)|®|™", "", RegexOptions.IgnoreCase);
            int a = s.IndexOf('('), b = s.LastIndexOf(')');
            if (a >= 0 && b > a)
            {
                // Windows: "Headphones (Model)" - the adapter part is the model. PipeWire: "Model Digital Stereo (IEC958)".
                string inner = Clean(s.Substring(a + 1, b - a - 1));
                if (inner.Length > 0) return inner;
                return Clean(s.Substring(0, a) + " " + s.Substring(b + 1));
            }
            return Clean(s);
        }

        static string Clean(string s)
        {
            s = Regex.Replace(s, @"^\s*\d+\s*-\s*", "");                    // Windows numbers duplicates: "2- Arctis 7"
            s = Regex.Replace(s, @"\S+['’]s\b", "");                    // "Sam's AirPods" -> "AirPods"
            s = Regex.Replace(s, @"\bQC\s?(\d+|Ultra)\b", "QuietComfort $1", RegexOptions.IgnoreCase);   // Bose names them in full
            return string.Join(" ", Words(s).Where(w => !Generic.Contains(w)));
        }

        /// <summary>Downloads the correction for one entry (AutoEq's ParametricEQ.txt, Equalizer APO format).</summary>
        public static List<EqFilter> Download(Entry e, out double preamp)
        {
            string url = Base + e.Path + "/" + Uri.EscapeDataString(e.Name + " ParametricEQ.txt");
            string text;
            using (var wc = Client()) text = wc.DownloadString(url);
            var filters = ParseParametric(text, out preamp);
            if (filters.Count == 0) throw new InvalidDataException("No filters in the AutoEq file for " + e.Name + ".");
            return filters;
        }

        static readonly Regex FilterRx = new Regex(@"^\s*Filter\s*\d*\s*:\s*(ON|OFF)\s+([A-Z]+)\s+Fc\s+([\d.]+)\s*Hz(?:\s+Gain\s+(-?[\d.]+)\s*dB)?(?:\s+Q\s+([\d.]+))?", RegexOptions.IgnoreCase);
        static readonly Regex PreampRx = new Regex(@"^\s*Preamp\s*:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase);

        /// <summary>Parses an Equalizer APO / AutoEq parametric EQ text (LSC / PK / HSC filters).</summary>
        public static List<EqFilter> ParseParametric(string text, out double preamp)
        {
            preamp = 0;
            var inv = CultureInfo.InvariantCulture;
            var list = new List<EqFilter>();
            foreach (var raw in text.Split('\n'))
            {
                var pm = PreampRx.Match(raw);
                if (pm.Success) { preamp = double.Parse(pm.Groups[1].Value, inv); continue; }
                var m = FilterRx.Match(raw);
                if (!m.Success || !m.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase)) continue;
                string type = m.Groups[2].Value.ToUpperInvariant();
                if (type == "LS") type = "LSC"; else if (type == "HS") type = "HSC";
                if (type != "PK" && type != "LSC" && type != "HSC") continue;
                list.Add(new EqFilter
                {
                    Type = type, Freq = double.Parse(m.Groups[3].Value, inv),
                    Gain = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, inv) : 0,
                    Q = m.Groups[5].Success ? double.Parse(m.Groups[5].Value, inv) : 0.71
                });
            }
            return list;
        }
    }
}
