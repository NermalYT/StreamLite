// A damaged settings file never silently resets everything: the last copy that loaded is used and the damaged one
// is kept. Uses its own folder (STREAMLITE_DATA). Usage: run.ps1 SettingsRecoveryTest
using System;
using System.IO;
using System.Linq;
using StreamLite;

class SettingsRecoveryTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_settingsrecovery");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        string file = Path.Combine(dir, "settings.json");

        var s = AppSettings.Load();
        s.Strength = 33; s.Game.Enabled = true; s.Profiles.ManualProfile = "Rainbow Six Siege";
        s.Save();
        var a = AppSettings.Load();
        Check(a.Strength == 33 && File.Exists(file + ".lastgood"), "a good file loads and is kept as the last good copy");

        // Damaged the way a hand edit did it: an array cut in the middle.
        string text = File.ReadAllText(file);
        int cut = text.IndexOf("\"Bands\":[");
        File.WriteAllText(file, text.Substring(0, cut + 12) + "}]," + text.Substring(cut + 40));
        var b = AppSettings.Load();
        Check(b.Strength == 33 && b.Game.Enabled && b.Profiles.ManualProfile == "Rainbow Six Siege", "a damaged file: your settings come back from the last good copy (not defaults)");
        Check(Directory.GetFiles(dir, "settings.broken-*.json").Length == 1, "the damaged file is kept for a bug report");

        File.WriteAllText(file, "not json at all");
        File.Delete(file + ".lastgood");
        var c = AppSettings.Load();
        Check(c != null && c.Strength == new AppSettings().Strength, "nothing usable at all: defaults (still starts)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
