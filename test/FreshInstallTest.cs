// A new install starts with the setup (4.2.1): the installer's mark sets the settings there aside once per install, as
// a kept copy Scan for settings lists; updates (no mark, or one already seen) leave them alone. Uses its own folder
// (STREAMLITE_DATA). Usage: run.ps1 FreshInstallTest
using System;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.UI;

class FreshInstallTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_freshinstall");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        string file = Path.Combine(dir, "settings.json");

        // Someone's set-up StreamLite 4.2.0.
        var s = AppSettings.Load();
        s.LastVersion = "4.2.0"; s.VoiceSetupDone = true; s.SetupDoneFor = RetuneWizard.RequiredVersion; s.WelcomeShown = true; s.Strength = 33;
        s.Save();
        AppSettings.Load();   // leaves settings.json.lastgood, as every start does
        Check(!FreshInstall.Check("") && File.Exists(file), "no mark (an update, or no installer): the settings stay");

        Check(FreshInstall.Check("2026-11-03 14:00:00"), "a new install's mark: the settings are set aside");
        Check(!File.Exists(file) && !File.Exists(file + ".lastgood"), "settings.json and its last good copy are gone");
        var kept = Directory.GetFiles(dir, "settings.kept-*~4.2.0.json");
        Check(kept.Length == 1 && AppSettings.ReadFile(kept[0]).Strength == 33, "kept as settings.kept-<when>~4.2.0.json, unchanged");
        Check(FreshInstall.KeptFrom == "4.2.0", "the setup's first step is told which version's settings wait");
        var fresh = AppSettings.Load();
        Check(fresh.LastVersion == null && !fresh.WelcomeShown && !fresh.VoiceSetupDone && fresh.Strength != 33 && RetuneWizard.Needed(fresh),
            "StreamLite starts as a new install: the thank-you and the whole setup");
        var found = SettingsTransfer.Scan();
        Check(found.Any(f => f.Path == kept[0] && f.Title == "Your settings from StreamLite 4.2.0" && f.Detail.Contains("mic tuning")),
            "Scan for settings lists them: \"Your settings from StreamLite 4.2.0\"");

        // The setup done on the new install, then StreamLite starts again (or updates): the same mark does nothing.
        fresh.LastVersion = "4.2.1"; fresh.Strength = 44; fresh.Save();
        Check(!FreshInstall.Check("2026-11-03 14:00:00") && AppSettings.Load().Strength == 44, "the same install again: your new settings stay");
        Check(Directory.GetFiles(dir, "settings.kept-*.json").Length == 1, "nothing more is kept");

        // Installed again later, with a damaged settings file: the last good copy is what's kept.
        AppSettings.Load();
        File.WriteAllText(file, "{ damaged");
        System.Threading.Thread.Sleep(1100);   // a new name (the time is in it)
        Check(FreshInstall.Check("2026-11-04 09:30:00"), "another new install sets the settings aside again");
        var kept2 = Directory.GetFiles(dir, "settings.kept-*~4.2.1.json");
        Check(kept2.Length == 1 && AppSettings.ReadFile(kept2[0]).Strength == 44, "a damaged file: its last good copy is kept");

        // A new install on an account that never ran StreamLite: nothing to keep, the mark is remembered.
        string dir2 = Path.Combine(Path.GetTempPath(), "sl_freshinstall2");
        if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir2);
        Check(!FreshInstall.Check("2026-11-04 09:30:00") && File.ReadAllText(Path.Combine(dir2, "install-seen.txt")).Trim() == "2026-11-04 09:30:00" &&
            !Directory.GetFiles(dir2, "settings.kept-*.json").Any(), "a brand-new account: nothing to set aside, the install is remembered");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
