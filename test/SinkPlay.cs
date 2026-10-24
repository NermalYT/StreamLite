using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Plays quiet 7.1 noise into "StreamLite - Out" like a game would (event-driven, the device's own period) for N
// seconds, so the running app's 3D path can be watched for dropouts. Usage: SinkPlay [seconds]
class SinkPlay
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 30;
        string id = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith("StreamLite - Out")).Id;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
        var ev = new AutoResetEvent(false);
        int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 0, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("init 0x" + hr.ToString("X8")); return; }
        ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        var rnd = new Random(1); var tmp = new float[size * 8];
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        long frames = 0;
        while (DateTime.Now < end)
        {
            ev.WaitOne(100);
            uint pad; ac.GetCurrentPadding(out pad);
            uint n = size - pad; if (n == 0) continue;
            IntPtr data; if (rc.GetBuffer(n, out data) != 0) continue;
            for (int i = 0; i < n * 8; i++) tmp[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.02);
            Marshal.Copy(tmp, 0, data, (int)n * 8);
            rc.ReleaseBuffer(n, 0); frames += n;
        }
        ac.Stop();
        Console.WriteLine("played " + (frames / 48000.0).ToString("0.0") + " s into " + id.Substring(0, 20) + "..., buffer " + size + " frames");
    }
}
