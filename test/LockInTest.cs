// "Lock in" (hotkey): another app playing sound is turned down to 50 % while locked in, the game keeps 100 %, and
// everything is put back after. Plays a WAV in a separate PowerShell (the "other app"). Usage: run.ps1 LockInTest
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using StreamLite.Interop;

class LockInTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static float Vol(string proc)
    {
        float v = -1;
        LockIn.ForEachSession((inst, name, vol) => { float x; if (name != null && name.Equals(proc, StringComparison.OrdinalIgnoreCase) && vol.GetMasterVolume(out x) == 0) v = x; });
        return v;
    }

    static void Main()
    {
        string wav = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\rings_test.wav"));
        var other = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"(New-Object Media.SoundPlayer '" + wav + "').PlayLooping(); Start-Sleep 30\"") { CreateNoWindow = true, UseShellExecute = false });
        try
        {
            for (int i = 0; i < 20 && Vol("powershell") < 0; i++) Thread.Sleep(250);
            float before = Vol("powershell");
            Check(before > 0, "the other app plays (volume " + before.ToString("0.00") + ")");
            LockIn.Engage("SomeGame", 50); Thread.Sleep(600);
            float during = Vol("powershell");
            Check(Math.Abs(during - before * 0.5f) < 0.02f, "locked in: other app at " + during.ToString("0.00"));
            Check(LockIn.Active, "Active while locked in");
            LockIn.Release(); Thread.Sleep(200);
            float after = Vol("powershell");
            Check(Math.Abs(after - before) < 0.02f, "released: back at " + after.ToString("0.00"));

            LockIn.Engage("powershell", 50); Thread.Sleep(600);
            Check(Math.Abs(Vol("powershell") - before) < 0.02f, "the game itself keeps its volume");
            LockIn.Release();
        }
        finally { try { other.Kill(); } catch { } }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
