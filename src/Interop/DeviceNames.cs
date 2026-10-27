using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamLite.Interop
{
    /// <summary>
    /// Every name Windows knows a sound device by, for finding its headphone model (3.5.9): the name it shows
    /// ("Headphones (2- Arctis Nova 7)"), the device's own description when you renamed it in Sound settings, and the
    /// hardware's - what a USB device reports about itself (its product string) and its driver's name, also of the
    /// device it hangs off. A USB headset Windows calls "Speakers (USB Audio Device)" usually reports its model there.
    /// </summary>
    internal static class DeviceNames
    {
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid Fmtid; public int Pid; }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out int devInst, string id, int flags);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out int parent, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out int type, byte[] buffer, ref int size, int flags);

        static readonly Guid DeviceProps = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        static readonly DEVPROPKEY BusReported = new DEVPROPKEY { Fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Pid = 4 };
        static readonly DEVPROPKEY Friendly = new DEVPROPKEY { Fmtid = DeviceProps, Pid = 14 };
        static readonly DEVPROPKEY Description = new DEVPROPKEY { Fmtid = DeviceProps, Pid = 2 };

        // What Windows calls an endpoint unless you renamed it.
        static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Headphones", "Headset", "Headset Earphone", "Speakers", "Speaker", "Earphones", "Digital Audio", "Line Out", "Headphone", "Speakers/Headphones",
        };

        public static List<string> For(AudioDeviceInfo d)
        {
            var names = new List<string> { d.Name };
            try
            {
                // Renamed in Sound settings ("Momentum 5" instead of "Headphones").
                string desc = AudioDevices.GetString(d.Id, new PropertyKey(DeviceProps, 2));
                if (!string.IsNullOrWhiteSpace(desc) && !Plain.Contains(desc.Trim())) names.Add(desc);
                // The hardware: the audio function and the device it belongs to.
                string inst = d.Instance ?? "";
                int dot = inst.IndexOf("}.", StringComparison.Ordinal);
                if (dot >= 0) inst = inst.Substring(dot + 2);
                int node;
                if (inst.Length > 0 && CM_Locate_DevNodeW(out node, inst, 0) == 0)
                    for (int level = 0; level < 2; level++)
                    {
                        foreach (var key in new[] { BusReported, Friendly, Description }) { var s = Read(node, key); if (s != null) names.Add(s); }
                        int parent; if (CM_Get_Parent(out parent, node, 0) != 0) break;
                        node = parent;
                    }
            }
            catch { }
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Read(int node, DEVPROPKEY key)
        {
            int type, size = 0;
            CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
            if (size <= 2 || size > 4096) return null;
            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(node, ref key, out type, buf, ref size, 0) != 0 || type != 0x12) return null;   // DEVPROP_TYPE_STRING
            return Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0');
        }
    }
}
