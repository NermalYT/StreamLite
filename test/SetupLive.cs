// Setup export / import on this PC's real setup, read-only: a copy of settings.json is exported (no device ids or
// paths may be in it), then imported back into another copy against this PC's real device list - every device must
// come back as itself. The live settings are never written. Usage: run.ps1 SetupLive
using System;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Interop;

class SetupLive
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        string dir = Path.Combine(Path.GetTempPath(), "sl_setup_live");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.Copy(real, Path.Combine(dir, "settings.json"));
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var s = AppSettings.Load();
        GameProfiles.Merge(s.Profiles, null);

        var all = AudioDevices.List(EDataFlow.Render, 1 | 2 | 8).Concat(AudioDevices.List(EDataFlow.Capture, 1 | 2 | 8)).ToList();
        Func<string, string> name = id => all.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefault();
        var f = SetupExport.Export(s, name(s.MicId), name, "3.0.0");
        string json = SetupExport.ToJson(f);
        var ids = new[] { s.MicId, s.VirtualOutId, s.Game.SinkId, s.Game.HeadphonesId, s.MonitorId, s.OutputDeviceId }
            .Concat(s.ExtraMics.Select(m => m.Id)).Concat(s.ExtraOutputs.Select(o => o.Id)).Concat(s.OutputEq.Keys).Concat(s.Profiles.Corrections.Keys)
            .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        Check(ids.All(x => json.IndexOf(x, StringComparison.OrdinalIgnoreCase) < 0) && SetupExport.Leaks(json).Count == 0,
            "your setup exports without any of its " + ids.Count + " device ids or a path (" + (json.Length / 1024) + " KB)");
        foreach (var l in SetupExport.Describe(f)) Console.WriteLine("       " + l.Key + ": " + l.Value);

        var back = SetupExport.Parse(json);
        var inputs = AudioDevices.List(EDataFlow.Capture).Where(d => d.Id != s.MicId && !AudioDevices.LooksVirtual(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
        var outputs = AudioDevices.List(EDataFlow.Render).Where(d => !AudioDevices.LooksVirtual(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
        string phones = s.Game.HeadphonesId ?? AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
        var plan = SetupExport.AutoPlan(back, inputs, outputs, s.MicId, phones);
        for (int i = 1; i < back.Mics.Count; i++) Console.WriteLine("       mic " + (i + 1) + " \"" + back.Mics[i].Name + "\" -> " + (name(plan.MicMap[i]) ?? "left out"));
        for (int i = 0; i < back.Outputs.Count; i++) Console.WriteLine("       output " + (i + 2) + " \"" + back.Outputs[i].Name + "\" -> " + (name(plan.OutputMap[i]) ?? "left out"));
        for (int i = 0; i < back.Headphones.Count; i++) Console.WriteLine("       headphones \"" + back.Headphones[i].Name + "\" -> " + (name(plan.HeadphoneMap[i]) ?? "left out"));
        var presentMics = s.ExtraMics.Where(m => inputs.Any(d => d.Id == m.Id)).Select(m => m.Id).ToList();
        var presentOuts = s.ExtraOutputs.Where(o => outputs.Any(d => d.Id == o.Id)).Select(o => o.Id).ToList();
        Check(presentMics.All(id => plan.MicMap.Values.Contains(id)) && presentOuts.All(id => plan.OutputMap.Values.Contains(id)),
            "every plugged-in mic (" + presentMics.Count + ") and output (" + presentOuts.Count + ") of yours maps back to itself");

        // Import into a second copy: the same devices, EQs and profiles come back.
        var dir2 = Path.Combine(Path.GetTempPath(), "sl_setup_live2");
        if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        Directory.CreateDirectory(dir2);
        File.Copy(real, Path.Combine(dir2, "settings.json"));
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir2);
        var t = AppSettings.Load();
        GameProfiles.Merge(t.Profiles, null);
        var names = inputs.Concat(outputs).ToList();
        SetupExport.Apply(t, back, plan, t.MicId, phones, id => names.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefault());
        Check(t.ExtraMics.Select(m => m.Id).SequenceEqual(s.ExtraMics.Where(m => presentMics.Contains(m.Id)).Select(m => m.Id)) &&
              t.ExtraOutputs.Select(o => o.Id).SequenceEqual(s.ExtraOutputs.Where(o => presentOuts.Contains(o.Id)).Select(o => o.Id)),
            "imported back: the same microphones and outputs, in the same order");
        Check(t.Profiles.Profiles.Count == s.Profiles.Profiles.Count && t.Profiles.Profiles.All(p => s.Profiles.Profiles.Any(q => q.Name == p.Name && q.Exes.Count == p.Exes.Count)),
            "imported back: the same " + t.Profiles.Profiles.Count + " profiles with the same apps");
        Check(t.Strength == s.Strength && t.NsEngine == s.NsEngine && t.Game.Preset == s.Game.Preset && t.Hotkeys.Count == s.Hotkeys.Count, "imported back: mic chain, 3D preset and hotkeys");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
