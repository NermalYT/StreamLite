// Mic presets without devices: a mic's identity on any PC (USB vendor/product + name), finding its preset, keeping
// and applying the whole voice chain, the .slmic file (nothing from this PC in it), and taking a mic to a friend's PC.
// Usage: MicPresetsTest
using System;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class MicPresetsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static AppSettings Fresh() { var s = new AppSettings(); typeof(AppSettings).GetMethod("Normalize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null); return s; }

    static void Main()
    {
        Console.WriteLine("Identity:");
        string yeti = MicPresets.KeyOf(@"{1}.USB\VID_b58e&PID_9e84&MI_00\7&2A6F2C1&0&0000", "Microphone (Yeti Stereo Microphone)");
        string yetiOtherPort = MicPresets.KeyOf(@"{1}.USB\VID_B58E&PID_9E84&MI_00\9&FFFF&0&0000", "Microphone (Yeti Stereo Microphone)");
        string airpods = MicPresets.KeyOf(@"{1}.BTHHFENUM\BTHHFPAUDIO\E&3E55F83&0&97", "Headset (AirPods Pro)");
        Check(yeti == "usb:B58E:9E84|Microphone (Yeti Stereo Microphone)" && yeti == yetiOtherPort, "a USB mic is the same mic on any port or PC: " + yeti);
        Check(airpods == "name|Headset (AirPods Pro)", "a Bluetooth mic goes by its name");

        Console.WriteLine("Keeping and applying:");
        var mine = Fresh();
        mine.Strength = 83; mine.GateThreshold = -61; mine.CompRatio = 31; mine.DeEsser = true; mine.AutoLevelMaxBoost = 18; mine.MainMicGainDb = 6;
        mine.MicEq.Bands[2] = 4.5; mine.StudioStrength = 60;
        var p = MicPresets.Capture(mine, "Microphone (Yeti Stereo Microphone)", yeti, "300;1,2,3");
        MicPresets.Keep(mine, p);
        Check(MicPresets.Find(mine, yetiOtherPort) == p, "found again on another port");
        var renamed = MicPresets.KeyOf(@"{1}.USB\VID_B58E&PID_9E84&MI_00\1&1&0&0000", "Microphone (2- Yeti Stereo Microphone)");
        Check(MicPresets.Find(mine, renamed) == p, "found when Windows numbers it (\"2- Yeti\"): same USB mic");
        Check(MicPresets.Find(mine, MicPresets.KeyOf(@"{1}.USB\VID_0B05&PID_1B9B&MI_00\9&1&0&0000", "Microphone (Realtek)")) == null, "another mic doesn't get it");
        var again = MicPresets.Capture(mine, p.Device, p.Key, "300;1,2,3"); again.Updated = "later";
        Check(MicPresets.Same(p, again), "unchanged settings count as the same (not saved again)");
        mine.Strength = 84;
        Check(!MicPresets.Same(p, MicPresets.Capture(mine, p.Device, p.Key, "300;1,2,3")), "a changed setting doesn't");

        Console.WriteLine("The file, and a friend's PC:");
        string json = MicPresets.ToJson(p, "3.5.0");
        Check(SetupExport.Leaks(json).Count == 0 && json.Contains("usb:B58E:9E84") && !json.Contains("VID_"), "the .slmic file has the mic's identity but nothing from this PC");
        var friend = Fresh();
        string friendMicId = "{0.0.1.00000000}.{abcd}";
        var got = MicPresets.Parse(json);
        got.Note = "Imported from Yeti.slmic";
        MicPresets.Keep(friend, got);
        // The friend plugs the Yeti in and picks it: its preset is found and applied.
        var found = MicPresets.Find(friend, MicPresets.KeyOf(@"{1}.USB\VID_B58E&PID_9E84&MI_00\3&3&0&0000", "Microphone (Yeti Stereo Microphone)"));
        Check(found != null, "on the friend's PC the Yeti finds its preset");
        MicPresets.Apply(friend, found, friendMicId);
        Check(friend.Strength == 83 && friend.GateThreshold == -61 && friend.CompRatio == 31 && friend.DeEsser && friend.AutoLevelMaxBoost == 18 && friend.MainMicGainDb == 6 && friend.MicEq.Bands[2] == 4.5 && friend.StudioStrength == 60,
            "the whole chain, EQ, volume and Studio voice come along");
        Check(friend.StudioProfiles[friendMicId] == "300;1,2,3", "what Studio voice learned about the mic too (ready at once)");
        try { MicPresets.Parse("{\"Format\":\"something else\"}"); Check(false, "other files are refused"); }
        catch (System.IO.InvalidDataException) { Check(true, "other files are refused"); }
        Check(MicPresets.FileName(p) == "Yeti Stereo Microphone.slmic", "file name: " + MicPresets.FileName(p));

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
