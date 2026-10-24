using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    // Per-app output device (Settings > Sound > Volume mixer > per-app "Output device").
    // Undocumented Windows.Media.Internal.AudioPolicyConfig, interface layout from EarTrumpet (MIT, File-New-Project).
    [Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    internal interface IAudioPolicyConfigFactory21H2
    {
        int __add_CtxVolumeChange(); int __remove_CtxVolumeChanged(); int __add_RingerVibrateStateChanged(); int __remove_RingerVibrateStateChange();
        int __SetVolumeGroupGainForId(); int __GetVolumeGroupGainForId(); int __GetActiveVolumeGroupForEndpointId(); int __GetVolumeGroupsForEndpoint();
        int __GetCurrentVolumeContext(); int __SetVolumeGroupMuteForId(); int __GetVolumeGroupMuteForId(); int __SetRingerVibrateState();
        int __GetRingerVibrateState(); int __SetPreferredChatApplication(); int __ResetPreferredChatApplication(); int __GetPreferredChatApplication();
        int __GetCurrentChatApplications(); int __add_ChatContextChanged(); int __remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.HString)] out string deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    /// <summary>Sends chosen apps (music, voice chat, browsers) straight to a device, bypassing the 3D game device.</summary>
    internal static class AppRouting
    {
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string src, uint length, out IntPtr hstring);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

        static IAudioPolicyConfigFactory21H2 factory;
        static readonly Dictionary<int, string> applied = new Dictionary<int, string>();

        static IAudioPolicyConfigFactory21H2 Factory()
        {
            if (factory != null) return factory;
            IntPtr cls;
            const string name = "Windows.Media.Internal.AudioPolicyConfig";
            WindowsCreateString(name, (uint)name.Length, out cls);
            try
            {
                Guid iid = typeof(IAudioPolicyConfigFactory21H2).GUID;
                IntPtr p;
                int hr = RoGetActivationFactory(cls, ref iid, out p);
                if (hr != 0) throw new COMException("Per-app audio routing is not available on this Windows version", hr);
                factory = (IAudioPolicyConfigFactory21H2)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
                return factory;
            }
            finally { WindowsDeleteString(cls); }
        }

        static string Packed(string endpointId) { return @"\\?\SWD#MMDEVAPI#" + endpointId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}"; }

        /// <summary>Points every running process with one of these names at the device (null = back to Windows default).</summary>
        public static int Route(IEnumerable<string> exeNames, string endpointId)
        {
            int count = 0;
            var f = Factory();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in exeNames)
            {
                string n = raw.Trim();
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                if (n.Length > 0) names.Add(n);
            }
            if (names.Count == 0) return 0;
            // One process snapshot for all names (this runs every few seconds).
            var procs = Process.GetProcesses();
            // Forget exited processes, so a new one that reuses the ID still gets routed.
            var live = new HashSet<int>(procs.Select(x => x.Id));
            foreach (var gone in applied.Keys.Where(k => !live.Contains(k)).ToList()) applied.Remove(gone);
            foreach (var p in procs)
            {
                string pn;
                try { pn = p.ProcessName; } catch { p.Dispose(); continue; }
                if (!names.Contains(pn)) { p.Dispose(); continue; }
                {
                    try
                    {
                        string cur;
                        if (applied.TryGetValue(p.Id, out cur) && cur == endpointId) continue;
                        IntPtr hs = IntPtr.Zero;
                        if (endpointId != null) { string s = Packed(endpointId); WindowsCreateString(s, (uint)s.Length, out hs); }
                        int hr1, hr2;
                        try
                        {
                            hr1 = f.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.Render, ERole.Multimedia, hs);
                            hr2 = f.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.Render, ERole.Console, hs);
                        }
                        finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
                        // Windows refuses (E_INVALIDARG) until the app has opened audio: not done yet, the caller retries.
                        if (hr1 != 0 || hr2 != 0) continue;
                        applied[p.Id] = endpointId;
                        count++;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            return count;
        }

        /// <summary>True when a running process with this name has been pointed at the device (by Route).</summary>
        public static bool IsRouted(string exeName, string endpointId)
        {
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                string cur;
                bool hit = applied.TryGetValue(p.Id, out cur) && cur == endpointId;
                p.Dispose();
                if (hit) return true;
            }
            return false;
        }

        /// <summary>The device an app is pinned to (null = follows the Windows default).</summary>
        public static string Current(int pid)
        {
            string id;
            if (Factory().GetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.Render, ERole.Multimedia, out id) != 0 || string.IsNullOrEmpty(id)) return null;
            return id;
        }

        /// <summary>
        /// Windows remembers per-app output devices for each app's later launches too; this forgets them all (every app
        /// back to "Default", as in Settings > Sound > Volume mixer > Reset). Used when uninstalling.
        /// </summary>
        public static bool ClearAll()
        {
            try { return Factory().ClearAllPersistedApplicationDefaultEndpoints() == 0; }
            catch { return false; }
        }

        /// <summary>Undoes Route for every app we changed that is still running.</summary>
        public static void ResetAll()
        {
            if (factory == null) return;
            foreach (var pid in new List<int>(applied.Keys))
            {
                try
                {
                    factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.Render, ERole.Multimedia, IntPtr.Zero);
                    factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.Render, ERole.Console, IntPtr.Zero);
                }
                catch { }
            }
            applied.Clear();
        }
    }
}
