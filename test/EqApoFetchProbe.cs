// Downloads Equalizer APO the way the installer does (does not run it) and prints size + SHA-256. Usage: EqApoFetchProbe
using System;
using System.IO;
using System.Security.Cryptography;
using StreamLite.Interop;
class EqApoFetchProbe
{
    static void Main()
    {
        string f = DriverInstaller.FetchEqualizerApo();
        using (var s = File.OpenRead(f)) Console.WriteLine(f + "  " + new FileInfo(f).Length + " bytes  sha256 " + BitConverter.ToString(SHA256.Create().ComputeHash(s)).Replace("-", "").ToLowerInvariant());
    }
}
