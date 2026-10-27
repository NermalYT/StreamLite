// Installing the cables keeps your default devices (4.0.3): which roles go back after the install, decided offline.
// With "live", also on this PC's real devices: a cable made the default (as a cable install did), then put back.
// Usage: run.ps1 DefaultDevicesTest [live]
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Interop;

class DefaultDevicesTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine("  " + (ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        Func<string, bool> cable = id => id.StartsWith("cable"), present = id => id != "unplugged-headset";
        Func<Dictionary<string, string>, Dictionary<string, string>, List<DefaultDevices.Change>> plan = (b, n) => DefaultDevices.Plan(b, n, cable, present);
        var before = new Dictionary<string, string> { { "Render/Console", "headphones" }, { "Render/Communications", "headphones" }, { "Capture/Console", "yeti" } };

        var p = plan(before, new Dictionary<string, string> { { "Render/Console", "cable-out" }, { "Render/Communications", "cable-out" }, { "Capture/Console", "cable-hifi-output" } });
        Check(p.Count == 3 && p.All(c => c.To == before[c.Role]), "a cable took over playback, voice chat and the mic: all three back to yours");
        Check(plan(before, new Dictionary<string, string>(before)).Count == 0, "nothing changed: nothing to do");
        Check(plan(before, new Dictionary<string, string> { { "Render/Console", "speakers" } }).Count == 0, "a real device picked meanwhile stays");
        Check(plan(new Dictionary<string, string> { { "Capture/Console", "cable-streamlite-in" } }, new Dictionary<string, string> { { "Capture/Console", "cable-hifi-output" } }).Count == 0, "a cable you had as the default (StreamLite's mic) isn't overruled");
        Check(plan(new Dictionary<string, string> { { "Render/Console", "unplugged-headset" } }, new Dictionary<string, string> { { "Render/Console", "cable-out" } }).Count == 0, "your device unplugged meanwhile: left alone");
        Check(plan(new Dictionary<string, string> { { "Render/Console", null } }, new Dictionary<string, string> { { "Render/Console", "cable-out" } }).Count == 0, "no default before: left alone");

        if (args.Contains("live"))
        {
            Console.WriteLine("Live, on this PC:");
            var outs = AudioDevices.List(EDataFlow.Render); var ins = AudioDevices.List(EDataFlow.Capture);
            var cableOut = outs.FirstOrDefault(AudioDevices.IsCable); var cableIn = ins.FirstOrDefault(d => AudioDevices.IsCable(d) && d.Name.IndexOf("Hi-Fi", StringComparison.OrdinalIgnoreCase) >= 0) ?? ins.FirstOrDefault(AudioDevices.IsCable);
            string playBefore = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console), micBefore = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Communications);
            Console.WriteLine("  before: playback " + Name(outs, playBefore) + ", mic " + Name(ins, micBefore));
            var snap = DefaultDevices.Take();
            AudioDevices.SetDefaultRole(cableOut.Id, ERole.Console);
            AudioDevices.SetDefaultRole(cableIn.Id, ERole.Communications);
            Check(AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console) == cableOut.Id && AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Communications) == cableIn.Id,
                "the cables made the defaults (" + cableOut.Name + ", " + cableIn.Name + ") - as installing them did");
            var back = snap.Restore();
            foreach (var b in back) Console.WriteLine("    " + b);
            Check(AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console) == playBefore && AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Communications) == micBefore,
                "your playback and mic are the defaults again: " + Name(outs, AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console)) + ", " + Name(ins, AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Communications)));
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    static string Name(List<AudioDeviceInfo> l, string id) { var d = l.FirstOrDefault(x => x.Id == id); return d != null ? d.Name : id ?? "(none)"; }
}
