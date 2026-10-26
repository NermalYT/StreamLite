using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// How long Thread.Sleep(2) really takes in a process without a visible window (StreamLite in the tray): with
// timeBeginPeriod(1) alone, and after opting out of Windows 11's timer-resolution throttling. Usage: TimerProbe
class TimerProbe
{
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [StructLayout(LayoutKind.Sequential)] struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, int size);

    static void Measure(string what)
    {
        var sw = new Stopwatch(); double sum = 0, max = 0; int n = 200;
        for (int i = 0; i < n; i++) { sw.Restart(); Thread.Sleep(2); double ms = sw.Elapsed.TotalMilliseconds; sum += ms; max = Math.Max(max, ms); }
        Console.WriteLine(what + ": Sleep(2) takes " + (sum / n).ToString("0.0") + " ms on average, longest " + max.ToString("0.0") + " ms");
    }

    static void Main()
    {
        Measure("default");
        timeBeginPeriod(1);
        Measure("timeBeginPeriod(1)");
        // ProcessPowerThrottling = 4; PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 4: control it, state 0 = honour requests.
        var p = new PowerThrottling { Version = 1, ControlMask = 4, StateMask = 0 };
        bool ok = SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref p, Marshal.SizeOf(p));
        Measure("+ always honour timer resolution (" + ok + ")");
    }
}
