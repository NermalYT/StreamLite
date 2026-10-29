// Updater, offline: versions, which release file each install kind uses, the bug report link, checksum checking
// (file:// "downloads") and the whole zip update done by the helper (bin\StreamLite.exe --apply-update) on a fake
// install folder with dummy programs - including a file in use and an update that fails. Never touches the real app.
// Usage: run.ps1 UpdaterTest
using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CSharp;
using StreamLite;

class UpdaterTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static string Sha(string f)
    {
        using (var s = SHA256.Create()) using (var st = File.OpenRead(f)) return BitConverter.ToString(s.ComputeHash(st)).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>A stand-in for StreamLite.exe that writes "tag args" to started.txt next to itself.</summary>
    static void Dummy(string path, string tag)
    {
        string src = "class D { static void Main(string[] a) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"started.txt\"), \"" + tag + " \" + string.Join(\" \", a)); } }";
        var r = new CSharpCodeProvider().CompileAssemblyFromSource(new CompilerParameters { GenerateExecutable = true, OutputAssembly = path }, src);
        if (r.Errors.HasErrors) throw new Exception(r.Errors[0].ToString());
    }

    static string Started(string app)
    {
        string f = Path.Combine(app, "started.txt");
        for (int i = 0; i < 100 && !File.Exists(f); i++) Thread.Sleep(100);
        Thread.Sleep(200);
        return File.Exists(f) ? File.ReadAllText(f).Trim() : "(not started)";
    }

    static void RunHelper(string helper, string kind, string file, string app, string data, bool show)
    {
        var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true });
        dead.WaitForExit();
        var psi = new ProcessStartInfo(helper, "--apply-update " + kind + " \"" + file + "\" \"" + app + "\" " + dead.Id + (show ? " --show" : "")) { UseShellExecute = false };
        psi.EnvironmentVariables["STREAMLITE_DATA"] = data;
        using (var p = Process.Start(psi))
            if (!p.WaitForExit(60000)) { p.Kill(); Check(false, "helper finished in time"); }
    }

    static void Main()
    {
        Console.WriteLine("Versions and release files");
        Check(Updater.ParseVersion("v2.1.0").Equals(new Version(2, 1, 0)), "\"v2.1.0\" is 2.1.0");
        Check(Updater.ParseVersion("2.1.0.0").Equals(new Version(2, 1, 0)), "2.1.0.0 equals 2.1.0");
        Check(Updater.ParseVersion("v2.2.0-beta1").Equals(new Version(2, 2, 0)), "pre-release suffix ignored");
        Check(Updater.ParseVersion("nightly") == null, "a tag that isn't a version gives no version");
        var rel = new Updater.Release { Version = new Version(2, 2, 0), Tag = "v2.2.0" };
        foreach (var n in new[] { "StreamLite-2.2.0-x64.msi", "StreamLite-2.2.0-win-x64-portable.zip", "StreamLite-2.2.0-x86_64.AppImage", "streamlite-2.2.0-linux-x64.tar.gz",
            "streamlite_2.2.0_amd64.deb", "streamlite-2.2.0-1.x86_64.rpm", "streamlite-2.2.0-arch.tar.gz", "SHA256SUMS.txt" })
            rel.Assets[n] = "x";
        Check(Updater.IsNewer(rel, new Version(2, 1, 0, 0)), "2.2.0 is newer than 2.1.0.0");
        Check(!Updater.IsNewer(rel, new Version(2, 2, 0, 0)), "2.2.0 is not newer than 2.2.0.0");
        Check(!Updater.IsNewer(rel, new Version(3, 0, 0, 0)), "2.2.0 is not newer than 3.0");
        string[][] kinds =
        {
            new[] { "msi", "StreamLite-2.2.0-x64.msi" }, new[] { "zip", "StreamLite-2.2.0-win-x64-portable.zip" }, new[] { "user", "StreamLite-2.2.0-win-x64-portable.zip" },
            new[] { "appimage", "StreamLite-2.2.0-x86_64.AppImage" }, new[] { "tar", "streamlite-2.2.0-linux-x64.tar.gz" }, new[] { "deb", "streamlite_2.2.0_amd64.deb" },
            new[] { "rpm", "streamlite-2.2.0-1.x86_64.rpm" }, new string[] { "arch", null }, new string[] { null, null },
        };
        foreach (var k in kinds) Check(Updater.AssetFor(rel, k[0]) == k[1], "install kind " + (k[0] ?? "(none)") + " -> " + (k[1] ?? "no automatic update"));
        Check(!Updater.MovesToMsi("zip", Updater.AssetFor(rel, "zip")), "a release with the portable zip updates a portable copy in place");
        // 4.1.4 on: only the .msi and the .sh - a portable copy moves to the MSI.
        var two = new Updater.Release { Version = new Version(4, 1, 4), Tag = "v4.1.4" };
        foreach (var n in new[] { "StreamLite-4.1.4-x64.msi", "StreamLite-4.1.4-linux.sh", "SHA256SUMS.txt" }) two.Assets[n] = "x";
        Check(Updater.AssetFor(two, "msi") == "StreamLite-4.1.4-x64.msi" && Updater.AssetFor(two, "sh") == "StreamLite-4.1.4-linux.sh", "4.1.4 releases: the MSI and the Linux installer update their installs");
        Check(Updater.AssetFor(two, "zip") == "StreamLite-4.1.4-x64.msi" && Updater.MovesToMsi("zip", "StreamLite-4.1.4-x64.msi") && Updater.MovesToMsi("user", Updater.AssetFor(two, "user")), "4.1.4 releases: a portable or per-user copy moves to the MSI");
        Check(Updater.AssetFor(two, "deb") == null && Updater.AssetFor(two, "appimage") == null, "4.1.4 releases: old Linux packages get no file (they're told to run the .sh once)");

        string bug = Updater.BugReportUrl("2.1.0");
        Check(bug.StartsWith("https://github.com/NermalYT/StreamLite/issues/new?template=bug_report.yml&version=2.1.0&os=Windows%20"), "bug report link: " + bug);
        Check(bug.IndexOf(Environment.UserName, StringComparison.OrdinalIgnoreCase) < 0 && bug.IndexOf(Environment.MachineName, StringComparison.OrdinalIgnoreCase) < 0, "bug report link has no user or computer name");

        Console.WriteLine("Download checking");
        string root = Path.Combine(Path.GetTempPath(), "sl-updtest");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string srv = Path.Combine(root, "server"), build = Path.Combine(root, "build"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(srv); Directory.CreateDirectory(Path.Combine(build, "ai")); Directory.CreateDirectory(Path.Combine(build, "cam"));
        Dummy(Path.Combine(build, "StreamLite.exe"), "new");
        File.WriteAllText(Path.Combine(build, "ai", "model.txt"), "model v2");
        File.WriteAllText(Path.Combine(build, "cam", "StreamLiteCam.dll"), "cam v2");
        File.WriteAllText(Path.Combine(build, "install-kind.txt"), "zip");
        string zipName = "StreamLite-2.2.0-win-x64-portable.zip", zip = Path.Combine(srv, zipName);
        ZipFile.CreateFromDirectory(build, zip);
        File.WriteAllText(Path.Combine(srv, "bad.zip"), "tampered");
        File.WriteAllText(Path.Combine(srv, "SHA256SUMS.txt"), Sha(zip) + "  " + zipName + "\n" + new string('0', 64) + "  bad.zip\n");
        rel.Assets.Clear();
        foreach (var f in Directory.GetFiles(srv)) rel.Assets[Path.GetFileName(f)] = new Uri(f).AbsoluteUri;
        string got = Updater.Download(rel, zipName);
        Check(File.Exists(got) && Sha(got) == Sha(zip), "download matches SHA256SUMS.txt");
        bool refused = false;
        try { Updater.Download(rel, "bad.zip"); } catch (InvalidDataException) { refused = true; }
        Check(refused, "a damaged download is refused");
        rel.Assets.Remove("SHA256SUMS.txt");
        refused = false;
        try { Updater.Download(rel, zipName); } catch (InvalidDataException) { refused = true; }
        Check(refused, "no SHA256SUMS.txt in the release -> no update");

        Console.WriteLine("Zip update by the helper");
        string exe = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\bin\StreamLite.exe"));
        string helper = Path.Combine(root, "StreamLite-updater.exe");
        File.Copy(exe, helper, true);
        string app = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(app, "cam"));
        Dummy(Path.Combine(app, "StreamLite.exe"), "old");
        File.WriteAllText(Path.Combine(app, "install-kind.txt"), "user");
        File.WriteAllText(Path.Combine(app, "cam", "StreamLiteCam.dll"), "cam v1");
        File.WriteAllText(Path.Combine(app, "my-notes.txt"), "keep me");
        // The camera driver is loaded by other apps: it can be renamed but not overwritten.
        using (new FileStream(Path.Combine(app, "cam", "StreamLiteCam.dll"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            RunHelper(helper, "user", got, app, data, false);
        Check(Started(app) == "new --minimized", "the new version starts (in the tray): " + Started(app));
        Check(File.ReadAllText(Path.Combine(app, "ai", "model.txt")) == "model v2", "new files are copied");
        Check(File.ReadAllText(Path.Combine(app, "cam", "StreamLiteCam.dll")) == "cam v2", "a file in use is replaced");
        var aside = Directory.GetFiles(Path.Combine(app, "cam"), "StreamLiteCam.dll.old*");
        Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "cam v1", "...and the old one moved aside (deleted at the next start)");
        Check(File.ReadAllText(Path.Combine(app, "install-kind.txt")).Trim() == "user", "install kind stays \"user\"");
        Check(File.Exists(Path.Combine(app, "my-notes.txt")), "other files in the folder stay");
        string log = Path.Combine(data, "update.log");
        Check(File.Exists(log) && File.ReadAllText(log).Contains("copied the new files"), "update.log: " + (File.Exists(log) ? File.ReadAllLines(log).Last() : "(missing)"));

        Console.WriteLine("Failed update");
        string app2 = Path.Combine(root, "app2");
        Directory.CreateDirectory(app2);
        Dummy(Path.Combine(app2, "StreamLite.exe"), "old");
        RunHelper(helper, "zip", Path.Combine(srv, "bad.zip"), app2, data, true);
        Check(Started(app2) == "old", "the old version starts again, window shown: " + Started(app2));
        Check(File.ReadAllText(log).Contains("update failed"), "the failure is logged");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        try { Directory.Delete(root, true); } catch { }
    }
}
