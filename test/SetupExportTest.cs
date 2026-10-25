// Setup export / import, offline: a setup full of device ids and file paths exports with none of them, the file reads
// back, importing on "another PC" maps every device by name (a renamed one too), unticked parts stay as they were,
// unmatched devices are left out, and Undo import puts back exactly the settings from before.
// Usage: run.ps1 SetupExportTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Audio;

class SetupExportTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static AppSettings Fresh(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var s = AppSettings.Load();
        GameProfiles.Merge(s.Profiles, null);
        return s;
    }

    static EqProfile Eq(double db) { var e = new EqProfile(); e.Bands[3] = db; return e; }

    static void Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "sl_setup_test");
        // ---- PC 1: a full setup, with device ids and file paths everywhere.
        var a = Fresh(Path.Combine(tmp, "pc1"));
        const string micA = "{0.0.1.00000000}.{11111111-aaaa-4bbb-8ccc-000000000001}", mic2A = "{0.0.1.00000000}.{11111111-aaaa-4bbb-8ccc-000000000002}";
        const string phonesA = "{0.0.0.00000000}.{22222222-aaaa-4bbb-8ccc-000000000001}", out2A = "{0.0.0.00000000}.{22222222-aaaa-4bbb-8ccc-000000000002}";
        a.MicId = micA; a.NsEngine = NoiseEngines.Normalize("classic"); a.Strength = 83; a.Compressor = true; a.CompThreshold = -31; a.DeEsser = true;
        a.MicEq = Eq(4); a.MainMicGainDb = -3; a.MainMicEq = Eq(-2);
        a.ExtraMics.Add(new ExtraMicSetting { Id = mic2A, Name = "Microphone (Yeti Nano)", GainDb = 5, Eq = Eq(3) });
        a.ExtraOutputs.Add(new ExtraOutputSetting { Id = out2A, Name = "Speakers (Realtek(R) Audio)", GainDb = -4, Eq = Eq(1) });
        a.OutputDelayMs[out2A] = 35; a.OutputDelayMs[phonesA] = 0;
        a.Profiles.Corrections[phonesA] = new DeviceCorrection { Name = "Sennheiser HD 600", Filters = new List<EqFilter> { new EqFilter { Type = "PK", Freq = 3000, Gain = -2, Q = 2 } } };
        a.Game.PersonalHeads[phonesA] = "kemar|70";
        a.Game.Preset = "Footsteps (competitive)"; a.Game.FootstepBoost = 77; a.Game.Hrtf = HrtfSet.Custom; a.Game.CustomHrir = @"C:\Users\Someone\Documents\my-hrir.wav";
        a.Game.BypassApps = "spotify, discord";
        var mine = new GameProfile { Name = "My shooter", ThreeD = "bl", Eq = Eq(6), Exes = new List<string> { "shooter" } };
        a.Profiles.Profiles.Add(mine);
        a.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Parametric[0].Gain = -6;
        a.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Extra.Add(new EqFilter { Freq = 100 });   // the correction underneath
        a.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "2", Key = 0x77, Mods = 1 });
        a.CamFx.Source = @"\\?\usb#vid_046d&pid_085e&mi_00#7&1234abcd&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
        a.CamFx.Background = "image"; a.CamFx.BackgroundImage = @"C:\Users\Someone\Pictures\room.png"; a.CamFx.Zoom = 180;
        a.LastCamera = @"\\?\usb#vid_046d"; a.FormatsDone.Add(phonesA); a.RoutedGames.Add("cs2");

        Func<string, string> namesA = id => id == phonesA ? "Headphones (QCC Dongle Pro)" : id == out2A ? "Speakers (Realtek(R) Audio)" : null;
        var file = SetupExport.Export(a, "Microphone (Shure MV7)", namesA, "3.0.0");
        string json = SetupExport.ToJson(file);
        var ids = new[] { micA, mic2A, phonesA, out2A, "11111111", "22222222", "usb#vid", "my-hrir", "room.png", "Someone" };
        Check(ids.All(x => json.IndexOf(x, StringComparison.OrdinalIgnoreCase) < 0) && SetupExport.Leaks(json).Count == 0,
            "the file has no device ids, file paths or user names (" + (json.Length / 1024) + " KB)");
        Check(SetupExport.Leaks("{\"x\":\"C:\\\\Users\\\\a\\\\b.wav\"}").Count > 0 && SetupExport.Leaks("{\"x\":\"{0.0.1.00000000}.{a}\"}").Count > 0, "the leak guard catches a path and a device id");
        bool refused = false;
        try { var bad = SetupExport.Export(a, "Mic", namesA, "3.0.0"); bad.Mics[0].Name = @"D:\secret\x"; SetupExport.ToJson(bad); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "a file that would leak a path is refused, not written");

        var back = SetupExport.Parse(json);
        Check(back.Mics.Count == 2 && back.Outputs.Count == 1 && back.Headphones.Count == 1 && back.Headphones[0].Name == "Headphones (QCC Dongle Pro)" && back.Headphones[0].PersonalHead == "kemar|70",
            "the file reads back: 2 mics, 1 added output, the headphones by name with their personal 3D");
        Check(!back.Game3D.ContainsKey("Hrtf") && (string)back.Camera["Background"] == "blur", "a custom head model file and a background picture stay on this PC (blur instead)");
        Check(back.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Extra.Count == 0, "profiles carry their game EQ, not this PC's headphone correction under it");
        var lines = SetupExport.Describe(back);
        foreach (var l in lines) Console.WriteLine("       " + l.Key + ": " + l.Value);
        Check(lines.Select(l => l.Key).SequenceEqual(new[] { "mic", "mics", "outputs", "headphones", "3d", "profiles", "hotkeys", "camera" }), "the preview lists every part");
        bool rejected = false, newer = false;
        try { SetupExport.Parse("{\"Strength\":5}"); } catch (InvalidDataException) { rejected = true; }
        try { SetupExport.Parse(json.Replace("\"FormatVersion\":1", "\"FormatVersion\":9")); } catch (InvalidDataException) { newer = true; }
        Check(rejected && newer, "other JSON files and files from a newer StreamLite are turned down with a message");

        // ---- PC 2: different device ids, one name slightly different.
        var b = Fresh(Path.Combine(tmp, "pc2"));
        const string micB = "{0.0.1.00000000}.{33333333-0000-0000-0000-000000000001}", yetiB = "{0.0.1.00000000}.{33333333-0000-0000-0000-000000000002}";
        const string phonesB = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000001}", spkB = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000002}";
        b.MicId = micB; b.CamFx.Zoom = 120; b.CamFx.Source = "cam-b";
        var inputs = new List<SetupDevice> { new SetupDevice { Id = micB, Name = "Microphone (Razer Seiren)" }, new SetupDevice { Id = yetiB, Name = "Yeti Nano" } };
        var outputs = new List<SetupDevice> { new SetupDevice { Id = phonesB, Name = "Headphones (QCC Dongle Pro)" }, new SetupDevice { Id = spkB, Name = "Speakers (Realtek(R) Audio)" } };
        var plan = SetupExport.AutoPlan(back, inputs, outputs, micB, phonesB);
        Check(plan.MicMap[1] == yetiB && plan.OutputMap[0] == spkB && plan.HeadphoneMap[0] == phonesB, "devices matched by name here (\"Microphone (Yeti Nano)\" -> \"Yeti Nano\")");
        plan.Camera = false;
        Func<string, string> namesB = id => inputs.Concat(outputs).Where(d => d.Id == id).Select(d => d.Name).FirstOrDefault();
        SetupExport.Snapshot(b);
        string before = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(b);
        SetupExport.Apply(b, back, plan, micB, phonesB, namesB);
        Check(b.NsEngine == a.NsEngine && b.Strength == 83 && b.CompThreshold == -31 && b.DeEsser && Math.Abs(b.MicEq.Bands[3] - 4) < 1e-9 && b.MainMicGainDb == -3 && Math.Abs(b.MainMicEq.Bands[3] + 2) < 1e-9,
            "mic chain imported (engine, strength, compressor, de-esser, voice EQ, microphone 1's own EQ and volume)");
        Check(b.MicId == micB && b.ExtraMics.Count == 1 && b.ExtraMics[0].Id == yetiB && b.ExtraMics[0].Name == "Yeti Nano" && b.ExtraMics[0].GainDb == 5,
            "microphone 2 is this PC's Yeti, with its volume and EQ; the main mic stays yours");
        Check(b.ExtraOutputs.Count == 1 && b.ExtraOutputs[0].Id == spkB && b.OutputDelayMs[spkB] == 35, "the added output is this PC's speakers, with its 35 ms sync delay");
        Check(b.Profiles.CorrectionFor(phonesB) != null && b.Profiles.CorrectionFor(phonesB).Name == "Sennheiser HD 600" && b.Game.PersonalHeads[phonesB] == "kemar|70",
            "headphone correction and personal 3D land on this PC's headphones");
        Check(b.Game.FootstepBoost == 77 && b.Game.Hrtf == HrtfSet.Ku100 && b.Game.BypassApps == "spotify, discord", "3D settings and app rules imported (your head model stays: theirs was a file)");
        var cs2 = b.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2");
        var shooter = b.Profiles.Profiles.FirstOrDefault(p => p.Name == "My shooter");
        Check(Math.Abs(cs2.Eq.Parametric[0].Gain + 6) < 1e-9 && shooter != null && shooter.Exes.Contains("shooter") && Math.Abs(shooter.Eq.Bands[3] - 6) < 1e-9 && b.Profiles.Profiles.Count == a.Profiles.Profiles.Count,
            "game profiles: an edited built-in and your own profile with its app");
        Check(b.Hotkeys.Count == 1 && b.Hotkeys[0].Action == "mic.mute" && b.Hotkeys[0].Arg == "2", "hotkeys imported");
        Check(b.CamFx.Zoom == 120 && b.CamFx.Source == "cam-b", "unticked camera: as it was");

        // A device that isn't here is left out.
        var c = Fresh(Path.Combine(tmp, "pc3"));
        var plan3 = SetupExport.AutoPlan(back, new List<SetupDevice> { new SetupDevice { Id = "m", Name = "Mic" } }, new List<SetupDevice> { new SetupDevice { Id = "p", Name = "Headphones (Other)" } }, "m", "p");
        SetupExport.Apply(c, back, plan3, "m", "p", id => null);
        Check(plan3.MicMap[1] == null && c.ExtraMics.Count == 0 && plan3.OutputMap[0] == null && c.ExtraOutputs.Count == 0 && plan3.HeadphoneMap[0] == "p",
            "no Yeti or speakers here: left out; the one pair of headphones goes to yours");

        // Undo import: exactly the settings from before.
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", Path.Combine(tmp, "pc2"));
        Check(SetupExport.CanUndo, "undo is offered after an import");
        var gameObj = b.Game; var camObj = b.CamFx;
        SetupExport.Undo(b);
        string after = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(b);
        Check(after == before && ReferenceEquals(gameObj, b.Game) && ReferenceEquals(camObj, b.CamFx) && !SetupExport.CanUndo, "Undo import: every setting exactly as before, in the same objects the app holds");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
