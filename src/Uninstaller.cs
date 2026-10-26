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
            if (!dry)
            {
                if (!confirmed && DarkMessage.Show("Remove StreamLite?\n\nYour sound devices go back to how Windows had them, and the program, its startup entry, admin task and shortcuts are deleted.",
                    "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (!Elevation.IsElevated)
                {
                    // Device names and switched-off endpoints need admin rights.
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--uninstall --confirmed") { Verb = "runas", UseShellExecute = true }); }
                    catch { DarkMessage.Show("StreamLite needs administrator permission to put your sound devices back. Nothing was removed.", "Uninstall StreamLite"); }
                    return;
                }
            }
            var log = new StringBuilder();
            Run(dry, s => log.AppendLine(s));
            if (dry)
            {
                string path = Path.Combine(Path.GetTempPath(), "StreamLite-uninstall-plan.txt");
                File.WriteAllText(path, log.ToString());
                return;
            }
            bool wipe = DarkMessage.Show("StreamLite was removed.\n\nAlso delete your StreamLite settings (mic tuning, EQs, profiles)?\nKeep them if you might install StreamLite again.",
                "Uninstall StreamLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (wipe) try { Directory.Delete(AppSettings.Folder, true); } catch { }
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
            if (!Elevation.IsElevated)
            {
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--uninstall --confirmed --from-msi --skip-pid=" + Process.GetCurrentProcess().Id) { Verb = "runas", UseShellExecute = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(180000);
                }
                catch { }   // permission refused: the MSI still removes StreamLite, only the device names stay
                return;
            }
            int skip = 0;
            foreach (var a in args) if (a.StartsWith("--skip-pid=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(11), out skip);
            Run(false, s => { }, true, skip);
        }

        static void Step(bool dry, Action<string> log, string what, Action act)
        {
            log((dry ? "would: " : "") + what);
            if (dry) return;
            try { act(); } catch (Exception ex) { log("  failed: " + ex.Message); }
        }

        public static void Run(bool dry, Action<string> log) { Run(dry, log, false, 0); }

        static void Run(bool dry, Action<string> log, bool fromMsi, int skipPid)
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
            }

            if (EqualizerApo.IsInstalled)
                Step(dry, log, "remove StreamLite's EQ from Equalizer APO", () => EqualizerApo.Remove());
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
