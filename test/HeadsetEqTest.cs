// Each headset's own EQ, and the game EQs on top of it working together (3.5.0, more device names 3.5.9):
//  1. AutoEq.BestFor: device names as Windows shows them -> the measured model StreamLite picks by itself, or none
//     when unsure (never a wrong model). Real AutoEq index (cached in %TEMP%\StreamLite-matchtest).
//  2. GameProfiles.Blend3D: the 3D footstep EQ / footstep booster and a shooter profile EQ lifting the same bands
//     don't add up - where both lift, the larger lift plays; cuts stay.
// Usage: run.ps1 HeadsetEqTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamLite;
using StreamLite.Audio;

class HeadsetEqTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        var index = AutoEq.Index(Path.Combine(Path.GetTempPath(), "StreamLite-matchtest"));
        // device name, the model it must be ("" = must pick nothing), whether it must find one
        var cases = new[]
        {
            new { Dev = "Headphones (HyperX Cloud II Wireless)", Want = "HyperX Cloud II", Must = true },
            new { Dev = "Headphones (2- Arctis Nova 7)", Want = "Arctis Nova 7", Must = true },
            new { Dev = "Headphones (WH-1000XM4)", Want = "WH-1000XM4", Must = true },
            new { Dev = "Headset (WH-1000XM4 Hands-Free AG Audio)", Want = "WH-1000XM4", Must = true },
            new { Dev = "Headphones (Sam's AirPods Pro)", Want = "AirPods Pro", Must = true },
            new { Dev = "Speakers (Logitech G733 Gaming Headset)", Want = "G733", Must = false },
            new { Dev = "Speakers (Razer BlackShark V2 Pro)", Want = "BlackShark V2 Pro", Must = false },
            new { Dev = "Speakers (Corsair HS80 RGB Wireless Gaming Headset)", Want = "HS80", Must = false },
            new { Dev = "Headset Earphone (CORSAIR VOID ELITE Wireless Gaming Dongle)", Want = "Void", Must = false },
            new { Dev = "Speakers (Astro A50 Game)", Want = "A50", Must = false },
            new { Dev = "Headphones (beyerdynamic DT 770 PRO)", Want = "DT 770", Must = false },
            new { Dev = "Speakers (Audio-Technica ATH-M50x)", Want = "M50x", Must = false },
            new { Dev = "Headphones (MOMENTUM 4 Wireless)", Want = "Momentum 4", Must = false },
            new { Dev = "Speakers (SteelSeries Arctis Nova Pro Wireless)", Want = "Arctis Nova Pro", Must = false },
            new { Dev = "Speakers (Realtek(R) Audio)", Want = "", Must = false },
            new { Dev = "Speakers (USB Audio Device)", Want = "", Must = false },
            new { Dev = "Headphones (High Definition Audio Device)", Want = "", Must = false },
            new { Dev = "Speakers (2- Sony)", Want = "", Must = false },
            new { Dev = "Headphones (Steam Streaming Speakers)", Want = "", Must = false },
            // 3.5.9: more of what Windows really shows - brandless, Bluetooth LE, short forms, sub-models.
            new { Dev = "Speakers (G733 Gaming Headset)", Want = "G733", Must = true },
            new { Dev = "Speakers (PRO X Wireless Gaming Headset)", Want = "Logitech G Pro X", Must = true },
            new { Dev = "Headphones (LE_WH-1000XM5)", Want = "WH-1000XM5", Must = true },
            new { Dev = "Headphones (LE-WF-1000XM5)", Want = "WF-1000XM5", Must = true },
            new { Dev = "Headphones (Bose NC 700 HP)", Want = "Noise Cancelling Headphones 700", Must = true },
            new { Dev = "Headphones (Bose QC Ultra Headphones)", Want = "QuietComfort Ultra Headphones", Must = true },
            new { Dev = "Headphones (Bose QuietComfort Ultra Earbuds)", Want = "QuietComfort Ultra Earbuds", Must = true },
            new { Dev = "Headphones (MOMENTUM TW 3)", Want = "Momentum True Wireless 3", Must = true },
            new { Dev = "Headphones (PXC 550-II)", Want = "PXC 550-II", Must = true },
            new { Dev = "Headphones (Surface Headphones 2)", Want = "Surface Headphones 2", Must = true },
            new { Dev = "Headphones (ATH-M50xBT2)", Want = "ATH-M50xBT2", Must = true },
            new { Dev = "Headphones (Beoplay H95)", Want = "Beoplay H95", Must = true },
            new { Dev = "Headphones (Sennheiser HD 660S)", Want = "HD 660 S", Must = true },
            new { Dev = "Headphones (AirPods Pro 2)", Want = "AirPods Pro2", Must = true },
            new { Dev = "Headphones (Nothing Ear (2))", Want = "Nothing ear (2)", Must = true },
            new { Dev = "Headphones (Galaxy Buds2 Pro)", Want = "Galaxy Buds2 Pro", Must = true },
            new { Dev = "Speakers (Razer BlackShark V2 Pro 2.4)", Want = "BlackShark V2 Pro", Must = true },
            new { Dev = "Speakers (Corsair HS80 RGB Wireless Gaming Receiver)", Want = "HS80", Must = true },
            new { Dev = "Speakers (ASTRO A50 Game)", Want = "", Must = false },
            new { Dev = "Speakers (Stealth 700 Gen 2 MAX)", Want = "", Must = false },
            new { Dev = "Speakers (FiiO BTR5)", Want = "", Must = false },
            new { Dev = "Speakers (Apple USB-C to 3.5mm Headphone Jack Adapter)", Want = "", Must = false },
            new { Dev = "Speakers (Logitech G560 Gaming Speaker)", Want = "", Must = false },
            new { Dev = "Speakers (Xbox Controller)", Want = "", Must = false },
            new { Dev = "Odyssey G40B (NVIDIA High Definition Audio)", Want = "", Must = false },
            new { Dev = "Speakers (Yeti Stereo Microphone)", Want = "", Must = false },
            new { Dev = "Speakers (Elgato Wave:3)", Want = "", Must = false },
        };
        int found = 0;
        foreach (var c in cases)
        {
            var e = AutoEq.BestFor(index, c.Dev);
            if (e != null) found++;
            bool right = e == null ? c.Want == "" || !c.Must : c.Want != "" && Regex.IsMatch(e.Name, Regex.Escape(c.Want).Replace("\\ ", "[ -]?"), RegexOptions.IgnoreCase);
            Check(right, "\"" + c.Dev + "\" -> " + (e == null ? "none (asks you)" : e.Name + " (" + e.Source + ")"));
        }
        Console.WriteLine("  picked by itself for " + found + " of " + cases.Length + " names");

        // Every name Windows has for a device counts (3.5.9): your own name for it, the USB product string...
        Check(AutoEq.BestFor(index, new[] { "Speakers (USB Audio Device)", "USB Audio Device", "Arctis Nova 7" }) != null, "a USB headset Windows calls \"USB Audio Device\": found by the product string it reports");
        Check(AutoEq.BestFor(index, new[] { "Headphones (QCC Dongle Pro)", "Momentum 5" }) != null, "a dongle you renamed \"Momentum 5\" in Sound settings: found by your name for it");
        Check(AutoEq.BestFor(index, new[] { "Speakers (Realtek(R) Audio)", "Realtek High Definition Audio", "High Definition Audio Controller" }) == null, "onboard sound: nothing picked from any of its names");

        // StreamLite's own corrections for models AutoEq hasn't measured (3.5.6): the MOMENTUM 5.
        var m5 = AutoEq.BestFor(index, "Headphones (MOMENTUM 5)");
        Check(m5 != null && m5.Builtin != null, "\"Headphones (MOMENTUM 5)\" -> " + (m5 != null ? m5.Name + " (" + m5.Source + ")" : "none") + ", picked by itself");
        var m5s = AutoEq.Search(index, "momentum 5");
        Check(m5s.Count > 0 && m5s[0].Builtin != null, "searching \"momentum 5\" lists it first (" + (m5s.Count > 0 ? m5s[0].Name : "nothing") + ")");
        double pre; var m5f = m5 != null ? AutoEq.Download(m5, out pre) : null;
        Check(m5f != null && m5f.Count == 7, "its correction comes without a download (" + (m5f != null ? m5f.Count : 0) + " filters)");

        // Wireless dongles name the dongle, not the headphones: Find my headphones asks for the model instead (3.5.6).
        Check(AutoEq.IsAdapter("Headphones (QCC Dongle Pro)") && AutoEq.IsAdapter("Speakers (USB Audio Adapter)"), "dongles / adapters are recognised");
        Check(!AutoEq.IsAdapter("Headphones (MOMENTUM 5)") && !AutoEq.IsAdapter("Headphones (WH-1000XM4)") && !AutoEq.IsAdapter("Speakers (Astro A50 Game)"), "headphones aren't taken for dongles");

        // ---- Blend3D:Footsteps (competitive) 3D settings under the Apex profile (StreamLite 3D shooter).
        var g = new GameAudioSettings(); g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
        var ps = new ProfileSettings(); GameProfiles.Merge(ps, null);
        var apex = ps.Profiles.First(p => p.Name == "Apex Legends");
        var before = g.ToConfig(); var after = g.ToConfig();
        GameProfiles.Blend3D(after, apex);
        var prof = apex.Eq.AllFilters();
        double worstSum = 0, worstOver = 0, lostCut = 0;
        foreach (double hz in new[] { 1000.0, 2000, 2500, 3000, 4000, 5000, 6000, 8000 })
        {
            double p = Biquad.ResponseDb(prof, hz, 48000), d0 = Biquad.ResponseDb(before.DirEq["front"].AllFilters(), hz, 48000), d1 = Biquad.ResponseDb(after.DirEq["front"].AllFilters(), hz, 48000);
            double bigger = Math.Max(Math.Max(p, d0), 0);
            Console.WriteLine(string.Format("  {0,5} Hz: profile {1,4:+0.0;-0.0} dB, 3D front {2,4:+0.0;-0.0} -> {3,4:+0.0;-0.0}, together {4,4:+0.0;-0.0} -> {5,4:+0.0;-0.0}", hz, p, d0, d1, p + d0, p + d1));
            worstSum = Math.Max(worstSum, p + d0 - bigger);
            worstOver = Math.Max(worstOver, p + d1 - bigger);
        }
        foreach (double hz in new[] { 31.0, 62, 125 })
            lostCut = Math.Max(lostCut, Biquad.ResponseDb(after.DirEq["front"].AllFilters(), hz, 48000) - Biquad.ResponseDb(before.DirEq["front"].AllFilters(), hz, 48000));
        Check(worstSum > 2, "before: the two stacked up to " + worstSum.ToString("0.0") + " dB over the larger lift");
        Check(worstOver < 1.2, "after: at most " + worstOver.ToString("0.0") + " dB over the larger lift (band edges)");
        Check(lostCut < 0.3, "the rumble cuts of the 3D EQ are kept");
        Check(after.FootstepBoost < before.FootstepBoost && after.FootstepBoost > before.FootstepBoost * 0.6,
            "footstep booster " + (before.FootstepBoost * 100).ToString("0") + "% -> " + (after.FootstepBoost * 100).ToString("0") + "% (the profile already lifts the step band)");
        Check(before.DirEq["front"].Bands[7] == g.DirEq["front"].Bands[7], "your saved 3D EQ isn't changed (only what plays)");

        var flat = new GameProfile { Name = "x", Eq = new EqProfile { Mode = "parametric" } };
        var same = g.ToConfig(); GameProfiles.Blend3D(same, flat);
        Check(same.FootstepBoost == before.FootstepBoost && same.DirEq["front"].Bands.SequenceEqual(before.DirEq["front"].Bands), "a flat profile leaves the 3D EQ as it is");
        var corrOnly = new GameProfile { Name = "y", Eq = new EqProfile { Mode = "parametric" } };
        corrOnly.Eq.Extra.Add(new EqFilter { Type = "PK", Freq = 3000, Gain = 6, Q = 1 });
        var c2 = g.ToConfig(); GameProfiles.Blend3D(c2, corrOnly);
        Check(c2.DirEq["front"].Bands.SequenceEqual(before.DirEq["front"].Bands), "the headphone correction (makes them neutral) isn't counted as a lift");

        // ---- Real headsets with "virtual" in their name are real (they were skipped as headphones and mics before 3.5.0).
        Func<string, string, string, bool> virt = (n, a, i) => StreamLite.Interop.AudioDevices.LooksVirtual(new StreamLite.Interop.AudioDeviceInfo { Name = n, Adapter = a, Instance = i });
        Check(!virt("Speakers (Razer Kraken 7.1 V2)", "Razer Kraken 7.1 V2", "{1}.USB\\VID_1532&PID_0510&MI_00\\7&1&0&0000"), "USB headset is real");
        Check(!virt("Headphones (HyperX Virtual Surround Sound)", "HyperX Virtual Surround Sound", "{1}.USB\\VID_0951&PID_16A4&MI_00\\7&2&0&0000"), "\"HyperX Virtual Surround Sound\" (USB) is real");
        Check(!virt("Headset Earphone (CORSAIR VIRTUOSO Wireless)", "CORSAIR VIRTUOSO", "{1}.USB\\VID_1B1C&PID_0A42&MI_00\\7&3&0&0000"), "Corsair Virtuoso is real");
        Check(!virt("Headphones (Logitech G432 Virtual 7.1)", "Logitech G432", ""), "\"... Virtual 7.1\" with no driver info is real");
        Check(!virt("Headset (AirPods Pro)", "AirPods Pro", "{1}.BTHHFENUM\\BTHHFPAUDIO\\E&3&0&97"), "Bluetooth headset is real");
        Check(virt("CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", "{1}.ROOT\\MEDIA\\0000"), "VB-CABLE is virtual");
        Check(virt("Line 1 (Virtual Audio Cable)", "Virtual Audio Cable", "{1}.ROOT\\{83ED7F0E-2028-4956-B0B4-39C76FDAEF1D}\\0000"), "Virtual Audio Cable is virtual");
        Check(virt("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", "VB-Audio Voicemeeter VAIO", "{1}.ROOT\\VBAudioVoicemeeterWDM\\0000"), "Voicemeeter is virtual");
        Check(virt("Speakers (Steam Streaming Speakers)", "Steam Streaming Speakers", "{1}.ROOT\\MEDIA\\0003"), "Steam Streaming Speakers are virtual");
        Check(virt("CABLE Output (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", ""), "VB-CABLE with no driver info is virtual");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
