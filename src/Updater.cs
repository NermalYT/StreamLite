using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamLite
{
    /// <summary>
    /// Updates from the GitHub releases of StreamLite: finds the newest release, downloads the file that matches how this
    /// copy was installed (MSI, portable zip, AppImage, tarball, .deb, .rpm), checks it against the release's
    /// SHA256SUMS.txt and installs it, then StreamLite starts again. Also builds the "Report a bug" link.
    /// How this copy was installed comes from install-kind.txt next to the program (each installer writes it). A copy
    /// without one (a build folder) is never replaced: it only says that a newer version exists.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "NermalYT/StreamLite";
        public const string Home = "https://github.com/" + Repo;
        public const string Manual = "https://nermalyt.github.io/StreamLite/";
        public const string ReleasesPage = Home + "/releases/latest";
        public const string IssuesPage = Home + "/issues";

        /// <summary>Last result of a check, for the Settings page.</summary>
        public static volatile string Status;

        public sealed class Release
        {
            public Version Version;
            public string Tag, Page;
            public readonly Dictionary<string, string> Assets = new Dictionary<string, string>();   // file name -> download URL
        }

        static WebClient Client()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StreamLite-updater";
            return wc;
        }

        public static Release Latest()
        {
            string json;
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                // STREAMLITE_UPDATE_FEED: a local copy of the release JSON (file:// or http), for testing the updater.
                string feed = Environment.GetEnvironmentVariable("STREAMLITE_UPDATE_FEED");
                json = wc.DownloadString(string.IsNullOrEmpty(feed) ? "https://api.github.com/repos/" + Repo + "/releases/latest" : feed);
            }
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) throw new InvalidDataException("no release found");
            var r = new Release { Tag = tag.Groups[1].Value };
            r.Version = ParseVersion(r.Tag);
            r.Page = Home + "/releases/tag/" + Uri.EscapeDataString(r.Tag);
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value;
                r.Assets[Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1))] = url;
            }
            return r;
        }

        /// <summary>"v2.1.0" -> 2.1.0 (three parts, so 2.1.0.0 and 2.1.0 compare equal).</summary>
        public static Version ParseVersion(string s)
        {
            s = (s ?? "").Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            Version v;
            return Version.TryParse(s, out v) ? Three(v) : null;
        }

        static Version Three(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        public static bool IsNewer(Release r, Version current) { return r != null && r.Version != null && r.Version > Three(current); }

        public static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'); } }

        /// <summary>
        /// "msi", "zip", "user" (install.ps1) on Windows; "sh" (the Linux installer, 4.1.3) on Linux;
        /// null when no StreamLite installer put this copy here.
        /// </summary>
        public static string InstallKind
        {
            get
            {
                try
                {
                    string f = Path.Combine(AppDir, "install-kind.txt");
                    if (File.Exists(f)) return File.ReadAllText(f).Trim().ToLowerInvariant();
                }
                catch { }
                return null;
            }
        }

        /// <summary>The release file that updates this kind of install, or null (it has to be updated by hand).</summary>
        public static string AssetFor(Release r, string kind)
        {
            string suffix = kind == "msi" ? "-x64.msi" : kind == "zip" || kind == "user" ? "-win-x64-portable.zip"
                : kind == "appimage" ? "-x86_64.AppImage" : kind == "tar" ? "-linux-x64.tar.gz"
                : kind == "deb" ? "_amd64.deb" : kind == "rpm" ? ".x86_64.rpm" : kind == "sh" ? "-linux.sh" : null;
            if (suffix == null || r == null) return null;
            return r.Assets.Keys.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        static string UpdateDir { get { return Path.Combine(Path.GetTempPath(), "StreamLite-update"); } }

        /// <summary>Downloads a release file and checks it against SHA256SUMS.txt. Returns the local path.</summary>
        public static string Download(Release r, string name)
        {
            string sumsName = r.Assets.Keys.FirstOrDefault(n => n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sumsName == null) throw new InvalidDataException("the release has no SHA256SUMS.txt");
            string sums;
            using (var wc = Client()) sums = wc.DownloadString(r.Assets[sumsName]);
            var m = Regex.Match(sums, "^([0-9a-fA-F]{64})\\s+\\*?" + Regex.Escape(name) + "\\s*$", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidDataException(name + " is not listed in SHA256SUMS.txt");
            Directory.CreateDirectory(UpdateDir);
            string path = Path.Combine(UpdateDir, name);
            using (var wc = Client()) wc.DownloadFile(r.Assets[name], path);
            string hash;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
            if (!hash.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidDataException("the download is damaged (checksum mismatch)");
            }
            return path;
        }

        // ---- remembering a declined install (so StreamLite doesn't ask at every start)

        static string DeclinedFile { get { return Path.Combine(AppSettings.Folder, "update-declined.txt"); } }

        public static bool WasDeclined(Version v)
        {
            try { return File.Exists(DeclinedFile) && File.ReadAllText(DeclinedFile).Trim() == v.ToString(); } catch { return false; }
        }

        /// <summary>"Skip" in the update prompt: this version isn't offered again (a newer one is).</summary>
        public static void Skip(Version v) { Declined(v); }

        static void Declined(Version v)
        {
            try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(DeclinedFile, v.ToString()); } catch { }
        }

        /// <summary>Adds a line to update.log (what the updater found and did).</summary>
        public static void Note(string text) { Log(text); }

        static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "update.log"), DateTime.Now + "  " + text + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Copies a new version's files over the installed ones. A file in use is moved aside first.</summary>
        static void CopyTree(string from, string to)
        {
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\', '/');
                string dest = Path.Combine(to, rel);
                if (rel.Equals("install-kind.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                try { File.Copy(f, dest, true); }
                catch (IOException)
                {
                    // Running (Linux) or loaded by other apps (the camera driver on Windows): renaming still works.
                    File.Move(dest, dest + ".old" + DateTime.Now.Ticks);
                    File.Copy(f, dest, true);
                }
            }
        }

        /// <summary>At start: removes files an earlier update moved aside, and the downloads.</summary>
        public static void Cleanup()
        {
            if (InstallKind == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var aside = new Regex(@"\.old\d{17,19}$");
                foreach (var d in new[] { AppDir, Path.Combine(AppDir, "ai"), Path.Combine(AppDir, "cam"), Path.Combine(AppDir, "hrtf") })
                    try { foreach (var f in Directory.GetFiles(d)) if (aside.IsMatch(f)) try { File.Delete(f); } catch { } } catch { }
                try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); } catch { }
            });
        }

        static string Quote(string s) { return "\"" + s + "\""; }

        /// <summary>The page that opens when you click "Report a bug": GitHub's bug form with the version and system filled in.</summary>
        public static string BugReportUrl(string version)
        {
            return Home + "/issues/new?template=bug_report.yml&version=" + Uri.EscapeDataString(version) + "&os=" + Uri.EscapeDataString(SystemDescription());
        }

        /// <summary>Operating system (no names, no devices): goes into the bug report.</summary>
        public static string SystemDescription()
        {
#if LINUX
            string os = "Linux";
            try
            {
                foreach (var line in File.ReadAllLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=")) os = line.Substring(12).Trim('"');
            }
            catch { }
            string desk = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            string server = "";
            try { server = Interop.Pactl.ServerName(); } catch { }
            return os + (string.IsNullOrEmpty(desk) ? "" : ", " + desk) + (string.IsNullOrEmpty(session) ? "" : " (" + session + ")") +
                (string.IsNullOrEmpty(server) ? "" : ", " + server) + (InstallKind != null ? ", " + InstallKind : "");
#else
            // The registry has the real build (Environment.OSVersion depends on the exe's compatibility manifest).
            int build = Environment.OSVersion.Version.Build;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int b;
                    if (k != null && int.TryParse(k.GetValue("CurrentBuildNumber") as string, out b)) build = b;
                }
            }
            catch { }
            return "Windows " + (build >= 22000 ? "11" : "10") + " (build " + build + ")" + (InstallKind != null ? ", " + InstallKind : "");
#endif
        }

        public static void OpenUrl(string url)
        {
#if LINUX
            try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); } catch { }
#else
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
#endif
        }

#if !LINUX
        // ---- Windows: a temporary copy of StreamLite.exe installs the update after StreamLite exits

        /// <summary>Starts the helper; the caller exits StreamLite right after.</summary>
        public static void StartHelper(string kind, string file, bool showWindow)
        {
            string dir = Path.GetDirectoryName(file);
            string helper = Path.Combine(dir, "StreamLite-updater.exe");
            File.Copy(Process.GetCurrentProcess().MainModule.FileName, helper, true);
            string args = "--apply-update " + kind + " " + Quote(file) + " " + Quote(AppDir) + " " + Process.GetCurrentProcess().Id + (showWindow ? " --show" : "");
            var psi = new ProcessStartInfo(helper, args) { UseShellExecute = true, WorkingDirectory = dir };
            if (kind != "msi" && !Elevation.IsElevated && !CanWrite(AppDir)) psi.Verb = "runas";
            Process.Start(psi);
            Log("installing " + Path.GetFileName(file) + " (" + kind + ")");
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string t = Path.Combine(dir, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(t, "");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        /// <summary>"--apply-update kind file appDir pid [--show]" (runs from the temp folder).</summary>
        public static void RunHelper(string[] args)
        {
            int i = Array.IndexOf(args, "--apply-update");
            string kind = args[i + 1], file = args[i + 2], appDir = args[i + 3].TrimEnd('\\');
            int pid; int.TryParse(args[i + 4], out pid);
            bool show = args.Contains("--show");
            try
            {
                try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(60000); } catch { }
                Thread.Sleep(500);
                if (kind == "msi")
                {
                    // Windows asks for permission (the MSI installs for all users), unless StreamLite ran as administrator.
                    using (var p = Process.Start(new ProcessStartInfo("msiexec.exe", "/i " + Quote(file) + " /passive /norestart") { UseShellExecute = false }))
                    {
                        p.WaitForExit();
                        Log("msiexec finished with " + p.ExitCode);
                        if (p.ExitCode == 1602 || p.ExitCode == 1625) Declined(ParseVersion(Regex.Match(Path.GetFileName(file), @"\d+\.\d+\.\d+").Value));
                    }
                }
                else
                {
                    string ex = Path.Combine(Path.GetDirectoryName(file), "files");
                    if (Directory.Exists(ex)) Directory.Delete(ex, true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(file, ex);
                    string src = ex;
                    var top = Directory.GetDirectories(ex);
                    if (!File.Exists(Path.Combine(ex, "StreamLite.exe")) && top.Length == 1) src = top[0];
                    if (!File.Exists(Path.Combine(src, "StreamLite.exe"))) throw new InvalidDataException("StreamLite.exe is missing from the update");
                    CopyTree(src, appDir);
                    Log("copied the new files to " + appDir);
                }
            }
            catch (Exception e) { Log("update failed: " + e); }
            // Start StreamLite again: the new version, or the old one if the update didn't go through. With "Run as
            // administrator" through its task, like any normal start (so it can be stopped and restarted the same way).
            try
            {
                bool viaTask = false;
                try { viaTask = AppSettings.Load().RunAsAdmin && Elevation.TaskRuns(Path.Combine(appDir, "StreamLite.exe")) && Elevation.RunTask(!show); } catch { }
                if (!viaTask) Process.Start(new ProcessStartInfo(Path.Combine(appDir, "StreamLite.exe"), show ? "" : "--minimized") { UseShellExecute = true, WorkingDirectory = appDir });
                Log("started StreamLite again" + (viaTask ? " (as administrator, through its task)" : ""));
            }
            catch (Exception e) { Log("restart failed: " + e.Message); }
        }
#else
        // ---- Linux (4.1.3): one installer for every distro, StreamLite-<version>-linux.sh. An update runs it in update
        // mode: it waits for StreamLite to close, installs over this copy (for you, or system-wide - as it was) and
        // starts StreamLite again. Your settings stay.

        static string Script(string file) { return file; }

        /// <summary>Starts the installer in update mode; the caller exits StreamLite right after.</summary>
        public static void StartHelper(string kind, string file, bool showWindow)
        {
            var args = new[] { file, "--update", "--wait", Process.GetCurrentProcess().Id.ToString() }.Concat(showWindow ? new string[0] : new[] { "--minimized" });
            Process.Start(new ProcessStartInfo("bash", string.Join(" ", args.Select(Interop.Shell.Quote))) { UseShellExecute = false });
            Log("started the installer for the update: " + Path.GetFileName(file));
        }

        /// <summary>Windows' update helper; never started on Linux.</summary>
        public static void RunHelper(string[] args) { }

        /// <summary>"streamlite --update" from a terminal: runs the installer now (StreamLite restarts if it was running).</summary>
        public static bool ApplyLinux(string kind, string file, bool show, bool restart, out string message)
        {
            message = null;
            try
            {
                var psi = new ProcessStartInfo("bash", Interop.Shell.Quote(file) + " --update" + (restart ? "" : " --no-start")) { UseShellExecute = false };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) { message = "The installer stopped with code " + p.ExitCode + "."; return false; }
                }
            }
            catch (Exception e) { Log("update failed: " + e); message = "The update failed: " + e.Message; return false; }
            Log("installed " + Path.GetFileName(file) + " (" + kind + ")");
            return true;
        }
#endif
    }
}
