// 3.5.1: which mic StreamLite listens to when none was picked (software mics like NVIDIA Broadcast count, cables,
// Stereo Mix and line inputs don't), whether this PC's cables carry StreamLite's names, and "Reset voice settings".
// Runs on a copy of the real settings (STREAMLITE_DATA) and starts no audio.
// Usage: run.ps1 MicPickTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

class MicPickTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static AudioDeviceInfo D(string id, string name, string adapter, string inst) { return new AudioDeviceInfo { Id = id, Name = name + " (" + adapter + ")", Adapter = adapter, Instance = inst }; }

    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-micpicktest");
        Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);

        Console.WriteLine("Cables vs mics:");
        Check(AudioDevices.IsCable(D("a", "CABLE Output", "VB-Audio Virtual Cable", "{1}.ROOT\\MEDIA\\0000")), "VB-CABLE is a cable");
        Check(AudioDevices.IsCable(D("a", "StreamLite - In", "StreamLite Mic", "{1}.ROOT\\MEDIA\\0000")), "StreamLite - In is a cable");
        Check(AudioDevices.IsCable(D("a", "Line 1", "Virtual Audio Cable", "{1}.ROOT\\X\\0000")), "Virtual Audio Cable is a cable");
        Check(!AudioDevices.IsCable(D("a", "Microphone", "NVIDIA Broadcast", "{1}.ROOT\\NVVAD\\0000")), "NVIDIA Broadcast's mic is a mic");
        Check(!AudioDevices.IsCable(D("a", "Chat Mic", "SteelSeries Sonar Virtual Audio Device", "{1}.ROOT\\SONAR\\0000")), "SteelSeries Sonar's mic is a mic");

        var core = new AppCore(true);
        var best = typeof(AppCore).GetMethod("BestMic", BindingFlags.Instance | BindingFlags.NonPublic);
        Func<List<AudioDeviceInfo>, AudioDeviceInfo> pick = l => (AudioDeviceInfo)best.Invoke(core, new object[] { l, null });
        var stereoMix = D("1", "Stereo Mix", "Realtek USB Audio", "{1}.USB\\VID_0B05&PID_1B9B&MI_00\\9");
        var lineIn = D("2", "Line In", "Realtek USB Audio", "{1}.USB\\VID_0B05&PID_1B9B&MI_00\\9");
        var broadcast = D("3", "Microphone", "NVIDIA Broadcast", "{1}.ROOT\\NVVAD\\0000");
        var cable = D("4", "CABLE Output", "VB-Audio Virtual Cable", "{1}.ROOT\\MEDIA\\0000");
        var headset = D("5", "Headset Microphone", "Arctis Nova 7", "{1}.USB\\VID_1038&PID_2202&MI_03\\8");
        core.S.PreviousDefaultMic = null;
        Console.WriteLine("Picking a mic (none picked, Windows' default isn't among these):");
        var p1 = pick(new List<AudioDeviceInfo> { stereoMix, lineIn, cable, broadcast });
        Check(p1 == broadcast, "Stereo Mix, Line In, a cable and NVIDIA Broadcast -> NVIDIA Broadcast (" + (p1 != null ? p1.Name : "none") + ")");
        var p2 = pick(new List<AudioDeviceInfo> { stereoMix, lineIn, cable });
        Check(p2 == lineIn, "no real mic -> a line input rather than Stereo Mix (" + (p2 != null ? p2.Name : "none") + ")");
        core.S.PreviousDefaultMic = "5";
        var p3 = pick(new List<AudioDeviceInfo> { stereoMix, broadcast, headset });
        Check(p3 == headset, "the mic that was the default before StreamLite comes first (" + (p3 != null ? p3.Name : "none") + ")");
        Check(pick(new List<AudioDeviceInfo> { cable }) == null, "only a cable -> nothing (StreamLite never listens to a cable)");

        Console.WriteLine("This PC's cables:");
        bool named = CableNames.Named();
        Console.WriteLine("  StreamLite's names on every cable: " + named);
        Check(named, "the cables here carry StreamLite's names (installed by 3.5.0 with admin rights)");
        var clutter = CableNames.Clutter();
        foreach (var c in clutter) Console.WriteLine("  would hide: " + c.Name);
        Check(clutter.Count == 0, "nothing left to hide here (Windows lists only real devices plus StreamLite - In / Out)");
        Check(!clutter.Any(c => c.Name.StartsWith(AppCore.MicName) || c.Name.StartsWith(AppCore.GameOutName)), "StreamLite - In / Out are never hidden");

        Console.WriteLine("Reset voice settings:");
        var s = core.S;
        string engine = s.NsEngine;
        s.Gate = true; s.GateThreshold = -10; s.Strength = 5; s.NoisePrintOn = true; s.VoiceLockOn = true; s.FarVoice = true; s.StudioVoice = false;
        s.MicEq.Bands[3] = 9; s.MainMicGainDb = 12; s.VolumeDb = -15; s.AutoLevel = false; s.MicChannel = "2"; s.ShareMic = true;
        core.ResetVoiceSettings();
        var d = AppSettings.Defaults();
        Check(s.Gate == d.Gate && s.GateThreshold == d.GateThreshold && s.Strength == d.Strength && s.VolumeDb == d.VolumeDb && s.AutoLevel == d.AutoLevel,
            "gate, noise removal strength, volume and auto level back to the defaults");
        Check(!s.NoisePrintOn && !s.VoiceLockOn && !s.FarVoice && s.StudioVoice == d.StudioVoice, "Remove a specific sound, Only my voice and Across the room off; Studio voice as a new install has it");
        Check(s.MicEq.Bands.All(b => b == 0) && s.MainMicGainDb == 0 && s.MicChannel == d.MicChannel, "EQ flat, mic gain 0, all inputs of the mic");
        Check(s.NsEngine == engine, "the AI quality (noise engine) is kept: " + engine);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
