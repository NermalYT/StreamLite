using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamLite;
using StreamLite.Audio;

// "Natural sound for videos and films": the 3D settings actually used follow the active profile. Runs on a copy of
// the real settings (STREAMLITE_DATA) and starts no audio.
class NaturalTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

    static SpatialConfig Used(AppCore core)
    {
        return (SpatialConfig)typeof(SpatialProcessor).GetField("cfg", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(core.Game.Processor);
    }

    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-naturaltest");
        Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);

        var core = new AppCore();
        var g = core.S.Game;
        g.NaturalWhenNoGame = true; g.FootstepBoost = 75; g.CueSharpen = 35; g.LoudTamer = 45;
        g.DirGain["behind"] = 2;

        core.Profiles.ActiveName = GameProfiles.Movies;
        core.PushGameParams();
        var c = Used(core);
        Check(core.NaturalSound, "Movies & videos -> natural");
        Check(c.FootstepBoost == 0 && c.CueSharpen == 0 && c.LoudTamer == 0, "footstep boost, cue sharpening and loud-sound tamer off");
        Check(SpatialProcessor.Dirs.All(d => !c.DirEq[d].Enabled && c.DirGainDb[d] == 0), "directional EQ off");
        Check(g.FootstepBoost == 75 && g.DirGain["behind"] == 2 && g.DirEq["front"].Enabled, "your saved 3D settings untouched");

        core.Profiles.ActiveName = "Counter-Strike 2";
        core.PushGameParams();
        c = Used(core);
        Check(!core.NaturalSound && Math.Abs(c.FootstepBoost - 0.75) < 1e-9 && Math.Abs(c.CueSharpen - 0.35) < 1e-9 && c.DirGainDb["behind"] == 2, "competitive game -> your full 3D settings");

        core.Profiles.ActiveName = "Story & open world";
        core.PushGameParams();
        Check(core.NaturalSound, "story game -> natural");

        g.NaturalWhenNoGame = false;
        core.Profiles.ActiveName = GameProfiles.Movies;
        core.PushGameParams();
        Check(!core.NaturalSound && Math.Abs(Used(core).FootstepBoost - 0.75) < 1e-9, "switch off -> full settings everywhere");

        g.NaturalWhenNoGame = true;
        core.Game.Processor.StartTest();
        core.PushGameParams();
        Check(!core.NaturalSound && Math.Abs(Used(core).FootstepBoost - 0.75) < 1e-9, "footstep test always uses your game settings");
        core.Game.Processor.StopTest();

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
