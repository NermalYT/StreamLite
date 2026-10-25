using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(string v, IntPtr ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string p);
        [PreserveSig] int SetIconPath(string v, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    /// <summary>Which apps are making sound right now (an active playback session that isn't silent), on any output.</summary>
    internal static class AudioSessions
    {
        /// <summary>True when another app has an active recording session on this capture endpoint (e.g. Discord in a call).</summary>
        public static bool AnyoneRecording(string captureId)
        {
            if (string.IsNullOrEmpty(captureId)) return false;
            int me = Process.GetCurrentProcess().Id;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object o = null; IAudioSessionEnumerator e = null;
            try
            {
                if (en.GetDevice(captureId, out dev) != 0 || dev == null) return false;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return false;
                if (((IAudioSessionManager2)o).GetSessionEnumerator(out e) != 0 || e == null) return false;
                int n; e.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 s = null;
                    try
                    {
                        if (e.GetSession(i, out s) != 0 || s == null) continue;
                        int st; uint pid;
                        if (s.GetState(out st) == 0 && st == 1 && s.GetProcessId(out pid) == 0 && pid != 0 && pid != me) return true;
                    }
                    finally { if (s != null) Marshal.ReleaseComObject(s); }
                }
                return false;
            }
            catch { return false; }
            finally
            {
                if (e != null) Marshal.ReleaseComObject(e);
                if (o != null) Marshal.ReleaseComObject(o);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>One app's sound session on one device (recording or playback), active (1) or opened earlier (0).</summary>
        public sealed class Session { public string DeviceId, Process; public bool Active; }

        /// <summary>Every app's sessions on every active device of a flow (setup check: which mic Discord records from).</summary>
        public static List<Session> All(EDataFlow flow)
        {
            var list = new List<Session>();
            int me = Process.GetCurrentProcess().Id;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                foreach (var d in AudioDevices.List(flow))
                {
                    IMMDevice dev = null; object o = null; IAudioSessionEnumerator e = null;
                    try
                    {
                        if (en.GetDevice(d.Id, out dev) != 0 || dev == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) continue;
                        if (((IAudioSessionManager2)o).GetSessionEnumerator(out e) != 0 || e == null) continue;
                        int n; e.GetCount(out n);
                        for (int i = 0; i < n; i++)
                        {
                            IAudioSessionControl2 s = null;
                            try
                            {
                                if (e.GetSession(i, out s) != 0 || s == null) continue;
                                int st; uint pid;
                                if (s.GetState(out st) != 0 || st == 2 || s.GetProcessId(out pid) != 0 || pid == 0 || pid == me) continue;   // 2 = expired
                                string name = null;
                                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
                                if (name != null) list.Add(new Session { DeviceId = d.Id, Process = name, Active = st == 1 });
                            }
                            finally { if (s != null) Marshal.ReleaseComObject(s); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (e != null) Marshal.ReleaseComObject(e);
                        if (o != null) Marshal.ReleaseComObject(o);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        public static HashSet<string> Playing()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pids = new HashSet<uint>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                foreach (var d in AudioDevices.List(EDataFlow.Render))
                {
                    IMMDevice dev = null; object o = null; IAudioSessionEnumerator e = null;
                    try
                    {
                        if (en.GetDevice(d.Id, out dev) != 0 || dev == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) continue;
                        if (((IAudioSessionManager2)o).GetSessionEnumerator(out e) != 0 || e == null) continue;
                        int n; e.GetCount(out n);
                        for (int i = 0; i < n; i++)
                        {
                            IAudioSessionControl2 s = null;
                            try
                            {
                                if (e.GetSession(i, out s) != 0 || s == null) continue;
                                int st; uint pid;
                                if (s.GetState(out st) != 0 || st != 1 || s.GetProcessId(out pid) != 0 || pid == 0) continue;   // 1 = active
                                var meter = s as IAudioMeterInformation;
                                float pk;
                                if (meter != null && meter.GetPeakValue(out pk) == 0 && pk > 0.0005f) pids.Add(pid);
                            }
                            finally { if (s != null) Marshal.ReleaseComObject(s); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (e != null) Marshal.ReleaseComObject(e);
                        if (o != null) Marshal.ReleaseComObject(o);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            foreach (var pid in pids)
                try { using (var p = Process.GetProcessById((int)pid)) names.Add(p.ProcessName); } catch { }
            return names;
        }
    }
}
