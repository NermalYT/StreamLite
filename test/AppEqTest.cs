// App EQ (4.4): one app's EQ changes that app's sound and no other's, on the mixer's way to the headphones; it survives
// the settings file and a settings export; off or removed, the app sounds as before. Offline, no devices.
// Usage: run.ps1 AppEqTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class AppEqTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>A second of a tone through the app's packets into its buffer: how loud it comes out (dB, the last half).</summary>
    static double Level(AppMixer mix, AppMixer.Tap t, double hz)
    {
        const int N = 480;
        var inter = new float[N * 2]; var outp = new float[N * 2];
        double e = 0; int count = 0;
        for (int p = 0; p < 100; p++)
        {
            for (int i = 0; i < N; i++) { float v = (float)(0.2 * Math.Sin(2 * Math.PI * hz * (p * N + i) / 48000.0)); inter[2 * i] = v; inter[2 * i + 1] = v; }
            mix.Feed(t, inter, N, false);
            t.Ring.Read(outp, N);
            if (p >= 50) for (int i = 0; i < N * 2; i++) { e += outp[i] * (double)outp[i]; count++; }
        }
        return 10 * Math.Log10(e / count + 1e-12);
    }

    static void Main()
    {
        var mix = new AppMixer();
        var spotify = new AppMixer.Tap(1, "Spotify"); var discord = new AppMixer.Tap(2, "discord");
        var s = AppSettings.Defaults();
        double spotify0 = Level(mix, spotify, 1000), discord0 = Level(mix, discord, 1000);
        Console.WriteLine("  as they are: " + spotify0.ToString("0.0") + ", " + discord0.ToString("0.0") + " dB");

        var eq = new EqProfile { Mode = "graphic", AutoPreamp = false }; eq.Bands[5] = -12;   // 1 kHz down 12 dB
        s.AppEq["spotify"] = new AppEqSetting { Eq = eq };
        Func<Dictionary<string, EqProfile>> copy = () => { var d = new Dictionary<string, EqProfile>(StringComparer.OrdinalIgnoreCase); foreach (var kv in s.AppEq) if (kv.Value.On) d[kv.Key] = kv.Value.Eq.Clone(); return d; };
        mix.AppEqs = copy(); mix.AppEqVersion++;
        double spotify1 = Level(mix, spotify, 1000), discord1 = Level(mix, discord, 1000);
        Console.WriteLine("  with Spotify's EQ: " + spotify1.ToString("0.0") + ", " + discord1.ToString("0.0") + " dB");
        Check(spotify0 - spotify1 > 8, "the app's EQ changes its sound (" + (spotify0 - spotify1).ToString("0.0") + " dB down at 1 kHz; the program name's case doesn't matter)");
        Check(Math.Abs(discord0 - discord1) < 0.5, "the other app's sound stays as it was");

        s.AppEq["spotify"].On = false;
        mix.AppEqs = copy(); mix.AppEqVersion++;
        Check(Math.Abs(Level(mix, spotify, 1000) - spotify0) < 0.5, "off: as it was");
        s.AppEq["spotify"].On = true;

        Console.WriteLine("Saving");
        s.AppEqPresets["Night bass"] = eq.Clone();
        var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var back = js.Deserialize<AppSettings>(js.Serialize(s));
        Check(back.AppEq.ContainsKey("spotify") && Math.Abs(back.AppEq["spotify"].Eq.Bands[5] + 12) < 0.01 && back.AppEqPresets.ContainsKey("Night bass"), "an app's EQ and your presets are in the settings file");
        var parts = SetupParts.From(s, "Mic", id => null);
        var part = parts.FirstOrDefault(p => p.Id == "phones.appeq");
        Check(part != null && part.Area == SetupParts.Phones, "a settings export can carry them");
        var f = new SetupFile { Format = SetupFile.Kind, FormatVersion = 2, AppVersion = "4.4.0", Created = "2026-11-05", Parts = new List<SetupPart> { part } };
        var read = SetupParts.Of(SetupExport.Parse(SetupExport.ToJson(f)));
        var other = AppSettings.Defaults();
        SetupParts.Apply(other, read, p => null, null, id => null);
        Check(other.AppEq.ContainsKey("spotify") && Math.Abs(other.AppEq["spotify"].Eq.Bands[5] + 12) < 0.01 && other.AppEqPresets.ContainsKey("Night bass"), "and bring them back on another PC");
        Check(AppSettings.Defaults().AppEq.Count == 0, "a new install has none: every app sounds as it is");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
