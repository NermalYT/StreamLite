using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamLite.Interop
{
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    /// <summary>
    /// One app's own sound, as Windows mixes it (per-app capture, "process loopback", Windows 10 2004 and later): the
    /// app and the programs it started, in any channel layout - 7.1 stays 7.1. StreamLite's mixer uses it to take an
    /// app out of the 3D path and play it as it is.
    /// </summary>
    internal static class ProcessLoopback
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr prop,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        sealed class Done : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Event = new ManualResetEvent(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Event.Set(); }
        }

        /// <summary>An audio client capturing what this process (and the ones it started) plays - or, exclude, what every
        /// other app plays (how Discord takes "the whole screen" sound). Initialize it with LOOPBACK.</summary>
        public static IAudioClient Open(int pid, bool exclude = false)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } } in a VT_BLOB PROPVARIANT.
            IntPtr p = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(p, 0, 1); Marshal.WriteInt32(p, 4, pid); Marshal.WriteInt32(p, 8, exclude ? 1 : 0);
                for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, p);
                var done = new Done();
                IActivateAudioInterfaceAsyncOperation op;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", CoreAudioConst.IID_IAudioClient, pv, done, out op);
                if (hr != 0) throw new COMException("per-app capture unavailable", hr);
                if (!done.Event.WaitOne(3000)) throw new TimeoutException("per-app capture didn't start");
                int ahr; object o;
                op.GetActivateResult(out ahr, out o);
                Marshal.ReleaseComObject(op);
                if (ahr != 0 || o == null) throw new COMException("per-app capture failed", ahr);
                return (IAudioClient)o;
            }
            finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(pv); }
        }

        /// <summary>Apps with a sound session on a device (playing or paused, not closed; or only playing): process id -> name (without .exe).</summary>
        public static Dictionary<int, string> Sessions(string deviceId, bool playingOnly = false)
        {
            var list = new Dictionary<int, string>();
            int me = Process.GetCurrentProcess().Id;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object o = null; IAudioSessionEnumerator e = null;
            try
            {
                if (en.GetDevice(deviceId, out dev) != 0 || dev == null) return list;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return list;
                if (((IAudioSessionManager2)o).GetSessionEnumerator(out e) != 0 || e == null) return list;
                int n; e.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 s = null;
                    try
                    {
                        if (e.GetSession(i, out s) != 0 || s == null) continue;
                        int st; uint pid;
                        if (s.GetState(out st) != 0 || st == 2 || (playingOnly && st != 1) || s.GetProcessId(out pid) != 0 || pid == 0 || pid == me || list.ContainsKey((int)pid)) continue;
                        try { using (var pr = Process.GetProcessById((int)pid)) list[(int)pid] = pr.ProcessName; } catch { }
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
                Marshal.ReleaseComObject(en);
            }
            return list;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Every running process's parent: process id -> parent id.</summary>
        public static Dictionary<int, int> Parents()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    if (e.th32ProcessID != e.th32ParentProcessID) map[e.th32ProcessID] = e.th32ParentProcessID;
            }
            finally { CloseHandle(snap); }
            return map;
        }
    }
}
