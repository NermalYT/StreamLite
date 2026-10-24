using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Opens the Yeti twice at once: raw, and in Windows "communications" mode (where Windows applies its own
// voice processing, e.g. Voice Clarity on Windows 11). Lists the effects Windows reports and compares noise.
[ComImport, Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long d, out long m);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(IntPtr props);
}

[ComImport, Guid("4460B3AE-4B44-4527-8676-7548A8ACD260"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEffectsManager
{
    [PreserveSig] int RegisterAudioEffectsChangedNotificationCallback(IntPtr cb);
    [PreserveSig] int UnregisterAudioEffectsChangedNotificationCallback(IntPtr cb);
    [PreserveSig] int GetAudioEffects(out IntPtr effects, out uint count);
    [PreserveSig] int SetAudioEffectState(Guid id, int state);
}

class CommsTest
{
    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 8;
        var yeti = AudioDevices.List(EDataFlow.Capture).First(d => d.Name.Contains("Yeti"));
        double[] rawRes = null, comRes = null;
        var t1 = new Thread(() => rawRes = Capture(yeti.Id, false, secs));
        var t2 = new Thread(() => comRes = Capture(yeti.Id, true, secs));
        t2.Start(); Thread.Sleep(300); t1.Start(); t1.Join(); t2.Join();
        Console.WriteLine("raw:            quiet " + rawRes[0].ToString("0.0") + " dB, loud " + rawRes[1].ToString("0.0") + " dB");
        Console.WriteLine("communications: quiet " + comRes[0].ToString("0.0") + " dB, loud " + comRes[1].ToString("0.0") + " dB");
    }

    static double[] Capture(string id, bool comms, int secs)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac2 = (IAudioClient2)o;
        if (comms)
        {
            // AudioClientProperties: cbSize, bIsOffload, eCategory (Communications = 3), Options (0 = default processing)
            IntPtr props = Marshal.AllocHGlobal(16);
            Marshal.WriteInt32(props, 0, 16); Marshal.WriteInt32(props, 4, 0); Marshal.WriteInt32(props, 8, 3); Marshal.WriteInt32(props, 12, 0);
            int hrp = ac2.SetClientProperties(props);
            Console.WriteLine("SetClientProperties(Communications): 0x" + hrp.ToString("X8"));
        }
        var ac = (IAudioClient)o;
        var wf = new WaveFormatEx { wFormatTag = 3, nChannels = 1, nSamplesPerSec = 48000, nAvgBytesPerSec = 192000, nBlockAlign = 4, wBitsPerSample = 32 };
        IntPtr fmt = Marshal.AllocHGlobal(18); Marshal.StructureToPtr(wf, fmt, false);
        int hr = ac.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 1000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine((comms ? "comms" : "raw") + " init failed 0x" + hr.ToString("X8")); return new double[] { 0, 0 }; }
        if (comms)
        {
            object em; Guid eid = new Guid("4460B3AE-4B44-4527-8676-7548A8ACD260");
            int hre = ac.GetService(ref eid, out em);
            if (hre == 0)
            {
                IntPtr list; uint cnt;
                ((IAudioEffectsManager)em).GetAudioEffects(out list, out cnt);
                Console.WriteLine("Windows reports " + cnt + " capture effects in communications mode:");
                for (int i = 0; i < cnt; i++)
                {
                    var g = (Guid)Marshal.PtrToStructure(list + i * 24, typeof(Guid));
                    int can = Marshal.ReadInt32(list, i * 24 + 16), state = Marshal.ReadInt32(list, i * 24 + 20);
                    Console.WriteLine("  " + g + " " + Name(g) + " state=" + (state == 1 ? "on" : "off") + " canSet=" + (can != 0));
                }
                Marshal.FreeCoTaskMem(list);
            }
            else Console.WriteLine("IAudioEffectsManager not available: 0x" + hre.ToString("X8"));
        }
        object svc; Guid cid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref cid, out svc);
        var cap = (IAudioCaptureClient)svc;
        var blocks = new System.Collections.Generic.List<double>();
        double sq = 0; int c = 0; var tmp = new float[48000];
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr p; uint fr, fl; ulong a, b;
                cap.GetBuffer(out p, out fr, out fl, out a, out b);
                Marshal.Copy(p, tmp, 0, (int)fr);
                cap.ReleaseBuffer(fr);
                for (int i = 0; i < fr; i++) { sq += tmp[i] * tmp[i]; if (++c == 2400) { blocks.Add(10 * Math.Log10(sq / c + 1e-12)); sq = 0; c = 0; } }
                cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        var s = blocks.Skip(10).OrderBy(x => x).ToList(); // skip warm-up
        return new[] { s[(int)(s.Count * 0.15)], s[(int)(s.Count * 0.9)] };
    }

    static string Name(Guid g)
    {
        string s = g.ToString().ToLowerInvariant();
        string[] known = { "6f64adbe", "AEC (echo cancellation)", "6f64adbf", "Noise suppression", "6f64adc0", "Automatic gain control",
            "6f64adc1", "Beamforming", "6f64adc2", "Constant tone removal", "6f64adc3", "Equalizer", "6f64adc4", "Loudness equalizer",
            "6f64adce", "Dynamic range compression" };
        for (int i = 0; i < known.Length; i += 2) if (s.StartsWith(known[i])) return "(" + known[i + 1] + ")";
        return "(other / vendor effect)";
    }
}
