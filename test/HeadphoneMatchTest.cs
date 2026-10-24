// "Find my headphones" against the real AutoEq index: device names the way Windows, PipeWire and Bluetooth show them
// -> the first suggestion. Needs the internet once (the index is cached in %TEMP%\StreamLite-matchtest).
// Usage: run.ps1 HeadphoneMatchTest
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamLite.Audio;

class HeadphoneMatchTest
{
    static void Main()
    {
        string cache = Path.Combine(Path.GetTempPath(), "StreamLite-matchtest");
        var index = AutoEq.Index(cache);
        Console.WriteLine(index.Count + " measured models");
        // device name, what the first suggestion must match ("" = no search at all: nothing model-like in the name)
        string[][] cases =
        {
            new[] { "Headphones (HyperX Cloud II Wireless)", "HyperX Cloud II" },
            new[] { "Headset Earphone (HyperX Cloud Alpha Wireless)", "HyperX Cloud Alpha" },
            new[] { "Headphones (2- Arctis Nova 7)", "Arctis Nova 7" },
            new[] { "Speakers (Arctis 7 Game)", "Arctis 7" },
            new[] { "Headphones (Sam's AirPods Pro)", "AirPods Pro" },
            new[] { "Headphones (Sam’s AirPods Max)", "AirPods Max" },
            new[] { "Headset (WH-1000XM4 Hands-Free AG Audio)", "WH-1000XM4" },
            new[] { "Headphones (WH-1000XM5)", "WH-1000XM5" },
            new[] { "Speakers (Logitech PRO X Gaming Headset)", "Pro X" },
            new[] { "Speakers (Logitech G733 Gaming Headset)", "G733" },
            new[] { "Headphones (Galaxy Buds2 Pro)", "Galaxy Buds ?2 Pro" },
            new[] { "Speakers (Razer BlackShark V2 Pro)", "BlackShark V2" },
            new[] { "Headphones (Bose QC35 II)", "(QC|QuietComfort) ?35" },
            new[] { "Headphones (Bose QC45)", "(QC|QuietComfort) ?45" },
            new[] { "Speakers (Corsair HS80 RGB Wireless Gaming Headset)", "HS80" },
            new[] { "Headphones (Jabra Elite 85t)", "Elite 85t" },
            new[] { "Headphones (Beats Studio Buds)", "Studio Buds" },
            new[] { "Speakers (Astro A50 Game)", "A50" },
            new[] { "Headphones (Pixel Buds Pro)", "Pixel Buds Pro" },
            new[] { "Headphones (Nothing Ear (2))", "Nothing Ear" },
            new[] { "Kopfhörer (WH-1000XM4)", "WH-1000XM4" },
            new[] { "Headphones (Momentum 4)", "Momentum 4" },
            new[] { "Headphones (MOMENTUM 4 Wireless)", "Momentum 4" },
            new[] { "Headset (Galaxy Buds Live Hands-Free)", "Galaxy Buds Live" },
            new[] { "Headphones (Soundcore Life Q30)", "Life Q30" },
            new[] { "Speakers (SteelSeries Arctis Nova Pro Wireless)", "Arctis Nova Pro" },
            new[] { "Headphones (beyerdynamic DT 770 PRO)", "DT 770" },
            new[] { "Speakers (Audio-Technica ATH-M50x)", "M50x" },
            new[] { "Speakers (Realtek(R) Audio)", "" },
            new[] { "Speakers (USB Audio Device)", "" },
            // PipeWire / PulseAudio names on Linux
            new[] { "Sony WH-1000XM4", "WH-1000XM4" },
            new[] { "SteelSeries Arctis Nova 7 Analog Stereo", "Arctis Nova 7" },
            new[] { "HyperX Cloud II Wireless Digital Stereo (IEC958)", "HyperX Cloud II" },
            new[] { "Sam's AirPods Pro", "AirPods Pro" },
        };
        int ok = 0;
        foreach (var c in cases)
        {
            string q = AutoEq.GuessQuery(c[0]);
            string used; bool relaxed;
            var found = AutoEq.SearchBest(index, q, out used, out relaxed);
            string top = found.Count > 0 ? found[0].Name + " (" + found[0].Source + ")" : "-";
            bool pass = c[1] == "" ? q == "" : found.Count > 0 && Regex.IsMatch(found[0].Name, c[1], RegexOptions.IgnoreCase);
            if (pass) ok++;
            Console.WriteLine((pass ? "  PASS " : "  FAIL ") + c[0].PadRight(52) + " -> \"" + q + "\"" + (relaxed ? " (as \"" + used + "\")" : "") + " -> " + top);
        }
        Console.WriteLine(ok + " of " + cases.Length + " matched");
        Console.WriteLine(ok == cases.Length ? "ALL PASS" : (cases.Length - ok) + " FAILED");
    }
}
