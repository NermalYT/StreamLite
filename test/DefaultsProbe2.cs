// Windows' default devices (every role) and whether StreamLite's cables carry its names / clutter shows.
// Usage: run.ps1 DefaultsProbe2
using System;
using System.Linq;
using StreamLite.Interop;

class DefaultsProbe2
{
    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
        {
            var list = AudioDevices.List(flow, 1 | 2 | 4 | 8);
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                string id = AudioDevices.GetDefaultId(flow, role);
                var d = list.FirstOrDefault(x => x.Id == id);
                Console.WriteLine(flow + " " + role + ": " + (d != null ? d.Name + (AudioDevices.IsCable(d) ? "   [CABLE]" : "") : id));
            }
        }
        foreach (var x in VbTraces.Showing()) Console.WriteLine("VB trace: " + x);
        Console.WriteLine("needs tidying: " + CableNames.NeedsTidying(true) + ", other cable uninstaller: " + CableNames.OtherCableUninstaller());
        Console.WriteLine("named: " + CableNames.Named() + ", clutter: " + string.Join(", ", CableNames.Clutter().Select(c => c.Name)));
    }
}
