using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// Memory cost of DeepFilterNet: process RAM (private / working set) and graphics memory (GPU dedicated + shared, from
// Windows' "GPU Process Memory" counters). Usage: DfnMem cpu|gpu
class DfnMem
{
    [System.Runtime.InteropServices.DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
    static string Mb(long b) { return (b / 1048576.0).ToString("0") + " MB"; }

    static void Report(string what)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        var p = Process.GetCurrentProcess(); p.Refresh();
        long ded = 0, sh = 0;
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            string tag = "pid_" + p.Id + "_";
            foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
            {
                using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
                using (var c = new PerformanceCounter("GPU Process Memory", "Shared Usage", inst, true)) sh += c.RawValue;
            }
        }
        catch (Exception ex) { Console.WriteLine("  (gpu counters: " + ex.Message + ")"); }
        Console.WriteLine(what.PadRight(36) + "private " + Mb(p.PrivateMemorySize64).PadLeft(7) + "  working set " + Mb(p.WorkingSet64).PadLeft(7) +
            "  VRAM dedicated " + Mb(ded).PadLeft(6) + "  shared " + Mb(sh).PadLeft(6));
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool gpu = args.Length > 0 && args[0] == "gpu";
        Report("start");
        var d = new DeepFilter(gpu);
        while (!d.Ready && d.Error == null) System.Threading.Thread.Sleep(10);
        if (d.Error != null) { Console.WriteLine(d.Error); return; }
        Report("model loaded (" + (gpu ? "GPU" : "CPU") + ")");
        var x = new float[480]; var y = new float[480]; var r = new Random(1);
        for (int i = 0; i < 1000; i++) { for (int k = 0; k < 480; k++) x[k] = (float)(r.NextDouble() - 0.5) * 0.05f; d.Process(x, y); d.WaitIdle(1000); }
        Report("after 10 s of audio");
        System.Threading.Thread.Sleep(1500);
        Report("idle");
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        Report("working set trimmed");
        for (int i = 0; i < 1000; i++) { for (int k = 0; k < 480; k++) x[k] = (float)(r.NextDouble() - 0.5) * 0.05f; d.Process(x, y); d.WaitIdle(1000); }
        Report("10 s more audio after trim");
        d.Dispose();
        System.Threading.Thread.Sleep(1500);
        Report("unloaded (no app recording the mic)");
        var d2 = new DeepFilter(gpu);
        while (!d2.Ready && d2.Error == null) System.Threading.Thread.Sleep(10);
        Report("loaded again");
        d2.Dispose(); System.Threading.Thread.Sleep(1500);
        Report("unloaded again");
    }
}
