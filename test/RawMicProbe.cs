// The mic as apps normally get it vs its raw stream (4.0.5): 3 s each, the level of what's in the room (fans) and
// whether the raw stream was granted. Effects Windows or the mic's maker run (Blue VO!CE...) show as a quieter room.
// Usage: run.ps1 RawMicProbe [part of the mic's name]
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

class RawMicProbe
{
    static void Main(string[] args)
    {
        string want = args.Length > 0 ? args[0] : null;
        var mic = AudioDevices.List(EDataFlow.Capture).FirstOrDefault(d => want == null ? !AudioDevices.IsCable(d) : d.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
        if (mic == null) { Console.WriteLine("no such mic"); return; }
        Console.WriteLine("mic: " + mic.Name + "   raw stream offered: " + RawCapture.Supported(mic.Id));
        foreach (bool raw in new[] { false, true, false, true })
        {
            double db; bool got = Record(mic.Id, raw, out db);
            Console.WriteLine(string.Format("  {0,-8} raw granted: {1,-5}  room level {2,6:0.0} dBFS RMS", raw ? "raw" : "normal", got, db));
        }
    }

    static bool Record(string id, bool raw, out double db)
    {
        IMMDevice dev; var client = AudioEngine.OpenClient(id, out dev);
        RawCapture.Enabled = true;
        bool got = raw && RawCapture.Request(client, id);
        IntPtr fmt = AudioEngine.AllocFormat(); object svc = null;
        double sum = 0; long n = 0;
        try
        {
            int flags = CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
            int hr = client.Initialize(0, flags, 2000000, 0, fmt, IntPtr.Zero);
            if (hr != 0) throw new COMException("init", hr);
            Guid iid = CoreAudioConst.IID_IAudioCaptureClient; client.GetService(ref iid, out svc);
            var cap = (IAudioCaptureClient)svc;
            client.Start();
            var end = DateTime.Now.AddSeconds(3); Thread.Sleep(300);
            while (DateTime.Now < end)
            {
                Thread.Sleep(20);
                uint packet; cap.GetNextPacketSize(out packet);
                while (packet > 0)
                {
                    IntPtr data; uint frames; uint fl; ulong pos, qpc;
                    cap.GetBuffer(out data, out frames, out fl, out pos, out qpc);
                    var f = new float[frames]; Marshal.Copy(data, f, 0, (int)frames);
                    if ((fl & 2) == 0) foreach (var v in f) { sum += v * (double)v; n++; }
                    cap.ReleaseBuffer(frames); cap.GetNextPacketSize(out packet);
                }
            }
            client.Stop();
        }
        finally { Marshal.FreeHGlobal(fmt); if (svc != null) Marshal.ReleaseComObject(svc); Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev); }
        db = 10 * Math.Log10(sum / Math.Max(1, n) + 1e-20);
        return got;
    }
}

