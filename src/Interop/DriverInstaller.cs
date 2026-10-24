using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamLite.Interop
{
    /// <summary>
    /// Downloads and runs the free add-ons StreamLite needs on a new PC, from their makers' own sites (nothing is
    /// bundled): VB-CABLE (the virtual mic), VB-Audio Hi-Fi Cable (the 3D game device) and Equalizer APO (headphone
    /// EQ). Each installer asks Windows for permission (UAC) and installs silently where it supports that.
    /// </summary>
    internal static class DriverInstaller
    {
        public const string VbCableZip = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
        public const string HiFiCableZip = "https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip";
        public const string EqualizerApoPage = "https://sourceforge.net/projects/equalizerapo/files/latest/download";

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamLite";
            return wc;
        }

        static string WorkDir(string name)
        {
            string d = Path.Combine(Path.GetTempPath(), "StreamLite-" + name);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Downloads a VB-Audio zip and returns the extracted setup program (64-bit when there is one).</summary>
        public static string FetchVbAudio(string zipUrl, string setupPattern, string name)
        {
            string dir = WorkDir(name);
            string zip = Path.Combine(dir, Path.GetFileName(new Uri(zipUrl).LocalPath));
            using (var wc = Client()) wc.DownloadFile(zipUrl, zip);
            string ex = Path.Combine(dir, "x");
            if (Directory.Exists(ex)) Directory.Delete(ex, true);
            ZipFile.ExtractToDirectory(zip, ex);
            var setups = Directory.GetFiles(ex, setupPattern, SearchOption.AllDirectories);
            string setup = setups.FirstOrDefault(f => f.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0) ?? setups.FirstOrDefault();
            if (setup == null) throw new FileNotFoundException("The setup program wasn't in the download.");
            return setup;
        }

        /// <summary>
        /// Equalizer APO's installer from SourceForge. Its download link is a page that hands out a short-lived mirror
        /// link, so read that from the page, then fetch the installer.
        /// </summary>
        public static string FetchEqualizerApo()
        {
            string dir = WorkDir("EqualizerAPO");
            string exe = Path.Combine(dir, "EqualizerAPO-x64-setup.exe");
            using (var wc = Client())
            {
                byte[] first = wc.DownloadData(EqualizerApoPage);
                if (!(first.Length > 2 && first[0] == 'M' && first[1] == 'Z'))
                {
                    string html = System.Text.Encoding.UTF8.GetString(first);
                    var m = Regex.Match(html, "data-release-url=\"([^\"]+)\"");
                    if (!m.Success) m = Regex.Match(html, "url=(https://downloads\\.sourceforge\\.net/[^\"]+)\"");
                    if (!m.Success) throw new InvalidDataException("SourceForge's download page changed - get it from sourceforge.net/projects/equalizerapo.");
                    string url = WebUtility.HtmlDecode(m.Groups[1].Value);
                    if (url.IndexOf("x64", StringComparison.OrdinalIgnoreCase) < 0 && url.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0)
                        url = url.Replace("x86", "x64");
                    first = wc.DownloadData(url);
                }
                if (!(first.Length > 2 && first[0] == 'M' && first[1] == 'Z')) throw new InvalidDataException("The download wasn't an installer.");
                // SourceForge publishes the latest release's SHA-256: check the download against it when it's there.
                string expected = null;
                try
                {
                    var j = Regex.Match(wc.DownloadString("https://sourceforge.net/projects/equalizerapo/best_release.json"), "\"sha256sum\"\\s*:\\s*\"([0-9a-f]{64})\"");
                    if (j.Success) expected = j.Groups[1].Value;
                }
                catch { }
                if (expected != null)
                {
                    string got;
                    using (var sha = System.Security.Cryptography.SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(first)).Replace("-", "").ToLowerInvariant();
                    if (got != expected) throw new InvalidDataException("The Equalizer APO download doesn't match SourceForge's checksum.");
                }
                File.WriteAllBytes(exe, first);
            }
            return exe;
        }

        /// <summary>Runs an installer with admin rights and waits. Returns false if the user said no to the prompt.</summary>
        public static bool RunElevated(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) }))
                    p.WaitForExit();
                return true;
            }
            catch (Win32Exception) { return false; }   // UAC declined
        }

        /// <summary>Installs VB-CABLE and waits for the new device. null = done, otherwise what went wrong.</summary>
        public static string InstallVbCable(Func<bool> present, Action<string> progress)
        {
            try
            {
                progress("Downloading VB-CABLE...");
                string setup = FetchVbAudio(VbCableZip, "VBCABLE_Setup*.exe", "VBCABLE");
                progress("Installing (approve the Windows prompt)...");
                if (!RunElevated(setup, "-i -h")) return "Install cancelled.";
                for (int i = 0; i < 30 && !present(); i++) Thread.Sleep(500);
                if (!present())
                {
                    // Silent mode not supported by this version: open the installer normally.
                    if (!RunElevated(setup, "")) return "Install cancelled.";
                    for (int i = 0; i < 30 && !present(); i++) Thread.Sleep(500);
                }
                return present() ? null : "VB-CABLE didn't show up yet. If the installer asked to restart, restart Windows and open StreamLite again.";
            }
            catch (Exception ex) { return "Install failed: " + ex.Message; }
        }

        /// <summary>Installs Equalizer APO silently. null = done, otherwise what went wrong.</summary>
        public static string InstallEqualizerApo(Func<bool> present, Action<string> progress)
        {
            try
            {
                progress("Downloading Equalizer APO...");
                string exe = FetchEqualizerApo();
                progress("Installing (approve the Windows prompt)...");
                if (!RunElevated(exe, "/S")) return "Install cancelled.";
                for (int i = 0; i < 20 && !present(); i++) Thread.Sleep(500);
                return present() ? null : "Equalizer APO didn't finish installing. Try again, or install it from sourceforge.net/projects/equalizerapo.";
            }
            catch (Exception ex) { return "Install failed: " + ex.Message; }
        }
    }
}
