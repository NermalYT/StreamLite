using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

// One app's sound the way Direct to headphones' mixer takes it (Windows' per-app capture, polled every 2 ms), with a
// reader taking 3 ms steps like the headphones: when packets arrive, how big, silent or not, and how often a buffer
// with the mixer's margin runs dry. Usage: AppTapProbe <process name> [seconds] [margin frames]
class AppTapProbe
{
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr sec, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    static void Main(string[] args)
    {
        timeBeginPeriod(1);
        string name = args.Length > 0 ? args[0] : "vivaldi";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 20;
        int margin = args.Length > 2 ? int.Parse(args[2]) : 240;
        var pids = new HashSet<int>(Process.GetProcessesByName(name).Select(p => p.Id));
        var parents = ProcessLoopback.Parents();
        int root = AppMixer.Roots(pids, parents).First();
        var client = ProcessLoopback.Open(root);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        int hr = client.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("init failed 0x" + hr.ToString("X8")); return; }
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; client.GetService(ref iid, out svc);
        var cap = (IAudioCaptureClient)svc;
        var ring = new StereoRing(96); ring.SetBaseMargin(margin, 480); ring.Grace(1500);
        client.Start();
        var sw = Stopwatch.StartNew();
        bool done = false; int dry = 0; var dryAt = new List<double>();
        var reader = new Thread(() =>
        {
            IntPtr tm = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
            var o = new float[288]; double next = 3; bool wasDry = false;
            while (!done)
            {
                double left = next - sw.Elapsed.TotalMilliseconds;
                if (left > 0) { long due = -(long)(left * 10000); SetWaitableTimer(tm, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(tm, 50); }
                next += 3;
                int before = ring.Fill; ring.Read(o, 144);
                bool isDry = before < 150 && sw.ElapsedMilliseconds > 2000;
                if (isDry && !wasDry) { dry++; if (dryAt.Count < 30) dryAt.Add(sw.Elapsed.TotalMilliseconds); }
                wasDry = isDry;
            }
        }) { Priority = ThreadPriority.Highest };
        reader.Start();
        var inBuf = new float[96000]; var l = new float[48000]; var r = new float[48000];
        var gaps = new List<double>(); double last = -1; int packets = 0, silentPk = 0; long frames = 0; var sizes = new Dictionary<uint, int>();
        double worst = 0; int bursts = 0;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(2);
            uint packet; if (cap.GetNextPacketSize(out packet) != 0) break;
            int inThisWake = 0;
            while (packet > 0)
            {
                IntPtr data; uint fr, fl; ulong p1, p2;
                if (cap.GetBuffer(out data, out fr, out fl, out p1, out p2) != 0) break;
                double now = sw.Elapsed.TotalMilliseconds;
                if (last >= 0) { double g = now - last; if (now > 2000) { worst = Math.Max(worst, g); if (g > 15) gaps.Add(g); } }
                last = now; packets++; inThisWake++;
                int sz; sizes.TryGetValue(fr, out sz); sizes[fr] = sz + 1;
                bool silent = (fl & 2) != 0; if (silent) silentPk++;
                int n = (int)fr;
                if (!silent) Marshal.Copy(data, inBuf, 0, n * 2);
                for (int i = 0; i < n; i++) { l[i] = silent ? 0 : inBuf[2 * i]; r[i] = silent ? 0 : inBuf[2 * i + 1]; }
                ring.Write(l, r, n); ring.InputArrived(); frames += n;
                cap.ReleaseBuffer(fr);
                if (cap.GetNextPacketSize(out packet) != 0) break;
            }
            if (inThisWake > 1) bursts++;
        }
        done = true; reader.Join();
        client.Stop();
        Console.WriteLine(name + " (pid " + root + "): " + packets + " packets, " + silentPk + " silent, " + (frames / 48000.0).ToString("0.0") + " s of sound in " + secs + " s; sizes " + string.Join(", ", sizes.OrderByDescending(k => k.Value).Take(4).Select(k => k.Key + "x" + k.Value)));
        Console.WriteLine("  longest wait between packets " + worst.ToString("0.0") + " ms; waits over 15 ms: " + gaps.Count + (gaps.Count > 0 ? " (" + string.Join(", ", gaps.Take(12).Select(g => g.ToString("0"))) + ")" : "") + "; wakes with several packets " + bursts);
        Console.WriteLine("  buffer (margin " + (margin / 48.0).ToString("0.0") + " ms): ran dry " + dry + "x" + (dryAt.Count > 0 ? " at " + string.Join(", ", dryAt.Select(t => (t / 1000).ToString("0.0") + "s")) : "") + "; ring gaps " + ring.Underruns + ", margin now " + (ring.Margin / 48.0).ToString("0.0") + " ms");
    }
}
