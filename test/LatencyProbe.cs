using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Measures the extra delay of the 3D audio path: clicks played into the game device (7.1, center channel) vs. the
// same clicks arriving in the headphone mix. Both sides are loopback-captured and compared by QPC timestamps.
class LatencyProbe
{
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long f);

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static volatile bool stop;

    static List<double> Capture(string id, int ch)
    {
        var onsets = new List<double>();
        var c = Open(id);
        IntPtr f = AudioDevices.MakeFormat(ch, ch == 8 ? 0x63F : 3, 48000, true, 32);
        int hr = c.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, f, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("loopback init 0x" + hr.ToString("X8")); return onsets; }
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; c.GetService(ref iid, out svc);
        var cc = (IAudioCaptureClient)svc; c.Start();
        var buf = new float[48000 * ch]; long qf; QueryPerformanceFrequency(out qf);
        double lastOn = 0; float env = 0; float peak = 0; long frames = 0;
        while (!stop)
        {
            Thread.Sleep(2);
            uint pk; cc.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint fr, fl; ulong pos, qpc; cc.GetBuffer(out data, out fr, out fl, out pos, out qpc);
                if ((fl & 2) == 0) Marshal.Copy(data, buf, 0, (int)fr * ch); else Array.Clear(buf, 0, (int)fr * ch);
                for (int i = 0; i < fr; i++)
                {
                    float a = 0; for (int k = 0; k < ch; k++) a = Math.Max(a, Math.Abs(buf[i * ch + k]));
                    double t = qpc / 1e7 + i / 48000.0;      // qpc is in 100 ns units
                    if (a > 0.04f && a > 8 * env && t - lastOn > 0.2) { onsets.Add(t); lastOn = t; }
                    env = 0.998f * env + 0.002f * a; peak = Math.Max(peak, a); frames++;
                }
                cc.ReleaseBuffer(fr); cc.GetNextPacketSize(out pk);
            }
        }
        c.Stop();
        Console.WriteLine(id.Substring(id.Length - 12) + " peak " + peak.ToString("0.000") + " frames " + frames);
        return onsets;
    }

    static void Main()
    {
        string sink = null, phones = null;
        foreach (var d in AudioDevices.List(EDataFlow.Render))
        {
            if (d.Name.Contains("Hi-Fi Cable")) sink = d.Id;
            if (d.Name.Contains("QCC")) phones = d.Id;
        }
        List<double> a = null, b = null;
        var t1 = new Thread(() => a = Capture(sink, 8)); var t2 = new Thread(() => b = Capture(phones, 2));
        t1.Start(); t2.Start();
        Thread.Sleep(300);
        // Play clicks into the game device's center channel.
        var c = Open(sink);
        IntPtr f = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
        c.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 500000, 0, f, IntPtr.Zero);
        uint bufFrames; c.GetBufferSize(out bufFrames);
        object svc; Guid iid = CoreAudioConst.IID_IAudioRenderClient; c.GetService(ref iid, out svc);
        var rc = (IAudioRenderClient)svc; c.Start();
        long n = 0; var tmp = new float[bufFrames * 8];
        var end = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < end)
        {
            uint pad; c.GetCurrentPadding(out pad); uint av = bufFrames - pad;
            if (av > 0)
            {
                IntPtr data; rc.GetBuffer(av, out data);
                Array.Clear(tmp, 0, (int)av * 8);
                for (int i = 0; i < av; i++) { long k = (n + i) % 24000; if (k < 96) tmp[i * 8 + 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 1500 * k / 48000.0)); }
                n += av; Marshal.Copy(tmp, 0, data, (int)av * 8); rc.ReleaseBuffer(av, 0);
            }
            Thread.Sleep(5);
        }
        c.Stop();
        Thread.Sleep(500); stop = true; t1.Join(); t2.Join();
        Console.WriteLine("clicks seen: game device " + a.Count + ", headphones " + b.Count);
        var d2 = new List<double>();
        foreach (var x in a) { double best = double.MaxValue; foreach (var y in b) if (y >= x && y - x < best) best = y - x; if (best < 0.5) d2.Add(best * 1000); }
        d2.Sort();
        if (d2.Count > 0) Console.WriteLine("added delay ms: min " + d2[0].ToString("0.0") + ", median " + d2[d2.Count / 2].ToString("0.0") + ", max " + d2[d2.Count - 1].ToString("0.0"));
    }
}
