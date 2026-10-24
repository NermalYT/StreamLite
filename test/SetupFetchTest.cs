using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using StreamLite.Audio;
using StreamLite.Interop;

// What a friend's PC downloads on first use, without installing anything: the VB-CABLE, Hi-Fi Cable and Equalizer APO
// setups (must be signed by their makers) and an AutoEq headphone correction.
class SetupFetchTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static string Signer(string file)
    {
        try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(file)).GetNameInfo(X509NameType.SimpleName, false); }
        catch { return null; }
    }

    static void Main()
    {
        foreach (var t in new[] { new[] { DriverInstaller.VbCableZip, "VBCABLE_Setup*.exe", "VBCABLE" }, new[] { DriverInstaller.HiFiCableZip, "HiFiCableAsioBridgeSetup*.exe", "HiFiCable" } })
        {
            string setup = null; string err = null;
            try { setup = DriverInstaller.FetchVbAudio(t[0], t[1], t[2] + "-test"); } catch (Exception ex) { err = ex.Message; }
            string who = setup != null ? Signer(setup) : null;
            Check(setup != null && who != null && who.IndexOf("Burel", StringComparison.OrdinalIgnoreCase) >= 0,   // VB-Audio's developer
                t[2] + ": " + (setup != null ? Path.GetFileName(setup) + ", signed by " + (who ?? "nobody") : "failed: " + err));
        }
        {
            string exe = null, err = null;
            try { exe = DriverInstaller.FetchEqualizerApo(); } catch (Exception ex) { err = ex.Message; }
            string who = exe != null ? Signer(exe) : null;
            Check(exe != null && new FileInfo(exe).Length > 5000000, "Equalizer APO: " + (exe != null ? (new FileInfo(exe).Length / 1048576.0).ToString("0.0") + " MB installer" + (who != null ? ", signed by " + who : ", unsigned (Equalizer APO isn't code-signed)") : "failed: " + err));
        }

        string cache = Path.Combine(Path.GetTempPath(), "StreamLite-autoeq-test");
        var index = AutoEq.Index(cache);
        Check(index.Count > 5000, "AutoEq index: " + index.Count + " headphones");
        Check(AutoEq.GuessQuery("Headphones (HyperX Cloud II Wireless)") == "hyperx cloud ii wireless", "device name -> search: \"" + AutoEq.GuessQuery("Headphones (HyperX Cloud II Wireless)") + "\"");
        Check(AutoEq.GuessQuery("Speakers (Realtek(R) Audio)") == "", "generic device name -> empty search: \"" + AutoEq.GuessQuery("Speakers (Realtek(R) Audio)") + "\"");
        var found = AutoEq.Search(index, "hyperx cloud ii");
        Check(found.Count > 0 && found[0].Name == "HyperX Cloud II", "search \"hyperx cloud ii\" -> " + (found.Count > 0 ? found[0].ToString() : "nothing"));
        double pre;
        var f = AutoEq.Download(found[0], out pre);
        Check(f.Count >= 5 && f.All(x => x.Freq > 10 && x.Freq < 24000 && Math.Abs(x.Gain) <= 20), "correction downloaded: " + f.Count + " filters, preamp " + pre.ToString("0.0") + " dB");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
