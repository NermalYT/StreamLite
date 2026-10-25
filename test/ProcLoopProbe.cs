using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// What Windows' per-app capture (process loopback) gives: one app's sound in 7.1 or only stereo, and whether muting
// that app's session on its device keeps it in the capture. Plays inaudible noise (-70 dBFS) through SinkPlay.
class ProcLoopProbe
{
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float v, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float v);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }
    class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Done.Set(); }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr prop, IActivateAudioInterfaceCompletionHandler h, out IActivateAudioInterfaceAsyncOperation op);

    static IAudioClient ProcessClient(int pid, int mode)
    {
        IntPtr p = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24);
        Marshal.WriteInt32(p, 0, 1); Marshal.WriteInt32(p, 4, pid); Marshal.WriteInt32(p, 8, mode);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
        Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, p);
        var h = new Handler(); IActivateAudioInterfaceAsyncOperation op;
        int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", CoreAudioConst.IID_IAudioClient, pv, h, out op);
        if (hr != 0) throw new COMException("activate", hr);
        h.Done.WaitOne(5000);
        int ahr; object o; op.GetActivateResult(out ahr, out o);
        if (ahr != 0) throw new COMException("activate result", ahr);
        return (IAudioClient)o;
    }

    static double[] Capture(IAudioClient ac, int ch, double secs)
    {
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
        int hr = ac.GetService(ref iid, out svc); if (hr != 0) throw new COMException("svc", hr);
        var cap = (IAudioCaptureClient)svc;
        ac.Start();
        var e = new double[ch]; long n = 0; var buf = new float[48000 * ch];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(5);
            uint packet; cap.GetNextPacketSize(out packet);
            while (packet > 0)
            {
                IntPtr data; uint frames, fl; ulong p1, p2;
                cap.GetBuffer(out data, out frames, out fl, out p1, out p2);
                int m = (int)Math.Min(frames, 48000);
                if ((fl & 2) == 0) Marshal.Copy(data, buf, 0, m * ch); else Array.Clear(buf, 0, m * ch);
                if (sw.Elapsed.TotalSeconds > 0.3) { for (int i = 0; i < m; i++) for (int c = 0; c < ch; c++) e[c] += buf[i * ch + c] * (double)buf[i * ch + c]; n += m; }
                cap.ReleaseBuffer(frames);
                cap.GetNextPacketSize(out packet);
            }
        }
        ac.Stop();
        return e.Select(x => n > 0 ? 10 * Math.Log10(x / n + 1e-30) : -999).ToArray();
    }

    static ISimpleAudioVolume SessionOf(string devId, int pid)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(devId, out dev);
        object o; Guid iid = typeof(IAudioSessionManager2).GUID; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        IAudioSessionEnumerator e; ((IAudioSessionManager2)o).GetSessionEnumerator(out e);
        int n; e.GetCount(out n);
        for (int i = 0; i < n; i++) { IAudioSessionControl2 s; e.GetSession(i, out s); uint p; s.GetProcessId(out p); if (p == pid) return (ISimpleAudioVolume)s; }
        return null;
    }

    // Both captures at once: the device's loopback (everything on StreamLite - Out) and this app's own capture.
    sealed class Cap { public IAudioClient Ac; public IAudioCaptureClient C; public System.Collections.Generic.List<float> S = new System.Collections.Generic.List<float>(); public long FirstQpc = -1, FirstPos = -1; }
    static long BufDur = 200000;
    static Cap Make(IAudioClient ac)
    {
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
        int hr = ac.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | (BufDur < 100000 ? CoreAudioConst.STREAMFLAGS_EVENTCALLBACK : 0), BufDur, 0, fmt, IntPtr.Zero);
        if (hr == 0 && BufDur < 100000) { var ev = new AutoResetEvent(false); ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()); evs.Add(ev); }
        if (hr != 0) throw new COMException("init", hr);
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref iid, out svc);
        return new Cap { Ac = ac, C = (IAudioCaptureClient)svc };
    }
    static System.Collections.Generic.List<AutoResetEvent> evs = new System.Collections.Generic.List<AutoResetEvent>();
    static void Pump(Cap c, float[] buf)
    {
        uint packet; c.C.GetNextPacketSize(out packet);
        while (packet > 0)
        {
            IntPtr data; uint frames, fl; ulong p1, p2;
            c.C.GetBuffer(out data, out frames, out fl, out p1, out p2);
            if (c.FirstQpc < 0) { c.FirstQpc = (long)p2; c.FirstPos = (long)p1; }
            if ((fl & 2) == 0) Marshal.Copy(data, buf, 0, (int)frames * 8); else Array.Clear(buf, 0, (int)frames * 8);
            for (int i = 0; i < frames * 8; i++) c.S.Add(buf[i]);
            c.C.ReleaseBuffer(frames);
            c.C.GetNextPacketSize(out packet);
        }
    }
    [MTAThread]
    static void Main()
    {
        if (Environment.GetEnvironmentVariable("BUFDUR") != null) BufDur = long.Parse(Environment.GetEnvironmentVariable("BUFDUR"));
        string devName = Environment.GetCommandLineArgs().Length > 1 ? Environment.GetCommandLineArgs()[1] : "StreamLite - Out";
        string sink = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith(devName)).Id;
        float vol0, vdb; AudioDevices.GetVolume(sink, out vol0, out vdb);
        if (Environment.GetCommandLineArgs().Length > 2) AudioDevices.SetVolume(sink, float.Parse(Environment.GetCommandLineArgs()[2], System.Globalization.CultureInfo.InvariantCulture));
        float v1, v1db; AudioDevices.GetVolume(sink, out v1, out v1db); Console.WriteLine("volume " + v1.ToString("0.00") + " (" + v1db.ToString("0.0") + " dB)");
        var play = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SinkPlay.exe"), "12 0.0003 \"" + devName + "\" no " + (Environment.GetCommandLineArgs().Length > 3 ? Environment.GetCommandLineArgs()[3] : "48000")) { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(1500);
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDevice(sink, out dev);
            object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            var a = Make((IAudioClient)o); var b = Make(ProcessClient(play.Id, 0));
            var buf = new float[48000 * 8];
            a.Ac.Start(); b.Ac.Start();
            var sw = Stopwatch.StartNew();
            var diffs = new System.Collections.Generic.List<long>();
            while (sw.Elapsed.TotalSeconds < 3)
            {
                Thread.Sleep(1); int da0 = a.S.Count; Pump(a, buf); bool devNew = a.S.Count > da0; Pump(b, buf);
                if (devNew && sw.Elapsed.TotalSeconds > 0.5) diffs.Add(b.S.Count / 8 - a.S.Count / 8);   // app frames - device frames, right after a device packet
            }
            Console.WriteLine("device frames " + a.S.Count / 8 + " first qpc " + a.FirstQpc + " pos " + a.FirstPos + " | app frames " + b.S.Count / 8 + " first qpc " + b.FirstQpc + " pos " + b.FirstPos);
            double dq = (b.FirstQpc - a.FirstQpc) * 48000.0 / 1e7;
            Console.WriteLine("qpc offset (app - device) " + dq.ToString("0.00") + " frames");
            // Best integer lag by residual energy (channel 0), over the middle 1.5 s.
            int na = a.S.Count / 8, nb = b.S.Count / 8; double best = double.MaxValue; int bestLag = 0; double sig = 0;
            for (int lag = -2000; lag <= 2000; lag++)
            {
                double e = 0; int cnt = 0;
                for (int i = 24000; i < 96000 && i < na; i++) { int j = i - lag; if (j < 0 || j >= nb) { cnt = -1; break; } double d = a.S[i * 8] - b.S[j * 8]; e += d * d; cnt++; }
                if (cnt > 0 && e < best) { best = e; bestLag = lag; }
            }
            for (int i = 24000; i < 96000 && i < na; i++) sig += a.S[i * 8] * (double)a.S[i * 8];
            double sda = 0, saa = 0; for (int i = 24000; i < 96000 && i < na; i++) { int j = i - bestLag; if (j >= 0 && j < nb) { sda += a.S[i * 8] * (double)b.S[j * 8]; saa += b.S[j * 8] * (double)b.S[j * 8]; } }
            Console.WriteLine("gain device/app " + (sda / saa).ToString("0.0000") + " = " + (20 * Math.Log10(sda / saa)).ToString("0.0") + " dB");
            var hist = diffs.GroupBy(x => x).OrderBy(g => g.Key).Select(g => (g.Key + bestLag) + ":" + g.Count());
            Console.WriteLine("app frames ahead of what the device packet needs (frames:count): " + string.Join("  ", hist));
            Console.WriteLine("best lag " + bestLag + " frames, residual " + (10 * Math.Log10(best / sig + 1e-30)).ToString("0.0") + " dB below the signal");
        }
        finally { try { play.Kill(); } catch { } AudioDevices.SetVolume(sink, vol0); }
    }
}