using System;
using System.Linq;
using StreamLite.Interop;

// Prints Windows' default playback and recording devices for each role.
class Defaults
{
    static void Main()
    {
        var all = AudioDevices.List(EDataFlow.Render).Concat(AudioDevices.List(EDataFlow.Capture)).ToList();
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                string id = AudioDevices.GetDefaultId(flow, role);
                var d = all.FirstOrDefault(x => x.Id == id);
                Console.WriteLine((flow == EDataFlow.Render ? "playback " : "recording ") + role + ": " + (d != null ? d.Name : id ?? "-"));
            }
    }
}
