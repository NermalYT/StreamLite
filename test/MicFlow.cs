using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Records "StreamLite - In" for a few seconds like Discord would and reports whether StreamLite's mic signal arrives.
class MicFlow
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 4;
        var cap = AudioDevices.List(EDataFlow.Capture).First(d => d.Name.StartsWith("StreamLite - In"));
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(cap.Id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4);
        ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero);
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cc = (IAudioCaptureClient)svc;
        var buf = new float[rate * ch]; var che = new double[ch]; long zrun = 0, shortHoles = 0, longRuns = 0; long n = 0, nonzero = 0; double e = 0, pk = 0; int silentPackets = 0;
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint p; cc.GetNextPacketSize(out p);
            while (p > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cc.GetBuffer(out data, out frames, out fl, out a, out b);
                if ((fl & 2) != 0) silentPackets++;
                else { Marshal.Copy(data, buf, 0, (int)frames * ch); for (int i = 0; i < frames * ch; i++) che[i % ch] += buf[i] * buf[i]; for (int i = 0; i < frames; i++) { float v = buf[i * ch]; n++; if (v != 0) { if (zrun >= 48 && zrun < 960) shortHoles++; else if (zrun >= 960) longRuns++; zrun = 0; nonzero++; } else zrun++; e += v * v; pk = Math.Max(pk, Math.Abs(v)); } }
                cc.ReleaseBuffer(frames); cc.GetNextPacketSize(out p);
            }
        }
        ac.Stop();
        Console.WriteLine("StreamLite - In (" + cap.Name + "): " + n + " samples at " + rate + " Hz, non-zero " + (100.0 * nonzero / Math.Max(1, n)).ToString("0.0") + "%, level " + (10 * Math.Log10(e / Math.Max(1, n) + 1e-20)).ToString("0.0") + " dBFS, peak " + (20 * Math.Log10(pk + 1e-12)).ToString("0.0") + " dBFS, silent packets " + silentPackets);
        Console.WriteLine("  silent stretches: " + shortHoles + " short (1-20 ms, would be glitches), " + longRuns + " long (20 ms+, noise gate in pauses)");
        Console.WriteLine("  per channel (dBFS): " + string.Join(" ", che.Select(x => (10 * Math.Log10(x / Math.Max(1, n) + 1e-20)).ToString("0"))));
    }
}
