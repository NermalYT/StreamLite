using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// The headphone EQ backup (4.0.7): squig.link's databases and StreamLite's own EQ fit. Offline: reading squigsites.json
// and phone books (every kind of entry), measurement files, and the fit finding a known EQ again. Online (skipped
// without internet): the whole list, and an EQ for a gaming headset AutoEq hasn't measured.
// Usage: run.ps1 SquigLinkTest
class SquigLinkTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }

    static double Rms(double[] f, Func<int, double> e, double lo, double hi)
    {
        var i = Enumerable.Range(0, f.Length).Where(k => f[k] >= lo && f[k] <= hi).ToList();
        return Math.Sqrt(i.Average(k => e(k) * e(k)));
    }

    static void Main(string[] args)
    {
        // squigsites.json: the four kinds of address.
        var dbs = SquigLink.Databases(@"[{""username"":""aden"",""name"":""aden"",""urlType"":""subdomain"",""dbs"":[{""type"":""IEMs"",""folder"":""/""},{""type"":""Headphones"",""folder"":""/headphones/""}]},
            {""username"":""aqua"",""name"":""Aqua"",""urlType"":""labFolder"",""dbs"":[{""type"":""IEMs"",""folder"":""/""}]},
            {""username"":""crinacle"",""name"":""Hangout.Audio"",""urlType"":""altDomain"",""altDomain"":""https://graph.hangout.audio"",""dbs"":[{""type"":""5128"",""folder"":""/iem/5128/""}]},
            {""username"":""superreview"",""name"":""Super* Review"",""urlType"":""root"",""dbs"":[{""type"":""Earbuds"",""folder"":""/earbuds/""}]}]");
        var urls = dbs.Select(d => d.Item1).ToList();
        Check(urls.SequenceEqual(new[] { "https://aden.squig.link/", "https://aden.squig.link/headphones/", "https://squig.link/lab/aqua/", "https://graph.hangout.audio/iem/5128/", "https://squig.link/earbuds/" }),
            "databases from squigsites.json: " + string.Join(", ", urls));

        // A phone book: plain names, a file name, several files with suffixes.
        var book = SquigLink.PhoneBook(@"[{""name"":""HyperX"",""phones"":[""Cloud II"",{""name"":""Cloud III"",""file"":""HyperX Cloud III""}]},
            {""name"":""Logitech"",""phones"":[{""name"":""G733"",""file"":[""G733"",""G733 worn""],""suffix"":[""(fresh pads)"",""(worn pads)""]}]}]", "https://x.squig.link/", "Headphones", "X");
        Check(book.Select(m => m.Name + "=" + m.File).SequenceEqual(new[] { "HyperX Cloud II=Cloud II", "HyperX Cloud III=HyperX Cloud III", "Logitech G733 (fresh pads)=G733", "Logitech G733 (worn pads)=G733 worn" }),
            "phone book: " + string.Join("; ", book.Select(m => m.Name + " -> " + m.File)));
        var extra = SquigLink.Extra(book, new[] { new AutoEq.Entry { Name = "HyperX Cloud II" } }, Path.GetTempPath());
        Check(extra.Count == 3 && extra.All(e => e.Fetch != null) && extra[0].Name == "HyperX Cloud III", "only the models AutoEq hasn't got are added");
        Check(AutoEq.Search(extra.Concat(new[] { new AutoEq.Entry { Name = "HyperX Cloud III Wireless", Source = "oratory1990" } }).ToList(), "cloud iii")[0].Name == "HyperX Cloud III", "they're found by the same search");

        // A REW measurement file.
        var fr = SquigLink.ParseFr("* Measurement data measured by REW V5.20.13\n* Source: averaged\n20.000000\t70.1\n1000.0\t80.5\n20000.0,60\n");
        Check(fr.Count == 3 && fr[1000] == 80.5 && fr[20000] == 60, "measurement file read (comments skipped, tabs and commas)");

        // The fit finds a known EQ again: a target, a "headphone" that's the target minus 7 filters.
        var f = SquigLink.Grid();
        var target = f.Select(h => 6 / (1 + Math.Pow(h / 105, 2)) + 8 * Math.Exp(-Math.Pow(Math.Log(h / 3000, 2) / 0.7, 2)) - 2 * Math.Log(Math.Max(1, h / 5000), 2)).ToArray();
        var known = new List<EqFilter>
        {
            new EqFilter { Type = "LSC", Freq = 105, Q = 0.7, Gain = -4 }, new EqFilter { Type = "PK", Freq = 220, Q = 1.2, Gain = 3 },
            new EqFilter { Type = "PK", Freq = 1800, Q = 2, Gain = -3.5 }, new EqFilter { Type = "PK", Freq = 4200, Q = 3, Gain = 5 },
            new EqFilter { Type = "PK", Freq = 6500, Q = 4, Gain = -4 }, new EqFilter { Type = "PK", Freq = 9000, Q = 2, Gain = 3 }, new EqFilter { Type = "PK", Freq = 60, Q = 1, Gain = 2 },
        };
        var rnd = new Random(5);
        var meas = f.Select(h => 80 + Biquad.ResponseDb(known.Select(k => new EqFilter { Type = k.Type, Freq = k.Freq, Q = k.Q, Gain = -k.Gain }).ToList(), h, 48000) + (rnd.NextDouble() - 0.5) * 0.6).ToArray();
        var sw = Stopwatch.StartNew();
        var fit = PeqFit.Fit(f, meas, f, target, 0);
        double off = Enumerable.Range(0, f.Length).Where(i => f[i] >= 300 && f[i] <= 3000).Average(i => target[i] - meas[i]);
        double rms = Rms(f, i => meas[i] + off + Biquad.ResponseDb(fit, f[i], 48000) - target[i], 30, 10000);
        Check(rms < 1.0 && fit.Count <= 10, "fit: " + fit.Count + " filters take it to the target within " + rms.ToString("0.00") + " dB RMS, 30 Hz - 10 kHz (" + sw.ElapsedMilliseconds + " ms)");
        Check(fit.All(x => x.Gain <= 6.01 && x.Gain >= -12.01), "boosts within +6 dB, cuts within -12 dB");

        // Online: the whole list, and a gaming headset only squig.link has.
        if (args.Contains("offline")) { Console.WriteLine(fails == 0 ? "PASS" : fails + " FAILED"); Environment.Exit(fails); }
        string dir = Path.Combine(Path.GetTempPath(), "StreamLite-squigtest"); Directory.CreateDirectory(dir);
        foreach (var x in Directory.GetFiles(dir)) File.Delete(x);
        List<SquigLink.Measurement> all = null;
        try { sw.Restart(); all = SquigLink.Index(dir); } catch (Exception ex) { Console.WriteLine("  --   no internet? " + ex.Message); }
        if (all != null)
        {
            Check(all.Count > 5000, "squig.link: " + all.Count + " measurements in " + all.Select(m => m.Db).Distinct().Count() + " databases (" + sw.ElapsedMilliseconds + " ms)");
            var hx = all.FirstOrDefault(x => x.Name.Equals("HyperX Cloud III", StringComparison.OrdinalIgnoreCase) && x.Kind == "Headphones") ?? all.FirstOrDefault(x => x.Name.IndexOf("Cloud III", StringComparison.OrdinalIgnoreCase) >= 0);
            Check(hx != null, "HyperX Cloud III is there" + (hx != null ? " (" + hx.Site + ", " + hx.Kind + ")" : ""));
            if (hx != null)
            {
                try
                {
                    var eq = SquigLink.Correction(hx, dir);
                    Check(eq.Count >= 3, "its EQ: " + string.Join(", ", eq.Select(x => x.Type + " " + x.Freq + " Hz " + x.Gain.ToString("+0.0;-0.0") + " dB Q" + x.Q)));
                }
                catch (Exception ex) { Check(false, "its EQ: " + ex.Message); }
            }
        }
        Console.WriteLine(fails == 0 ? "PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
