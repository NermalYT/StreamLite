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
        static string MachineFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamLite"); } }

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
                // 4.3.2: installed by the MSI (Settings > Uninstall in the app): the MSI removes it - this path alone left
                // the program, its Apps entry and shortcuts where they are.
                string msi = confirmed ? null : InstalledMsi();
                if (msi != null)
                {
                    try { Process.Start("msiexec.exe", "/x " + msi); } catch (Exception ex) { DarkMessage.Show("Couldn't start the uninstaller: " + ex.Message, "Uninstall StreamLite"); }
                    return;
                }
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
            // 4.4: asked first (settings name their devices, which are about to go), done last.
            var settings = dry ? null : UninstallSettings.Ask(s => log.AppendLine(s));
            Run(dry, s => log.AppendLine(s), false, 0, drivers || dry);
            if (dry)
            {
                string path = Path.Combine(Path.GetTempPath(), "StreamLite-uninstall-plan.txt");
                File.WriteAllText(path, log.ToString());
                return;
            }
            UninstallSettings.Finish(settings, s => log.AppendLine(s), DriverRemoval.DeleteFolder);
            TellAboutRestart(false);
            // This program is running from the folder being removed: delete it once we've exited.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }

        /// <summary>This copy was installed by the MSI: its product code, for msiexec /x. Otherwise null.</summary>
        static string InstalledMsi()
        {
            try
            {
                string kind = Path.Combine(Application.StartupPath, "install-kind.txt");
                if (!File.Exists(kind) || !File.ReadAllText(kind).Trim().StartsWith("msi", StringComparison.OrdinalIgnoreCase)) return null;
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var u = b.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    foreach (var name in u.GetSubKeyNames().Where(n => n.StartsWith("{")))
                        using (var k = u.OpenSubKey(name))
                            if (k != null && (k.GetValue("DisplayName") as string) == "StreamLite") return name;
            }
            catch { }
            return null;
        }

        /// <summary>A yes / no that answers itself after a while (no one may be there: a script's /qb), and stays in front.</summary>
        static bool AskWithTimeout(string text, string title, int seconds)
        {
            if (!Environment.UserInteractive) return false;   // a service, a remote session: no one can see it, and showing it throws
            bool timedOut = false; int ticks = 0;
            try
            {
            using (var t = new Timer { Interval = 500 })
            {
                t.Tick += (s, e) =>
                {
                    var open = Application.OpenForms.Cast<Form>().LastOrDefault();
                    if (open != null) open.TopMost = true;
                    if (++ticks >= seconds * 2 && open != null) { timedOut = true; open.DialogResult = DialogResult.No; open.Close(); }
                };
                t.Start();
                var r = DarkMessage.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                return r == DialogResult.Yes && !timedOut;
            }
            }
            catch { return false; }   // a question must never stop the removal
        }

        /// <summary>
        /// Called by the MSI while it uninstalls, however it was started: sound devices back, and everything StreamLite put
        /// on this PC off again (needs admin rights - this asks once and waits). Files, shortcuts and the Apps entry are
        /// the MSI's. 4.3.2: Windows' Settings > Apps uninstalls with no questions, and that used to keep the drivers.
        /// </summary>
        static void FromMsi(string[] args)
        {
            // What it did, for a bug report (the settings folder may be going).
            string logFile = Path.Combine(Path.GetTempPath(), "StreamLite-uninstall.log");
            Action<string> log = s => { try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine); } catch { } };
            // The MSI's last step before it removes the files, as SYSTEM (no one is asked anything). The prerequisites log goes
            // with the machine folder at the end of the step below, so it's still here only if that step didn't run: its
            // permission prompt was refused. (The log, not the folder: a file in use can keep the folder until a restart,
            // and with the log gone the cables would count as StreamLite's even if you chose to keep them.)
            if (args.Any(a => a.Equals("--msi-system", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(Path.Combine(MachineFolder, "prerequisites.log"))) return;
                log("StreamLite " + Program.Version + " uninstall, the earlier step didn't run: removing what StreamLite installed. Cables StreamLite's: " + Prerequisites.CablesAreOurs + ", Equalizer APO StreamLite's: " + Prerequisites.ApoIsOurs);
                Run(false, log, true, 0, false);
                // Looking up the devices made settings for the SYSTEM account.
                if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem) DriverRemoval.DeleteFolder(AppSettings.Folder);
                return;
            }
            // Only msiexec /qn (scripts, deployment tools) can't be asked anything.
            int ui = 0;
            foreach (var a in args) if (a.StartsWith("--msi-ui=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(9), out ui);
            bool quiet = ui == 2;
            if (!Elevation.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--uninstall --confirmed --from-msi --msi-ui=" + ui + " --skip-pid=" + Process.GetCurrentProcess().Id) { Verb = "runas", UseShellExecute = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(30 * 60 * 1000);   // 4.4: while you pick which settings to save
                }
                catch
                {
                    // Permission refused: what this account can do without it (default devices, launch at startup). The
                    // cables and the rest go in the MSI's last step, which needs no permission.
                    log("no permission: the installer's own last step removes the rest");
                    try { PutDevicesBack(false, log, Process.GetCurrentProcess().Id); } catch (Exception ex) { log("  failed: " + ex.Message); }
                    try { StartupRegistration.Set(false); } catch { }
                }
                return;
            }
            int skip = 0;
            foreach (var a in args) if (a.StartsWith("--skip-pid=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(11), out skip);
            bool drivers = !quiet && AskDrivers();
            log("StreamLite " + Program.Version + " uninstall, installer window level " + ui + ", cables StreamLite's: " + Prerequisites.CablesAreOurs + ", Equalizer APO StreamLite's: " + Prerequisites.ApoIsOurs + ", yes to the ones it found here: " + drivers);
            // 4.4: keep, pick and save, or delete. Asked first: settings name their devices, which are about to go.
            var settings = quiet ? null : UninstallSettings.Ask(log);
            Run(false, log, true, skip, drivers);
            if (quiet) return;
            UninstallSettings.Finish(settings, log, DriverRemoval.DeleteFolder);
            TellAboutRestart(true);
        }

        /// <summary>
        /// Something was still in use and goes with a Windows restart: said plainly, with the choice to restart now.
        /// 4.3.2: while the MSI is running it waits until the MSI has finished. Asked before that, it held the uninstall up
        /// until someone answered, and Yes would have restarted Windows half way through it. This program is about to be
        /// deleted by then, so a small PowerShell script does the asking.
        /// </summary>
        static void TellAboutRestart(bool installerRunning)
        {
            if (!DriverRemoval.RestartNeeded || !Environment.UserInteractive) return;
            const string text = "Restart Windows to finish uninstalling StreamLite.\n\nIt's removed, but Windows only lets go of a driver or file that was in use when it restarts. Until then a virtual cable can still show in your sound lists, or a few files can stay on disk.\n\nRestart now?";
            const string title = "Uninstall StreamLite";
            if (!installerRunning)
            {
                if (DarkMessage.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                return;
            }
            Func<string, string> quoted = s => "'" + s.Replace("'", "''") + "'";
            // Up to 15 minutes for the MSI to delete this program. Still here after that: it was cancelled, so no question.
            // Popup flags: Yes/No 4, question 32, No is the default 256, always on top 4096, to the front 65536. (A WinForms
            // message box from here never showed up on screen in the lab, with Settings open.) 6 = Yes.
            string script = "$i = 0; while ((Test-Path -LiteralPath " + quoted(Application.ExecutablePath) + ") -and $i -lt 900) { Start-Sleep 1; $i++ }\n"
                + "if ($i -ge 900) { exit }\n"
                + "Start-Sleep 5\n"
                + "if ((New-Object -ComObject WScript.Shell).Popup(" + quoted(text) + ", 0, " + quoted(title) + ", 4 + 32 + 256 + 4096 + 65536) -eq 6) { shutdown.exe /r /t 5 }\n";
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }

        /// <summary>
        /// What StreamLite installed comes off without asking (4.3.2). The one question: VB-CABLE, Hi-Fi Cable or Equalizer
        /// APO that were on this PC before StreamLite - remove those too?
        /// </summary>
        static bool AskDrivers()
        {
            bool cables = (Prerequisites.VbCableInstalled || Prerequisites.HiFiCableInstalled) && !Prerequisites.CablesAreOurs, apo = EqualizerApo.IsInstalled && !Prerequisites.ApoIsOurs;
            if (!cables && !apo) return false;
            string what = cables && apo ? "the virtual cables (VB-CABLE, Hi-Fi Cable) and Equalizer APO" : cables ? "the virtual cables (VB-CABLE, Hi-Fi Cable)" : "Equalizer APO";
            return AskWithTimeout("Also remove " + what + "?\n\nThey were on this PC before StreamLite, so another app of yours may use them. Removed, they no longer show up in Discord, games and Windows. (Voicemeeter and other VB-Audio products stay either way.)",
                "Uninstall StreamLite", 60);
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
                    // 4.3.2: PreviousComms set = StreamLite still has the communications device moved (to your speakers, say).
                    var was = flow == EDataFlow.Render && role == ERole.Communications ? real.FirstOrDefault(d => d.Id == S.Game.PreviousComms) : null;
                    bool moved = was != null && curDev != null && curDev.Id != was.Id;
                    if (curDev == null || !(moved || AudioDevices.IsCable(curDev) || (flow == EDataFlow.Render && role == ERole.Communications && curDev.Id == S.Game.SideMutedId))) continue;
                    var r = role; var to = was ?? pick; if (to.Id == curDev.Id) continue;
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
            // 4.3.2: nothing that goes wrong with the sound devices may stop what comes after it.
            try { PutDevicesBack(dry, log, skipPid); } catch (Exception ex) { log("putting the sound devices back stopped: " + ex.Message); }
            RemoveFromPc(dry, log, fromMsi, removeDrivers);
        }

        static void PutDevicesBack(bool dry, Action<string> log, int skipPid)
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
                if (S.Game.SideMutedId != null) Step(dry, log, "give the side output its sound back, with calls off it first", () => core.UnmuteSide());

                string play = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
                if (play != null && core.GameSinkCandidates().Any(d => d.Id == play))
                {
                    var phones = core.FindHeadphones(play);
                    string back = !string.IsNullOrEmpty(S.Game.PreviousDefault) && S.Game.PreviousDefault != play ? S.Game.PreviousDefault : phones != null ? phones.Id : null;
                    string backName = outs.Where(d => d.Id == back).Select(d => d.Name).FirstOrDefault() ?? back;
                    if (back != null) Step(dry, log, "set default playback back to " + backName, () => AudioDevices.SetDefaultPlayback(back));
                }

                // The mic cable from the settings, else by name: the installer names and hides it before StreamLite has
                // ever run on this account (4.3.2: a cable that stayed kept StreamLite's name and stayed hidden).
                var allOuts = AudioDevices.List(EDataFlow.Render, 3);
                var feed = allOuts.FirstOrDefault(d => d.Id == S.VirtualOutId) ?? allOuts.FirstOrDefault(CableNames.IsMicCable);
                var cap = feed == null ? null : AudioDevices.List(EDataFlow.Capture, 3).FirstOrDefault(d => AudioDevices.LooksVirtual(d) && AudioDevices.SameDevice(d, feed));
                string mic = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
                if (cap != null && mic == cap.Id)
                {
                    string back = ins.Any(d => d.Id == S.PreviousDefaultMic) && S.PreviousDefaultMic != cap.Id ? S.PreviousDefaultMic : ins.Any(d => d.Id == S.MicId) ? S.MicId : null;
                    string backName = ins.Where(d => d.Id == back).Select(d => d.Name).FirstOrDefault();
                    if (back != null) Step(dry, log, "set default microphone back to " + backName, () => AudioDevices.SetDefault(back));
                }

                var adapters = new HashSet<string>();
                foreach (var id in new[] { feed != null ? feed.Id : null, core.GameSinkId })
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
        }

        static void RemoveFromPc(bool dry, Action<string> log, bool fromMsi, bool removeDrivers)
        {
            if (EqualizerApo.IsInstalled)
                Step(dry, log, "remove StreamLite's EQ from Equalizer APO", () => EqualizerApo.Remove());
            // What StreamLite installed always goes; what was here before only when asked (removeDrivers).
            bool cablesGo = removeDrivers || Prerequisites.CablesAreOurs, apoGoes = removeDrivers || Prerequisites.ApoIsOurs;
            string apoDir = EqualizerApo.InstallPath;
            if (cablesGo)
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
                Step(dry, log, "take VB-Audio's certificates back out of the trusted publishers", () => log("  removed " + Prerequisites.UntrustVbAudio()));
                Step(dry, log, "delete VB-Audio's settings", () => Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\VB-Audio", false));
            }
            else Step(dry, log, "give the cables Device Manager's own names back", () => VbTraces.RestoreNames());
            if (apoGoes && EqualizerApo.IsInstalled) Step(dry, log, "uninstall Equalizer APO", () => DriverRemoval.RemoveEqualizerApo());
            Step(dry, log, "remove the \"" + Camera.VirtualCamera.FriendlyName + "\" virtual camera registration", () => Camera.VirtualCamera.Unregister());
            if (Elevation.TaskExists())
                Step(dry, log, "delete the \"" + Elevation.TaskName + "\" scheduled task", () => Elevation.DeleteTask());
            if (StartupRegistration.IsEnabled)
                Step(dry, log, "remove the launch-at-startup entry", () => StartupRegistration.Set(false));
            if (cablesGo || apoGoes) Step(dry, log, "restart Windows Audio, so it lets go of what was removed", () => DriverRemoval.ReleaseAudio());
            if (apoGoes && apoDir != null) Step(dry, log, "delete what's left of Equalizer APO (" + apoDir + ")", () => DriverRemoval.DeleteFolder(apoDir));
            Step(dry, log, "delete StreamLite's machine-wide folder", () =>
            {
                try { Directory.Delete(MachineFolder, true); } catch { }
                if (!Directory.Exists(MachineFolder)) return;
                // Windows' camera service keeps StreamLite Camera's files open after it was used.
                log("  in use: stopping Windows' camera service (it starts again when an app opens a camera)");
                DriverRemoval.ReleaseCamera();
                DriverRemoval.DeleteFolder(MachineFolder);
                if (Directory.Exists(MachineFolder)) log("  still in use, goes with the next restart: " + string.Join(", ", Directory.GetFiles(MachineFolder, "*", SearchOption.AllDirectories).Select(Path.GetFileName)));
            });
            if (!dry)
            {
                // What a restart would really still finish, now that Windows Audio has let go: pnputil says "reboot" for
                // a driver that was in use, and by now it may not be. (A cable you chose to keep is no reason to restart.)
                bool cablesLeft = false; try { cablesLeft = cablesGo && DriverRemoval.CablesStillShowing(); } catch { }
                DriverRemoval.RestartNeeded = cablesLeft || (apoGoes && apoDir != null && Directory.Exists(apoDir)) || Directory.Exists(MachineFolder);
                log("restart needed: " + DriverRemoval.RestartNeeded);
            }
            if (fromMsi) return;   // the MSI removes its own files, shortcuts and Apps entry
            foreach (var lnk in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StreamLite.lnk"),
                                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StreamLite.lnk") })
                if (File.Exists(lnk)) { var l = lnk; Step(dry, log, "delete shortcut " + l, () => File.Delete(l)); }
            Step(dry, log, "remove the Settings > Apps entry", () => Registry.CurrentUser.DeleteSubKeyTree(Key, false));
            log((dry ? "would: " : "") + "delete " + InstallDir + " after closing" + (dry ? " (and ask about " + AppSettings.Folder + ")" : ""));
        }
    }
}
