// Prints what "Copy details" would put on the clipboard (devices, formats, status) and checks the Windows user name
// isn't in it. Reads this PC's devices and StreamLite's real status.txt; changes nothing.
// Usage: run.ps1 DetailsProbe
using System;
using StreamLite;

class DetailsProbe
{
    static void Main()
    {
        var core = new AppCore(true);
        string r = Diagnostics.Report(core);
        Console.WriteLine(r);
        bool clean = r.IndexOf("\\" + Environment.UserName + "\\", StringComparison.OrdinalIgnoreCase) < 0;
        Console.WriteLine((clean ? "PASS" : "FAIL") + " no user name in file paths");
        Console.WriteLine(r.Contains("Audio devices") && r.Contains("StreamLite " + Program.Version) && clean ? "ALL PASS" : "FAILED");
    }
}
