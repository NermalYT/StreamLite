using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Health check of the virtual mic: endpoint names/states, whether the mic feed is held exclusively (so screen-share
// capture can't hear it), and whether audio frames actually arrive on "StreamLite - In" while the raw mic is live.
class FeedProbe
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 4;
        var outs = AudioDevices.List(EDataFlow.Render);
        var ins = AudioDevices.List(EDataFlow.Capture);
        Console.WriteLine("Outputs: " + string.Join(" | ", outs.Select(d => d.Name)));
        Console.WriteLine("Inputs:  " + string.Join(" | ", ins.Select(d => d.Name)));
        var feed = outs.FirstOrDefault(d => d.Name.StartsWith("StreamLite - Mic Feed"));
        var vin = ins.FirstOrDefault(d => d.Name.StartsWith("StreamLite - In"));
        var yeti = ins.FirstOrDefault(d => d.Name.IndexOf("Yeti", StringComparison.OrdinalIgnoreCase) >= 0);
        if (feed != null)
        {
            int hr = TryShared(feed.Id);
            Console.WriteLine("Mic feed shared open: 0x" + hr.ToString("X8") + (hr == unchecked((int)0x8889000A) ? "  (held exclusively by StreamLite - private)" : hr == 0 ? "  (NOT exclusive - screen share could hear it)" : ""));
        }
        else Console.WriteLine("Mic feed: not found");
        var t1 = vin != null ? Start(vin, secs) : null;
        var t2 = yeti != null ? Start(yeti, secs) : null;
        if (t1 != null) t1.Join();
        if (t2 != null) t2.Join();
    }

    static int TryShared(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int hr = ac.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero);
        Marshal.FreeCoTaskMem(fmt);
        Marshal.ReleaseComObject(o);
        return hr;
    }

    static Thread Start(AudioDeviceInfo d, int secs)
    {
        var t = new Thread(() => Probe(d, secs));
        t.Start();
        return t;
    }

    static void Probe(AudioDeviceInfo d, int secs)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev; en.GetDevice(d.Id, out dev);
            object o; Guid iid = CoreAudioConst.IID_IAudioClient;
            dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            var ac = (IAudioClient)o;
            IntPtr fmt; ac.GetMixFormat(out fmt);
            var wf = (WaveFormatEx)Marshal.PtrToStructure(fmt, typeof(WaveFormatEx));
            int hr = ac.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero);
            if (hr != 0) { Console.WriteLine(d.Name + ": open failed 0x" + hr.ToString("X8")); return; }
            Guid ci = CoreAudioConst.IID_IAudioCaptureClient; object svc; ac.GetService(ref ci, out svc);
            var cap = (IAudioCaptureClient)svc;
            int ch = wf.nChannels;
            double peak = 0, sum = 0; long n = 0, silentFlags = 0;
            var buf = new float[48000 * 8];
            ac.Start();
            var end = DateTime.Now.AddSeconds(secs);
            while (DateTime.Now < end)
            {
                Thread.Sleep(10);
                uint pk; cap.GetNextPacketSize(out pk);
                while (pk > 0)
                {
                    IntPtr data; uint frames, fl; ulong a, b;
                    cap.GetBuffer(out data, out frames, out fl, out a, out b);
                    int count = (int)frames * ch;
                    if ((fl & 2) != 0) silentFlags += frames;
                    else if (wf.wBitsPerSample == 32 && count <= buf.Length)
                    {
                        Marshal.Copy(data, buf, 0, count);
                        for (int i = 0; i < count; i += ch) { double v = Math.Abs(buf[i]); if (v > peak) peak = v; sum += v * v; }
                    }
                    n += frames;
                    cap.ReleaseBuffer(frames);
                    cap.GetNextPacketSize(out pk);
                }
            }
            ac.Stop();
            Console.WriteLine(d.Name + ": " + n + " frames (" + (n / (double)wf.nSamplesPerSec).ToString("0.0") + " s of " + secs + " s), silent-flagged " + silentFlags +
                ", peak " + Db(peak) + ", rms " + Db(Math.Sqrt(sum / Math.Max(1, n - silentFlags))));
        }
        catch (Exception ex) { Console.WriteLine(d.Name + ": " + ex.Message); }
    }

    static string Db(double v) { return (20 * Math.Log10(Math.Max(v, 1e-7))).ToString("0.0") + " dB"; }
}
