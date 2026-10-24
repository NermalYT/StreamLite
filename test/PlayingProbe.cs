using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Interop;

// Checks "is this app making sound" detection (used by the Music profile): a fake Spotify.exe playing silence must not
// count, the same playing a quiet tone must. Usage: PlayingProbe <path to fake Spotify.exe>
class PlayingProbe
{
    static bool Seen(string exe, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (AudioSessions.Playing().Contains(exe)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static void Main(string[] args)
    {
        string fake = args[0];
        int fails = 0;
        var sw = Stopwatch.StartNew();
        var set = AudioSessions.Playing();
        Console.WriteLine("playing now: " + (set.Count == 0 ? "-" : string.Join(", ", set)) + " (" + sw.ElapsedMilliseconds + " ms)");
        foreach (var mode in new[] { "silent", "tone" })
        {
            using (var p = Process.Start(new ProcessStartInfo(fake, mode + " 5") { UseShellExecute = false, CreateNoWindow = true }))
            {
                bool seen = Seen(Path.GetFileNameWithoutExtension(fake), 4000);
                bool want = mode == "tone";
                Console.WriteLine((seen == want ? "  PASS " : "  FAIL ") + mode + ": " + (seen ? "detected as playing" : "not playing"));
                if (seen != want) fails++;
                p.WaitForExit(10000);
            }
        }
        // Cost of one check (it runs every 3 s while Spotify is open).
        sw.Restart(); for (int i = 0; i < 10; i++) AudioSessions.Playing();
        Console.WriteLine("  one check: " + (sw.ElapsedMilliseconds / 10.0).ToString("0.0") + " ms");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
