using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// Lists which apps have audio sessions on each active endpoint (e.g. is Discord recording from "StreamLite - In"?).
// Read-only: nothing is changed.
class SessionProbe
{
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int n);
        int GetSession(int i, out IAudioSessionControl2 s);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName(string v, IntPtr ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string p);
        int SetIconPath(string v, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
    }

    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Capture, EDataFlow.Render })
            foreach (var d in AudioDevices.List(flow))
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev; en.GetDevice(d.Id, out dev);
                Guid iid = typeof(IAudioSessionManager2).GUID; object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) continue;
                IAudioSessionEnumerator e; ((IAudioSessionManager2)o).GetSessionEnumerator(out e);
                int n; e.GetCount(out n);
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 s; e.GetSession(i, out s);
                    int st; s.GetState(out st);
                    uint pid; s.GetProcessId(out pid);
                    string pn = "pid " + pid;
                    try { pn = pid == 0 ? "system sounds" : Process.GetProcessById((int)pid).ProcessName; } catch { }
                    names.Add(pn + (st == 1 ? " (active)" : st == 0 ? " (idle)" : " (expired)"));
                }
                Console.WriteLine((flow == EDataFlow.Capture ? "IN  " : "OUT ") + d.Name + ": " + (names.Count == 0 ? "-" : string.Join(", ", names)));
            }
    }
}
