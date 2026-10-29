using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using StreamLite.UI;
using Microsoft.Win32;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>
    /// Settings > Apps entry and a clean removal: sound devices back to how Windows had them (default devices, names,
    /// switched-off cable endpoints), per-app routing cleared, Equalizer APO include removed, admin task, startup
    /// entry, shortcuts and the program folder deleted. "--uninstall --dry-run" only writes what it would do.
    /// </summary>
    internal static class Uninstaller
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamLite";
        // The names VB-Audio's drivers give these endpoints.
        const string CableInput = "CABLE Input", CableOutput = "CABLE Output", HiFiInput = "Hi-Fi Cable Input";

        static string InstallDir { get { return Path.GetDirectoryName(Migration.NewInstallExe); } }

        /// <summary>Lists StreamLite in Settings > Apps (installed copy only).</summary>
        public static void Register()
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Migration.NewInstallExe), StringComparison.OrdinalIgnoreCase)) return;
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    k.SetValue("DisplayName", "StreamLite");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("DisplayVersion", new Version(Application.ProductVersion).ToString(2));
                    k.SetValue("Publisher", "StreamLite");
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void Start(string[] args)
        {
            bool dry = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
            if (args.Any(a => a.Equals("--from-msi", StringComparison.OrdinalIgnoreCase))) { FromMsi(args); return; }
            bool confirmed = args.Any(a => a.Equals("--confirmed", StringComparison.OrdinalIgnoreCase));
            bool drivers = args.Any(a => a.Equals("--remove-drivers", StringComparison.OrdinalIgnoreCase));
            if (!dry)
            {
                if (!confirmed && DarkMessage.Show("Remove StreamLite?\n\nYour sound devices go back to how Windows had them, and the program, its startup entry, admin task and shortcuts are deleted.",
                    "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (!confirmed) drivers = AskDrivers();
                if (!Elevation.IsElevated)
                {
                    // Device names, switched-off endpoints and drivers need admin rights.
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--uninstall --confirmed" + (drivers ? " --remove-drivers" : "")) { Verb = "runas", UseShellExecute = true }); }
                    catch { DarkMessage.Show("StreamLite needs administrator permission to put your sound devices back. Nothing was removed.", "Uninstall StreamLite"); }
                    return;
                }
            }
            var log = new StringBuilder();
            Run(dry, s => log.AppendLine(s), false, 0, drivers || dry);
            if (dry)
            {
                string path = Path.Combine(Path.GetTempPath(), "StreamLite-uninstall-plan.txt");
                File.WriteAllText(path, log.ToString());
                return;
            }
            bool wipe = DarkMessage.Show("StreamLite was removed.\n\nAlso delete your StreamLite settings (mic tuning, EQs, profiles)?\nKeep them if you might install StreamLite again.",
                "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (wipe) try { Directory.Delete(AppSettings.Folder, true); } catch { }
            TellAboutRestart(drivers);
            // This program is running from the folder being removed: delete it once we've exited.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }

        /// <summary>
        /// Called by the MSI while it uninstalls: put the sound devices back (needs admin rights - this asks once and
        /// waits), but leave files, shortcuts and the Apps entry to the MSI, and keep your settings.
        /// </summary>
        static void FromMsi(string[] args)
        {
            // A quiet uninstall (msiexec /qn or /qb, scripts, deployment tools): nobody to answer a question - keep the drivers.
            int ui = 0;
            foreach (var a in args) if (a.StartsWith("--msi-ui=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(9), out ui);
            bool quiet = ui > 0 && ui <= 3;
            if (!Elevation.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--uninstall --confirmed --from-msi --msi-ui=" + ui + " --skip-pid=" + Process.GetCurrentProcess().Id) { Verb = "runas", UseShellExecute = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(180000);
                }
                catch { }   // permission refused: the MSI still removes StreamLite, only the device names stay
                return;
            }
            int skip = 0;
            foreach (var a in args) if (a.StartsWith("--skip-pid=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(11), out skip);
            bool drivers = !quiet && AskDrivers();
            Run(false, s => { }, true, skip, drivers);
            if (!quiet) TellAboutRestart(drivers);
        }

        /// <summary>
        /// Removing the cables needs a Windows restart to finish when a driver was still in use (Hi-Fi Cable's usually is):
        /// until then they can still show in the sound lists. Said plainly, with the choice to restart now.
        /// </summary>
        static void TellAboutRestart(bool drivers)
        {
            if (!drivers) return;
            bool showing = false; try { showing = DriverRemoval.CablesStillShowing(); } catch { }
            if (!DriverRemoval.RestartNeeded && !showing) return;
            if (DarkMessage.Show("Restart Windows to finish removing the virtual cables and Equalizer APO.\n\nThey're uninstalled, but Windows only lets go of a driver that was in use when it restarts - until then \"VB-Audio Hi-Fi Cable\" or \"CABLE Input\" can still show in your sound lists, and Equalizer APO's files stay in Program Files.\n\nRestart now?",
                "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }

        /// <summary>
        /// Also take off what StreamLite installed for itself - VB-CABLE, Hi-Fi Cable (they'd stay in every app's device
        /// list as "VB-Audio Virtual Cable" / "Hi-Fi Cable") and Equalizer APO? Yes unless another app uses them.
        /// </summary>
        static bool AskDrivers()
        {
            bool cables = Prerequisites.VbCableInstalled || Prerequisites.HiFiCableInstalled, apo = EqualizerApo.IsInstalled;
            if (!cables && !apo) return false;
            string what = cables && apo ? "the virtual cables (VB-CABLE, Hi-Fi Cable) and Equalizer APO" : cables ? "the virtual cables (VB-CABLE, Hi-Fi Cable)" : "Equalizer APO";
            return DarkMessage.Show("Also remove " + what + "?\n\nStreamLite installed them for you. Removed, they no longer show up in Discord, games and Windows (\"VB-Audio Virtual Cable\", \"Hi-Fi Cable\"). Keep them only if another app of yours uses them (Voicemeeter and other VB-Audio products stay either way). A restart may finish it.",
                "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>
        /// No cable may stay a default device (playback or mic, any role): each goes back to the real device you used
        /// before StreamLite, or the most fitting real one. 3.5.2 could leave "Hi-Fi Cable Output" as the default mic.
        /// </summary>
        static void RealDefaults(bool dry, Action<string> log, AppSettings S)
        {
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                var list = AudioDevices.List(flow);
                var real = list.Where(d => !AudioDevices.IsCable(d)).ToList();
                if (real.Count == 0) continue;
                AudioDeviceInfo pick;
                if (flow == EDataFlow.Render)
                    pick = real.FirstOrDefault(d => d.Id == S.Game.PreviousDefault) ?? real.FirstOrDefault(d => d.Id == S.Game.HeadphonesId) ?? real.FirstOrDefault(d => d.Id == S.OutputDeviceId)
                        ?? real.OrderBy(d => { string n = (d.Name ?? "").ToLowerInvariant(); return n.Contains("headphone") || n.Contains("headset") ? 0 : n.Contains("speaker") ? 1 : 2; }).First();
                else
                    pick = real.FirstOrDefault(d => d.Id == S.PreviousDefaultMic) ?? real.FirstOrDefault(d => d.Id == S.MicId) ?? real.OrderBy(AppCore.MicRank).First();
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                {
                    string cur = AudioDevices.GetDefaultId(flow, role);
                    var curDev = list.FirstOrDefault(d => d.Id == cur);
                    // 4.1.1: also the side output Direct to headphones muted (calls were moved there; your speakers otherwise).
                    if (curDev == null || !(AudioDevices.IsCable(curDev) || (flow == EDataFlow.Render && role == ERole.Communications && curDev.Id == S.Game.SideMutedId))) continue;
                    var r = role; var to = pick; if (to.Id == curDev.Id) continue;
                    Step(dry, log, "default " + (flow == EDataFlow.Render ? "playback" : "microphone") + " (" + role + "): " + curDev.Name + " -> " + to.Name, () => AudioDevices.SetDefaultRole(to.Id, r));
                }
            }
        }

        static void Step(bool dry, Action<string> log, string what, Action act)
        {
            log((dry ? "would: " : "") + what);
            if (dry) return;
            try { act(); } catch (Exception ex) { log("  failed: " + ex.Message); }
        }

        public static void Run(bool dry, Action<string> log) { Run(dry, log, false, 0, dry); }

        static void Run(bool dry, Action<string> log, bool fromMsi, int skipPid, bool removeDrivers)
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("StreamLite").Where(p => p.Id != self && p.Id != skipPid))
            {
                var proc = p;
                Step(dry, log, "close the running StreamLite (pid " + p.Id + ")", () => { proc.Kill(); proc.WaitForExit(3000); });
            }

            // Only looks things up; nothing is started (and it is never disposed, which would touch devices itself).
            var core = new AppCore(true);
            {
                var S = core.S;
                core.ActiveOutId = S.VirtualOutId;
                var sink = core.FindGameSink();
                core.GameSinkId = sink != null ? sink.Id : null;
                var outs = core.Outputs(); var ins = core.Mics();

                Step(dry, log, "reset per-app output devices to Default (StreamLite sent games, music and voice apps straight to the headphones)", () => AppRouting.ClearAll());

                string play = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
                if (play != null && core.GameSinkCandidates().Any(d => d.Id == play))
                {
                    var phones = core.FindHeadphones(play);
                    string back = !string.IsNullOrEmpty(S.Game.PreviousDefault) && S.Game.PreviousDefault != play ? S.Game.PreviousDefault : phones != null ? phones.Id : null;
                    string backName = outs.Where(d => d.Id == back).Select(d => d.Name).FirstOrDefault() ?? back;
                    if (back != null) Step(dry, log, "set default playback back to " + backName, () => AudioDevices.SetDefaultPlayback(back));
                }

                var cap = core.PairedCapture(S.VirtualOutId);
                string mic = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
                if (cap != null && mic == cap.Id)
                {
                    string back = ins.Any(d => d.Id == S.PreviousDefaultMic) && S.PreviousDefaultMic != cap.Id ? S.PreviousDefaultMic : ins.Any(d => d.Id == S.MicId) ? S.MicId : null;
                    string backName = ins.Where(d => d.Id == back).Select(d => d.Name).FirstOrDefault();
                    if (back != null) Step(dry, log, "set default microphone back to " + backName, () => AudioDevices.SetDefault(back));
                }

                var adapters = new HashSet<string>();
                var allOuts = AudioDevices.List(EDataFlow.Render, 3);
                foreach (var id in new[] { S.VirtualOutId, core.GameSinkId })
                {
                    var d = id != null ? allOuts.FirstOrDefault(o => o.Id == id) : null;
                    if (d != null && AudioDevices.DeviceKey(d).Length > 0) adapters.Add(AudioDevices.DeviceKey(d));
                }
                foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                    foreach (var d in AudioDevices.List(flow, 2).Where(d => adapters.Contains(AudioDevices.DeviceKey(d))))
                    {
                        var dd = d;
                        Step(dry, log, "switch " + d.Name + " back on", () => AudioDevices.SetEnabled(dd.Id, true));
                    }
                Step(dry, log, "show the other virtual devices StreamLite hid (Virtual Audio Cable...) again", () => CableNames.Untidy());

                Action<AudioDeviceInfo, string, string> rename = (d, ours, original) =>
                {
                    if (d != null && d.Name.StartsWith(ours, StringComparison.Ordinal))
                        Step(dry, log, "rename " + d.Name + " back to " + original, () => AudioDevices.Rename(d.Id, original));
                };
                var feed = allOuts.FirstOrDefault(d => d.Id == S.VirtualOutId);
                Action<AudioDeviceInfo, string> adapterBack = (d, original) =>
                {
                    if (d != null && (AudioDevices.AdapterName(d.Id) ?? "").StartsWith("StreamLite", StringComparison.Ordinal))
                        Step(dry, log, "rename " + d.Name + "'s device name back to " + original, () => AudioDevices.RenameAdapter(d.Id, original));
                };
                adapterBack(feed, "VB-Audio Virtual Cable");
                adapterBack(cap, "VB-Audio Virtual Cable");
                adapterBack(sink, "VB-Audio Hi-Fi Cable");
                rename(feed, AppCore.MicFeedName, CableInput);
                rename(cap, AppCore.MicName, CableOutput);
                rename(sink, AppCore.GameOutName, HiFiInput);
                RealDefaults(dry, log, S);
            }

            if (EqualizerApo.IsInstalled)
                Step(dry, log, "remove StreamLite's EQ from Equalizer APO", () => EqualizerApo.Remove());
            if (removeDrivers)
            {
                // The cables' devices first (their driver can only go once nothing runs on it), then their packages.
                foreach (var node in DriverRemoval.CableNodes())
                {
                    var n = node;
                    Step(dry, log, "remove the cable device " + n, () =>
                    {
                        string err = DriverRemoval.RemoveDevice(n);
                        if (err != null) throw new Exception(err);
                    });
                }
                foreach (var pkg in DriverRemoval.CablePackages())
                {
                    var p = pkg;
                    Step(dry, log, "remove the " + (p.Value.StartsWith("vbhf", StringComparison.OrdinalIgnoreCase) ? "Hi-Fi Cable" : "VB-CABLE") + " driver (" + p.Value + ", " + p.Key + ")", () =>
                    {
                        string err = DriverRemoval.Remove(p.Key);
                        if (err != null) throw new Exception(err);
                    });
                }
                if (EqualizerApo.IsInstalled) Step(dry, log, "uninstall Equalizer APO", () => DriverRemoval.RemoveEqualizerApo());
            }
            else Step(dry, log, "give the cables Device Manager's own names back", () => VbTraces.RestoreNames());
            Step(dry, log, "remove the \"" + Camera.VirtualCamera.FriendlyName + "\" virtual camera registration", () => Camera.VirtualCamera.Unregister());
            if (Elevation.TaskExists())
                Step(dry, log, "delete the \"" + Elevation.TaskName + "\" scheduled task", () => Elevation.DeleteTask());
            if (StartupRegistration.IsEnabled)
                Step(dry, log, "remove the launch-at-startup entry", () => StartupRegistration.Set(false));
            if (fromMsi) return;   // the MSI removes its own files, shortcuts and Apps entry
            foreach (var lnk in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StreamLite.lnk"),
                                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StreamLite.lnk") })
                if (File.Exists(lnk)) { var l = lnk; Step(dry, log, "delete shortcut " + l, () => File.Delete(l)); }
            Step(dry, log, "remove the Settings > Apps entry", () => Registry.CurrentUser.DeleteSubKeyTree(Key, false));
            log((dry ? "would: " : "") + "delete " + InstallDir + " after closing" + (dry ? " (and ask about " + AppSettings.Folder + ")" : ""));
        }
    }
}
