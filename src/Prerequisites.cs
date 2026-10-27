using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.Win32;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>
    /// Everything StreamLite needs on a new PC, installed by the MSI in its one window (StreamLite.exe
    /// --install-prerequisites, run elevated and hidden by the installer): VB-CABLE (the virtual mic), VB-Audio Hi-Fi
    /// Cable (the 3D game device) and Equalizer APO (headphone EQ). Each comes from its maker's own site (nothing is
    /// bundled) and installs silently; what's already there is skipped. VB-Audio's own signing certificates are added
    /// to Windows' trusted publishers first, so Windows doesn't stop the silent install with its "Would you like to
    /// install this device software?" window. Never fails the StreamLite install: whatever didn't work, the app offers
    /// again. Log: %ProgramData%\StreamLite\prerequisites.log.
    /// </summary>
    internal static class Prerequisites
    {
        public static bool VbCableInstalled { get { return ServiceExists("VBAudioVACMME"); } }
        public static bool HiFiCableInstalled { get { return ServiceExists("VBAudioHFVAIOMME"); } }
        public static bool EqualizerApoInstalled { get { return EqualizerApo.IsInstalled; } }

        /// <summary>
        /// The driver's service is there and so is its driver file. Removing a driver package (an uninstall) can leave the
        /// service entry behind with nothing in it (4.0.1): that counts as not installed.
        /// </summary>
        static bool ServiceExists(string name)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                if (k == null) return false;
                string image = k.GetValue("ImagePath") as string;
                if (string.IsNullOrEmpty(image)) return true;
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(win, image.Substring(12));
                else if (image.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(win, image);
                else if (image.StartsWith(@"\??\", StringComparison.Ordinal)) image = image.Substring(4);
                return File.Exists(image);
            }
        }

        static StreamWriter log;
        static void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            Console.WriteLine(line);
            if (log != null) try { log.WriteLine(line); log.Flush(); } catch { }
        }

        /// <summary>Installs whatever is missing. dryRun = only say what would happen. Exit code: 0 = all present now.</summary>
        public static int InstallAll(bool dryRun)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamLite");
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "prerequisites.log"), true);
            }
            catch { }
            Log("StreamLite " + Program.Version + " prerequisites" + (dryRun ? " (dry run)" : "") + ", " + (Elevation.IsElevated ? "elevated" : "NOT elevated - installs will ask for permission"));
            var steps = new[]
            {
                Tuple.Create("VB-CABLE (virtual mic)", (Func<bool>)(() => VbCableInstalled), (Action)InstallVbCable),
                Tuple.Create("VB-Audio Hi-Fi Cable (3D game audio)", (Func<bool>)(() => HiFiCableInstalled), (Action)InstallHiFiCable),
                Tuple.Create("Equalizer APO (headphone EQ)", (Func<bool>)(() => EqualizerApoInstalled), (Action)InstallEqualizerApo),
            };
            int missing = 0;
            foreach (var s in steps)
            {
                if (s.Item2()) { Log(s.Item1 + ": already installed"); continue; }
                if (dryRun) { Log(s.Item1 + ": would download and install silently"); missing++; continue; }
                try
                {
                    Log(s.Item1 + ": installing...");
                    s.Item3();
                    for (int i = 0; i < 40 && !s.Item2(); i++) Thread.Sleep(250);
                    Log(s.Item1 + (s.Item2() ? ": installed" : ": NOT detected after its installer finished"));
                    if (!s.Item2()) missing++;
                }
                catch (Exception ex) { Log(s.Item1 + ": failed - " + ex.Message); missing++; }
            }
            // StreamLite's names on the cables (apps list "StreamLite - In", not VB-Audio's): needs the rights this step has.
            if (!dryRun)
                try
                {
                    var named = CableNames.ApplyWhenReady(30000, VbCableInstalled, HiFiCableInstalled);
                    named.AddRange(CableNames.Tidy());   // only real devices and StreamLite - In / Out in the lists
                    Log((named.Count == 0 ? "device names: nothing renamed" : "device names: " + string.Join("; ", named)) + (CableNames.Named() ? " - all StreamLite's" : " - NOT all renamed (StreamLite offers it)"));
                }
                catch (Exception ex) { Log("device names: " + ex.Message); }
            // StreamLite Camera registered while the installer has admin rights: the app never needs to ask.
            if (!dryRun && Camera.VirtualCamera.Supported)
                try { string e = Camera.VirtualCamera.Register(); Log("StreamLite Camera: " + (e ?? "registered")); }
                catch (Exception ex) { Log("StreamLite Camera: " + ex.Message); }
            Log(missing == 0 ? "everything is installed" : missing + " item(s) missing - StreamLite offers them again");
            if (log != null) log.Dispose();
            return missing == 0 ? 0 : 1;
        }

        static void InstallVbCable()
        {
            string setup = DriverInstaller.FetchVbAudio(DriverInstaller.VbCableZip, "VBCABLE_Setup*.exe", "VBCABLE");
            TrustVbAudio(Path.GetDirectoryName(setup));
            Run(setup, "-i -h");
        }

        static void InstallHiFiCable()
        {
            string setup = DriverInstaller.FetchVbAudio(DriverInstaller.HiFiCableZip, "HiFiCableAsioBridgeSetup*.exe", "HiFiCable");
            TrustVbAudio(Path.GetDirectoryName(setup));
            Run(setup, "-i -h");
        }

        /// <summary>
        /// Equalizer APO's installer also sets up its update checker as a scheduled task for whoever runs it. Run as
        /// SYSTEM (the MSI's install step) it can't name a user, and stops with an "Error installing Update Checker" box
        /// until someone clicks OK. So it runs as the signed-in user with their admin rights, as if they ran it; when
        /// that's not possible (nobody signed in, a standard account) it's left out here, and StreamLite installs it
        /// later in the user's own session. Its device list, which its setup always opens and waits for, is closed the
        /// moment it appears (4.0.2): StreamLite puts it on your headphones itself when you pick them.
        /// </summary>
        static void InstallEqualizerApo()
        {
            string exe = DriverInstaller.FetchEqualizerApo();
            if (!UserProcess.IsSystem) { ApoDevices.RunSetupQuietly(exe, 5 * 60 * 1000, (e, a) => { Run(e, a); return 0; }); return; }
            ApoDevices.RunSetupQuietly(exe, 5 * 60 * 1000, (e, a) =>
            {
                int code; string why;
                if (UserProcess.RunElevated(e, a, 5 * 60 * 1000, out code, out why)) Log("  " + Path.GetFileName(e) + " " + a + " (as the signed-in user) -> exit code " + code);
                else Log("  not installed here (" + why + ") - StreamLite offers it in your session");
                return code;
            });
        }

        static void Run(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) }))
            {
                if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException(Path.GetFileName(exe) + " didn't finish in 5 minutes"); }
                Log("  " + Path.GetFileName(exe) + " " + args + " -> exit code " + p.ExitCode);
            }
        }

        /// <summary>
        /// Adds the certificates VB-Audio signs its setup programs and older driver catalogs with (subject "Vincent
        /// Burel" / "BUREL VINCENT") to the machine's Trusted Publishers - only those, read from the downloaded files'
        /// valid signatures - so the silent driver install isn't interrupted by Windows' publisher prompt.
        /// </summary>
        /// <summary>A real code-signing certificate from a trusted root (revocation isn't checked: the install may be offline-ish).</summary>
        static bool ChainsToTrustedRoot(X509Certificate2 c)
        {
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;   // signatures are timestamped; an expired signer is fine
                return chain.Build(c);
            }
        }

        static void TrustVbAudio(string folder)
        {
            var found = VbAudioCertificates(folder);
            if (found.Count == 0) { Log("  no VB-Audio certificate found to trust"); return; }
            using (var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (var c in found)
                    if (store.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count == 0) { store.Add(c); Log("  trusted publisher: " + c.Subject); }
            }
        }

        /// <summary>VB-Audio's signing certificates in a downloaded package (valid signatures only).</summary>
        internal static List<X509Certificate2> VbAudioCertificates(string folder)
        {
            var found = new Dictionary<string, X509Certificate2>();
            foreach (var f in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var certs = new List<X509Certificate2>();
                    if (f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
                    {
                        var c = new X509Certificate2Collection(); c.Import(f);
                        certs.AddRange(c.Cast<X509Certificate2>());
                    }
                    else certs.Add(new X509Certificate2(X509Certificate.CreateFromSignedFile(f)));
                    foreach (var c in certs)
                        if (c.Subject.IndexOf("Burel", StringComparison.OrdinalIgnoreCase) >= 0 && ChainsToTrustedRoot(c)) found[c.Thumbprint] = c;
                }
                catch { }   // unsigned file
            }
            return found.Values.ToList();
        }

    }
}
