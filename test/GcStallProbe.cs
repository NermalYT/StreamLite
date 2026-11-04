// How long does the app's own work freeze an audio thread? A Pro Audio thread wakes every 3 ms (as Direct to headphones does) while
// the real Personalize 3D scene is drawn off screen at the monitor's rate - WPF's drawing calls, no window, no sound, no devices,
// settings in a temp folder - with the CPUs free or busy. A wake-up over 6 ms would have run the headphones out of sound.
// The busy CPUs are another process (as a VM is): busy threads inside this one would only measure how long a collection takes to stop them.
// Not part of all.ps1: it loads the CPUs for a minute or two. Usage: run.ps1 GcStallProbe [below|normal|above]: only the hard phases, with the low-priority work at that level (above: the whole process above normal)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;
using M = System.Windows.Media;

class GcStallProbe
{
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetWaitableTimer(IntPtr t, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32")] static extern UIntPtr SetThreadAffinityMask(IntPtr t, UIntPtr mask);

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const ulong Ccd0 = 0x0000FFFF, Ccd1 = 0xFFFF0000;
    static Personal3DView view;
    static object Get(string name) { return typeof(Personal3DView).GetField(name, Any).GetValue(view); }
    static object Call(string name, params object[] args) { return typeof(Personal3DView).GetMethod(name, Any).Invoke(view, args); }

    static volatile bool quit;
    static ulong lowMask;
    static int fps = 480;

    static void Stamp(string label, Action<Func<bool>> work, int seconds, ulong workMask, ulong spin)
    {
        Process spinner = null;
        if (spin != 0) { spinner = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "spin " + spin.ToString("X") + " " + (seconds + 2)) { UseShellExecute = false, CreateNoWindow = true }); Thread.Sleep(500); }
        var t = new double[seconds * 500];
        int n = 0; bool done = false;
        var audio = new Thread(() =>
        {
            int idx = 0; StreamLite.Audio.AudioEngine.ProAudio();
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003);
            long last = Stopwatch.GetTimestamp(), end = last + seconds * Stopwatch.Frequency;
            while (last < end && n < t.Length)
            {
                long due = -30000;
                SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                WaitForSingleObject(timer, 1000);
                long now = Stopwatch.GetTimestamp();
                t[n++] = (now - last) * 1000.0 / Stopwatch.Frequency;
                last = now;
            }
            done = true;
        }) { IsBackground = true, Priority = ThreadPriority.Highest };
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var q0 = Process.GetProcessesByName("qemu-system-x86_64").Sum(p => p.TotalProcessorTime.TotalSeconds);
        audio.Start();
        if (workMask != 0) SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(workMask));
        else SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(0xFFFFFFFF));
        work(() => done);
        audio.Join();
        if (spinner != null) { spinner.WaitForExit(5000); spinner.Dispose(); }
        var q1 = Process.GetProcessesByName("qemu-system-x86_64").Sum(p => p.TotalProcessorTime.TotalSeconds);
        var s = new double[n]; Array.Copy(t, s, n); Array.Sort(s);
        Console.WriteLine("{0,-44} {1,5} steps, median {2:0.0}, 99.9% {3,5:0.0}, longest {4,5:0.0} ms; over 6 ms: {5,4}, 10: {6,4}, 20: {7,3}; GCs {8}/{9}/{10}; VMs {11:0.0} cores",
            label, n, s[n / 2], s[(int)(n * 0.999)], s[n - 1], s.Count(x => x > 6), s.Count(x => x > 10), s.Count(x => x > 20),
            GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2, (q1 - q0) / seconds);
    }

    /// <summary>Another process: Normal-priority threads busy on the given CPUs for a while.</summary>
    static void Spin(ulong mask, int seconds)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
        var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        var ts = new List<Thread>();
        for (int c = 0; c < 32; c++)
        {
            if ((mask & (1UL << c)) == 0) continue;
            int cpu = c;
            var th = new Thread(() => { SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << cpu)); while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(1000); });
            th.Start(); ts.Add(th);
        }
        foreach (var th in ts) th.Join();
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "spin") { Spin(Convert.ToUInt64(args[1], 16), int.Parse(args[2])); return; }
        string opt = args.Length > 0 ? args[0] : "";
        if (opt == "above") Program.RaisePriority();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-gcstall");
        Directory.CreateDirectory(data);
        foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);

        var core = new AppCore(true);
        var snap = new Bitmap(1280, 860); var desk = new Bitmap(1600, 1000);
        view = new Personal3DView(core, snap, desk, new Rectangle(160, 70, 1280, 860), new Rectangle(0, 0, 1600, 1000), new PointF(760, 470), 200, 0, 30, null, null) { NoMouse = true, NoAudio = true };
        view.Bounds = new Rectangle(0, 0, 1600, 1000);
        typeof(Personal3DView).GetField("phones", Any).SetValue(view, "test");
        Call("ShowButtons", "Start", "Back to StreamLite");
        Call("Key", Keys.Enter);
        var t0 = DateTime.Now;
        while ((int)Get("loaded") < (int)Get("toLoad") && (DateTime.Now - t0).TotalSeconds < 120) Thread.Sleep(50);
        Call("BeginTrial");
        Console.WriteLine("Scene: " + Get("stage") + ", drawn at " + fps + " fps. Heap " + (GC.GetTotalMemory(false) >> 20) + " MB, " + Environment.ProcessorCount + " CPUs");

        var canvas = new WpfCanvas(1.0, WpfCanvas.FromBitmap(snap), WpfCanvas.FromBitmap(desk));
        var size = new Size(1600, 1000);
        Action<Func<bool>> draw = over =>
        {
            long frame = Stopwatch.Frequency / fps, next = Stopwatch.GetTimestamp();
            int frames = 0;
            while (!over())
            {
                var dv = new M.DrawingVisual();
                using (var dc = dv.RenderOpen()) { canvas.Begin(dc); view.Render(canvas, size); }
                frames++;
                next += frame;
                while (Stopwatch.GetTimestamp() < next && !over()) Thread.Sleep(0);
            }
            drawn = frames;
        };
        Action<Func<bool>> idle = over => { while (!over()) Thread.Sleep(20); };

        // Low-priority managed work, like the app's gesture, voice-lock and app-scan threads: a collection has to wait for these to stop.
        for (int w = 0; w < 4; w++)
            new Thread(() =>
            {
                var a = new float[4096]; float x = 1;
                while (!quit)
                {
                    if (lowMask == 0) { Thread.Sleep(20); continue; }
                    SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(lowMask));
                    for (int i = 0; i < a.Length; i++) { x = x * 0.999f + a[i] * 0.001f + 1e-3f; a[i] = x; }
                }
            }) { IsBackground = true, Priority = opt == "normal" ? ThreadPriority.Normal : ThreadPriority.BelowNormal }.Start();

        if (opt != "")
        {
            Console.WriteLine("Low-priority work: " + (opt == "normal" ? "Normal" : "BelowNormal") + ", process " + Process.GetCurrentProcess().PriorityClass);
            lowMask = Ccd1;
            Stamp("low-priority work on 16-31, busy", draw, 15, 0, Ccd1);
            lowMask = Ccd0 | Ccd1;
            Stamp("every CPU busy", draw, 15, 0, Ccd0 | Ccd1);
            Stamp("every CPU busy, again", draw, 15, 0, Ccd0 | Ccd1);
            quit = true; Environment.Exit(0);
        }
        Stamp("nothing drawn", idle, 8, 0, 0);
        Stamp("drawn, any CPU", draw, 12, 0, 0);
        lowMask = Ccd0 | Ccd1;
        Stamp("drawn + low-priority work", draw, 12, 0, 0);
        Stamp("same, CPUs 16-31 busy (other process)", draw, 15, 0, Ccd1);
        lowMask = Ccd1;
        Stamp("low-priority work on 16-31, busy", draw, 15, 0, Ccd1);
        Stamp("all drawing on 16-31 too, busy", draw, 15, Ccd1, Ccd1);
        lowMask = Ccd0 | Ccd1;
        Stamp("every CPU busy", draw, 12, 0, Ccd0 | Ccd1);
        lowMask = 0;
        quit = true;
        Console.WriteLine("Frames in the last drawn part: " + drawn + ". ALL DONE");
        Environment.Exit(0);
    }
    static int drawn;
}
