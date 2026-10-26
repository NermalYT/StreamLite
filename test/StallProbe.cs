using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

// When does an app's sound stall, and does it line up with StreamLite's 4-second device check? Captures one process
// (per-app capture, its own tree only) and notes every wait over 15 ms between packets, and when StreamLite rewrote
// status.txt (once per check). Usage: StallProbe <pid> [seconds]
class StallProbe
{
    static void Main(string[] args)
    {
        int pid = int.Parse(args[0]);
        int secs = args.Length > 1 ? int.Parse(args[1]) : 20;
        var client = ProcessLoopback.Open(pid);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        int hr = client.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("init failed 0x" + hr.ToString("X8")); return; }
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; client.GetService(ref iid, out svc);
        var cap = (IAudioCaptureClient)svc;
        client.Start();
        string status = Path.Combine(AppSettings.Folder, "status.txt");
        var ticks = new List<DateTime>(); DateTime lastW = File.GetLastWriteTimeUtc(status);
        var stalls = new List<KeyValuePair<DateTime, double>>();
        var sw = Stopwatch.StartNew(); double last = -1; long frames = 0;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(2);
            var w = File.GetLastWriteTimeUtc(status); if (w != lastW) { lastW = w; ticks.Add(w); }
            uint p; if (cap.GetNextPacketSize(out p) != 0) break;
            while (p > 0)
            {
                IntPtr d; uint fr, fl; ulong a, b;
                if (cap.GetBuffer(out d, out fr, out fl, out a, out b) != 0) break;
                double now = sw.Elapsed.TotalMilliseconds;
                if (last >= 0 && now - last > 15 && now > 1000) stalls.Add(new KeyValuePair<DateTime, double>(DateTime.UtcNow, now - last));
                last = now; frames += fr;
                cap.ReleaseBuffer(fr);
                if (cap.GetNextPacketSize(out p) != 0) break;
            }
        }
        client.Stop();
        Console.WriteLine((frames / 48000.0).ToString("0.0") + " s of sound in " + secs + " s, " + stalls.Count + " stalls over 15 ms");
        foreach (var s in stalls)
        {
            // Nearest status rewrite before the stall ended (the stall started 'length' ms before that).
            var start = s.Key.AddMilliseconds(-s.Value);
            var prev = ticks.Where(t => t <= s.Key.AddMilliseconds(200)).Select(t => (start - t).TotalMilliseconds).Where(x => x > -300).DefaultIfEmpty(double.NaN).Min(x => Math.Abs(x));
            Console.WriteLine("  stall " + s.Value.ToString("0") + " ms at " + start.ToString("HH:mm:ss.fff") + (double.IsNaN(prev) ? "" : ", " + prev.ToString("0") + " ms from a status rewrite"));
        }
        Console.WriteLine("status rewrites: " + string.Join(" ", ticks.Select(t => t.ToString("ss.fff"))));
    }
}
