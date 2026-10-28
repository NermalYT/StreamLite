using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Interop;
using StreamLite.UI;

[assembly: AssemblyTitle("StreamLite")]
[assembly: AssemblyProduct("StreamLite")]
[assembly: AssemblyDescription("Microphone noise removal, EQ, 3D game audio and camera controls")]
[assembly: AssemblyCompany("StreamLite")]
[assembly: AssemblyVersion("4.1.0.0")]
[assembly: AssemblyFileVersion("4.1.0.0")]

namespace StreamLite
{
    internal static class Program
    {
        const string MutexName = "StreamLite.SingleInstance";
        const string EventName = "StreamLite.Show";
        public const string WindowTitle = "StreamLite";
        public static readonly int ShowMessage = RegisterWindowMessage("StreamLite.Show");
        /// <summary>"StreamLite.exe --quit" asks the running copy (even an elevated one) to quit cleanly.</summary>
        public static readonly int QuitMessage = RegisterWindowMessage("StreamLite.Quit");
        /// <summary>"StreamLite.exe --setup-check" asks the running copy to run the setup check into setup-check.txt.</summary>
        public static readonly int CheckMessage = RegisterWindowMessage("StreamLite.SetupCheck");
        static TrayContext context;
        /// <summary>The running app (null in helpers and tests).</summary>
        internal static TrayContext Context { get { return context; } }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

        [STAThread]
        static void Main(string[] args)
        {
            bool minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            bool fromTask = args.Any(a => a.Equals("--from-task", StringComparison.OrdinalIgnoreCase));

            if (args.Any(a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase))) { Updater.RunHelper(args); return; }
            if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase))) { QuitRunning(); return; }
            if (args.Any(a => a.Equals("--setup-check", StringComparison.OrdinalIgnoreCase))) { IntPtr w = FindAppWindow(); if (w != IntPtr.Zero) PostMessage(w, CheckMessage, IntPtr.Zero, IntPtr.Zero); return; }
            if (args.Any(a => a.Equals("--install-prerequisites", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = Prerequisites.InstallAll(args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
                return;
            }
            // The cables under StreamLite's names (from the app's offer, with admin rights): 0 = all named.
            if (args.Any(a => a.Equals("--name-devices", StringComparison.OrdinalIgnoreCase)))
            {
                try { CableNames.ApplyWhenReady(10000, Prerequisites.VbCableInstalled, Prerequisites.HiFiCableInstalled); } catch { }
                if (args.Any(a => a.Equals("--restore-task", StringComparison.OrdinalIgnoreCase))) try { Elevation.CreateTask(Application.ExecutablePath); } catch { }
                if (!args.Any(a => a.Equals("--keep-all", StringComparison.OrdinalIgnoreCase)))
                {
                    try { CableNames.Tidy(); } catch { }
                    // Another maker's virtual cable StreamLite never installed: offered, with its own uninstaller.
                    string other = CableNames.OtherCableUninstaller();
                    if (other != null)
                    {
                        Application.EnableVisualStyles();
                        if (DarkMessage.Show("Virtual Audio Cable (lite) is also installed on this PC - another virtual cable, not from StreamLite and not used by it. StreamLite hides its devices.\n\nUninstall it completely?", "Tidy up sound devices", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            try
                            {
                                string exe = other.StartsWith("\"") ? other.Substring(1, other.IndexOf('"', 1) - 1) : other.Split(' ')[0];
                                string rest = other.Substring(other.IndexOf(exe, StringComparison.Ordinal) + exe.Length).TrimStart('"', ' ');
                                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, rest) { UseShellExecute = false })) p.WaitForExit(300000);
                            }
                            catch { }
                    }
                }
                Environment.ExitCode = CableNames.Named() ? 0 : 1;
                return;
            }
            // The mic feed's direct path failed while it's switched off (and StreamLite has no admin rights): back on.
            if (args.Any(a => a.Equals("--show-feed", StringComparison.OrdinalIgnoreCase)))
            {
                try { foreach (var d in AudioDevices.List(EDataFlow.Render, 2).Where(CableNames.IsMicCable)) AudioDevices.SetEnabled(d.Id, true); } catch { }
                return;
            }
            // Equalizer APO installed and put on the headphones you picked, without its own device list (asked for by ApoDevices.Setup).
            if (args.Any(a => a.Equals("--apo-setup", StringComparison.OrdinalIgnoreCase)))
            {
                try { Environment.ExitCode = StreamLite.Audio.ApoDevices.RunHelper(args); } catch { Environment.ExitCode = 1; }
                return;
            }
            // StreamLite Camera's one-time machine-wide registration (the app asks Windows for permission for this).
            if (args.Any(a => a.Equals("--register-camera", StringComparison.OrdinalIgnoreCase)))
            {
                try { Environment.ExitCode = StreamLite.Camera.VirtualCamera.Register() == null ? 0 : 1; } catch { Environment.ExitCode = 1; }
                return;
            }
            // "Keep the device list tidy" switched off without admin rights: everything StreamLite hid, back.
            if (args.Any(a => a.Equals("--untidy-devices", StringComparison.OrdinalIgnoreCase)))
            {
                try { CableNames.Untidy(); } catch { }
                return;
            }
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) { Application.EnableVisualStyles(); Uninstaller.Start(args); return; }
            // The mic path keeps only a few ms of audio in flight: avoid blocking full garbage collections (loading
            // an AI model allocates a lot), which would pause the audio threads for longer than that.
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            Migration.Settings();
            // 4.1: what the last runs used gets compiled on spare cores while StreamLite starts (no first-use hitches).
            try { System.IO.Directory.CreateDirectory(AppSettings.Folder); System.Runtime.ProfileOptimization.SetProfileRoot(AppSettings.Folder); System.Runtime.ProfileOptimization.StartProfile("startup.jitprofile"); } catch { }
            Mutex mutex = TakeMutex();
            if (mutex == null) { ShowRunningInstance(); return; }
            if (Migration.Task(minimized)) { mutex.ReleaseMutex(); mutex.Dispose(); return; }   // the renamed copy took over
            Migration.Startup();
            Migration.Cleanup();
            Updater.Cleanup();
            Uninstaller.Register();

            // "Run as administrator": hand off to the elevated scheduled task (no UAC prompt).
            if (!fromTask && !Elevation.IsElevated && AppSettings.Load().RunAsAdmin && Elevation.TaskRuns(Application.ExecutablePath))
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
                try { AllowSetForegroundWindow(-1); } catch { }   // let the elevated copy come to the front
                if (Elevation.RunTask(minimized)) return;
                mutex = TakeMutex();
                if (mutex == null) { ShowRunningInstance(); return; }
            }
            if (fromTask) minimized = Elevation.TakePendingMinimized();

            using (mutex)
            {
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => LogError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    LogError(e.ExceptionObject as Exception);
                    // Never leave Windows playing into the (silent) game device if we crash.
                    try { if (context != null) context.EmergencyRestore(); } catch { }
                };

                context = new TrayContext(minimized, showEvent);
                Application.Run(context);
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>Returns the owned single-instance mutex, or null if another copy (maybe elevated) is running.</summary>
        static Mutex TakeMutex()
        {
            try
            {
                bool created;
                var m = new Mutex(true, MutexName, out created);
                if (created) return m;
                m.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException) { return null; } // held by the elevated copy
        }

        static void ShowRunningInstance()
        {
            // We were started by the user (desktop icon / Start menu), so we may hand our right to take the
            // foreground to the running copy - otherwise Windows leaves its window behind other apps.
            try
            {
                IntPtr w = FindWindow(null, WindowTitle);
                int pid;
                if (w != IntPtr.Zero && GetWindowThreadProcessId(w, out pid) != 0) AllowSetForegroundWindow(pid);
                else AllowSetForegroundWindow(-1);
            }
            catch { }
            try { using (var ev = EventWaitHandle.OpenExisting(EventName)) { ev.Set(); return; } }
            catch { }
            // The running copy is elevated: a window message it explicitly allows crosses the privilege boundary.
            IntPtr hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder sb, int max);

        /// <summary>The main window of a running StreamLite (by title and by process - an Explorer window of a folder named StreamLite doesn't count).</summary>
        static IntPtr FindAppWindow()
        {
            IntPtr found = IntPtr.Zero;
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                var sb = new System.Text.StringBuilder(64);
                GetWindowText(h, sb, 64);
                if (sb.ToString() != WindowTitle) return true;
                int pid; GetWindowThreadProcessId(h, out pid);
                try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) if (pid != me && p.ProcessName.Equals("StreamLite", StringComparison.OrdinalIgnoreCase)) { found = h; return false; } }
                catch { }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Asks the running StreamLite to quit (puts the sound devices back first) and waits for it.</summary>
        static void QuitRunning()
        {
            IntPtr h = FindAppWindow();
            if (h == IntPtr.Zero) return;
            int pid; GetWindowThreadProcessId(h, out pid);
            PostMessage(h, QuitMessage, IntPtr.Zero, IntPtr.Zero);
            try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) p.WaitForExit(15000); } catch { }
        }

        /// <summary>From "--quit": stop at once, no questions (the way the tray's Quit stops after you confirm).</summary>
        public static void QuitNow() { if (context != null) context.ExitApp(); }

        /// <summary>Tray "Quit" / Settings "Quit": stop everything (asks first).</summary>
        public static void Exit() { if (context != null) context.RequestExit(); }

        /// <summary>Window close button / Alt+F4: ask whether to keep running in the background.</summary>
        public static void CloseWindow() { if (context != null) context.CloseWindow(); }

        public static Version Version { get { return Updater.ParseVersion(Application.ProductVersion); } }

        /// <summary>Settings "Check now" / tray "Check for updates".</summary>
        public static void CheckForUpdates() { if (context != null) context.CheckForUpdates(true); }

        /// <summary>Opens GitHub's bug report form with the version and Windows version filled in.</summary>
        public static void ReportBug() { Updater.OpenUrl(Updater.BugReportUrl(Version.ToString())); }

        public static void RestartElevated()
        {
            if (context == null) return;
            Elevation.RestartElevatedAfterExit();
            context.ExitApp();
        }

        static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.Folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "error.log"), DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        readonly AppCore core;
        readonly MainForm form;
        readonly NotifyIcon tray;
        readonly HotkeyManager hotkeys;
        readonly ToolStripMenuItem micItem, nrItem, gameItem, styleItem, profileItem;

        public TrayContext(bool startMinimized, EventWaitHandle showEvent)
        {
            core = new AppCore();
            StartupRegistration.Refresh();
            form = new MainForm(core);
            var handle = form.Handle; // create the window so Invoke works while hidden
            form.ShowRequested += (s, e) => ShowForm();
            // Global hotkeys (Hotkeys page), with a small note on screen for what they did.
            hotkeys = new HotkeyManager(core);
            form.Hotkeys = hotkeys;
            var toast = new HotkeyToast();
            hotkeys.Fired += note => { if (core.S.HotkeyToasts) toast.Note(note); form.ReloadFromHotkey(); UpdateTrayText(); };
            // After an update: check the setup once (no test tones) and say so in the window if something broke.
            string last = core.S.LastVersion, thisVersion = Program.Version.ToString();
            setupDue = RetuneWizard.Needed(core.S); firstRunDue = last == null && !core.S.VoiceSetupDone;   // first run: nothing to reset
            // 4.1's defaults, once: Run as administrator on (every setting works) and, for a new install, launch with Windows.
            if (core.S.Defaults41 == null)
            {
                if (last == null) try { StartupRegistration.Set(true); } catch { }
                core.S.RunAsAdmin = true; core.S.Defaults41 = "4.1.0"; core.SaveSoon();
            }
            core.S.LastVersion = thisVersion; core.SaveSoon();
            if (last != null && last != thisVersion)
                new Thread(() =>
                {
                    Thread.Sleep(25000);
                    try
                    {
                        var res = SetupCheck.Run(core, false, _ => { });
                        var bad = res.Where(x => x.State == CheckItem.Fail).ToList();
                        if (bad.Count > 0)
                            form.BeginInvoke(new Action(() =>
                            {
                                form.ShowUpdatePrompt("After the update to " + thisVersion + ", the setup check found " + (bad.Count == 1 ? "a problem" : bad.Count + " problems") + ": " + bad[0].Title + " - " + bad[0].Detail, () => { form.HideUpdatePrompt(); ShowForm(); form.ShowSetupCheck(); }, null, true, "Show me", "Dismiss");
                                if (!form.Visible) Balloon("StreamLite needs a look", "After the update: " + bad[0].Title + ". Open StreamLite to fix it.", null);
                            }));
                    }
                    catch { }
                }) { IsBackground = true, Name = "after-update check" }.Start();
            // From 4.0.5 StreamLite reads the mic raw: everyone - new, or updating from any version - goes through the setup
            // again (required until it's been done through Personalize 3D; it starts every time StreamLite opens).
            if (setupDue)
            {
                var setupTimer = new System.Windows.Forms.Timer { Interval = 1200 };
                setupTimer.Tick += (s, e) =>
                {
                    if (!RetuneWizard.Needed(core.S)) { setupTimer.Stop(); return; }
                    if (!form.Visible || form.WindowState == FormWindowState.Minimized || Form.ActiveForm != form) return;
                    setupTimer.Stop();
                    RetuneWizard.Required(form, core, !firstRunDue);
                };
                setupTimer.Start();
            }
            // After installing or updating: "Tune StreamLite to your voice", once, when the window is open and the mic runs
            // (it took the place of the Direct to headphones question).
            var askTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            askTimer.Tick += (s, e) =>
            {
                if (core.S.VoiceSetupDone) { askTimer.Stop(); return; }
                if (RetuneWizard.Needed(core.S)) return;   // the required setup (it has Tune to my voice in it) comes first
                if (!core.VoiceSetupDue || !form.Visible || form.WindowState == FormWindowState.Minimized || Form.ActiveForm != form) return;
                askTimer.Stop();
                form.OpenVoiceSetup();
            };
            askTimer.Start();
            // Cables still under VB-Audio's names (a portable copy, or an install that renamed too early): offered once
            // per version - renaming needs admin rights, so Windows asks.
            // Another note in the banner (the setup check, an update) doesn't make it give up: it asks again every 30 s.
            var namesTimer = new System.Windows.Forms.Timer { Interval = 20000 };
            namesTimer.Tick += (s, e) => { namesTimer.Interval = 30000; if (OfferNames()) namesTimer.Stop(); };
            namesTimer.Start();
            core.ToggleWindowRequested += (s, e) => { if (form.Visible && form.WindowState != FormWindowState.Minimized) form.Hide(); else ShowForm(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open StreamLite", null, (s, e) => ShowForm());
            menu.Items.Add(new ToolStripSeparator());
            micItem = new ToolStripMenuItem("Virtual microphone", null, (s, e) => { core.S.VirtualMicOn = !core.S.VirtualMicOn; core.RestartAudio(); });
            nrItem = new ToolStripMenuItem("Noise removal", null, (s, e) => { core.S.NoiseRemoval = !core.S.NoiseRemoval; core.PushEngineParams(); });
            gameItem = new ToolStripMenuItem("Game 3D audio", null, (s, e) => { core.S.Game.Enabled = !core.S.Game.Enabled; core.RestartGameAudio(); });
            styleItem = new ToolStripMenuItem("Voice style");
            foreach (var name in new[] { VoiceStyles.Mine }.Concat(VoiceStyles.All.Select(x => x.Name)))
            {
                string n = name;
                styleItem.DropDownItems.Add(new ToolStripMenuItem(n, null, (s, e) => SetStyle(n)));
            }
            profileItem = new ToolStripMenuItem("Headphone profile");
            profileItem.DropDownItems.Add("(filled when the menu opens)");
            menu.Items.Add(micItem);
            menu.Items.Add(nrItem);
            menu.Items.Add(styleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(profileItem);
            menu.Items.Add(gameItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check for updates", null, (s, e) => CheckForUpdates(true));
            menu.Items.Add("Report a bug...", null, (s, e) => Program.ReportBug());
            menu.Items.Add("Quit", null, (s, e) => RequestExit());
            menu.Opening += (s, e) =>
            {
                micItem.Checked = core.S.VirtualMicOn; nrItem.Checked = core.S.NoiseRemoval; gameItem.Checked = core.S.Game.Enabled;
                string current = VoiceStyles.Match(core.S) ?? VoiceStyles.Mine;
                foreach (ToolStripMenuItem it in styleItem.DropDownItems)
                {
                    it.Checked = it.Text == current;
                    if (it.Text == VoiceStyles.Mine) it.Enabled = current == VoiceStyles.Mine || VoiceStyles.HasMine(core.S);
                }
                // Headphone profile: auto-detect, or one picked by hand (same as Headphones > Game profiles).
                var ps = core.S.Profiles;
                profileItem.DropDownItems.Clear();
                var auto = new ToolStripMenuItem(GameProfiles.AutoEq, null, (s2, e2) => { ps.AutoSwitch = true; ps.ManualProfile = null; core.SaveSoon(); core.Profiles.Reapply(); }) { Checked = ps.AutoSwitch };
                profileItem.DropDownItems.Add(auto);
                profileItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var p in ps.Profiles)
                {
                    string n = p.Name;
                    profileItem.DropDownItems.Add(new ToolStripMenuItem(n, null, (s2, e2) => { ps.AutoSwitch = false; ps.ManualProfile = n; core.SaveSoon(); core.Profiles.Reapply(); })
                        { Checked = n == core.Profiles.ActiveName });
                }
            };

            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon = SystemIcons.Application; }
            tray = new NotifyIcon { Icon = icon, Text = "StreamLite", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };
            tray.BalloonTipClicked += (s, e) => { if (balloonUrl != null) Updater.OpenUrl(balloonUrl); else ShowForm(); };   // no link: the note points into the window (update prompt)
            tray.BalloonTipClosed += (s, e) => balloonUrl = null;
            core.ParamsChanged += (s, e) => UpdateTrayText();

            core.RestartAudio();
            core.RestartGameAudio();
            string lastShown = null;
            core.MicFollowed += (s, name) =>
            {
                try { form.BeginInvoke(new Action(() => tray.ShowBalloonTip(4000, "Clean mic now uses " + name, "Your microphone was unplugged or came back - StreamLite switched by itself.", ToolTipIcon.Info))); } catch { }
            };
            core.HeadphonesFollowed += (s, name) =>
            {
                try { form.BeginInvoke(new Action(() => tray.ShowBalloonTip(4000, "3D audio now plays on " + name, "StreamLite followed your headphones (plugged in, unplugged or picked in Windows).", ToolTipIcon.Info))); } catch { }
            };
            // Direct to headphones (built in): an app set to the headphones themselves gets no sound - say what to change.
            core.AppLockedOut += (s, app) =>
            {
                try { form.BeginInvoke(new Action(() => tray.ShowBalloonTip(8000, app + " is set to play straight to your headphones", "StreamLite plays everything to them now, so " + app + " gets no sound that way. In " + app + "'s own sound settings pick \"Default\" as the output - StreamLite then plays it.", ToolTipIcon.Warning))); } catch { }
            };
            core.CorrectionFound += (s, what) =>
            {
                try { form.BeginInvoke(new Action(() => tray.ShowBalloonTip(5000, "Headphone EQ for your headset", what + " - its measured correction is now under every game profile (Headphones page to turn it off or pick another).", ToolTipIcon.Info))); } catch { }
            };
            core.GameRoutedFirstTime += (s, name) =>
                tray.ShowBalloonTip(6000, name + ": restart the game once",
                    "It uses its own 3D audio, so StreamLite now sends it straight to your headphones. Windows applies that from the game's next launch on.", ToolTipIcon.Info);
            core.Profiles.Changed += (s, e) =>
            {
                // Tell the user when a game profile kicks in (not on every re-apply).
                string exe = core.Profiles.DetectedExe;
                if (exe != null && exe != lastShown)
                    tray.ShowBalloonTip(3000, core.Profiles.ActiveName + " detected", "Headphone EQ switched to the " + core.Profiles.ActiveName + " profile.", ToolTipIcon.Info);
                lastShown = exe;
                UpdateTrayText();
            };
            core.Profiles.Start();
            core.Cameras.Start();
            if (EqualizerApoReady()) core.OutputEqError = core.WriteOutputEqAsking();

            var t = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    try { form.BeginInvoke(new Action(ShowForm)); } catch { return; }
            }) { IsBackground = true };
            t.Start();

            // The required setup due (4.0.5: everyone, new or updating): StreamLite opens on the whole primary monitor, the setup in front.
            if (setupDue) form.BeginSetupScreen(firstRunDue ? RetuneWizard.FirstTitle : RetuneWizard.UpdateTitle, RetuneWizard.Subtitle);
            if (!(startMinimized && core.S.StartMinimized)) ShowForm();
            if (core.S.UpdateOnStartup)
                new Thread(() => { Thread.Sleep(4000); CheckForUpdates(false); }) { IsBackground = true, Name = "update check" }.Start();
            // StreamLite often runs for days in the tray (the PC sleeps instead of restarting): look again every 6 hours.
            var periodic = new System.Windows.Forms.Timer { Interval = 6 * 3600 * 1000 };
            periodic.Tick += (s, e) => { if (core.S.UpdateOnStartup) CheckForUpdates(false); };
            periodic.Start();
            form.VisibleChanged += (s, e) =>
            {
                if (!form.Visible && !core.S.TrayHintShown)
                {
                    core.S.TrayHintShown = true;
                    core.SaveSoon();
                    tray.ShowBalloonTip(4000, "StreamLite is still running", "Your mic and camera effects stay on. Open or quit it from the tray icon.", ToolTipIcon.Info);
                }
            };
        }

        string balloonUrl;
        bool setupDue, firstRunDue;   // the required setup is due (first run: nothing to reset): StreamLite opens on its setup screen
        int checking;

        void Balloon(string title, string text, string url)
        {
            try { form.BeginInvoke(new Action(() => { balloonUrl = url; tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info); })); } catch { }
        }

        /// <summary>
        /// Asks GitHub for the newest release; a newer one is downloaded and installed (StreamLite restarts by itself).
        /// manual = from the Settings page or the tray menu: asks before installing, and reports "up to date" too.
        /// </summary>
        public void CheckForUpdates(bool manual)
        {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            Updater.Status = "Checking for updates...";
            new Thread(() =>
            {
                try
                {
                    var r = Updater.Latest();
                    if (!Updater.IsNewer(r, Program.Version)) { Updater.Status = "StreamLite " + Program.Version + " is the newest version."; return; }
                    Updater.Note("found " + r.Tag + " (have " + Program.Version + ")" + (manual ? ", checked by hand" : ""));
                    string kind = Updater.InstallKind, asset = kind == null ? null : Updater.AssetFor(r, kind);
                    // Whatever happens below: "Update to X" in the title bar until it's installed.
                    form.BeginInvoke(new Action(() => form.SetUpdateAvailable(r.Version.ToString(), asset == null ? (Action)(() => Updater.OpenUrl(r.Page)) : () => CheckForUpdates(true))));
                    if (asset == null)
                    {
                        Updater.Status = "StreamLite " + r.Version + " is out - download it from GitHub.";
                        Balloon("StreamLite " + r.Version + " is out", "Click to download it from GitHub.", r.Page);
                        if (manual) Updater.OpenUrl(r.Page);
                        return;
                    }
                    if (!manual && Updater.WasDeclined(r.Version)) { Updater.Status = "StreamLite " + r.Version + " is out - skipped (Check now offers it again)."; return; }
                    // Ask inside the window: Update now or Skip. While StreamLite sits in the tray, a note says so and
                    // the prompt is waiting when you open it.
                    Updater.Status = "StreamLite " + r.Version + " is available.";
                    form.BeginInvoke(new Action(() =>
                    {
                        form.ShowUpdatePrompt("StreamLite " + r.Version + " is available (you have " + Program.Version + ").\nUpdate now? StreamLite restarts by itself; your mic and game sound drop out for a few seconds.",
                            () => InstallUpdate(r, kind, asset),
                            () => { Updater.Skip(r.Version); Updater.Note("skipped " + r.Tag); Updater.Status = "StreamLite " + r.Version + " skipped (Check now offers it again)."; });
                        if (!form.Visible) Balloon("StreamLite " + r.Version + " is available", "Open StreamLite to update now or skip it.", null);
                    }));
                }
                catch (Exception ex)
                {
                    Updater.Status = "Couldn't check for updates: " + ex.Message;
                    Updater.Note("check failed: " + ex.Message);
                    if (manual) Balloon("Couldn't update StreamLite", ex.Message, Updater.ReleasesPage);
                }
                finally { Interlocked.Exchange(ref checking, 0); }
            }) { IsBackground = true, Name = "updates" }.Start();
        }

        /// <summary>"Update now": download, then install and restart (the prompt shows the progress).</summary>
        void InstallUpdate(Updater.Release r, string kind, string asset)
        {
            form.ShowUpdatePrompt("Downloading StreamLite " + r.Version + "...", null, null, false);
            new Thread(() =>
            {
                try
                {
                    Updater.Status = "Downloading StreamLite " + r.Version + "...";
                    string file = Updater.Download(r, asset);
                    Updater.Status = "Installing StreamLite " + r.Version + "...";
                    Updater.Note("installing " + r.Tag + " (update now)");
                    form.Invoke(new Action(() =>
                    {
                        form.ShowUpdatePrompt("Installing StreamLite " + r.Version + " - it restarts by itself in a moment.", null, null, false);
                        Updater.StartHelper(kind, file, form.Visible);
                        ExitApp();
                    }));
                }
                catch (Exception ex)
                {
                    Updater.Status = "Couldn't update: " + ex.Message;
                    Updater.Note("update failed: " + ex.Message);
                    try { form.BeginInvoke(new Action(() => form.ShowUpdatePrompt("Couldn't update StreamLite: " + ex.Message, () => InstallUpdate(r, kind, asset), null))); } catch { }
                }
            }) { IsBackground = true, Name = "update install" }.Start();
        }

        public void EmergencyRestore() { core.Game.Stop(); core.RestoreGameDefault(); }

        void SetStyle(string name)
        {
            if (name == VoiceStyles.Mine)
            {
                if (VoiceStyles.Match(core.S) == null) return;   // already your own tuning
                if (!VoiceStyles.RestoreMine(core.S)) return;
            }
            else VoiceStyles.Apply(core.S, name);
            core.PushEngineParams();
            form.ReloadVoice();
        }

        /// <summary>Hovering the tray icon shows the voice style and headphone profile in use.</summary>
        void UpdateTrayText()
        {
            string t = "StreamLite\nVoice: " + (VoiceStyles.Match(core.S) ?? VoiceStyles.Mine);
            if (core.Profiles.ActiveName != null) t += "\nHeadphones: " + core.Profiles.ActiveName;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;   // Windows limit
        }

        /// <summary>True when there's nothing (more) to offer; false: the banner was busy, try again later.</summary>
        bool OfferNames()
        {
            bool needTask, tidy;
            try
            {
                if (core.S.HideVirtualDevices) VbTraces.CleanUserStartMenu();
                if (Elevation.IsElevated || core.S.NamesOffered == Program.Version.ToString()) return true;
                if (RetuneWizard.Needed(core.S)) return false;   // the setup asks for this itself (its one Windows prompt)
                // Run as administrator (4.1: on by default) without its startup task yet: the same prompt makes it.
                needTask = core.S.RunAsAdmin && !Elevation.TaskExists();
                tidy = (Prerequisites.VbCableInstalled || Prerequisites.HiFiCableInstalled) && CableNames.NeedsTidying(core.S.HideVirtualDevices);
                if (!tidy && !needTask) return true;
                if (form.UpdatePromptVisible) return false;
            }
            catch { return true; }
            form.ShowUpdatePrompt(tidy
                ? "Tidy up your sound devices? Windows, Discord and games then list only your real devices plus StreamLite - In and StreamLite - Out - no \"VB-Audio\", \"CABLE\" or \"Hi-Fi Cable\" anywhere (also not in Device Manager or Settings > Apps)." + (needTask ? " StreamLite also gets the administrator rights some of its settings need." : "") + " Windows asks for permission once."
                : "Some settings (hidden virtual devices, the virtual camera, the lowest-delay mic) need administrator rights. Give them to StreamLite? Windows asks once; after that StreamLite starts with them by itself.",
                () => TidyNow(), () => { core.S.NamesOffered = Program.Version.ToString(); core.SaveSoon(); }, true, tidy ? "Tidy up" : "Give admin rights", "Not now");
            return true;
        }

        /// <summary>
        /// The tidy-up with admin rights (one Windows prompt): names, hidden cables, VB-Audio's traces - and Run as
        /// administrator's startup task back when it was on but went missing (reinstalling deletes it).
        /// </summary>
        public void TidyNow()
        {
                    bool restoreTask = core.S.RunAsAdmin && !Elevation.TaskExists();
                    form.ShowUpdatePrompt("Tidying up (approve the Windows prompt)...", null, null, false);
                    new System.Threading.Thread(() =>
                    {
                        bool asked = DriverInstaller.RunElevated(Application.ExecutablePath, "--name-devices" + (core.S.HideVirtualDevices ? "" : " --keep-all") + (restoreTask ? " --restore-task" : ""));
                        bool ok = false; try { ok = CableNames.Named(); } catch { }
                        try
                        {
                            form.BeginInvoke(new Action(() =>
                            {
                                core.S.NamesOffered = Program.Version.ToString(); core.SaveSoon();
                                if (ok)
                                {
                                    form.HideUpdatePrompt(); Balloon("Sound devices tidied up", "Pick \"" + AppCore.MicName + "\" as your mic in Discord and games.", null);
                                    if (restoreTask && Elevation.TaskRuns(Application.ExecutablePath)) Program.RestartElevated();   // Run as administrator, as you had it
                                }
                                else form.ShowUpdatePrompt(asked ? "Windows didn't let StreamLite rename them. They still work under their old names - in apps pick \"CABLE Output\" as your mic." : "Not renamed (the Windows prompt was declined). They still work under their old names.", () => form.HideUpdatePrompt(), null, true, "OK", "Dismiss");
                            }));
                        }
                        catch { }
                    }) { IsBackground = true, Name = "rename devices" }.Start();
        }

        static bool EqualizerApoReady() { return StreamLite.Audio.EqualizerApo.IsInstalled; }

        void ShowForm()
        {
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            // Raise above other windows even if Windows refuses to give us focus.
            form.TopMost = true; form.TopMost = false;
            form.Activate();
            form.BringToFront();
        }

        /// <summary>Quitting silences the virtual mic for apps pinned to it, so ask first.</summary>
        public void RequestExit()
        {
            if (core.S.VirtualMicOn && core.Engine.Running)
            {
                if (form.Visible) form.Activate();
                using (var d = new ConfirmDialog("Stop StreamLite?",
                    "Your cleaned-up microphone turns off. Apps set to \"" + AppCore.MicName + "\" (like Discord) will hear silence until StreamLite runs again. Headphone EQ keeps working.",
                    "Stop service & quit", "Cancel"))
                    if (d.ShowDialog(form.Visible ? form : null) != DialogResult.OK) return;
            }
            ExitApp();
        }

        /// <summary>The window's X: keep the service running in the tray, or stop it - per setting or by asking.</summary>
        public void CloseWindow()
        {
            string action = core.S.CloseAction ?? "ask";
            if (action == "ask")
            {
                using (var d = new CloseDialog())
                {
                    if (d.ShowDialog(form) != DialogResult.OK) return;
                    action = d.Choice;
                    if (d.Remember) { core.S.CloseAction = action; core.SaveSoon(); }
                }
            }
            if (action == "exit") ExitApp();
            else form.Hide();
        }

        public void ExitApp()
        {
            tray.Visible = false;
            hotkeys.Dispose();
            form.AllowClose = true;
            form.Close();
            core.Dispose();
            tray.Dispose();
            ExitThread();
        }
    }
}
