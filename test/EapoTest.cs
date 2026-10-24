using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Plays quiet pink noise to a render device while capturing its loopback, for several Equalizer APO configs,
// to see whether (and how) the config changes what actually reaches the device.
// Usage: EapoTest <renderDeviceId> <configFile> <label>=<config text with \n> ...
class EapoTest
{
    static volatile bool stop;

    static void Main(string[] args)
    {
        string id = args[0], cfg = args[1];
        string original = File.ReadAllText(cfg);
        var player = new Thread(() => Play(id)) { IsBackground = true };
        player.Start();
        try
        {
            for (int i = 2; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                string label = args[i].Substring(0, eq), text = args[i].Substring(eq + 1).Replace("\\n", "\r\n");
                File.WriteAllText(cfg, text + "\r\n");
                Thread.Sleep(1800);                       // Equalizer APO reloads on file change
                double[] bands = Capture(id, 1500);
                Console.WriteLine(string.Format("{0,-22} total {1,6:0.0} dB | 60Hz {2,6:0.0} | 250Hz {3,6:0.0} | 1k {4,6:0.0} | 4k {5,6:0.0} | 10k {6,6:0.0}",
                    label, bands[0], bands[1], bands[2], bands[3], bands[4], bands[5]));
            }
        }
        finally
        {
            stop = true;
            File.WriteAllText(cfg, original);
        }
    }

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static void Play(string id)
    {
        var ac = Open(id);
        var wf = new WaveFormatEx { wFormatTag = 3, nChannels = 1, nSamplesPerSec = 48000, nAvgBytesPerSec = 192000, nBlockAlign = 4, wBitsPerSample = 32 };
        IntPtr fmt = Marshal.AllocHGlobal(18); Marshal.StructureToPtr(wf, fmt, false);
        int hr = ac.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 2000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("play init " + hr.ToString("X")); return; }
        uint buf; ac.GetBufferSize(out buf);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        var rnd = new Random(1);
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        var tmp = new float[buf];
        ac.Start();
        while (!stop)
        {
            uint pad; ac.GetCurrentPadding(out pad);
            uint n = buf - pad;
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    // Paul Kellet pink noise at about -30 dBFS.
                    double w = rnd.NextDouble() * 2 - 1;
                    b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
                    b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                    double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362; b6 = w * 0.115926;
                    tmp[i] = (float)(pink * 0.011);
                }
                IntPtr p; rc.GetBuffer(n, out p); Marshal.Copy(tmp, 0, p, (int)n); rc.ReleaseBuffer(n, 0);
            }
            Thread.Sleep(10);
        }
        ac.Stop();
    }

    // Loopback capture -> overall level and a few band levels (simple Goertzel-free band energy via biquad band-passes).
    static double[] Capture(string id, int ms)
    {
        var ac = Open(id);
        IntPtr mix; ac.GetMixFormat(out mix);
        var wf = (WaveFormatEx)Marshal.PtrToStructure(mix, typeof(WaveFormatEx));
        int hr = ac.Initialize(0, 0x00020000, 2000000, 0, mix, IntPtr.Zero); // LOOPBACK
        if (hr != 0) throw new Exception("loopback init " + hr.ToString("X"));
        object svc; Guid cid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref cid, out svc);
        var cap = (IAudioCaptureClient)svc;
        int ch = wf.nChannels; double fs = wf.nSamplesPerSec;
        double[] centers = { 60, 250, 1000, 4000, 10000 };
        var bp = new double[5][]; var st = new double[5][]; var acc = new double[6];
        for (int k = 0; k < 5; k++)
        {
            double w0 = 2 * Math.PI * centers[k] / fs, alpha = Math.Sin(w0) / (2 * 4.0), a0 = 1 + alpha;
            bp[k] = new[] { alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w0) / a0, (1 - alpha) / a0 };
            st[k] = new double[2];
        }
        long n = 0;
        ac.Start();
        var end = DateTime.Now.AddMilliseconds(ms);
        var data = new float[96000 * 8];
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr p; uint frames, fl; ulong a, b;
                cap.GetBuffer(out p, out frames, out fl, out a, out b);
                int cnt = (int)frames * ch;
                if (cnt <= data.Length) Marshal.Copy(p, data, 0, cnt);
                cap.ReleaseBuffer(frames);
                for (int i = 0; i < frames; i++)
                {
                    double x = data[i * ch];
                    acc[0] += x * x;
                    for (int k = 0; k < 5; k++)
                    {
                        var c = bp[k]; var s = st[k];
                        double y = c[0] * x + s[0];
                        s[0] = c[1] * x - c[3] * y + s[1];
                        s[1] = c[2] * x - c[4] * y;
                        acc[k + 1] += y * y;
                    }
                    n++;
                }
                cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        Marshal.ReleaseComObject(cap); Marshal.ReleaseComObject(ac);
        var r = new double[6];
        for (int k = 0; k < 6; k++) r[k] = 10 * Math.Log10(acc[k] / Math.Max(1, n) + 1e-20);
        return r;
    }
}
