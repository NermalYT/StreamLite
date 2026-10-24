using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Audio;

// Game profile checks: built-ins are sane, no game is claimed by two profiles, and upgrading saved profiles adds the
// new games/profiles without overwriting EQ edits. Runs against a copy of the real saved profiles too.
class ProfileTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

    static ProfileSettings Roundtrip(ProfileSettings p) { var js = new JavaScriptSerializer(); return js.Deserialize<ProfileSettings>(js.Serialize(p)); }

    static void Main()
    {
        var b = GameProfiles.Builtins();
        Console.WriteLine(b.Count + " built-in profiles, " + b.Sum(p => p.Exes.Count) + " games");
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<string>();
        foreach (var p in b) foreach (var x in p.Exes) { string o; if (seen.TryGetValue(x, out o)) dupes.Add(x + " (" + o + " / " + p.Name + ")"); else seen[x] = p.Name; }
        Check(dupes.Count == 0, "no game in two profiles" + (dupes.Count > 0 ? ": " + string.Join(", ", dupes) : ""));
        Check(b.All(p => p.Exes.All(x => !x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))), "process names without .exe");
        Check(b.Last().Name == GameProfiles.Movies, "Movies is the fallback at the end");
        foreach (var p in b)
        {
            var f = p.Eq.AllFilters();
            bool ok = f.All(x => x.Freq >= 20 && x.Freq <= 20000 && Math.Abs(x.Gain) <= 6 && x.Q > 0.3 && x.Q <= 5);
            double pre = p.Eq.EffectivePreamp(48000);
            // Peak boost of layer + correction must stay inside the auto preamp.
            var withCorr = p.Eq.Clone(); withCorr.Extra.AddRange(GameProfiles.Momentum5Correction());
            double preC = withCorr.EffectivePreamp(48000);
            if (!ok || pre > 0.01 || preC > 0.01) Check(false, p.Name + ": filter ranges/preamp");
        }
        Check(true, "all layers within +-6 dB, Q 0.3-5, auto preamp never boosts");

        // Fresh install.
        var fresh = new ProfileSettings();
        GameProfiles.Merge(fresh, null);
        Check(fresh.Profiles.Count == b.Count && fresh.Profiles.All(p => p.Version == GameProfiles.BuiltinVersion && p.EqSignature == GameProfiles.Signature(p.Eq)), "fresh install gets every profile, current version");

        // Upgrade from version 1: one untouched profile, one edited, one with an edited mode, plus a user profile.
        var v1 = Roundtrip(fresh);
        foreach (var p in v1.Profiles) { p.Version = 1; p.EqSignature = null; }
        v1.Profiles.RemoveAll(p => p.Name == "Horror & co-op" || p.Name == "Party & casual");
        var cod = v1.Profiles.First(p => p.Name == "Call of Duty"); cod.Exes.RemoveAll(x => x.StartsWith("t6")); cod.Exes.Add("mycustomcod");
        var cs = v1.Profiles.First(p => p.Name == "Counter-Strike 2"); cs.Eq.Parametric[1].Gain = 4.5;
        var val = v1.Profiles.First(p => p.Name == "Valorant"); val.Eq.Mode = "graphic";
        v1.Profiles.Insert(0, new GameProfile { Name = "Mine", Exes = new List<string> { "foo" } });
        var up = Roundtrip(v1);
        GameProfiles.Merge(up, null);
        Check(up.Profiles.Any(p => p.Name == "Horror & co-op") && up.Profiles.Any(p => p.Name == "Party & casual"), "new genre profiles added");
        var cod2 = up.Profiles.First(p => p.Name == "Call of Duty");
        Check(cod2.Exes.Contains("t6mp") && cod2.Exes.Contains("mycustomcod"), "new games added to an existing profile, custom ones kept");
        Check(up.Profiles.First(p => p.Name == "Counter-Strike 2").Eq.Parametric[1].Gain == 4.5, "an edited EQ is kept");
        Check(up.Profiles.First(p => p.Name == "Valorant").Eq.Mode == "graphic", "an edited EQ mode is kept");
        Check(up.Profiles.First(p => p.Name == "Fortnite").EqSignature == GameProfiles.Signature(b.First(p => p.Name == "Fortnite").Eq), "untouched profile is tracked as built-in");
        Check(up.Profiles.All(p => p.Name == "Mine" || p.Version == GameProfiles.BuiltinVersion), "all built-ins at the new version");
        Check(up.Profiles.Last().Name == "Mine" && up.Profiles[up.Profiles.Count - 2].Name == GameProfiles.Movies, "order: built-ins, Movies, then your own");
        // Second merge changes nothing.
        string before = new JavaScriptSerializer().Serialize(up);
        GameProfiles.Merge(up, null);
        Check(new JavaScriptSerializer().Serialize(up) == before, "merging again is a no-op");

        // The real saved profiles (copy).
        var real = AppSettings.Load();
        var mine = Roundtrip(real.Profiles);
        int had = mine.Profiles.Count;
        GameProfiles.Merge(mine, null);
        Console.WriteLine("  your profiles: " + had + " -> " + mine.Profiles.Count + " (" + string.Join(", ", mine.Profiles.Select(p => p.Name)) + ")");
        Check(mine.Profiles.Count >= b.Count, "your saved profiles upgrade cleanly");
        Check(!real.Profiles.Correction || (mine.CorrectionFilters != null && mine.CorrectionFilters.Count > 0), "an existing correction is kept (" + (mine.CorrectionName ?? "none") + ")");
        var mineDev = Roundtrip(real.Profiles);
        if (mineDev.CorrectionFilters != null && mineDev.Corrections.Count == 0)
            Check(mineDev.CorrectionFor("{your-headphones}") != null, "your correction moves to your headphones");

        // Chat preset and which profile wins.
        var chat = b.FirstOrDefault(p => p.Name == GameProfiles.Chat);
        Check(chat != null && chat.ChatApp && !chat.WhilePlaying && chat.ThreeD == "game"
            && new[] { "Discord", "Zoom", "ts3client_win64", "TeamSpeak", "mumble", "ms-teams" }.All(x => chat.Exes.Contains(x)), "Chat preset: Discord, Zoom, Teams, TeamSpeak, Mumble... straight to the headphones");
        Check(fresh.Profiles.FindIndex(p => p.Name == GameProfiles.Chat) < fresh.Profiles.FindIndex(p => p.Name == GameProfiles.Movies), "Chat comes before the Movies fallback");
        Check(!fresh.Correction && fresh.CorrectionFilters == null && fresh.ActiveCorrection("{headset}") == null, "fresh install: no headphone correction until you pick your headphones");

        // Corrections per device: each headset keeps its own.
        var old = new ProfileSettings { Correction = true, CorrectionName = "Old one", CorrectionFilters = GameProfiles.Momentum5Correction() };
        old = Roundtrip(old);
        var a = old.ActiveCorrection("{headphones-a}");
        Check(a != null && a.Name == "Old one", "the single correction from before moves to the headphones in use");
        Check(old.ActiveCorrection("{earbuds-b}") == null, "...and doesn't follow you to other headphones");
        old.SetCorrection("{earbuds-b}", "Earbuds", new List<EqFilter> { new EqFilter { Type = "PK", Freq = 3000, Gain = -2, Q = 1 } });
        var back = Roundtrip(old);
        Check(back.ActiveCorrection("{headphones-a}").Name == "Old one" && back.ActiveCorrection("{earbuds-b}").Name == "Earbuds"
            && back.ActiveCorrection("{earbuds-b}").Filters.Count == 1, "two devices, two corrections (kept through saving)");
        back.CorrectionFor("{earbuds-b}").On = false;
        Check(back.ActiveCorrection("{earbuds-b}") == null && back.ActiveCorrection("{headphones-a}") != null, "switching one off leaves the other on");
        Check(back.ActiveCorrection(null) == null && back.ActiveCorrection("") == null, "no device -> no correction");
        var withMine = Roundtrip(fresh); withMine.Profiles.Add(new GameProfile { Name = "Mine", Exes = new List<string> { "mygame" } });
        Func<string[], string[], string> pick = (run, play) =>
        {
            string exe;
            var p = GameProfiles.Pick(withMine.Profiles, x => run.Contains(x, StringComparer.OrdinalIgnoreCase), x => play.Contains(x, StringComparer.OrdinalIgnoreCase), out exe);
            return p == null ? "(default)" : p.Name;
        };
        Check(pick(new[] { "Discord" }, new string[0]) == GameProfiles.Chat, "Discord open -> Chat");
        Check(pick(new[] { "Zoom" }, new string[0]) == GameProfiles.Chat, "Zoom open -> Chat");
        Check(pick(new[] { "Discord", "cs2" }, new string[0]) == "Counter-Strike 2", "Discord + CS2 -> the game wins");
        Check(pick(new[] { "Discord", "mygame" }, new string[0]) == "Mine", "Discord + your own game profile -> your profile wins");
        Check(pick(new[] { "Discord", "Spotify" }, new[] { "Spotify" }) == GameProfiles.Music, "Discord + Spotify playing -> Music");
        Check(pick(new[] { "Discord", "Spotify" }, new string[0]) == GameProfiles.Chat, "Discord + Spotify paused -> Chat");
        Check(pick(new string[0], new string[0]) == "(default)", "nothing running -> default profile");

        // Auto EQ apps: add, move and remove apps; updates don't undo your changes.
        Check(GameProfiles.NormalizeExe(@" C:\Games\My Game\Game.EXE ") == "Game" && GameProfiles.NormalizeExe(" .exe") == null, "program names: path and .exe dropped");
        var ed = Roundtrip(fresh);
        Func<string, GameProfile> prof = n => ed.Profiles.First(p => p.Name == n);
        Func<string[], string[], string> pickEd = (run, play) =>
        {
            string exe;
            var p = GameProfiles.Pick(ed.Profiles, x => run.Contains(x, StringComparer.OrdinalIgnoreCase), x => play.Contains(x, StringComparer.OrdinalIgnoreCase), out exe);
            return p == null ? "(default)" : p.Name;
        };
        GameProfiles.AddApp(ed, prof(GameProfiles.Movies), "vlc.exe");
        Check(prof(GameProfiles.Movies).Exes.Contains("vlc") && pickEd(new[] { "vlc" }, new string[0]) == GameProfiles.Movies, "an added app switches to its profile (vlc -> Movies & videos)");
        var from = GameProfiles.AddApp(ed, prof(GameProfiles.Chat), "spotify");
        Check(from != null && from.Name == GameProfiles.Music && !prof(GameProfiles.Music).Exes.Contains("Spotify", StringComparer.OrdinalIgnoreCase)
            && pickEd(new[] { "Spotify" }, new string[0]) == GameProfiles.Chat, "adding an app to another profile moves it (Spotify -> Chat)");
        GameProfiles.RemoveApp(prof("Counter-Strike 2"), "cs2");
        Check(pickEd(new[] { "cs2" }, new string[0]) == "(default)", "a removed app no longer switches the EQ");
        foreach (var p in ed.Profiles.Where(p => p.Builtin)) p.Version = 1;   // pretend an update arrives
        ed = Roundtrip(ed);
        GameProfiles.Merge(ed, null);
        Check(!prof("Counter-Strike 2").Exes.Contains("cs2") && !prof(GameProfiles.Music).Exes.Contains("Spotify", StringComparer.OrdinalIgnoreCase)
            && prof(GameProfiles.Chat).Exes.Contains("spotify") && prof(GameProfiles.Movies).Exes.Contains("vlc"), "updates keep your Auto EQ app changes");
        GameProfiles.AddApp(ed, prof("Counter-Strike 2"), "cs2");
        Check(prof("Counter-Strike 2").Exes.Contains("cs2") && !prof("Counter-Strike 2").RemovedExes.Contains("cs2"), "adding an app back undoes its removal");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
