// Prints the installer's new-install mark StreamLite reads (FreshInstall.Mark: HKLM\Software\StreamLite FreshInstall on
// Windows) - for checking the MSI in the lab VM: a new install sets it, an upgrade or KEEPSETTINGS=1 doesn't.
// Usage: run.ps1 FreshMarkProbe   (or bin\FreshMarkProbe.exe copied anywhere)
using System;
using StreamLite;

class FreshMarkProbe
{
    static void Main() { Console.WriteLine("mark=[" + FreshInstall.Mark() + "]"); }
}
