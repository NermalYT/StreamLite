using System;
using System.Linq;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// MuteTool <name part> [on|off]: prints (or sets) the mute state of a playback device, so loud tests stay silent.
class MuteTool
{
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr n); int UnregisterControlChangeNotify(IntPtr n); int GetChannelCount(out int c);
        int SetMasterVolumeLevel(float db, ref Guid ctx); int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
        int GetMasterVolumeLevel(out float db); int GetMasterVolumeLevelScalar(out float v);
        int SetChannelVolumeLevel(int ch, float db, ref Guid ctx); int SetChannelVolumeLevelScalar(int ch, float v, ref Guid ctx);
        int GetChannelVolumeLevel(int ch, out float db); int GetChannelVolumeLevelScalar(int ch, out float v);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx); int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static void Main(string[] args)
    {
        var d = AudioDevices.List(EDataFlow.Render).First(x => x.Name.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0);
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(d.Id, out dev);
        Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var v = (IAudioEndpointVolume)o;
        Guid ctx = Guid.Empty;
        if (args.Length > 1) v.SetMute(args[1] == "on", ref ctx);
        bool m; v.GetMute(out m); float s; v.GetMasterVolumeLevelScalar(out s);
        Console.WriteLine(d.Name + ": mute " + (m ? "on" : "off") + ", volume " + (s * 100).ToString("0") + "%");
    }
}
