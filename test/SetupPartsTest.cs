// Settings piece by piece (4.1.3), offline: every setting becomes a piece; a file with only some pieces imports only
// those; device pieces go to the device picked on "another PC" (or are left out); nothing from this PC (ids, paths)
// and nothing learned from a recording is ever in a file; old 4.1.2 files still import; Scan for settings finds kept
// settings and files. Usage: run.ps1 SetupPartsTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

class SetupPartsTest
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
        string tmp = Path.Combine(Path.GetTempPath(), "sl_parts_test");
        const string micA = "{0.0.1.00000000}.{11111111-aaaa-4bbb-8ccc-000000000001}", mic2A = "{0.0.1.00000000}.{11111111-aaaa-4bbb-8ccc-000000000002}";
        const string phonesA = "{0.0.0.00000000}.{22222222-aaaa-4bbb-8ccc-000000000001}", camA = @"\\?\usb#vid_046d&pid_085e&mi_00#7&1234abcd&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
        var a = Fresh(Path.Combine(tmp, "pc1"));
        a.MicId = micA; a.Strength = 83; a.Gate = true; a.GateThreshold = -44; a.Compressor = true; a.CompThreshold = -31; a.DeEsser = true; a.DeEssAmount = 9;
        a.MicEq = Eq(4); a.MainMicGainDb = -3; a.MainMicEq = Eq(-2);
        a.ExtraMics.Add(new ExtraMicSetting { Id = mic2A, Name = "Microphone (Yeti Nano)", GainDb = 5, Eq = Eq(3) });
        a.Profiles.Corrections[phonesA] = new DeviceCorrection { Name = "Sennheiser MOMENTUM 5", Filters = new List<EqFilter> { new EqFilter { Type = "PK", Freq = 3000, Gain = -2, Q = 2 } } };
        a.Game.PersonalHeads[phonesA] = "h12|35|75|104"; a.Game.HeadScores[phonesA] = "h12=98.6;ku100=80";
        a.Game.FootstepBoost = 77; a.Game.Also3DApps = "discord";
        a.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Parametric[0].Gain = -6;
        a.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Key = 0x77, Mods = 1 });
        a.Hotkeys.Add(new HotkeyBinding { Action = "cam.bg", Arg = "next", Key = 0x78 });
        a.Cameras[camA] = new CameraProfile { Lock = true };
        a.Cameras[camA].Values["Brightness"] = new CameraPropSetting { Value = 140 };
        a.CamFx.FaceTracking = true; a.CamFx.Zoom = 180; a.CamFx.Background = "image"; a.CamFx.BackgroundImage = @"C:\Users\Someone\Pictures\room.png";
        a.CamFx.PauseZones.Add(new[] { 0.1, 0.1, 0.4, 0.5 }); a.CamFx.Presets.Add(new CamPreset { Used = true, Name = "Desk", X = 0.3, Zoom = 150 });
        a.VoiceLockOn = true; a.NoisePrintOn = true; a.NoisePrintAbout = "my fan";
        a.MicPresets.Add(new MicPreset { Device = "Microphone (Shure MV7)", Key = "usb:14ED:1012|Shure MV7", Chain = new Dictionary<string, object> { { "Strength", 60 } }, MicEq = Eq(2), MainMicEq = Eq(1) });

        Func<string, string> namesA = id => id == phonesA ? "Headphones (Sennheiser MOMENTUM 5)" : id == camA ? "Logitech BRIO" : id == mic2A ? "Microphone (Yeti Nano)" : null;
        var parts = SetupParts.From(a, "Microphone (Shure MV7)", namesA);
        Func<string, bool> has = id => parts.Any(p => p.Id == id);
        Check(new[] { "mic.noise", "mic.filters", "mic.gate", "mic.comp", "mic.deess", "mic.level", "mic.far", "mic.studio", "mic.voiceeq", "mic.device", "mic.preset", "mic.options",
            "phones.correction", "phones.head", "profile", "profiles.rules", "3d.on", "3d.sound", "3d.head", "3d.direq", "3d.apps",
            "cam.base", "cam.on", "cam.image", "cam.tracking", "cam.background", "cam.gestures", "cam.views", "cam.privacy", "hotkey", "app" }.All(has),
            "every area becomes pieces (" + parts.Count + " pieces)");
        Check(parts.Count(p => p.Id == "mic.device") == 2 && parts.Count(p => p.Id == "hotkey") == 2 && parts.Count(p => p.Id == "profile") == a.Profiles.Profiles.Count,
            "one piece per mic, per hotkey and per game's headphone EQ");
        Check(parts.Where(p => p.Device != null).All(p => p.DeviceKind != null) && parts.First(p => p.Id == "cam.base").Device == "Logitech BRIO", "device pieces name their device (by name)");

        // The file: no ids, no paths, nothing learned from a recording.
        var file = new SetupFile { Format = SetupFile.Kind, FormatVersion = 2, AppVersion = "4.1.3", Created = "2026-10-29", Parts = parts };
        string json = SetupExport.ToJson(file);
        Check(new[] { micA, mic2A, phonesA, "11111111", "22222222", "usb#vid", "room.png", "Someone" }.All(x => json.IndexOf(x, StringComparison.OrdinalIgnoreCase) < 0) && SetupExport.Leaks(json).Count == 0,
            "the file has no device ids, file paths or user names (" + (json.Length / 1024) + " KB)");
        Check(json.IndexOf("VoiceLock", StringComparison.Ordinal) < 0 && json.IndexOf("NoisePrint", StringComparison.Ordinal) < 0 && json.IndexOf("my fan", StringComparison.Ordinal) < 0,
            "nothing about your voiceprint or a recorded sound is in it");

        // PC 2: other device ids and names; import some pieces, map the devices.
        var back = SetupParts.Of(SetupExport.Parse(json));
        Check(back.Count == parts.Count, "the file reads back with every piece");
        var b = Fresh(Path.Combine(tmp, "pc2"));
        const string micB = "{0.0.1.00000000}.{33333333-aaaa-4bbb-8ccc-000000000001}", phonesB = "{0.0.0.00000000}.{44444444-aaaa-4bbb-8ccc-000000000001}", camB = @"\\?\usb#vid_046d&pid_085e#other";
        b.MicId = micB;
        var chosen = back.Where(p => p.Id == "mic.gate" || p.Id == "mic.deess" || p.Id == "phones.correction" || p.Id == "phones.head" || p.Id == "cam.tracking" || p.Id == "cam.base" ||
            (p.Id == "profile" && p.Label.StartsWith("Counter-Strike 2")) || (p.Id == "mic.device" && p.Device == "Microphone (Shure MV7)") || (p.Id == "hotkey" && p.Label.StartsWith("F9"))).ToList();
        Func<SetupPart, string> target = p => p.DeviceKind == "output" ? phonesB : p.DeviceKind == "camera" ? camB : p.DeviceKind == "mic" ? micB : null;
        SetupParts.Apply(b, chosen, target, micB, id => id == phonesB ? "Headphones (MOMENTUM 5)" : null);
        Check(b.Gate && b.GateThreshold == -44 && b.DeEsser && b.DeEssAmount == 9, "the gate and de-esser came over");
        Check(b.Strength == AppSettings.Defaults().Strength && !b.Compressor == !AppSettings.Defaults().Compressor && b.CompThreshold == AppSettings.Defaults().CompThreshold, "noise removal and the compressor (not ticked) stayed as they were");
        Check(b.MainMicGainDb == -3 && Math.Abs(b.MainMicEq.Bands[3] + 2) < 1e-9 && b.ExtraMics.Count == 0, "microphone 1's own EQ and volume went to the mic picked here; the added mic (not ticked) didn't come");
        Check(b.Profiles.Corrections.ContainsKey(phonesB) && b.Profiles.Corrections[phonesB].Name == "Sennheiser MOMENTUM 5" && b.Game.PersonalHeads[phonesB] == "h12|35|75|104" && b.Game.HeadScores[phonesB].StartsWith("h12=98.6"),
            "the headphones' EQ, personal 3D and head scores went to the headphones picked here");
        Check(Math.Abs(b.Profiles.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Parametric[0].Gain + 6) < 1e-9, "one game's headphone EQ came over on its own");
        Check(b.CamFx.FaceTracking && b.CamFx.Zoom == 180 && b.CamFx.PauseZones.Count == 1 && b.CamFx.Presets.Count(p => p.Used && p.Name == "Desk") == 1, "camera AI tracking, its zones and saved views came over");
        Check(b.CamFx.Background == "off", "the background (not ticked) stayed as it was");
        Check(b.Cameras.ContainsKey(camB) && b.Cameras[camB].Lock && b.Cameras[camB].Values["Brightness"].Value == 140, "the camera's own base settings went to the camera picked here");
        Check(b.Hotkeys.Count == 1 && b.Hotkeys[0].Action == "cam.bg", "one hotkey came over on its own");
        Check(!b.VoiceLockOn && !b.NoisePrintOn, "your voiceprint and recorded sound stay on the PC they were made on");

        // A device left out: nothing goes anywhere.
        var c = Fresh(Path.Combine(tmp, "pc3"));
        SetupParts.Apply(c, back.Where(p => p.Id == "phones.correction").ToList(), p => null, "mic", id => null);
        Check(c.Profiles.Corrections.Count == 0, "a device left out brings nothing");

        // Old (4.1.2) files still import, as pieces.
        var a2 = Fresh(Path.Combine(tmp, "pc1"));
        a2.Strength = 91; a2.Profiles.Corrections[phonesA] = new DeviceCorrection { Name = "HD 600", Filters = new List<EqFilter>() };
        var old = SetupExport.Parse(SetupExport.ToJson(SetupExport.Export(a2, "Mic", id => id == phonesA ? "Headphones (HD 600)" : null, "4.1.2")));
        var oldParts = SetupParts.Of(old);
        Check(oldParts.Any(p => p.Id == "mic.noise" && Convert.ToInt32(p.Values["Strength"]) == 91) && oldParts.Any(p => p.Id == "phones.correction" && p.Device == "Headphones (HD 600)"),
            "a 4.1.2 setup file imports as pieces (" + oldParts.Count + ")");

        // Scan for settings: a kept copy and an exported file are found.
        var s = Fresh(Path.Combine(tmp, "scan"));
        s.VoiceSetupDone = true; s.Profiles.Corrections[phonesA] = new DeviceCorrection { Name = "HD 600", Filters = new List<EqFilter>() };
        SettingsTransfer.Keep(s);
        var found = SettingsTransfer.Scan();
        Check(found.Any(f => f.Title.StartsWith("Settings kept before a setup")), "a copy kept before a setup is found (" + found.Count + " found)");
        var kept = found.First(f => f.Title.StartsWith("Settings kept"));
        var keptParts = SetupParts.OfSettingsFile(kept.Path, id => id == phonesA ? "Headphones (HD 600)" : null);
        Check(keptParts != null && keptParts.Any(p => p.Id == "phones.correction"), "a kept copy opens as pieces");
        SettingsTransfer.Keep(s, "4.1.3");
        s.Save();
        found = SettingsTransfer.Scan();
        Check(found.Any(f => f.Title == "Your settings from StreamLite 4.1.3"), "the settings an older version left are found under its version");
        Check(!found.Any(f => Path.GetFileName(f.Path) == "settings.json"), "the settings in use are never offered back");

        // The setup skips the steps imported pieces cover.
        var covered = RetuneWizard.StepsCovered(chosen, target, phonesB);
        Check(covered.SequenceEqual(new[] { 2, 3, 4, 5 }), "imported mic tuning, headphone EQ, personal 3D and camera cover those setup steps (" + string.Join(",", covered) + ")");
        Check(RetuneWizard.StepsCovered(chosen.Where(p => p.Area == SetupParts.Mic || p.Area == SetupParts.Camera), target, phonesB).SequenceEqual(new[] { 2, 5 }),
            "keeping only mic tuning and camera: the setup still does the headphone EQ and 3D");
        var fresh = new AppSettings();
        SetupParts.Apply(fresh, chosen.Where(p => p.Area == SetupParts.Mic), target, "mic", id => null);
        Check(fresh.VoiceSetupDone, "a voice chain brought in counts as Tune to my voice done (not asked again after the setup)");
        var camOnly = new AppSettings();
        SetupParts.Apply(camOnly, chosen.Where(p => p.Area == SetupParts.Camera), target, "mic", id => null);
        Check(!camOnly.VoiceSetupDone, "the camera alone doesn't");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
