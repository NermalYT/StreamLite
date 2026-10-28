using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace StreamLite.Audio
{
    /// <summary>
    /// The backup for headphones AutoEq hasn't measured (4.0.7): squig.link, the network of measurement databases run
    /// by reviewers (squig.link/squigsites.json lists ~150 of them - IEMs, headphones, gaming headsets, earbuds). They
    /// publish raw frequency responses, not EQs, so StreamLite makes the EQ itself (PeqFit) against AutoEq's Harman
    /// target for the kind of rig the database uses. The list is cached for two weeks, a measurement is downloaded only
    /// for the model you pick.
    /// </summary>
    public static class SquigLink
    {
        const string Sites = "https://squig.link/squigsites.json";
        const string Targets = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/targets/";

        /// <summary>One measured model: its name, the database's address, its file name and the kind of database.</summary>
        public sealed class Measurement { public string Name, Db, File, Kind, Site; }

        /// <summary>A download that gives up after a few seconds (4.1.1: a database that doesn't answer used to hold the
        /// whole lookup for 100 s - the setup sat on "Looking up the EQ..." for minutes).</summary>
        sealed class TimedClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                r.Timeout = 8000;
                var h = r as HttpWebRequest; if (h != null) h.ReadWriteTimeout = 8000;
                return r;
            }
        }

        static WebClient Client()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            try { if (ServicePointManager.DefaultConnectionLimit < 12) ServicePointManager.DefaultConnectionLimit = 12; } catch { }
            var wc = new TimedClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StreamLite";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        /// <summary>Every model in every squig.link database (from the cache when it's under two weeks old).</summary>
        static readonly object indexGate = new object();

        /// <summary>One download at a time (Find my headphones and a new headset's lookup can ask together): the second waits for the cache.</summary>
        public static List<Measurement> Index(string cacheDir) { lock (indexGate) return IndexOnce(cacheDir); }

        static List<Measurement> IndexOnce(string cacheDir)
        {
            string cache = Path.Combine(cacheDir, "squig-index.tsv");
            bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(14);
            if (fresh) return Read(File.ReadAllLines(cache));
            List<Measurement> list;
            try { list = Fetch(); }
            catch { if (File.Exists(cache)) return Read(File.ReadAllLines(cache)); throw; }
            if (list.Count > 0)
            {
                try { Directory.CreateDirectory(cacheDir); File.WriteAllLines(cache, list.Select(m => string.Join("\t", m.Name, m.Kind, m.Db, m.File, m.Site))); }
                catch { }   // not cached this time: the list is still good
            }
            return list;
        }

        static List<Measurement> Read(IEnumerable<string> lines)
        {
            return lines.Select(l => l.Split('\t')).Where(p => p.Length >= 5)
                .Select(p => new Measurement { Name = p[0], Kind = p[1], Db = p[2], File = p[3], Site = p[4] }).ToList();
        }

        /// <summary>The databases' phone books, downloaded side by side (about 150 small files).</summary>
        static List<Measurement> Fetch()
        {
            string sitesJson; using (var wc = Client()) sitesJson = wc.DownloadString(Sites);
            var dbs = Databases(sitesJson);
            var found = new List<Measurement>[dbs.Count];
            int next = -1;
            var workers = Enumerable.Range(0, 12).Select(_ => new Thread(() =>
            {
                for (int i; (i = Interlocked.Increment(ref next)) < dbs.Count;)
                {
                    try { using (var wc = Client()) found[i] = PhoneBook(wc.DownloadString(dbs[i].Item1 + "data/phone_book.json"), dbs[i].Item1, dbs[i].Item2, dbs[i].Item3); }
                    catch { found[i] = new List<Measurement>(); }
                }
            }) { IsBackground = true }).ToList();
            workers.ForEach(w => w.Start());
            // 25 seconds for all of them together; the ones still out are left out this time.
            var until = DateTime.UtcNow.AddSeconds(25);
            foreach (var w in workers) w.Join(Math.Max(0, (int)(until - DateTime.UtcNow).TotalMilliseconds));
            lock (found) return found.Where(f => f != null).SelectMany(f => f).ToList();
        }

        /// <summary>Each database's address, kind (IEMs, Headphones, Earbuds, 5128) and site name from squigsites.json.</summary>
        internal static List<Tuple<string, string, string>> Databases(string sitesJson)
        {
            var list = new List<Tuple<string, string, string>>();
            var sites = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(sitesJson) as object[];
            if (sites == null) return list;
            foreach (Dictionary<string, object> s in sites.OfType<Dictionary<string, object>>())
            {
                string user = Str(s, "username"), type = Str(s, "urlType"), name = Str(s, "name") ?? user, root;
                if (type == "subdomain") root = "https://" + user + ".squig.link";
                else if (type == "labFolder") root = "https://squig.link/lab/" + user;
                else if (type == "root") root = "https://squig.link";
                else if (type == "altDomain") root = (Str(s, "altDomain") ?? "").TrimEnd('/');
                else continue;
                if (string.IsNullOrEmpty(user) || !root.StartsWith("https://")) continue;
                object dbs; if (!s.TryGetValue("dbs", out dbs) || !(dbs is object[])) continue;
                foreach (var d in ((object[])dbs).OfType<Dictionary<string, object>>())
                {
                    string folder = Str(d, "folder") ?? "/";
                    list.Add(Tuple.Create(root + (folder.StartsWith("/") ? folder : "/" + folder).TrimEnd('/') + "/", Str(d, "type") ?? "IEMs", name));
                }
            }
            return list;
        }

        /// <summary>
        /// squig.link's models as AutoEq entries, for the ones AutoEq hasn't got (same name, ignoring case and spacing):
        /// one entry a model, the first database that has it. Picking one fits its EQ (Fetch).
        /// </summary>
        public static List<AutoEq.Entry> Extra(List<Measurement> ms, IEnumerable<AutoEq.Entry> have, string cacheDir)
        {
            var seen = new HashSet<string>(have.Select(e => Key(e.Name)));
            var list = new List<AutoEq.Entry>();
            foreach (var m in ms)
            {
                if (!seen.Add(Key(m.Name))) continue;
                var mm = m;
                list.Add(new AutoEq.Entry { Name = m.Name, Path = "", Source = m.Site + " on squig.link - EQ made by StreamLite", Fetch = () => Correction(mm, cacheDir) });
            }
            return list;
        }

        static string Key(string name) { return Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", ""); }

        /// <summary>
        /// The EQ for a device by every name Windows has for it, only when the model is certain: AutoEq's first, then
        /// squig.link's (a model AutoEq hasn't measured). Null when neither is certain.
        /// </summary>
        public static AutoEq.Entry BestFor(string cacheDir, IEnumerable<string> deviceNames)
        {
            bool complete; return BestFor(cacheDir, deviceNames, out complete);
        }

        /// <summary>complete = both lists were looked through (false: squig.link couldn't be reached - worth another try later).</summary>
        public static AutoEq.Entry BestFor(string cacheDir, IEnumerable<string> deviceNames, out bool complete)
        {
            var names = deviceNames.ToList();
            var main = AutoEq.Index(cacheDir);
            var e = AutoEq.BestFor(main, names);
            complete = true;
            if (e != null) return e;
            List<Measurement> ms;
            try { ms = Index(cacheDir); } catch { complete = false; return null; }
            return AutoEq.BestFor(Extra(ms, main, cacheDir), names);
        }

        /// <summary>AutoEq's list with squig.link's extra models after it (squig.link left out if it can't be reached).</summary>
        public static List<AutoEq.Entry> All(string cacheDir)
        {
            var list = AutoEq.Index(cacheDir);
            try { list.AddRange(Extra(Index(cacheDir), list, cacheDir)); } catch { }
            return list;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }

        /// <summary>A database's phone book: brands, each with models; a model with several files (pads, versions) gives one entry each.</summary>
        internal static List<Measurement> PhoneBook(string json, string db, string kind, string site)
        {
            var list = new List<Measurement>();
            var brands = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as object[];
            if (brands == null) return list;
            foreach (var b in brands.OfType<Dictionary<string, object>>())
            {
                string brand = Str(b, "name") ?? "";
                object phones; if (!b.TryGetValue("phones", out phones) || !(phones is object[])) continue;
                foreach (var p in (object[])phones)
                {
                    string model; object file = null, suffix = null;
                    if (p is string) model = (string)p;
                    else
                    {
                        var pd = p as Dictionary<string, object>; if (pd == null) continue;
                        object n; pd.TryGetValue("name", out n); pd.TryGetValue("file", out file); pd.TryGetValue("suffix", out suffix);
                        model = n is object[] ? ((object[])n).FirstOrDefault() as string : n as string;
                    }
                    if (string.IsNullOrWhiteSpace(model)) continue;
                    string full = model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) || brand.Length == 0 ? model : brand + " " + model;
                    var files = file is object[] ? ((object[])file).Select(f => f as string).ToArray() : new[] { (file as string) ?? model };
                    var suffixes = suffix as object[];
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(files[i])) continue;
                        string sfx = files.Length > 1 && suffixes != null && i < suffixes.Length ? " " + suffixes[i] : files.Length > 1 && i > 0 ? " (" + files[i] + ")" : "";
                        list.Add(new Measurement { Name = Clean(full + sfx), Db = db, File = files[i], Kind = kind, Site = site });
                    }
                }
            }
            return list;
        }

        static string Clean(string s) { return Regex.Replace(s.Replace('\t', ' '), @"\s+", " ").Trim(); }

        // ------------------------------------------------------------------ one model's EQ

        /// <summary>The EQ for a measured model: its response (both sides averaged) to the Harman target for its kind.</summary>
        public static List<EqFilter> Correction(Measurement m, string cacheDir)
        {
            double[] f, db;
            if (!Response(m, out f, out db)) throw new InvalidDataException("squig.link has no measurement file for " + m.Name + ".");
            double[] tf, tdb; Target(TargetFor(m.Kind), cacheDir, out tf, out tdb);
            var filters = PeqFit.Fit(f, db, tf, tdb, m.Kind == "5128" ? 6 : 0);
            if (filters.Count == 0) throw new InvalidDataException("No EQ could be made from the measurement of " + m.Name + ".");
            return filters;
        }

        /// <summary>The Harman target matching how a database measures (AutoEq's targets, MIT).</summary>
        internal static string TargetFor(string kind)
        {
            if (kind == "Headphones") return "crinacle EARS + 711 Harman over-ear 2018";
            if (kind == "5128") return "Diffuse field 5128 -1dB per octave";
            return "Harman in-ear 2019";
        }

        /// <summary>A measured response: left and right (or the first sample of each) averaged in dB.</summary>
        static bool Response(Measurement m, out double[] f, out double[] db)
        {
            f = db = null;
            var sides = new List<SortedDictionary<double, double>>();
            foreach (var names in new[] { new[] { " L.txt", " R.txt" }, new[] { " L1.txt", " R1.txt" }, new[] { ".txt" } })
            {
                foreach (var n in names)
                {
                    try { using (var wc = Client()) { var s = ParseFr(wc.DownloadString(m.Db + "data/" + Uri.EscapeDataString(m.File + n))); if (s.Count > 20) sides.Add(s); } }
                    catch { }
                }
                if (sides.Count > 0) break;
            }
            if (sides.Count == 0) return false;
            var grid = Grid();
            db = new double[grid.Length];
            for (int i = 0; i < grid.Length; i++) { double hz = grid[i]; db[i] = sides.Average(s => Interp(s, hz)); }
            f = grid;
            return true;
        }

        static void Target(string name, string cacheDir, out double[] f, out double[] db)
        {
            string cache = Path.Combine(cacheDir, "target-" + Regex.Replace(name, @"[^\w]+", "-") + ".csv");
            string text = File.Exists(cache) ? File.ReadAllText(cache) : null;
            if (text == null)
            {
                using (var wc = Client()) text = wc.DownloadString(Targets + Uri.EscapeDataString(name + ".csv"));
                Directory.CreateDirectory(cacheDir); File.WriteAllText(cache, text);
            }
            var s = ParseFr(text);
            f = Grid(); db = f.Select(x => Interp(s, x)).ToArray();
        }

        /// <summary>A frequency response file: lines of "frequency level" (tabs, spaces or commas; comments and headers skipped).</summary>
        internal static SortedDictionary<double, double> ParseFr(string text)
        {
            var d = new SortedDictionary<double, double>();
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in text.Split('\n'))
            {
                var p = raw.Trim().Split(new[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                double hz, v;
                if (p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, inv, out hz) && double.TryParse(p[1], NumberStyles.Float, inv, out v) && hz > 0 && !double.IsNaN(v)) d[hz] = v;
            }
            return d;
        }

        /// <summary>The frequencies everything is compared on: 20 Hz - 20 kHz, 1/24 octave.</summary>
        internal static double[] Grid()
        {
            int n = (int)Math.Round(Math.Log(1000, 2) * 24) + 1;
            return Enumerable.Range(0, n).Select(i => 20 * Math.Pow(2, i / 24.0)).ToArray();
        }

        /// <summary>The level at a frequency, interpolated on a log-frequency scale (held flat past either end).</summary>
        static double Interp(SortedDictionary<double, double> s, double hz)
        {
            double pf = double.NaN, pv = 0;
            foreach (var kv in s)
            {
                if (kv.Key >= hz)
                {
                    if (double.IsNaN(pf)) return kv.Value;
                    double t = (Math.Log(hz) - Math.Log(pf)) / (Math.Log(kv.Key) - Math.Log(pf));
                    return pv + (kv.Value - pv) * t;
                }
                pf = kv.Key; pv = kv.Value;
            }
            return pv;
        }
    }

    /// <summary>
    /// Fits a parametric EQ (a bass shelf, a treble shelf and up to 8 peaks) that takes a measured response to a
    /// target, the way AutoEq does: the difference smoothed (wider in the treble, where rigs and ears disagree most),
    /// boosts kept to +6 dB and cuts to -12 dB, nothing past 16 kHz, then every filter tuned together.
    /// </summary>
    public static class PeqFit
    {
        const double Fs = 48000, MaxBoost = 6, MaxCut = -12;

        /// <summary>f, measured and target levels on the same grid; bassShelfDb adds a bass shelf to the target (for diffuse-field targets).</summary>
        public static List<EqFilter> Fit(double[] f, double[] meas, double[] tf, double[] target, double bassShelfDb)
        {
            int n = f.Length;
            var tgt = new double[n];
            for (int i = 0; i < n; i++) tgt[i] = target[i] + bassShelfDb * Shelf(f[i]);
            // Matched over the midrange (300 Hz - 3 kHz), where every rig agrees.
            double off = Enumerable.Range(0, n).Where(i => f[i] >= 300 && f[i] <= 3000).Average(i => tgt[i] - meas[i]);
            var err = new double[n];
            for (int i = 0; i < n; i++) err[i] = tgt[i] - (meas[i] + off);
            err = Smooth(f, err);
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                // Less and less weight above 10 kHz, none past 16 kHz; the result kept within the limits.
                w[i] = f[i] <= 10000 ? 1 : f[i] >= 16000 ? 0 : 1 - Math.Log(f[i] / 10000) / Math.Log(1.6);
                err[i] = Math.Max(MaxCut, Math.Min(MaxBoost, err[i] * Math.Max(w[i], 0.0)));
            }
            var filters = new List<EqFilter>
            {
                new EqFilter { Type = "LSC", Freq = 105, Q = 0.7, Gain = Clamp(Range(f, err, 20, 90)) },
                new EqFilter { Type = "HSC", Freq = 10000, Q = 0.7, Gain = Clamp(Range(f, err, 10000, 14000)) },
            };
            var resp = filters.Select(x => Resp(x, f)).ToList();
            for (int k = 0; k < 8; k++)
            {
                var r = Residual(err, resp);
                int at = -1; double best = 1.0;
                for (int i = 0; i < n; i++) if (f[i] >= 30 && f[i] <= 12000 && Math.Abs(r[i]) * (0.3 + 0.7 * w[i]) > best) { best = Math.Abs(r[i]) * (0.3 + 0.7 * w[i]); at = i; }
                if (at < 0) break;
                // Its width: where the difference falls to half on each side.
                int lo = at, hi = at;
                while (lo > 0 && Math.Sign(r[lo - 1]) == Math.Sign(r[at]) && Math.Abs(r[lo - 1]) > Math.Abs(r[at]) / 2) lo--;
                while (hi < n - 1 && Math.Sign(r[hi + 1]) == Math.Sign(r[at]) && Math.Abs(r[hi + 1]) > Math.Abs(r[at]) / 2) hi++;
                double bw = Math.Max(0.1, Math.Log(f[hi] / f[lo], 2)), q = Math.Sqrt(Math.Pow(2, bw)) / (Math.Pow(2, bw) - 1);
                var pk = new EqFilter { Type = "PK", Freq = f[at], Gain = Clamp(r[at]), Q = Math.Max(0.4, Math.Min(6, q)) };
                filters.Add(pk); resp.Add(Resp(pk, f));
                Refine(f, err, w, filters, resp, 12);
            }
            Refine(f, err, w, filters, resp, 40);
            filters.RemoveAll(x => Math.Abs(x.Gain) < 0.2);
            foreach (var x in filters) { x.Freq = Math.Round(x.Freq); x.Gain = Math.Round(x.Gain, 1); x.Q = Math.Round(x.Q, 2); }
            return filters;
        }

        static double Shelf(double f) { return 1 / (1 + Math.Pow(f / 105, 2)); }
        static double Clamp(double g) { return Math.Max(MaxCut, Math.Min(MaxBoost, g)); }
        static double Range(double[] f, double[] v, double a, double b) { var i = Enumerable.Range(0, f.Length).Where(k => f[k] >= a && f[k] <= b).ToList(); return i.Count == 0 ? 0 : i.Average(k => v[k]); }

        /// <summary>The difference to the target, smoothed: 1/6 octave in the bass and mids, widening to 1/2 octave above 6 kHz.</summary>
        static double[] Smooth(double[] f, double[] v)
        {
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                double oct = f[i] < 1000 ? 1 / 6.0 : f[i] > 6000 ? 0.5 : 1 / 6.0 + (0.5 - 1 / 6.0) * Math.Log(f[i] / 1000) / Math.Log(6);
                double lo = f[i] / Math.Pow(2, oct / 2), hi = f[i] * Math.Pow(2, oct / 2), s = 0; int c = 0;
                for (int k = 0; k < v.Length; k++) if (f[k] >= lo && f[k] <= hi) { s += v[k]; c++; }
                o[i] = c > 0 ? s / c : v[i];
            }
            return o;
        }

        static double[] Resp(EqFilter x, double[] f)
        {
            var b = Biquad.Create(x, Fs);
            return f.Select(h => h < Fs / 2 ? b.MagnitudeDb(h, Fs) : 0).ToArray();
        }

        static double[] Residual(double[] err, List<double[]> resp)
        {
            var r = (double[])err.Clone();
            foreach (var x in resp) for (int i = 0; i < r.Length; i++) r[i] -= x[i];
            return r;
        }

        static double Loss(double[] f, double[] err, double[] w, List<double[]> resp)
        {
            double s = 0, sw = 0;
            for (int i = 0; i < f.Length; i++)
            {
                double e = err[i]; foreach (var x in resp) e -= x[i];
                s += w[i] * e * e; sw += w[i];
            }
            return s / Math.Max(1e-9, sw);
        }

        /// <summary>Every filter's frequency, gain and width nudged in turn while that lowers the remaining difference.</summary>
        static void Refine(double[] f, double[] err, double[] w, List<EqFilter> filters, List<double[]> resp, int rounds)
        {
            double loss = Loss(f, err, w, resp), step = 1;
            for (int round = 0; round < rounds; round++, step *= 0.85)
                for (int k = 0; k < filters.Count; k++)
                {
                    var x = filters[k];
                    for (int p = 0; p < 3; p++)
                        foreach (int dir in new[] { 1, -1 })
                        {
                            if (x.Type != "PK" && p == 0) continue;   // the shelves stay where they are
                            double freq = x.Freq, gain = x.Gain, q = x.Q;
                            if (p == 0) x.Freq = Math.Max(20, Math.Min(16000, freq * Math.Pow(2, dir * 0.08 * step)));
                            else if (p == 1) x.Gain = Clamp(gain + dir * 0.5 * step);
                            else x.Q = Math.Max(x.Type == "PK" ? 0.4 : 0.5, Math.Min(x.Type == "PK" ? 6 : 1, q * Math.Pow(2, dir * 0.15 * step)));
                            var old = resp[k]; resp[k] = Resp(x, f);
                            double l = Loss(f, err, w, resp);
                            if (l < loss - 1e-6) { loss = l; break; }
                            x.Freq = freq; x.Gain = gain; x.Q = q; resp[k] = old;
                        }
                }
        }
    }
}
