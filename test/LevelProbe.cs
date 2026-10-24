using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Captures several endpoints at once (shared mode, native mix format) and prints level stats per channel.
class LevelProbe
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 6;
        var threads = new System.Collections.Generic.List<Thread>();
        foreach (var d in AudioDevices.List(EDataFlow.Capture))
        {
            var dev = d;
            var t = new Thread(() => Probe(dev, secs));
            t.Start();
            threads.Add(t);
        }
        // Also report the render mix format of virtual cables.
        foreach (var d in AudioDevices.List(EDataFlow.Render)) Console.WriteLine("render " + d.Name + ": " + MixFormat(d.Id));
        foreach (var t in threads) t.Join();
    }

    static string MixFormat(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        IntPtr p; ((IAudioClient)o).GetMixFormat(out p);
        var wf = (WaveFormatEx)Marshal.PtrToStructure(p, typeof(WaveFormatEx));
        uint mask = wf.cbSize >= 22 ? (uint)Marshal.ReadInt32(p, 20) : 0;
        Marshal.FreeCoTaskMem(p);
        return wf.nChannels + "ch " + wf.nSamplesPerSec + "Hz " + wf.wBitsPerSample + "bit tag=" + wf.wFormatTag + " mask=0x" + mask.ToString("X");
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
            if (hr != 0) { Console.WriteLine(d.Name + ": init failed " + hr.ToString("X")); return; }
            Guid ci = CoreAudioConst.IID_IAudioCaptureClient; object svc; ac.GetService(ref ci, out svc);
            var cap = (IAudioCaptureClient)svc;
            int ch = wf.nChannels;
            var peak = new double[ch]; var sum = new double[ch]; long n = 0;
            ac.Start();
            var end = DateTime.Now.AddSeconds(secs);
            var buf = new float[48000 * 8];
            while (DateTime.Now < end)
            {
                Thread.Sleep(10);
                uint pk; cap.GetNextPacketSize(out pk);
                while (pk > 0)
                {
                    IntPtr data; uint frames, fl; ulong a, b;
                    cap.GetBuffer(out data, out frames, out fl, out a, out b);
                    int count = (int)frames * ch;
                    if (wf.wBitsPerSample == 32 && count <= buf.Length)
                    {
                        Marshal.Copy(data, buf, 0, count);
                        for (int i = 0; i < count; i++) { int c = i % ch; double v = Math.Abs(buf[i]); if (v > peak[c]) peak[c] = v; sum[c] += v * v; }
                        n += frames;
                    }
                    cap.ReleaseBuffer(frames);
                    cap.GetNextPacketSize(out pk);
                }
            }
            ac.Stop();
            var sb = new System.Text.StringBuilder(d.Name + " [" + ch + "ch " + wf.nSamplesPerSec + "Hz]: ");
            for (int c = 0; c < ch && c < 4; c++)
                sb.Append("ch" + c + " peak " + Db(peak[c]) + " rms " + Db(Math.Sqrt(sum[c] / Math.Max(1, n))) + "  ");
            Console.WriteLine(sb.ToString());
        }
        catch (Exception ex) { Console.WriteLine(d.Name + ": " + ex.Message); }
    }

    static string Db(double v) { return (20 * Math.Log10(Math.Max(v, 1e-7))).ToString("0.0") + "dB"; }
}
