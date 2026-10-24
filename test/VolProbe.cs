using System;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// Endpoint volume of every active device: the Windows slider value and what it does in dB.
class VolProbe
{
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr n); int UnregisterControlChangeNotify(IntPtr n);
        int GetChannelCount(out uint c);
        int SetMasterVolumeLevel(float db, ref Guid ctx); int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
        int GetMasterVolumeLevel(out float db); int GetMasterVolumeLevelScalar(out float v);
    }

    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow))
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev; en.GetDevice(d.Id, out dev);
                Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                var v = (IAudioEndpointVolume)o; float db, sc; v.GetMasterVolumeLevel(out db); v.GetMasterVolumeLevelScalar(out sc);
                Console.WriteLine(flow + " | " + d.Name + ": slider " + (sc * 100).ToString("0") + " = " + db.ToString("0.0") + " dB");
            }
    }
}
