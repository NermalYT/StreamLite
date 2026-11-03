using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// How fast each recording device's clock really runs: counts the frames a shared-mode capture of it delivers against
// the PC's own clock (only counted - nothing is kept). For the mic and "StreamLite - In" (VB-CABLE's clock).
// Usage: run.ps1 ClockProbe [seconds] [name filter...]
class ClockProbe
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;
        var filters = args.Skip(1).ToArray();
        var devs = AudioDevices.List(EDataFlow.Capture).Where(d => filters.Length == 0 || filters.Any(f => d.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        var threads = devs.Select(d => new Thread(() => Measure(d, secs)) { IsBackground = true }).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
    }

    static void Measure(AudioDeviceInfo d, int secs)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev; en.GetDevice(d.Id, out dev);
            object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            var ac = (IAudioClient)o;
            IntPtr fmt; ac.GetMixFormat(out fmt);
            int rate = Marshal.ReadInt32(fmt, 4);
            ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero);
            object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
            var cc = (IAudioCaptureClient)svc;
            ac.Start();
            long frames = 0; double t0 = -1, tEnd = 0; long f0 = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < secs + 2)
            {
                Thread.Sleep(5);
                uint p; cc.GetNextPacketSize(out p);
                while (p > 0)
                {
                    IntPtr data; uint n, fl; ulong pos, qpc;
                    cc.GetBuffer(out data, out n, out fl, out pos, out qpc);
                    cc.ReleaseBuffer(n);
                    double now = qpc / 1e7;   // the packet's capture time (100 ns units)
                    if (sw.Elapsed.TotalSeconds > 2 && t0 < 0) { t0 = now; f0 = frames; }
                    frames += n; tEnd = now;
                    cc.GetNextPacketSize(out p);
                }
            }
            ac.Stop();
            double span = tEnd - t0, got = frames - f0;
            double actual = got / span;
            Console.WriteLine(d.Name + ": " + actual.ToString("0.0") + " frames/s against " + rate + " Hz nominal = " + ((actual / rate - 1) * 1e6).ToString("+0;-0") + " ppm (" + span.ToString("0.0") + " s)");
        }
        catch (Exception ex) { Console.WriteLine(d.Name + ": " + ex.Message); }
    }
}
