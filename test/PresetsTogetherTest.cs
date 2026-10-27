using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// Presets together (4.0.7): with CS2 running and Spotify playing, Auto EQ picks Music for everything and CS2 for the
// game's sound at the same time; the game layer (Music undone, then CS2) on top of Music adds up to exactly CS2 on
// the game's sound. Usage: run.ps1 PresetsTogetherTest
class PresetsTogetherTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        var ps = new ProfileSettings();
        GameProfiles.Merge(ps, null);
        var cs2 = ps.Profiles.First(p => p.Exes.Any(x => x.Equals("cs2", StringComparison.OrdinalIgnoreCase)));
        var music = ps.Profiles.First(p => p.Name == GameProfiles.Music);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs2", "Spotify" };
        Func<string, bool> run = x => running.Contains(x), play = x => true;

        var one = GameProfiles.Decide(ps, run, play);
        Check(one.Profile == cs2, "off: one preset at a time - the game wins (" + one.Profile.Name + ")");
        var apps = GameProfiles.Decide(ps, run, play, p => !GameProfiles.IsGame(p));
        var game = GameProfiles.Decide(ps, run, play, GameProfiles.IsGame);
        Check(apps.Profile == music && game.Profile == cs2, "together: " + apps.Profile.Name + " for everything, " + game.Profile.Name + " on the game's sound");
        running.Remove("cs2");
        Check(GameProfiles.Decide(ps, run, play, GameProfiles.IsGame).Profile == null, "no game running: no game layer");

        // Music on everything (Equalizer APO / the headphones' EQ) + the layer on the game's sound = CS2's preset.
        Func<GameProfile, List<EqFilter>> layerOf = p => { var e = p.Eq.Clone(); e.Extra.Clear(); return e.AllFilters(); };
        var layer = GameProfiles.GameLayer(cs2, music);
        Check(layer != null && layer.AllFilters().Count > 0, "the game layer: " + (layer == null ? 0 : layer.AllFilters().Count) + " filters");
        double worst = 0;
        for (double f = 20; f <= 20000; f *= 1.05)
        {
            double heard = Biquad.ResponseDb(layerOf(music), f, 48000) + Biquad.ResponseDb(layer.AllFilters(), f, 48000);
            worst = Math.Max(worst, Math.Abs(heard - Biquad.ResponseDb(layerOf(cs2), f, 48000)));
        }
        Check(worst < 0.01, "game sound hears exactly CS2's preset (largest difference " + worst.ToString("0.0000") + " dB, 20 Hz - 20 kHz)");
        Check(GameProfiles.GameLayer(music, music) == null, "the same preset on both: no layer");
        Console.WriteLine(fails == 0 ? "PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
