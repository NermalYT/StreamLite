// Uninstalling, your settings (4.4): keep all, save what you pick (StreamLite's folder or anywhere), delete all. The files
// that stay and go, on a test folder (STREAMLITE_DATA). The question itself is pictured by UiShots. Usage: run.ps1 UninstallSettingsTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.UI;

class UninstallSettingsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Fill(string dir)
    {
        Directory.CreateDirectory(dir);
        var s = AppSettings.Defaults(); s.Strength = 33; s.VoiceSetupDone = true; s.Save();
        File.WriteAllText(Path.Combine(dir, "health.log"), "log");
        File.WriteAllText(Path.Combine(dir, "voice-lock-you.bin"), "print");
    }

    [STAThread]
    static void Main()
    {
        string dir = AppSettings.Folder;
        Check(dir.IndexOf("StreamLiteTest", StringComparison.OrdinalIgnoreCase) >= 0, "runs on a test folder, not your settings: " + dir);
        if (fails > 0) { Console.WriteLine("1 FAILED"); return; }
        Action<string> del = f => { try { Directory.Delete(f, true); } catch { } };
        string outside = Path.Combine(Path.GetTempPath(), "sl-uninstall-" + Guid.NewGuid().ToString("N") + ".streamlite");

        Console.WriteLine("Keep all");
        Fill(dir);
        UninstallSettings.Finish(new UninstallSettingsPlan(), _ => { }, del);
        Check(File.Exists(Path.Combine(dir, "settings.json")), "nothing goes");

        Console.WriteLine("Save what you pick, in StreamLite's folder");
        Fill(dir);
        var s0 = AppSettings.Load();
        var parts = SetupParts.From(s0, "Mic", id => null).Where(p => p.Id == "mic.noise" || p.Id == "app").ToList();
        string inside = Path.Combine(dir, "StreamLite settings test" + SetupExport.Extension);
        UninstallSettings.Save(parts, inside);
        UninstallSettings.Finish(new UninstallSettingsPlan { Delete = true, Saved = inside }, _ => { }, del);
        Check(File.Exists(inside), "the saved file stays");
        Check(Directory.GetFiles(dir).Length == 1, "everything else in the folder goes: " + string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
        var back = SetupParts.Of(SetupExport.Parse(File.ReadAllText(inside)));
        Check(back.Count == 2 && back.Any(p => p.Id == "mic.noise"), "it holds exactly what was ticked");
        var applied = AppSettings.Defaults();
        SetupParts.Apply(applied, back, p => null, null, id => null);
        Check(applied.Strength == 33, "and brings the setting back");
        Check(SettingsTransfer.Scan().Any(f => string.Equals(f.Path, inside, StringComparison.OrdinalIgnoreCase)), "Scan for settings finds it");

        Console.WriteLine("Save what you pick, anywhere else");
        Fill(dir);
        UninstallSettings.Save(parts, outside);
        UninstallSettings.Finish(new UninstallSettingsPlan { Delete = true, Saved = outside }, _ => { }, del);
        Check(File.Exists(outside), "the exported file stays");
        Check(!File.Exists(Path.Combine(dir, "settings.json")), "the settings folder goes");
        try { File.Delete(outside); } catch { }

        Console.WriteLine("Delete all");
        Fill(dir);
        UninstallSettings.Finish(new UninstallSettingsPlan { Delete = true }, _ => { }, del);
        Check(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0, "nothing stays");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
