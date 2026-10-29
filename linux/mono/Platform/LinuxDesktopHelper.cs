using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// The desktop helper (desktop-helper.py, GTK + D-Bus): StreamLite's tray menu as an AppIndicator where old-style
    /// tray icons don't show (GNOME, KDE on Wayland), and hotkeys through the desktop's global-shortcuts portal on
    /// Wayland - where X11 only hears keys while an XWayland app has focus (4.1.4).
    /// </summary>
    internal sealed class LinuxDesktopHelper : IDisposable
    {
        public static LinuxDesktopHelper Current { get; private set; }

        /// <summary>Its tray shows (an AppIndicator is in place of Mono's tray icon).</summary>
        public volatile bool TrayReady;
        /// <summary>The desktop bound the hotkeys (Wayland).</summary>
        public volatile bool ShortcutsReady;
        public volatile string TrayProblem, ShortcutsProblem;
        public event Action TrayShown;
        /// <summary>A shortcut went down (true) or up: its id ("h0" = the first hotkey with a key).</summary>
        public event Action<string, bool> Shortcut;

        readonly Process process;
        readonly Control ui;
        ContextMenuStrip menu;
        string lastMenu;
        readonly Timer menuTimer;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public static bool IsWayland { get { return string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase); } }

        /// <summary>The tray goes through the helper: on Wayland, where no old-style tray is there, or when asked (STREAMLITE_TRAY=indicator).</summary>
        public static bool WantTray
        {
            get
            {
                string force = Environment.GetEnvironmentVariable("STREAMLITE_TRAY");
                if (force == "indicator") return true;
                if (force == "xembed") return false;
                return IsWayland || !LinuxDesktop.HasXEmbedTray;
            }
        }

        public static LinuxDesktopHelper Start(Control ui, bool tray, bool shortcuts, string icon)
        {
            if (!tray && !shortcuts) return null;
            if (!Shell.Has("python3")) return null;
            try { return Current = new LinuxDesktopHelper(ui, tray, shortcuts, icon); }
            catch { return null; }
        }

        LinuxDesktopHelper(Control ui, bool tray, bool shortcuts, string icon)
        {
            this.ui = ui;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "StreamLite");
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "desktop-helper.py");
            using (var s = typeof(LinuxDesktopHelper).Assembly.GetManifestResourceStream("desktop-helper.py"))
            using (var r = new StreamReader(s)) File.WriteAllText(script, r.ReadToEnd());
            var args = new List<string> { "-u", script, icon ?? "audio-input-microphone" };
            if (tray) args.Add("--tray");
            if (shortcuts) args.Add("--shortcuts");
            process = Process.Start(new ProcessStartInfo("python3", string.Join(" ", args.Select(Shell.Quote)))
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
            process.StandardError.ReadToEndAsync();
            process.OutputDataReceived += (s, e) => Line(e.Data);
            process.BeginOutputReadLine();
            menuTimer = new Timer { Interval = 2000 };
            menuTimer.Tick += (s, e) => SendMenu();
        }

        void Line(string l)
        {
            if (string.IsNullOrEmpty(l)) return;
            if (l == "ready tray") { TrayReady = true; Post(() => { SendMenu(); if (TrayShown != null) TrayShown(); }); }
            else if (l == "ready shortcuts") { ShortcutsReady = true; Log("Hotkeys: the desktop's shortcuts (Wayland) are bound"); }
            else if (l.StartsWith("no tray: ")) { TrayProblem = l.Substring(9); Log("Tray: " + TrayProblem); }
            else if (l.StartsWith("no shortcuts: ")) { ShortcutsProblem = l.Substring(14); Log("Hotkeys on Wayland: " + ShortcutsProblem); }
            else if (l.StartsWith("click ")) { string path = l.Substring(6); Post(() => Click(path)); }
            else if (l.StartsWith("down ") || l.StartsWith("up "))
            {
                bool down = l.StartsWith("down "); string id = l.Substring(down ? 5 : 3);
                if (Shortcut != null) Shortcut(id, down);
            }
        }

        static void Log(string s) { try { HealthMonitor.LogLine(s); } catch { } }

        void Post(Action a) { try { if (ui.IsHandleCreated) ui.BeginInvoke(a); } catch { } }

        void Send(object o)
        {
            try { process.StandardInput.WriteLine(json.Serialize(o)); process.StandardInput.Flush(); } catch { }
        }

        /// <summary>The tray menu to show (sent again whenever it changes).</summary>
        public void UseMenu(ContextMenuStrip m) { menu = m; menuTimer.Start(); SendMenu(); }

        void SendMenu()
        {
            if (menu == null || !TrayReady) return;
            try
            {
                // The menu fills some items as it opens (game profiles): let it, as if opened.
                var open = typeof(ToolStripDropDown).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (open != null) open.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
            }
            catch { }
            var data = new Dictionary<string, object> { { "title", "StreamLite" }, { "items", Items(menu.Items) } };
            string text = json.Serialize(new Dictionary<string, object> { { "menu", data } });
            if (text == lastMenu) return;
            lastMenu = text;
            try { process.StandardInput.WriteLine(text); process.StandardInput.Flush(); } catch { }
        }

        static List<object> Items(ToolStripItemCollection items)
        {
            var l = new List<object>();
            foreach (ToolStripItem it in items)
            {
                if (!it.Available) continue;
                if (it is ToolStripSeparator) { l.Add(new Dictionary<string, object> { { "sep", true } }); continue; }
                var mi = it as ToolStripMenuItem; if (mi == null) continue;
                var d = new Dictionary<string, object> { { "label", mi.Text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&") }, { "enabled", mi.Enabled } };
                if (mi.CheckOnClick || mi.Checked) { d["check"] = true; d["checked"] = mi.Checked; }
                if (mi.DropDownItems.Count > 0) d["items"] = Items(mi.DropDownItems);
                l.Add(d);
            }
            return l;
        }

        void Click(string path)
        {
            if (menu == null) return;
            ToolStripItemCollection items = menu.Items; ToolStripItem hit = null;
            foreach (var part in path.Split('/'))
            {
                int i; if (!int.TryParse(part, out i)) return;
                // Indexes count only what was sent (hidden items left out).
                var shown = items.Cast<ToolStripItem>().Where(x => x.Available).Where(x => x is ToolStripSeparator || x is ToolStripMenuItem).ToList();
                if (i >= shown.Count) return;
                hit = shown[i];
                var mi = hit as ToolStripMenuItem; if (mi == null) return;
                items = mi.DropDownItems;
            }
            if (hit != null) hit.PerformClick();
            lastMenu = null; SendMenu();
        }

        /// <summary>The hotkeys for the desktop to bind (Wayland): id, what it does, its keys ("CTRL+F9").</summary>
        public void UseShortcuts(IEnumerable<KeyValuePair<string, string[]>> shortcuts)
        {
            Send(new Dictionary<string, object> { { "shortcuts", shortcuts.Select(s => new Dictionary<string, object> { { "id", s.Key }, { "description", s.Value[0] }, { "trigger", s.Value[1] } }).ToList() } });
        }

        public void Dispose()
        {
            menuTimer.Stop();
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(500)) process.Kill(); } catch { }
            if (Current == this) Current = null;
        }
    }
}
