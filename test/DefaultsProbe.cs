// This PC's default sound devices (each role) and each output's mute. Plays nothing.
// Usage: run.ps1 DefaultsProbe [comms=<part of a name>]     (comms=: makes that output the default communications device)
using System;
using System.Linq;
using StreamLite.Interop;

class DefaultsProbe
{
    static void Main(string[] args)
    {
        var outs = AudioDevices.List(EDataFlow.Render);
        foreach (var a in args)
            if (a.StartsWith("comms="))
            {
                var d = outs.FirstOrDefault(o => (o.Name ?? "").IndexOf(a.Substring(6), StringComparison.OrdinalIgnoreCase) >= 0);
                if (d == null) { Console.WriteLine("no output named " + a.Substring(6)); Environment.Exit(1); }
                AudioDevices.SetDefaultRole(d.Id, ERole.Communications);
            }
        Func<EDataFlow, ERole, string> name = (f, r) => { string id = AudioDevices.GetDefaultId(f, r); var d = AudioDevices.List(f).FirstOrDefault(x => x.Id == id); return d != null ? d.Name : id ?? "none"; };
        foreach (var r in new[] { ERole.Console, ERole.Multimedia, ERole.Communications }) Console.WriteLine("playback " + r + ": " + name(EDataFlow.Render, r));
        foreach (var r in new[] { ERole.Console, ERole.Communications }) Console.WriteLine("mic " + r + ": " + name(EDataFlow.Capture, r));
        foreach (var o in outs) Console.WriteLine("output " + o.Name + ": muted " + AudioDevices.GetMute(o.Id));
    }
}
