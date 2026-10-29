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
            /// <summary>A correction StreamLite has itself (a model AutoEq hasn't measured yet): no download.</summary>
            public List<EqFilter> Builtin;
            /// <summary>"Your model, without a correction": for a model nobody has measured yet.</summary>
            public bool NoCorrection;
            /// <summary>A correction made some other way (a squig.link measurement, fitted by StreamLite): called instead of downloading AutoEq's file.</summary>
            public Func<List<EqFilter>> Fetch;
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
            var list = Parse(text);
            list.InsertRange(0, Builtins());
            return list;
        }

        /// <summary>
        /// Corrections StreamLite ships itself, for headphones AutoEq hasn't measured yet - found by "Find my headphones"
        /// and picked automatically like AutoEq's.
        /// </summary>
        public static List<Entry> Builtins()
        {
            return new List<Entry>
            {
                new Entry { Name = "Sennheiser MOMENTUM 5", Path = "", Source = "StreamLite's own, from SoundGuys' B&K 5128 measurement", Builtin = StreamLite.GameProfiles.Momentum5Correction() },
            };
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
            if (e.Builtin != null) return 2;   // a calibrated rig, like Rtings'
            if (e.Fetch != null) return Preferred.Length + 1;   // the backup: AutoEq's own come first
            for (int i = 0; i < Preferred.Length; i++) if (e.Source.StartsWith(Preferred[i], StringComparison.OrdinalIgnoreCase)) return i;
            return Preferred.Length;
        }

        /// <summary>Entries whose name contains every word of the query (or all of it run together), best matches first.</summary>
        public static List<Entry> Search(List<Entry> index, string query)
        {
            var words = Words(query);
            if (words.Length == 0) return new List<Entry>();
            string whole = Compact(string.Join("", words));
            return index.Where(e =>
                {
                    string n = e.Name.ToLowerInvariant(), compact = Compact(n);
                    return words.All(w => Has(n, compact, w)) || (whole.Length >= 5 && compact.Contains(whole));
                })
                .OrderBy(e => e.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(e => MatchKind(e.Name, words))
                .ThenBy(e => e.Name.Length).ThenBy(SourceRank).Take(200).ToList();
        }

        /// <summary>
        /// How well the query's words match the name's own words (4.1.4: "hd 600" found "Denon AH-D600" first, inside
        /// "d600"): 0 every word is one of the name's words (or two of them run together, "hd600"), 1 every word starts
        /// one, 2 found only inside a word.
        /// </summary>
        static int MatchKind(string name, string[] words)
        {
            var tokens = Regex.Split(name.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0).ToList();
            var whole = new HashSet<string>(tokens);
            for (int i = 0; i + 1 < tokens.Count; i++) whole.Add(tokens[i] + tokens[i + 1]);
            int kind = 0;
            var cs = words.Select(Compact).ToArray();
            for (int i = 0; i < cs.Length; i++)
            {
                string c = cs[i];
                if (c.Length == 0 || whole.Contains(c)) continue;
                // Two of your words that are one of the name's ("pro 2" for "Pro2").
                if ((i + 1 < cs.Length && whole.Contains(c + cs[i + 1])) || (i > 0 && whole.Contains(cs[i - 1] + c))) continue;
                kind = Math.Max(kind, tokens.Any(t => t.StartsWith(c, StringComparison.Ordinal)) ? 1 : 2);
            }
            return kind;
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

        // Words a headset's Windows name carries that AutoEq's model names don't ("... Wireless Gaming Dongle").
        static readonly HashSet<string> Soft = new HashSet<string> { "wireless", "dongle", "receiver", "transmitter", "adapter", "2.4ghz", "2.4", "ghz", "lightspeed", "hyperspeed", "slipstream", "base", "station" };

        /// <summary>
        /// The correction for a device from every name Windows has for it (DeviceNames: what it shows, your own name for
        /// it, the USB product string...): the first that names a model for certain.
        /// </summary>
        public static Entry BestFor(List<Entry> index, IEnumerable<string> deviceNames)
        {
            foreach (var n in deviceNames) { var e = BestFor(index, n); if (e != null) return e; }
            return null;
        }

        /// <summary>
        /// The correction for a device by its Windows name, only when the model is certain: every word of the name
        /// (except "wireless", "dongle" and such) is in an AutoEq model's name. Null when unsure - "Realtek Audio",
        /// "Speakers (High Definition Audio Device)", or a model AutoEq hasn't measured.
        /// </summary>
        public static Entry BestFor(List<Entry> index, string deviceName)
        {
            string q = GuessQuery(deviceName);
            var words = Words(q);
            if (words.Length == 0 || (words.Length == 1 && !words[0].Any(char.IsDigit) && words[0].Length < 6)) return null;   // "sony" alone isn't a model
            var found = Search(index, q);
            if (found.Count == 0)
            {
                var core = words.Where(w => !Soft.Contains(w)).ToArray();
                if (core.Length == 0 || core.Length == words.Length) return null;
                found = Search(index, string.Join(" ", core));
            }
            // Over-ears and earbuds of one model line ("QC Ultra Headphones" / "QC Ultra Earbuds"): the kind the name says.
            string kind = Regex.IsMatch(deviceName, @"\(.*\b(earbuds|buds)\b", RegexOptions.IgnoreCase) ? "earbuds"
                : Regex.IsMatch(deviceName, @"\(.*\bheadphones\b.*\)", RegexOptions.IgnoreCase) ? "headphones" : null;
            if (kind != null)
            {
                string other = kind == "earbuds" ? "headphones" : "earbuds";
                var same = found.Where(e => e.Name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                found = same.Count > 0 ? same : found.Where(e => e.Name.IndexOf(other, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            }
            if (found.Count == 0) return null;
            // Several different models match ("arctis 7" -> Arctis 7, Arctis 7+, Arctis 7P): only the shortest name, which
            // is all the words and nothing more, counts - and only if that name is the model's whole name: the brand in
            // front may be missing (Windows often leaves it out), nothing else ("A50" isn't the "A50 X") - besides
            // "wireless", "gaming" and such.
            return WholeModel(found[0].Name, words) ? found[0] : null;
        }

        /// <summary>
        /// An AutoEq model name is all the query and nothing more: each part of the name is a word of the query, one run
        /// together with the part before it ("HD 660 S" for "660s"), or a word that says nothing about the model
        /// ("Wireless", "Gaming"); only the first part (the brand) may be missing.
        /// </summary>
        static bool WholeModel(string modelName, string[] queryWords)
        {
            string query = Compact(string.Join("", queryWords));
            var parts = Regex.Replace(modelName.ToLowerInvariant(), @"\s*&\s*", "&").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var named = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var ws = Words(parts[i]);
                named[i] = ws.Length == 0 || ws.All(w => Soft.Contains(w) || Generic.Contains(w) || queryWords.Any(x => Has(w, Compact(w), x)));
            }
            for (int i = 1; i < parts.Length; i++)
                if ((!named[i] || !named[i - 1]) && query.Contains(Compact(parts[i - 1] + parts[i]))) named[i] = named[i - 1] = true;
            int missing = named.Count(n => !n);
            return missing == 0 || (missing == 1 && !named[0]);
        }

        static string Compact(string s) { return Regex.Replace(s, @"[\s\-_/.,()]+", ""); }

        // "Buds2" matches "Buds 2" (compact form), "2" matches "II" and the other way round.
        static readonly Dictionary<string, string> Roman = new Dictionary<string, string> { { "ii", "2" }, { "iii", "3" }, { "iv", "4" }, { "2", "ii" }, { "3", "iii" }, { "4", "iv" } };

        /// <summary>
        /// A query word in a model's name: where one of its words starts ("x" in "Pro X", not the x in "EDX"), or - four
        /// letters or more - anywhere in the name run together ("buds2" in "Buds 2").
        /// </summary>
        static bool Has(string name, string compact, string w)
        {
            for (int i = name.IndexOf(w, StringComparison.Ordinal); i >= 0; i = name.IndexOf(w, i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetterOrDigit(name[i - 1])) return true;
            string cw = Compact(w);
            if (cw.Length >= 4 && compact.Contains(cw)) return true;
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

        static readonly Regex AdapterRx = new Regex(@"\b(dongle|transmitter|receiver|adapter|adaptor|QCC\w*|USB Audio|Bluetooth Audio|BT Audio)\b",RegexOptions.IgnoreCase);

        /// <summary>
        /// A wireless dongle / adapter's name ("QCC Dongle Pro", "USB Audio Adapter"): Windows sees the dongle, not the
        /// headphones behind it, so its name says nothing about which model you have.
        /// </summary>
        public static bool IsAdapter(string deviceName) { return !string.IsNullOrEmpty(deviceName) && AdapterRx.IsMatch(deviceName); }

        static string Clean(string s)
        {
            s = Regex.Replace(s, @"^\s*\d+\s*-\s*", "");                    // Windows numbers duplicates: "2- Arctis 7"
            s = Regex.Replace(s, @"\S+['’]s\b", "");                    // "Sam's AirPods" -> "AirPods"
            s = Regex.Replace(s, @"^\s*LE[_-]", "", RegexOptions.IgnoreCase);              // Bluetooth LE Audio: "LE_WH-1000XM5"
            s = Regex.Replace(s, @"\bQC\s?(\d+|Ultra)\b", "QuietComfort $1", RegexOptions.IgnoreCase);   // Bose names them in full
            s = Regex.Replace(s, @"\bNC\s?700(\s?HP)?\b", "Noise Cancelling Headphones 700", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTW\s?(\d)\b", "True Wireless $1", RegexOptions.IgnoreCase);   // "MOMENTUM TW 3"
            s = Regex.Replace(s, @"^\s*PRO X\b", "Logitech G Pro X", RegexOptions.IgnoreCase);       // Logitech's "PRO X Wireless Gaming Headset"
            return string.Join(" ", Words(s).Where(w => !Generic.Contains(w)));
        }

        /// <summary>Downloads the correction for one entry (AutoEq's ParametricEQ.txt, Equalizer APO format).</summary>
        public static List<EqFilter> Download(Entry e, out double preamp)
        {
            preamp = 0;
            if (e.NoCorrection) return new List<EqFilter>();
            if (e.Builtin != null) return e.Builtin.Select(f => f.Copy()).ToList();
            if (e.Fetch != null) return e.Fetch();
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
