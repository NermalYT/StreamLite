using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite.UI;

[assembly: AssemblyTitle("StreamLite")]
[assembly: AssemblyProduct("StreamLite")]
[assembly: AssemblyDescription("Microphone noise removal, EQ, 3D game audio and camera controls")]
[assembly: AssemblyCompany("StreamLite")]
[assembly: AssemblyVersion("2.2.1.0")]
[assembly: AssemblyFileVersion("2.2.1.0")]

namespace StreamLite
{
    internal static class Program
    {
        const string MutexName = "StreamLite.SingleInstance";
        const string EventName = "StreamLite.Show";
        public const string WindowTitle = "StreamLite";
        public static readonly int ShowMessage = RegisterWindowMessage("StreamLite.Show");
        static TrayContext context;

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
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) { Application.EnableVisualStyles(); Uninstaller.Start(args); return; }
            Migration.Settings();
            Mutex mutex = TakeMutex();
            if (mutex == null) { ShowRunningInstance(); return; }
            if (Migration.Task(minimized)) { mutex.ReleaseMutex(); mutex.Dispose(); return; }   // the renamed copy took over
            Migration.Startup();
            Migration.Cleanup();
            Updater.Cleanup();
            Uninstaller.Register();

            // "Run as administrator": hand off to the elevated scheduled task (no UAC prompt).
            if (!fromTask && !Elevation.IsElevated && AppSettings.Load().RunAsAdmin && Elevation.TaskExists())
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
        readonly ToolStripMenuItem micItem, nrItem, gameItem, styleItem, profileItem;

        public TrayContext(bool startMinimized, EventWaitHandle showEvent)
        {
            core = new AppCore();
            StartupRegistration.Refresh();
            form = new MainForm(core);
            var handle = form.Handle; // create the window so Invoke works while hidden
            form.ShowRequested += (s, e) => ShowForm();

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
                var auto = new ToolStripMenuItem("Auto-detect games", null, (s2, e2) => { ps.AutoSwitch = true; ps.ManualProfile = null; core.SaveSoon(); core.Profiles.Reapply(); }) { Checked = ps.AutoSwitch };
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
            tray.BalloonTipClicked += (s, e) => { if (balloonUrl != null) Updater.OpenUrl(balloonUrl); };
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
            if (EqualizerApoReady()) core.OutputEqError = core.WriteOutputEq(false);

            var t = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    try { form.BeginInvoke(new Action(ShowForm)); } catch { return; }
            }) { IsBackground = true };
            t.Start();

            if (!(startMinimized && core.S.StartMinimized)) ShowForm();
            if (core.S.UpdateOnStartup)
                new Thread(() => { Thread.Sleep(15000); CheckForUpdates(false); }) { IsBackground = true, Name = "update check" }.Start();
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
                    string kind = Updater.InstallKind, asset = kind == null ? null : Updater.AssetFor(r, kind);
                    if (asset == null)
                    {
                        Updater.Status = "StreamLite " + r.Version + " is out - download it from GitHub.";
                        Balloon("StreamLite " + r.Version + " is out", "Click to download it from GitHub.", r.Page);
                        if (manual) Updater.OpenUrl(r.Page);
                        return;
                    }
                    if (!manual && Updater.WasDeclined(r.Version)) { Updater.Status = "StreamLite " + r.Version + " is out (Check now installs it)."; return; }
                    if (manual)
                    {
                        bool go = false;
                        form.Invoke(new Action(() =>
                        {
                            using (var d = new ConfirmDialog("Update StreamLite", "StreamLite " + r.Version + " is out (you have " + Program.Version + "). Install it now? StreamLite restarts by itself and your mic drops out for a few seconds.", "Update now", "Later"))
                                go = d.ShowDialog(form.Visible ? form : null) == DialogResult.OK;
                        }));
                        if (!go) { Updater.Status = "StreamLite " + r.Version + " is out."; return; }
                    }
                    Updater.Status = "Downloading StreamLite " + r.Version + "...";
                    Balloon("Updating StreamLite to " + r.Version, "Downloading. StreamLite restarts by itself in a moment.", r.Page);
                    string file = Updater.Download(r, asset);
                    Updater.Status = "Installing StreamLite " + r.Version + "...";
                    form.Invoke(new Action(() =>
                    {
                        Updater.StartHelper(kind, file, form.Visible);
                        ExitApp();
                    }));
                }
                catch (Exception ex)
                {
                    Updater.Status = "Couldn't check for updates: " + ex.Message;
                    if (manual) Balloon("Couldn't update StreamLite", ex.Message, Updater.ReleasesPage);
                }
                finally { Interlocked.Exchange(ref checking, 0); }
            }) { IsBackground = true, Name = "updates" }.Start();
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
            form.AllowClose = true;
            form.Close();
            core.Dispose();
            tray.Dispose();
            ExitThread();
        }
    }
}
