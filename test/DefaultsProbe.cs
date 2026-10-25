// The Windows default playback devices (console and communications roles). Read-only. Usage: DefaultsProbe
using System;
using System.Linq;
using StreamLite;
using StreamLite.Interop;

class DefaultsProbe
{
    static void Main()
    {
        var outs = AudioDevices.List(EDataFlow.Render);
        foreach (var role in new[] { ERole.Console, ERole.Communications })
        {
            string id = AudioDevices.GetDefaultId(EDataFlow.Render, role);
            var d = outs.FirstOrDefault(o => o.Id == id);
            Console.WriteLine(role + ": " + (d != null ? d.Name : id));
        }
    }
}
