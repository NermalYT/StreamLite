// Where the window stalls (4.1, "run like a dream"): the main window off screen with a copy of the settings, each page
// shown for 4 seconds while a 1 ms timer measures how late the UI thread gets to it (a stall = a hitch on screen), plus
// how long switching to each page takes. The audio engines aren't started. Usage: run.ps1 UiStallProbe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;
using StreamLite.Interop;

class UiStallProbe
{
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);

    static IEnumerable<Control> Descend(Control c) { yield return c; foreach (Control k in c.Controls) foreach (var d in Descend(k)) yield return d; }

    [STAThread]
    static void Main(string[] args)
    {
        timeBeginPeriod(1);
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-stallprobe");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        // As StreamLite itself does (4.1): the startup JIT profile - the second run of this probe shows its effect.
        if (args.Length == 0 || args[0] != "nojit") { string prof = Path.Combine(Path.GetTempPath(), "StreamLite-stallprobe-jit"); Directory.CreateDirectory(prof); System.Runtime.ProfileOptimization.SetProfileRoot(prof); System.Runtime.ProfileOptimization.StartProfile("startup.jitprofile"); }
        Application.EnableVisualStyles();
        var core = new AppCore();
        foreach (var f in typeof(AppCore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) if (f.FieldType == typeof(Timer)) { var tm = (Timer)f.GetValue(core); if (tm != null) { tm.Stop(); tm.Enabled = false; } }
        var sw0 = Stopwatch.StartNew();
        var main = new MainForm(core) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(1100, 900) };
        main.Show(); Application.DoEvents();
        Console.WriteLine("  window built and shown in " + sw0.ElapsedMilliseconds + " ms");
        // The real message loop raises Idle when it runs out of messages (DoEvents does not): as StreamLite settles after showing.
        var settle = Stopwatch.StartNew(); while (settle.ElapsedMilliseconds < 2000) { Application.DoEvents(); if (args.Length == 0 || Array.IndexOf(args, "noidle") < 0) Application.RaiseIdle(EventArgs.Empty); System.Threading.Thread.Sleep(5); }
        var pages = (System.Collections.IList)typeof(MainForm).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
        var show = typeof(MainForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (args.Length > 0 && args[args.Length - 1] == "split")
        {
            // Where the first switch to the 3D and Camera pages spends its time.
            var g3 = typeof(MainForm).GetField("gamePage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
            var la = g3.GetType().GetMethod("LoadAll", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var t1 = Stopwatch.StartNew(); la.Invoke(g3, null); Console.WriteLine("  3D LoadAll first call: " + t1.ElapsedMilliseconds + " ms");
            t1.Restart(); la.Invoke(g3, null); Console.WriteLine("  3D LoadAll second call: " + t1.ElapsedMilliseconds + " ms");
            var gpc = (Control)g3; t1.Restart(); foreach (Control c in Descend(gpc)) { var h = c.Handle; } Console.WriteLine("  3D handles: " + t1.ElapsedMilliseconds + " ms");
            t1.Restart(); for (int k = 0; k < 10; k++) AudioDevices.List(EDataFlow.Render); Console.WriteLine("  render device list: " + (t1.ElapsedMilliseconds / 10.0) + " ms each");
            t1.Restart(); for (int k = 0; k < 10; k++) core.GameSinkCandidates(); Console.WriteLine("  GameSinkCandidates: " + (t1.ElapsedMilliseconds / 10.0) + " ms each");
            int idx3 = pages.IndexOf(gpc);
            var cont = typeof(MainForm).GetField("content", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
            t1.Restart(); for (int i = 0; i < pages.Count; i++) ((Control)pages[i]).Visible = i == idx3; Console.WriteLine("  3D Visible=true: " + t1.ElapsedMilliseconds + " ms");
            t1.Restart(); cont.GetType().GetProperty("Content").SetValue(cont, gpc, null); Console.WriteLine("  3D content set: " + t1.ElapsedMilliseconds + " ms");
            show.Invoke(main, new object[] { 0 }); Application.DoEvents();
            t1.Restart(); show.Invoke(main, new object[] { idx3 }); Console.WriteLine("  3D ShowPage call: " + t1.ElapsedMilliseconds + " ms");
            t1.Restart(); Application.DoEvents(); Console.WriteLine("  3D first DoEvents (paint): " + t1.ElapsedMilliseconds + " ms");
            t1.Restart(); gpc.Refresh(); Console.WriteLine("  3D refresh again: " + t1.ElapsedMilliseconds + " ms");
            show.Invoke(main, new object[] { 0 }); Application.DoEvents();
        }
        var clock = Stopwatch.StartNew();
        double last = 0; var gaps = new List<double>(); string where = "";
        var worst = new List<KeyValuePair<double, string>>();
        var probe = new Timer { Interval = 1 };
        probe.Tick += (s, e) => { double now = clock.Elapsed.TotalMilliseconds; if (last > 0) { double g = now - last; gaps.Add(g); if (g > 25) worst.Add(new KeyValuePair<double, string>(g, where)); } last = now; };
        probe.Start();
        for (int round = 0; round < 2; round++)
            for (int i = 0; i < pages.Count; i++)
            {
                var name = ((Control)pages[i]).Controls.OfType<Label>().Select(l => l.Text).FirstOrDefault() ?? ("page " + i);
                where = name;
                var sw = Stopwatch.StartNew();
                show.Invoke(main, new object[] { i });
                Application.DoEvents();
                if (round == 0) Console.WriteLine("  switch to " + name + ": " + sw.ElapsedMilliseconds + " ms");
                var until = clock.Elapsed.TotalMilliseconds + (round == 0 ? 4000 : 1000);
                while (clock.Elapsed.TotalMilliseconds < until) { Application.DoEvents(); System.Threading.Thread.Sleep(1); }
            }
        probe.Stop();
        // The periodic refreshes, timed one by one.
        Action<string, object, string> time = (label, target, method) =>
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { Console.WriteLine("  (no " + method + ")"); return; }
            var t = Stopwatch.StartNew(); int n = 0;
            while (t.ElapsedMilliseconds < 400) { m.Invoke(target, null); n++; }
            Console.WriteLine("  " + label + ": " + (t.Elapsed.TotalMilliseconds / n).ToString("0.00") + " ms each");
        };
        var health = typeof(MainForm).GetField("healthPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
        time("Health refresh", health, "Refresh2");
        time("ShowTier", main, "ShowTier");
        time("TickOuts", main, "TickOuts");
        time("TickMics", main, "TickMics");
        time("RefreshVoiceTest", main, "RefreshVoiceTest");
        time("ShowVoiceIsolation", main, "ShowVoiceIsolation");
        time("RefreshMicInfo", main, "RefreshMicInfo");
        time("RefreshAutoValues", main, "RefreshAutoValues");
        time("UiTick", main, "UiTick");
        var gp = typeof(MainForm).GetField("gamePage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
        time("3D page LoadAll", gp, "LoadAll");
        time("3D page Tick", gp, "Tick");
        foreach (var pg in pages.Cast<Control>()) { show.Invoke(main, new object[] { pages.IndexOf(pg) }); Application.DoEvents(); var t = Stopwatch.StartNew(); for (int k = 0; k < 5; k++) pg.Refresh(); Console.WriteLine("  paint " + (pg.Controls.OfType<Label>().Select(l => l.Text).FirstOrDefault()) + ": " + (t.Elapsed.TotalMilliseconds / 5).ToString("0.0") + " ms"); }
        { var radar = (Control)gp.GetType().GetField("radar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(gp); show.Invoke(main, new object[] { pages.IndexOf(gp) }); Application.DoEvents(); var t = Stopwatch.StartNew(); for (int k = 0; k < 10; k++) radar.Refresh(); Console.WriteLine("  paint the 3D sphere radar: " + (t.Elapsed.TotalMilliseconds / 10).ToString("0.0") + " ms"); }
        gaps.Sort();
        Console.WriteLine("  UI thread: " + gaps.Count + " ticks, median " + gaps[gaps.Count / 2].ToString("0.0") + " ms, 99% under " + gaps[(int)(gaps.Count * 0.99)].ToString("0.0") + " ms, worst " + gaps[gaps.Count - 1].ToString("0") + " ms");
        foreach (var g in worst.OrderByDescending(w => w.Key).Take(12)) Console.WriteLine("    stall " + g.Key.ToString("0") + " ms on " + g.Value);
        main.Hide(); Environment.Exit(0);
    }
}
