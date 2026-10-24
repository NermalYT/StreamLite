using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// A silent stand-in for a game: waits for StreamLite to detect it, then opens the DEFAULT playback device the way
// games do and plays silence for a few seconds. SessionProbe (run meanwhile) shows which device Windows routed it to.
// Build it under a game's process name (e.g. VALORANT-Win64-Shipping.exe).
class FakeGame
{
    static void Main(string[] args)
    {
        int wait = args.Length > 0 ? int.Parse(args[0]) : 8;
        Thread.Sleep(wait * 1000);
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt; ac.GetMixFormat(out fmt);
        ac.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero);
        ac.Start();
        Thread.Sleep(10000);
        ac.Stop();
    }
}
