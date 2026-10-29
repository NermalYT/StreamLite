// Credits stay complete per version: every release in CHANGELOG.md (and this build's version) has a credits list and a
// "what changed" note, every credit names a listed release and has https links, and CREDITS.md is exactly what
// src\Credits.cs generates. Usage: run.ps1 CreditsTest [write]   (write = regenerate CREDITS.md)
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamLite;

class CreditsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\.."));
        string md = Path.Combine(root, "CREDITS.md");
        string generated = Credits.Document();
        if (args.Contains("write")) { File.WriteAllText(md, generated); Console.WriteLine("  wrote CREDITS.md"); }

        var released = Regex.Matches(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), @"(?m)^## (\d+\.\d+\.\d+)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        var missing = released.Where(v => !Credits.Versions.Contains(v)).ToList();
        Check(missing.Count == 0, "every release in CHANGELOG.md has credits (" + released.Count + " releases" + (missing.Count > 0 ? "; missing " + string.Join(", ", missing) : "") + ")");
        string me = Program.Version.ToString(3);
        Check(Credits.Versions.Contains(me), "this build's version " + me + " is listed");
        Check(Credits.Versions.All(v => Credits.Changes.ContainsKey(v)), "every version says what changed in its credits");
        Check(Credits.All.All(c => Credits.Versions.Contains(c.Since)), "every credit starts at a listed release");
        Check(Credits.All.All(c => c.Url.StartsWith("https://") && (c.File == null || c.File.StartsWith("https://"))), "every link is https");
        Check(Credits.All.All(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.What) && !string.IsNullOrEmpty(c.Authors) && !string.IsNullOrEmpty(c.License) && Credits.Groups.Contains(c.Group)),
            "every credit has a name, use, authors, license and group");
        Check(Credits.All.Select(c => c.Name).Distinct().Count() == Credits.All.Length, "no credit listed twice");
        Check(Credits.All.All(c => c.Until == null || (Credits.Versions.Contains(c.Until) && new Version(c.Until) >= new Version(c.Since))), "every credit no longer used ends at a listed release, after it started");
        // Lists only grow, except what a release stopped using (Until): a later release keeps every other earlier credit.
        bool grows = true;
        for (int i = 1; i < Credits.Versions.Length; i++)
            grows &= Credits.For(Credits.Versions[i - 1]).Where(c => c.Until == null || new Version(c.Until) >= new Version(Credits.Versions[i])).All(c => Credits.For(Credits.Versions[i]).Contains(c));
        Check(grows, "each release keeps every earlier credit it still uses");
        Check(Credits.For("4.1.2").Any(c => c.Name == "Avalonia UI") && Credits.For("4.1.3").All(c => c.Name != "Avalonia UI") && Credits.For("4.1.3").Any(c => c.Name == "Mono"), "per-version: 4.1.3 credits Mono, no longer Avalonia UI");
        Check(Credits.For("2.2.2").All(c => c.Name != "onnxconverter-common") && Credits.For("2.3.0").Any(c => c.Name == "onnxconverter-common"), "per-version: onnxconverter-common first credited in 2.3.0");
        foreach (var v in Credits.Versions) Console.WriteLine("    " + v + ": " + Credits.For(v).Count + " credits");
        string onDisk = File.Exists(md) ? File.ReadAllText(md).Replace("\r\n", "\n") : "";
        Check(onDisk == generated, "CREDITS.md matches src\\Credits.cs (run.ps1 CreditsTest write to update it)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
