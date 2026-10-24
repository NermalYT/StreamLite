// Live: with 3D audio on, pick another output as the Windows default (as the sound flyout or a newly plugged headset
// does) and check the running StreamLite moves 3D audio there and makes the game device the default again; then go
// back to the original headphones. Reads status.txt of the running app. Changes your default output for ~30 s.
// Usage: run.ps1 FollowProbe ["part of the other output's name"]
using System;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Interop;

class FollowProbe
{
    static string Status() { try { return File.ReadAllText(Path.Combine(AppSettings.Folder, "status.txt")); } catch { return ""; } }
    static string Line3D() { return Status().Split('\n').FirstOrDefault(l => l.StartsWith("3D audio:")) ?? "(no 3D line)"; }

    static bool WaitFor(Func<bool> ok, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++) { if (ok()) return true; Thread.Sleep(250); }
        return ok();
    }

    static void Main(string[] args)
    {
        var outs = AudioDevices.List(EDataFlow.Render);
        string def = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
        var sink = outs.FirstOrDefault(d => d.Id == def);
        Console.WriteLine("default now: " + (sink != null ? sink.Name : def));
        Console.WriteLine(Line3D());
        string phonesLine = Line3D();
        var phones = outs.FirstOrDefault(d => phonesLine.EndsWith("-> " + d.Name.Trim()) || phonesLine.Contains("-> " + d.Name));
        if (sink == null || !sink.Name.Contains("StreamLite") || phones == null) { Console.WriteLine("3D audio isn't running with the game device as default - nothing to test"); return; }
        var other = outs.FirstOrDefault(d => d.Id != sink.Id && d.Id != phones.Id && !AudioDevices.LooksVirtual(d) && (args.Length == 0 || d.Name.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0));
        if (other == null) { Console.WriteLine("no second real output to switch to"); return; }
        int fails = 0;
        try
        {
            Console.WriteLine("picking " + other.Name + " as the Windows default...");
            AudioDevices.SetDefaultPlayback(other.Id);
            bool moved = WaitFor(() => Line3D().Contains("-> " + other.Name) && AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console) == sink.Id, 15);
            Console.WriteLine((moved ? "  PASS " : "  FAIL ") + "3D moved to " + other.Name + " and the game device is the default again: " + Line3D());
            if (!moved) fails++;
            Console.WriteLine("picking " + phones.Name + " again...");
            AudioDevices.SetDefaultPlayback(phones.Id);
            bool back = WaitFor(() => Line3D().Contains("-> " + phones.Name) && AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console) == sink.Id, 15);
            Console.WriteLine((back ? "  PASS " : "  FAIL ") + "back on " + phones.Name + ": " + Line3D());
            if (!back) fails++;
        }
        finally
        {
            // Leave it the way it was, whatever happened.
            if (AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console) != sink.Id) AudioDevices.SetDefaultPlayback(sink.Id);
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
