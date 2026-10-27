using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StreamLite.Interop
{
    /// <summary>
    /// The cables StreamLite installs come from VB-Audio (credited in StreamLite's Credits). Outside the device lists
    /// their installers leave VB-Audio's names in Device Manager ("VB-Audio Virtual Cable", "VB-Audio Hi-Fi Cable"), in
    /// Settings > Apps ("VBCABLE, The Virtual Audio Cable", "ASIO Bridge and Hi-Fi Cable"), in the Start menu ("VB Audio")
    /// and in Program Files (their setup programs and control panels). StreamLite needs none of those: the drivers work
    /// from Windows' driver store, and StreamLite's own uninstaller removes them with pnputil. So Device Manager shows
    /// StreamLite's names and the rest goes. Voicemeeter and other VB-Audio products are left alone. Needs admin rights
    /// (the per-user Start menu doesn't).
    /// </summary>
    internal static class VbTraces
    {
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        static string[] Folders
        {
            get
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                return new[] { Path.Combine(pf, @"VB\CABLE"), Path.Combine(pf, @"VB\CABLEHiFi"), Path.Combine(pf86, @"VB\ASIOBridge") };
            }
        }

        static string[] StartMenus
        {
            get
            {
                return new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) }
                    .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p, "VB Audio")).ToArray();
            }
        }

        /// <summary>The Apps entries of the cables StreamLite uses (never Voicemeeter's).</summary>
        static List<KeyValuePair<RegistryKey, string>> AppEntries()
        {
            var l = new List<KeyValuePair<RegistryKey, string>>();
            foreach (var path in new[] { Uninstall, Uninstall32 })
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(path))
                        if (k != null)
                            foreach (var name in k.GetSubKeyNames())
                                using (var e = k.OpenSubKey(name))
                                {
                                    string dn = e != null ? e.GetValue("DisplayName") as string ?? "" : "", un = e != null ? e.GetValue("UninstallString") as string ?? "" : "";
                                    bool cable = dn.IndexOf("VBCABLE", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0;
                                    if (cable && dn.IndexOf("voicemeeter", StringComparison.OrdinalIgnoreCase) < 0 && un.IndexOf(@"\VB\", StringComparison.OrdinalIgnoreCase) >= 0)
                                        l.Add(new KeyValuePair<RegistryKey, string>(Registry.LocalMachine, path + "\\" + name));
                                }
                }
                catch { }
            return l;
        }

        /// <summary>The cables' device nodes ("ROOT\MEDIA\0000") with the name Device Manager should show.</summary>
        static List<KeyValuePair<string, string>> Nodes()
        {
            var l = new List<KeyValuePair<string, string>>();
            const int Any = 1 | 2 | 8;
            var outs = AudioDevices.List(EDataFlow.Render, Any);
            var mic = outs.FirstOrDefault(CableNames.IsMicCable);
            var game = outs.FirstOrDefault(d => CableNames.IsGameCable(d) && (mic == null || !AudioDevices.SameDevice(d, mic)));
            Func<AudioDeviceInfo, string> node = d => { string i = d.Instance ?? ""; int dot = i.IndexOf("}.", StringComparison.Ordinal); return dot >= 0 ? i.Substring(dot + 2) : i; };
            if (mic != null && node(mic).Length > 0) l.Add(new KeyValuePair<string, string>(node(mic), AppCore.MicAdapterName));
            if (game != null && node(game).Length > 0) l.Add(new KeyValuePair<string, string>(node(game), AppCore.GameAdapterName));
            return l;
        }

        /// <summary>Anything of VB-Audio's still showing (names in Device Manager, Apps entries, Start menu, folders).</summary>
        public static List<string> Showing()
        {
            var l = new List<string>();
            foreach (var n in Nodes()) { string fn = DeviceName(n.Key); if (fn != n.Value) l.Add("Device Manager: " + (fn ?? n.Key)); }
            foreach (var e in AppEntries()) l.Add("Settings > Apps: " + e.Value.Substring(e.Value.LastIndexOf('\\') + 1));
            foreach (var m in StartMenus) if (Directory.Exists(m) && OursOnly(m)) l.Add("Start menu: " + m);
            foreach (var f in Folders) if (Directory.Exists(f)) l.Add("Program Files: " + f);
            return l;
        }

        /// <summary>Device Manager under StreamLite's names, the rest of VB-Audio's traces removed. What it did.</summary>
        public static List<string> Clean()
        {
            var done = new List<string>();
            foreach (var n in Nodes())
                if (DeviceName(n.Key) != n.Value && SetDeviceName(n.Key, n.Value)) done.Add("Device Manager: " + n.Value);
            foreach (var e in AppEntries())
                try { e.Key.DeleteSubKeyTree(e.Value, false); done.Add("removed the Apps entry " + e.Value.Substring(e.Value.LastIndexOf('\\') + 1)); } catch { }
            foreach (var m in StartMenus)
                try { if (Directory.Exists(m) && OursOnly(m)) { Directory.Delete(m, true); done.Add("removed " + m); } } catch { }
            foreach (var f in Folders)
            {
                if (!Directory.Exists(f) || Running(f)) continue;
                try
                {
                    Directory.Delete(f, true); done.Add("removed " + f);
                    var parent = Path.GetDirectoryName(f);
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                }
                catch { }
            }
            return done;
        }

        /// <summary>Your own Start menu's "VB Audio" folder (no admin rights needed): the installer, as the system account, can't see it.</summary>
        public static void CleanUserStartMenu()
        {
            string m = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VB Audio");
            try { if (Directory.Exists(m) && OursOnly(m)) Directory.Delete(m, true); } catch { }
        }

        /// <summary>Uninstalling (drivers kept): Device Manager's own names back.</summary>
        public static void RestoreNames() { foreach (var n in Nodes()) SetDeviceName(n.Key, null); }

        /// <summary>A "VB Audio" Start menu folder holds only the cables' shortcuts (Voicemeeter's keep it).</summary>
        static bool OursOnly(string dir)
        {
            try { return !Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any(f => f.IndexOf("voicemeeter", StringComparison.OrdinalIgnoreCase) >= 0); }
            catch { return false; }
        }

        static bool Running(string dir)
        {
            foreach (var p in Process.GetProcesses())
                try { using (p) if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            return false;
        }

        // ---- SetupAPI: a device node's friendly name (what Device Manager shows)

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr parent, int flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int type, byte[] buffer, int size, out int required);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        const int SPDRP_DEVICEDESC = 0, SPDRP_FRIENDLYNAME = 0x0C;

        static T WithNode<T>(string instance, Func<IntPtr, SP_DEVINFO_DATA, T> f, T fallback)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return fallback;
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                return SetupDiOpenDeviceInfo(set, instance, IntPtr.Zero, 0, ref d) ? f(set, d) : fallback;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        /// <summary>What Device Manager shows for a node: its friendly name, else its description.</summary>
        public static string DeviceName(string instance)
        {
            return WithNode(instance, (set, d) => Prop(set, d, SPDRP_FRIENDLYNAME) ?? Prop(set, d, SPDRP_DEVICEDESC), null);
        }

        static string Prop(IntPtr set, SP_DEVINFO_DATA d, int prop)
        {
            int type, need; var buf = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, prop, out type, buf, buf.Length, out need)) return null;
            return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, need - 2)).TrimEnd('\0');
        }

        /// <summary>Sets (or with null removes) a node's friendly name. Admin rights.</summary>
        static bool SetDeviceName(string instance, string name)
        {
            return WithNode(instance, (set, d) =>
            {
                var bytes = name == null ? null : System.Text.Encoding.Unicode.GetBytes(name + "\0");
                return SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_FRIENDLYNAME, bytes, bytes == null ? 0 : bytes.Length);
            }, false);
        }
    }
}
