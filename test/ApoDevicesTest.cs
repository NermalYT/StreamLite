// Equalizer APO switched on and off by StreamLite (4.0.2), checked on a copy of the registry (HKCU, nothing real is
// touched): for a USB headset, a mic, a device without effects, a joined Bluetooth headset and an old-style device,
// it writes exactly what Equalizer APO's own device list writes (the expected values are what it wrote on a real PC),
// and taking it off puts every original value back. Also: the installer's device list is closed the moment it opens.
// Usage: run.ps1 ApoDevicesTest
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using StreamLite.Audio;

class ApoDevicesTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    const string Fx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},", Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},", Def = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    const string Root = @"Software\StreamLiteTest-Apo";

    static string V(RegistryKey k, string n) { var v = k.GetValue(n); return v == null ? null : v is string[] ? string.Join(";", (string[])v) : v.ToString(); }

    static string Snapshot(RegistryKey k)
    {
        return k == null ? "(none)" : string.Join("|", k.GetValueNames().OrderBy(n => n).Select(n => n + "=" + V(k, n)));
    }

    static void Main()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Root, false);
        var root = Registry.CurrentUser.CreateSubKey(Root);
        var audio = root.CreateSubKey("Audio"); var apo = root.CreateSubKey("EqualizerAPO");

        // A USB headset's playback device, as Windows set it up (the QCC dongle: SFX + MFX of Windows' own effects).
        string headset = "{d6ca0172-36c8-41d9-bd7c-f88a7a524eac}";
        using (var fx = audio.CreateSubKey(@"Render\" + headset + @"\FxProperties"))
        {
            fx.SetValue(Fx + "5", "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}"); fx.SetValue(Fx + "6", "{13AB3EBD-137E-4903-9D89-60BE8277FD17}");
            fx.SetValue(Fx + "3", "{5860E1C5-F95C-4a7a-8EC8-8AEF24F379A1}");
            foreach (var m in new[] { "5", "6", "7" }) fx.SetValue(Modes + m, new[] { Def }, RegistryValueKind.MultiString);
            fx.SetValue("{b725f130-47ef-101a-a5f1-02608c9eebac},10", "Microsoft Audio Home Theater Effects");
        }
        audio.CreateSubKey(@"Render\" + headset + @"\Properties").Dispose();
        string before;
        using (var fx = audio.OpenSubKey(@"Render\" + headset + @"\FxProperties")) before = Snapshot(fx);

        Check(!ApoDevices.IsAttached(audio, headset, false), "headset: not on yet");
        Check(ApoDevices.Attach(audio, apo, headset, false), "headset: switched on");
        using (var fx = audio.OpenSubKey(@"Render\" + headset + @"\FxProperties"))
            Check(V(fx, Fx + "5") == ApoDevices.PreMixApo && V(fx, Fx + "7") == ApoDevices.PostMixApo && V(fx, Fx + "6") == "{13AB3EBD-137E-4903-9D89-60BE8277FD17}",
                "headset: before the mix in SFX, after it in EFX, Windows' MFX kept (as Equalizer APO's list did)");
        using (var c = apo.OpenSubKey(@"Child APOs\" + headset))
            Check(V(c, Fx + "1") == "!VALUE" && V(c, Fx + "2") == "!VALUE" && V(c, Fx + "5") == "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}" && V(c, Fx + "6") == "{13AB3EBD-137E-4903-9D89-60BE8277FD17}" && V(c, Fx + "7") == "!VALUE"
                && V(c, "PreMixChild") == "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}" && V(c, "PostMixChild") == "" && V(c, "AllowSilentBufferModification") == "false" && V(c, "Version") == "2",
                "headset: its own effects kept and run inside it - the same record Equalizer APO wrote on a real PC");
        Check(ApoDevices.IsAttached(audio, headset, false) && !ApoDevices.Attach(audio, apo, headset, false), "headset: on, and switching it on again changes nothing");
        Check(ApoDevices.Detach(audio, apo, headset, false), "headset: switched off");
        using (var fx = audio.OpenSubKey(@"Render\" + headset + @"\FxProperties"))
        {
            string after = Snapshot(fx).Replace("|" + Modes + "7=" + Def, "").Replace(Modes + "7=" + Def + "|", "");
            Check(after == before.Replace("|" + Modes + "7=" + Def, "").Replace(Modes + "7=" + Def + "|", ""), "headset: every original value back");
        }
        Check(apo.OpenSubKey("Child APOs") == null, "headset: Equalizer APO's record of it gone");

        // A mic (the Yeti: LFX and EFX of its own): only before the mix, and off again.
        string mic = "{f4a1e330-e224-4562-a8b1-035341ec3bbf}";
        using (var fx = audio.CreateSubKey(@"Capture\" + mic + @"\FxProperties")) { fx.SetValue(Fx + "1", "{4D532D45-C740-44FB-99DC-C1A89D59AF3E}"); fx.SetValue(Fx + "7", "{4D532D45-C740-44FB-99DC-C1A89D59AF3E}"); }
        using (var fx = audio.OpenSubKey(@"Capture\" + mic + @"\FxProperties")) before = Snapshot(fx);
        ApoDevices.Attach(audio, apo, mic, true);
        using (var fx = audio.OpenSubKey(@"Capture\" + mic + @"\FxProperties"))
            Check(V(fx, Fx + "5") == ApoDevices.PreMixApo && V(fx, Fx + "1") == null && V(fx, Fx + "7") == "{4D532D45-C740-44FB-99DC-C1A89D59AF3E}", "mic: before the mix only");
        using (var c = apo.OpenSubKey(@"Child APOs\" + mic)) Check(V(c, "PreMixChild") == "" && V(c, "PostMixChild") == "", "mic: the same record Equalizer APO wrote for it");
        bool input; string g = ApoDevices.Guid("{0.0.1.00000000}." + mic, out input);
        Check(g == mic && input, "a mic's Windows id is read as an input");
        ApoDevices.Detach(audio, apo, mic, true);
        using (var fx = audio.OpenSubKey(@"Capture\" + mic + @"\FxProperties")) Check(Snapshot(fx).Replace("|" + Modes + "5=" + Def, "").Replace(Modes + "5=" + Def + "|", "") == before, "mic: switched off, every original value back");

        // A device without effects of its own: its effects key is made, and goes again.
        string plain = "{11111111-2222-3333-4444-555555555555}";
        audio.CreateSubKey(@"Render\" + plain + @"\Properties").Dispose();
        ApoDevices.Attach(audio, apo, plain, false);
        using (var fx = audio.OpenSubKey(@"Render\" + plain + @"\FxProperties"))
            Check(fx != null && V(fx, Fx + "1") == ApoDevices.PreMixApo && V(fx, Fx + "2") == ApoDevices.PostMixApo, "no effects: Equalizer APO in LFX / GFX of a new key");
        ApoDevices.Detach(audio, apo, plain, false);
        Check(audio.OpenSubKey(@"Render\" + plain + @"\FxProperties") == null, "no effects: switched off, the key gone again");

        // A Bluetooth headset Windows 11 joined into one: after the mix in MFX (EFX doesn't run on it).
        string bt = "{22222222-2222-3333-4444-555555555555}";
        using (var fx = audio.CreateSubKey(@"Render\" + bt + @"\FxProperties")) fx.SetValue(Fx + "5", "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}");
        using (var p = audio.CreateSubKey(@"Render\" + bt + @"\Properties")) p.SetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},41", "x");
        ApoDevices.Attach(audio, apo, bt, false);
        using (var fx = audio.OpenSubKey(@"Render\" + bt + @"\FxProperties"))
            Check(V(fx, Fx + "5") == ApoDevices.PreMixApo && V(fx, Fx + "6") == ApoDevices.PostMixApo && V(fx, Fx + "7") == null && V(fx, Modes + "6") == Def, "joined Bluetooth headset: SFX and MFX");

        // An old-style driver (LFX / GFX only): stays there.
        string old = "{33333333-2222-3333-4444-555555555555}";
        using (var fx = audio.CreateSubKey(@"Render\" + old + @"\FxProperties")) { fx.SetValue(Fx + "1", "{62DC1A93-AE24-464C-A43E-452F824C4250}"); fx.SetValue(Fx + "2", "{637C490D-EEE3-4C0A-973F-371958802DA2}"); }
        audio.CreateSubKey(@"Render\" + old + @"\Properties").Dispose();
        ApoDevices.Attach(audio, apo, old, false);
        using (var fx = audio.OpenSubKey(@"Render\" + old + @"\FxProperties"))
        using (var c = apo.OpenSubKey(@"Child APOs\" + old))
            Check(V(fx, Fx + "1") == ApoDevices.PreMixApo && V(fx, Fx + "2") == ApoDevices.PostMixApo && V(c, "PreMixChild") == "{62DC1A93-AE24-464C-A43E-452F824C4250}" && V(c, "PostMixChild") == "{637C490D-EEE3-4C0A-973F-371958802DA2}",
                "old-style driver: LFX / GFX, its own effects run inside");
        Check(!ApoDevices.Detach(audio, apo, "{44444444-2222-3333-4444-555555555555}", false), "switching off a device it isn't on changes nothing");
        try { ApoDevices.Attach(audio, apo, "{99999999-2222-3333-4444-555555555555}", false); Check(false, "an unknown device is refused"); }
        catch (ArgumentException) { Check(true, "an unknown device is refused"); }

        // The installer's device list: closed the moment it opens (a stand-in program named like it).
        string dir = Path.Combine(Path.GetTempPath(), "StreamLite-apo-test"); Directory.CreateDirectory(dir);
        string fake = Path.Combine(dir, "DeviceSelector.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake, true);
        var sw = Stopwatch.StartNew(); bool closed = false;
        ApoDevices.RunSetupQuietly("unused.exe", 30000, (e, a) =>
        {
            using (var p = Process.Start(new ProcessStartInfo(fake, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }))
                closed = p.WaitForExit(10000);
            return 0;
        });
        Check(closed && sw.Elapsed.TotalSeconds < 5, "the installer's device list closed after " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s (the install carries on)");

        root.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(Root, false);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
