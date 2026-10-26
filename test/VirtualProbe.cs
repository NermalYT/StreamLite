// Every audio endpoint (any state) with its driver instance and whether StreamLite counts it as virtual.
// Usage: run.ps1 VirtualProbe
using System;
using StreamLite.Interop;

class VirtualProbe
{
    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow, 1 | 2 | 4 | 8))
                Console.WriteLine((flow == EDataFlow.Render ? "out " : "in  ") + (AudioDevices.LooksVirtual(d) ? "VIRTUAL " : "real    ") + d.Name + " | " + d.Adapter + " | " + d.Instance);
    }
}
