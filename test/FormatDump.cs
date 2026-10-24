using System;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// Lists every active endpoint with its shared-mode mix format and the device ("Default Format") format.
class FormatDump
{
    static string Fmt(IntPtr f)
    {
        if (f == IntPtr.Zero) return "-";
        short tag = Marshal.ReadInt16(f, 0); int ch = Marshal.ReadInt16(f, 2), rate = Marshal.ReadInt32(f, 4), bits = Marshal.ReadInt16(f, 14);
        int valid = tag == -2 ? Marshal.ReadInt16(f, 18) : bits; int mask = tag == -2 ? Marshal.ReadInt32(f, 20) : 0;
        bool fl = tag == 3 || (tag == -2 && Marshal.ReadByte(f, 24) == 3);
        return ch + "ch " + valid + "/" + bits + (fl ? "f" : "i") + " " + rate + " Hz mask 0x" + mask.ToString("X");
    }

    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow))
            {
                var pc = (IPolicyConfig)new PolicyConfigClient();
                IntPtr dev, mix;
                pc.GetDeviceFormat(d.Id, 0, out dev); pc.GetMixFormat(d.Id, out mix);
                Console.WriteLine(flow + " | " + d.Name + " | device " + Fmt(dev) + " | mix " + Fmt(mix));
                Marshal.ReleaseComObject(pc);
            }
    }
}
