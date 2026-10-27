// Prints every name StreamLite finds for each sound device (DeviceNames.For) and the AutoEq model it picks from them.
// Usage: run.ps1 HwNameProbe
using System;
using System.IO;
using StreamLite.Audio;
using StreamLite.Interop;
class HwNameProbe
{
    static void Main()
    {
        var index = AutoEq.Index(Path.Combine(Path.GetTempPath(), "StreamLite-matchtest"));
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow, 3))
            {
                var names = DeviceNames.For(d);
                var e = AutoEq.BestFor(index, names);
                Console.WriteLine(flow + ": " + string.Join(" | ", names) + "  ->  " + (e == null ? "-" : e.Name));
            }
    }
}
