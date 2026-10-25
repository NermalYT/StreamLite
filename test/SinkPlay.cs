using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Plays quiet 7.1 noise into "StreamLite - Out" like a game would (event-driven, the device's own period) for N
// seconds, so the running app's 3D path can be watched for dropouts. Usage: SinkPlay [seconds] [amplitude] [device name start]
class SinkPlay
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 30;
        double amp = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0.02;   // 0.0001 = -80 dBFS: inaudible
        string id = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith(args.Length > 2 ? args[2] : "StreamLite - Out")).Id;   // [device name start]
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        int rate = args.Length > 4 ? int.Parse(args[4]) : 48000;   // [rate]: 44100 = the engine resamples it
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, rate, true, 32);
        var ev = new AutoResetEvent(false);
        int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | (rate != 48000 ? CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY : 0), 0, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("init 0x" + hr.ToString("X8")); return; }
        ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        var rnd = new Random(1); var tmp = new float[size * 8];
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        long frames = 0;
        bool pause = args.Length > 3 && args[3] == "pause"; var t0 = DateTime.Now; bool paused = false;
        while (DateTime.Now < end)
        {
            // [pause]: the stream stops for 1 s after 2 s, like a player pausing.
            if (pause && !paused && (DateTime.Now - t0).TotalSeconds > 2) { ac.Stop(); Thread.Sleep(1000); ac.Start(); paused = true; }
            ev.WaitOne(100);
            uint pad; ac.GetCurrentPadding(out pad);
            uint n = size - pad; if (n == 0) continue;
            IntPtr data; if (rc.GetBuffer(n, out data) != 0) continue;
            for (int i = 0; i < n * 8; i++) tmp[i] = (float)((rnd.NextDouble() * 2 - 1) * amp);
            Marshal.Copy(tmp, 0, data, (int)n * 8);
            rc.ReleaseBuffer(n, 0); frames += n;
        }
        ac.Stop();
        Console.WriteLine("played " + (frames / 48000.0).ToString("0.0") + " s into " + id.Substring(0, 20) + "..., buffer " + size + " frames");
    }
}
