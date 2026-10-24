using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using StreamLite;

// The Music profile follows Spotify actually playing: a stand-in Spotify.exe that is open but silent keeps Movies,
// playing switches to Music, a short pause holds it, a long one goes back; a game wins over music. Runs on a copy of
// the real settings (STREAMLITE_DATA) with auto-switch on; starts no audio engine.
// Usage: MusicProfileTest <fake Spotify.exe> <fake game exe>
class MusicProfileTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

    static string Settle(AppCore core, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { core.Profiles.Check(false); Thread.Sleep(500); }
        core.Profiles.Check(false);
        return core.Profiles.ActiveName;
    }

    static void Main(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-musictest");
        Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore();
        core.S.Profiles.AutoSwitch = true;
        core.Profiles.Check(true);
        Check(core.Profiles.ActiveName == GameProfiles.Movies, "nothing playing: " + core.Profiles.ActiveName);

        using (var p = Process.Start(new ProcessStartInfo(args[0], "silent 8") { UseShellExecute = false, CreateNoWindow = true }))
        {
            string a = Settle(core, 4000);
            Check(a == GameProfiles.Movies, "Spotify open but silent: " + a);
            p.WaitForExit();
        }
        using (var p = Process.Start(new ProcessStartInfo(args[0], "tone 12 25") { UseShellExecute = false, CreateNoWindow = true }))
        {
            string a = Settle(core, 4000);
            Check(a == GameProfiles.Music, "Spotify playing: " + a);
            var g = Process.Start(new ProcessStartInfo(args[1], "1") { UseShellExecute = false, CreateNoWindow = true });
            string b = Settle(core, 3000);
            Check(b != GameProfiles.Music && b != GameProfiles.Movies, "game started while music plays -> game profile wins: " + b);
            try { g.Kill(); } catch { }
            string c = Settle(core, 3000);
            Check(c == GameProfiles.Music, "game closed, music still playing: " + c);
            while (p.StartTime.AddSeconds(12.5) > DateTime.Now) Thread.Sleep(100);   // song over, player still open
            string held = Settle(core, 5000);
            Check(held == GameProfiles.Music, "5 s after the song stopped (pause between songs): still " + held);
            string back = Settle(core, 13000);
            Check(back == GameProfiles.Movies, "18 s after, player open but paused: back to " + back);
            try { p.Kill(); } catch { }
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures);
    }
}
