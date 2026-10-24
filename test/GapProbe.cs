using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Records the headphones (loopback) while a game-like noise stream starts, plays 3 s and stops in "StreamLite - Out",
// then looks for holes inside the sound: runs of 1 ms or more that are near-silent between the first and last loud
// millisecond. Usage: GapProbe [runs]
class GapProbe
{
    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static void Main(string[] args)
    {
        int runs = args.Length > 0 ? int.Parse(args[0]) : 3;
        var outs = AudioDevices.List(EDataFlow.Render);
        string sink = outs.First(d => d.Name.StartsWith("StreamLite - Out")).Id;
        string phones = outs.First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0).Id;
        for (int run = 0; run < runs; run++)
        {
            List<float> rec = null; int rate = 0;
            var th = new Thread(() => rec = Record(phones, 4.5, out rate));
            th.Start(); Thread.Sleep(300);
            Play(sink, 3.0);
            th.Join();
            // 1 ms energy envelope
            int ms = rate / 1000; int nMs = rec.Count / ms;
            var env = new double[nMs];
            for (int m = 0; m < nMs; m++) { double e = 0; for (int i = 0; i < ms; i++) { float v = rec[m * ms + i]; e += v * v; } env[m] = 10 * Math.Log10(e / ms + 1e-20); }
            int first = Array.FindIndex(env, v => v > -60), last = Array.FindLastIndex(env, v => v > -60);
            int holes = 0, longest = 0, cur = 0;
            for (int m = first; m <= last && first >= 0; m++)
            {
                if (env[m] < -80) { cur++; if (cur == 1) holes++; longest = Math.Max(longest, cur); } else cur = 0;
            }
            Console.WriteLine("run " + (run + 1) + ": sound " + ((last - first) / 1000.0).ToString("0.00") + " s, holes inside the sound: " + holes + " (longest " + longest + " ms)");
            Thread.Sleep(1500);
        }
    }

    static void Play(string id, double secs)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
        if (ac.Initialize(0, unchecked((int)0x80000000), 2000000, 0, fmt, IntPtr.Zero) != 0) throw new Exception("play init");
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc; var rnd = new Random(3); var tmp = new float[size * 8];
        ac.Start();
        long total = (long)(secs * 48000), done = 0;
        while (done < total)
        {
            Thread.Sleep(5);
            uint pad; ac.GetCurrentPadding(out pad);
            int n = (int)Math.Min(size - pad, total - done); if (n <= 0) continue;
            IntPtr data; rc.GetBuffer((uint)n, out data);
            for (int i = 0; i < n * 8; i++) tmp[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05);
            Marshal.Copy(tmp, 0, data, n * 8); rc.ReleaseBuffer((uint)n, 0); done += n;
        }
        Thread.Sleep(300);
        ac.Stop();
    }

    static List<float> Record(string id, double secs, out int rate)
    {
        var ac = Open(id);
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2); rate = Marshal.ReadInt32(fmt, 4);
        ac.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero);
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc; var outp = new List<float>(); var buf = new float[rate * ch];
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(5);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cap.GetBuffer(out data, out frames, out fl, out a, out b);
                if ((fl & 2) != 0 || frames * ch > buf.Length) for (int i = 0; i < frames; i++) outp.Add(0);
                else { Marshal.Copy(data, buf, 0, (int)frames * ch); for (int i = 0; i < frames; i++) outp.Add(buf[i * ch]); }
                cap.ReleaseBuffer(frames); cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        return outp;
    }
}
