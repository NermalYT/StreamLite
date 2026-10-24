using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Lists kernel-streaming audio filters (KSCATEGORY_RENDER) and their pins: data flow, communication, instance counts,
// and which streaming interfaces they offer (standard IOCTL streaming vs WaveRT looped buffers). Read-only.
class KsProbe
{
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);

    static readonly Guid KSCATEGORY_RENDER = new Guid("65E8773E-8F56-11D0-A3B9-00A0C9223196");
    static readonly Guid KSPROPSETID_Pin = new Guid("8C134960-51AD-11CF-878A-94F801C10000");
    static readonly Guid KSPROPSETID_General = new Guid("1464EDA5-6A8F-11D1-9AA7-00A0C9223196");
    const uint IOCTL_KS_PROPERTY = 0x002F0003;

    static List<string> Interfaces(Guid cls)
    {
        var r = new List<string>();
        IntPtr set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x12);   // PRESENT | DEVICEINTERFACE
        var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, i, ref d); i++)
        {
            int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(need);
            Marshal.WriteInt32(buf, 8);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) r.Add(Marshal.PtrToStringUni(buf + 4));
            Marshal.FreeHGlobal(buf);
        }
        SetupDiDestroyDeviceInfoList(set);
        return r;
    }

    static byte[] PinProp(SafeFileHandle h, int pin, int id, int outSize, out int got)
    {
        // KSP_PIN: KSPROPERTY (Guid set, ULONG id, ULONG flags) + ULONG PinId + ULONG Reserved
        var inb = new byte[32];
        KSPROPSETID_Pin.ToByteArray().CopyTo(inb, 0);
        BitConverter.GetBytes(id).CopyTo(inb, 16);
        BitConverter.GetBytes(1).CopyTo(inb, 20);    // KSPROPERTY_TYPE_GET
        BitConverter.GetBytes(pin).CopyTo(inb, 24);
        var outb = new byte[outSize];
        if (!DeviceIoControl(h, IOCTL_KS_PROPERTY, inb, id == 1 ? 24 : 32, outb, outSize, out got, IntPtr.Zero)) { got = -Marshal.GetLastWin32Error(); return null; }
        return outb;
    }

    static void Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0].ToLowerInvariant() : "vb";
        foreach (var path in Interfaces(KSCATEGORY_RENDER))
        {
            if (!path.ToLowerInvariant().Contains(filter)) continue;
            Console.WriteLine(path);
            using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) { Console.WriteLine("  open failed " + Marshal.GetLastWin32Error()); continue; }
                int got;
                var c = PinProp(h, 0, 1 /* CTYPES */, 4, out got);   // KSPROPERTY_PIN_CTYPES needs KSPROPERTY not KSP_PIN, but the pin field is ignored
                int pins = c != null ? BitConverter.ToInt32(c, 0) : 0;
                Console.WriteLine("  pins " + pins);
                for (int p = 0; p < pins; p++)
                {
                    var flow = PinProp(h, p, 2 /* DATAFLOW */, 4, out got);
                    var comm = PinProp(h, p, 7 /* COMMUNICATION */, 4, out got);
                    var inst = PinProp(h, p, 0 /* CINSTANCES */, 8, out got);
                    var ifs = PinProp(h, p, 5 /* INTERFACES */, 1024, out got);
                    var names = new List<string>();
                    if (ifs != null && got >= 8)
                    {
                        int count = BitConverter.ToInt32(ifs, 4);   // KSMULTIPLE_ITEM: Size, Count, then KSIDENTIFIERs (24 bytes)
                        for (int k = 0; k < count && 8 + k * 24 + 24 <= got; k++)
                        {
                            var gb = new byte[16]; Array.Copy(ifs, 8 + k * 24, gb, 0, 16); var g = new Guid(gb);
                            int id = BitConverter.ToInt32(ifs, 8 + k * 24 + 16);
                            names.Add(g.ToString().Substring(0, 8) + ":" + id);
                        }
                    }
                    Console.WriteLine(string.Format("  pin {0}: flow {1} comm {2} instances {3}/{4} interfaces {5}", p,
                        flow != null ? (BitConverter.ToInt32(flow, 0) == 1 ? "in(sink)" : "out") : "?",
                        comm != null ? BitConverter.ToInt32(comm, 0).ToString() : "?",
                        inst != null ? BitConverter.ToInt32(inst, 4).ToString() : "?", inst != null ? BitConverter.ToInt32(inst, 0).ToString() : "?",
                        string.Join(" ", names)));
                }
            }
        }
    }
}
