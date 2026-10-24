using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Clock drift between the Yeti and the virtual mic cable: captures both (shared) for a while and compares how many
// frames each delivers per second of the PC's clock. ppm difference = how fast the mic buffer drifts.
class DriftProbe
{
    static long[] frames = new long[2];
    static double[] t0 = new double[2], t1 = new double[2];
    static long[] f0 = new long[2];

    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 120;
        var ins = AudioDevices.List(EDataFlow.Capture);
        var devs = new[] { ins.First(d => d.Name.IndexOf("Yeti", StringComparison.OrdinalIgnoreCase) >= 0), ins.First(d => d.Name.StartsWith("StreamLite - In")) };
        var sw = Stopwatch.StartNew();
        var th = devs.Select((d, i) => new Thread(() => Run(d.Id, i, secs, sw))).ToArray();
        foreach (var t in th) t.Start();
        foreach (var t in th) t.Join();
        for (int i = 0; i < 2; i++)
        {
            double rate = (frames[i] - f0[i]) / (t1[i] - t0[i]);
            Console.WriteLine(devs[i].Name + ": " + rate.ToString("0.00") + " frames/s (" + ((rate / 48000 - 1) * 1e6).ToString("+0;-0") + " ppm vs nominal)");
        }
        double r0 = (frames[0] - f0[0]) / (t1[0] - t0[0]), r1 = (frames[1] - f0[1]) / (t1[1] - t0[1]);
        double ppm = (r0 / r1 - 1) * 1e6;
        Console.WriteLine("Yeti vs cable: " + ppm.ToString("+0.0;-0.0") + " ppm -> " + Math.Abs(ppm * 48000 / 1e6).ToString("0.0") + " samples/s of drift; the mic buffer (20-60 ms band) would hit its edge every " +
            (Math.Abs(ppm) < 0.01 ? "never" : (1440 / Math.Abs(ppm * 48000 / 1e6) / 60).ToString("0.0") + " minutes"));
    }

    static void Run(string id, int idx, int secs, Stopwatch sw)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt; ac.GetMixFormat(out fmt);
        var wf = (WaveFormatEx)Marshal.PtrToStructure(fmt, typeof(WaveFormatEx));
        ac.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero);
        Guid ci = CoreAudioConst.IID_IAudioCaptureClient; object svc; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc;
        ac.Start();
        double scale = 48000.0 / wf.nSamplesPerSec;
        bool marked = false;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(5);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint n, fl; ulong a, b;
                cap.GetBuffer(out data, out n, out fl, out a, out b);
                frames[idx] += n; cap.ReleaseBuffer(n);
                cap.GetNextPacketSize(out pk);
            }
            double now = sw.Elapsed.TotalSeconds;
            if (!marked && now > 5) { marked = true; t0[idx] = now; f0[idx] = frames[idx]; }
            t1[idx] = now;
        }
        ac.Stop();
        frames[idx] = (long)(frames[idx] * scale); f0[idx] = (long)(f0[idx] * scale);
    }
}
