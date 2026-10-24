using System;
using System.Linq;
using StreamLite.Interop;

// Sets a device's format like the Windows Sound settings dropdown: SetFmt <capture|render> <name prefix> <channels> <rate> <bits>
class SetFmt
{
    static void Main(string[] a)
    {
        var flow = a[0] == "capture" ? EDataFlow.Capture : EDataFlow.Render;
        var d = AudioDevices.List(flow).First(x => x.Name.StartsWith(a[1]));
        int ch = int.Parse(a[2]), bits = int.Parse(a[4]);
        var f = new DeviceFormat { Channels = ch, Mask = ch == 2 ? 3 : ch == 8 ? 0x63F : 0, Rate = int.Parse(a[3]), Container = bits, Valid = bits };
        Console.WriteLine(d.Name + ": SetDeviceFormat " + f + " -> 0x" + DeviceFormats.Set(d.Id, f).ToString("X8"));
    }
}
