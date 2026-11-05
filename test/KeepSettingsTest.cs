// Updating keeps your settings exactly (4.4): settings saved by an older version go through this version's load and save, and
// every value that was there comes out the same. New fields may appear; nothing that was there may change or go.
// With a path: that settings file, read only (run on a copy of real settings; nothing from it is printed but its field names).
// Usage: run.ps1 KeepSettingsTest [settings.json]
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Audio;

class KeepSettingsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    /// <summary>Every value in "was" that's different or missing in "now": their paths.</summary>
    static void Compare(object was, object now, string path, List<string> diffs)
    {
        var dw = was as IDictionary; var dn = now as IDictionary;
        if (dw != null)
        {
            if (dn == null) { diffs.Add(path + " (no longer an object)"); return; }
            foreach (DictionaryEntry e in dw)
            {
                string k = (string)e.Key;
                if (!dn.Contains(k)) { diffs.Add(path + "." + k + " (gone)"); continue; }
                Compare(e.Value, dn[k], path + "." + k, diffs);
            }
            return;
        }
        var lw = was as IList; var ln = now as IList;
        if (lw != null)
        {
            if (ln == null) { diffs.Add(path + " (no longer a list)"); return; }
            if (lw.Count != ln.Count) { diffs.Add(path + " (" + lw.Count + " items, now " + ln.Count + ")"); return; }
            for (int i = 0; i < lw.Count; i++) Compare(lw[i], ln[i], path + "[" + i + "]", diffs);
            return;
        }
        if (was == null && now == null) return;
        if (was == null || now == null) { diffs.Add(path + " (null changed)"); return; }
        if (IsNumber(was) && IsNumber(now))
        {
            if (Math.Abs(Convert.ToDouble(was) - Convert.ToDouble(now)) > 1e-6 * Math.Max(1, Math.Abs(Convert.ToDouble(was)))) diffs.Add(path);
            return;
        }
        if (!was.Equals(now)) diffs.Add(path);
    }

    static bool IsNumber(object o) { return o is int || o is long || o is double || o is decimal || o is float; }

    /// <summary>Saved, read and saved again by this version: what changed?</summary>
    static List<string> RoundTrip(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, json);
            var s = AppSettings.ReadFile(file);
            if (s == null) return new List<string> { "(it didn't load)" };
            string after = Js.Serialize(s);
            var diffs = new List<string>();
            Compare(Js.DeserializeObject(json), Js.DeserializeObject(after), "", diffs);
            return diffs;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            var d = RoundTrip(File.ReadAllText(args[0]));
            foreach (var x in d.Take(40)) Console.WriteLine("    changed: " + x);
            Check(d.Count == 0, "those settings come through this version exactly (" + d.Count + " values changed)");
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return;
        }
        // Someone who tuned everything, as this version's fields go: every field off its default.
        var s = AppSettings.Defaults();
        s.Strength = 37; s.NsVoiceProtect = 83; s.GateThreshold = -61; s.GateHold = 333; s.HighPassHz = 120; s.DeEsser = true; s.DeEssFreq = 5300; s.DeEssAmount = 7; s.CompRatio = 31;
        s.StudioStrength = 88; s.VoiceSetupDone = true; s.SetupDoneFor = "4.0.5"; s.WelcomeShown = true; s.LastVersion = "4.3.4"; s.StartMinimized = false; s.RunAsAdmin = false;
        s.Game.Hrtf = HrtfSet.Kemar; s.Game.CueSharpen = 58; s.Game.FloorContrast = 66; s.Game.HeadSize = 106; s.Game.Mode16D = "music"; s.Game.Music16D = true; s.Game.Music16DSeconds = 13; s.Game.Enabled = true;
        s.Game.FineTuned["ph|Sides"] = "Head size 106%"; s.Game.PersonalHeads["ph"] = "kemar|58|66|106";
        s.CamFx.AutoLight = true; s.CamFx.FaceExposure = true; s.CamFx.VAction = "draw"; s.CamFx.PenColor = 0x123456; s.CamFx.PenSize = 22; s.CamFx.Zoom = 155; s.CamFx.Hdr = 12; s.CamFx.Warmth = 7;
        s.OutputEq["ph"] = new EqProfile { Mode = "parametric", Parametric = new List<EqFilter> { new EqFilter { Type = "PK", Freq = 3100, Gain = -2.5, Q = 1.7 } } };
        s.Hotkeys.Add(new HotkeyBinding { Action = "cam.anim.flip", Key = 0x77, Mods = 1 });
        s.MicEq.Bands[3] = 2.5;
        var d0 = RoundTrip(Js.Serialize(s));
        foreach (var x in d0.Take(20)) Console.WriteLine("    changed: " + x);
        Check(d0.Count == 0, "tuned settings load and save without one value changing (" + d0.Count + ")");

        // A file from before this version's new fields: they just aren't there yet.
        var old = (Dictionary<string, object>)Js.DeserializeObject(Js.Serialize(s));
        foreach (var k in new[] { "AppEq", "AppEqPresets", "VoiceTests" }) old.Remove(k);
        var d1 = RoundTrip(Js.Serialize(old));
        Check(d1.Count == 0, "a file from 4.3.4, without 4.4's new fields, too (" + d1.Count + ")");
        var loaded = Js.Deserialize<AppSettings>(Js.Serialize(old));
        Check(loaded.AppEq != null && loaded.AppEq.Count == 0, "its new fields start empty");
        Check(!StreamLite.UI.RetuneWizard.Needed(s), "the setup isn't asked for again");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
