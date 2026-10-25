// Multi-mic calibration end to end, offline: two people at neighbouring desks (each mic hears the other person at
// -4 / -6 dB, person 2 talks 8 dB quieter), the calibration run through the real mixer (quiet, person 1, person 2),
// then: bleed and levels measured right, volumes evened out, and with the measured bleed the auto mix keeps one voice
// from doubling far better than the plain auto mix - while two people talking at once both stay in.
// Usage: run.ps1 MicCalibrationTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;

class MicCalibrationTest
{
    const int Fs = 48000, Pk = 144, Extra = 480;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Voice(int len, double f0, double amp, int seed)
    {
        var x = new float[len]; var r = new Random(seed);
        for (int i = 0; i < len; i++)
        {
            double t = (double)i / Fs, env = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 2.7 * t + seed);
            double v = 0; for (int k = 1; k < 10; k++) v += Math.Sin(2 * Math.PI * k * f0 * t + k * seed) / k;
            x[i] = (float)(amp * env * v);
        }
        return x;
    }

    /// <summary>Runs the two mic signals through a mixer; per packet: hook(packet index) before mixing, shares after.</summary>
    static void Run(MicMixer mixer, ExtraMic extra, float[] micA, float[] micB, Action<int> before, Action<int, float, float> after)
    {
        int fed = 0; var pkt = new float[Extra]; var buf = new float[Pk];
        for (int p = 0, pos = 0; pos + Pk <= micA.Length; p++, pos += Pk)
        {
            while (fed + Extra <= Math.Min(micB.Length, pos + Pk + Extra)) { Array.Copy(micB, fed, pkt, 0, Extra); extra.Feed(pkt, Extra); fed += Extra; }
            before(pos);
            Array.Copy(micA, pos, buf, 0, Pk);
            mixer.Mix(buf, Pk);
            after(pos, mixer.MainShare, extra.Share);
        }
    }

    static void Main()
    {
        double rAB = Math.Pow(10, -4 / 10.0), rBA = Math.Pow(10, -6 / 10.0);   // power ratios
        int quiet = 3 * Fs, talk = 6 * Fs, total = quiet + 2 * talk + 6 * Fs;      // + 6 s: both talk at once
        var a = Voice(total, 120, 0.15, 1); var b = Voice(total, 190, 0.15 * Math.Pow(10, -8 / 20.0), 2);
        var rnd = new Random(5);
        var micA = new float[total]; var micB = new float[total];
        int lag = Fs * 4 / 1000;
        Func<int, bool> aTalks = i => (i >= quiet && i < quiet + talk) || i >= quiet + 2 * talk;
        Func<int, bool> bTalks = i => i >= quiet + talk;
        for (int i = 0; i < total; i++)
        {
            float va = aTalks(i) ? a[i] : 0, vb = bTalks(i) ? b[i] : 0;
            float va2 = i >= lag && aTalks(i - lag) ? a[i - lag] : 0, vb2 = i >= lag && bTalks(i - lag) ? b[i - lag] : 0;
            micA[i] = va + (float)Math.Sqrt(rBA) * vb2 + (float)(0.001 * (rnd.NextDouble() * 2 - 1));
            micB[i] = vb + (float)Math.Sqrt(rAB) * va2 + (float)(0.001 * (rnd.NextDouble() * 2 - 1));
        }

        // 1. Calibration through the real mixer.
        var mixer = new MicMixer(); var extra = new ExtraMic("mic-b", 0); mixer.Use(extra); mixer.MainId = "mic-a";
        var cal = new MicCalibration(2);
        mixer.PowerTap = cal.Add;
        Run(mixer, extra, micA, micB, pos =>
        {
            // Settle 0.3 s into each part before measuring (as the dialog's gaps do).
            int s = Fs * 3 / 10;
            cal.Phase = pos >= s && pos < quiet ? 0 : pos >= quiet + s && pos < quiet + talk ? 1 : pos >= quiet + talk + s && pos < quiet + 2 * talk ? 2 : -1;
        }, (pos, sa, sb) => { });
        mixer.PowerTap = null;
        var r = cal.Analyze();
        Console.WriteLine("  measured: voice " + r.VoiceDb[0].ToString("0.0") + " / " + r.VoiceDb[1].ToString("0.0") + " dB, bleed 1->2 " + r.BleedDb[0, 1].ToString("0.0") + " dB, 2->1 " + r.BleedDb[1, 0].ToString("0.0") +
            " dB, volumes " + r.GainDb[0] + " / " + r.GainDb[1] + " dB, doubling " + r.DoublingBeforeDb.ToString("0") + " -> " + r.DoublingAfterDb.ToString("0") + " dB, together " + (r.TogetherShare * 100).ToString("0") + "%");
        Check(Math.Abs(r.BleedDb[0, 1] - -4) < 1.5 && Math.Abs(r.BleedDb[1, 0] - -6) < 1.5, "measures how much each mic hears the other person (-4 / -6 dB)");
        Check(Math.Abs((r.VoiceDb[0] - r.VoiceDb[1]) - 8) < 1.5, "measures that person 2 talks 8 dB quieter");
        Check(Math.Abs((r.GainDb[1] - r.GainDb[0]) - 8) <= 1, "volumes even the two voices out (+" + (r.GainDb[1] - r.GainDb[0]) + " dB for person 2)");
        Check(r.Problems.Count == 0, "no problems reported" + (r.Problems.Count > 0 ? ": " + r.Problems[0] : ""));
        Check(r.DoublingAfterDb < r.DoublingBeforeDb - 10 && r.DoublingAfterDb < -20, "predicted doubling drops (" + r.DoublingBeforeDb.ToString("0") + " -> " + r.DoublingAfterDb.ToString("0") + " dB)");

        // 2. Measured in the mix: plain auto mix vs calibrated, same volumes.
        foreach (bool calibrated in new[] { false, true })
        {
            var m = new MicMixer(); var x = new ExtraMic("mic-b", (float)r.GainDb[1]); m.Use(x); m.MainId = "mic-a"; m.MainGainDb = (float)r.GainDb[0];
            if (calibrated) m.Bleed = new Dictionary<string, Dictionary<string, float>> { { "mic-a", new Dictionary<string, float> { { "mic-b", r.BleedRatio[0, 1] } } }, { "mic-b", new Dictionary<string, float> { { "mic-a", r.BleedRatio[1, 0] } } } };
            var aOnly = new List<double>(); var bOnly = new List<double>(); var both = new List<double>();
            double ga = Math.Pow(10, r.GainDb[0] / 20), gb = Math.Pow(10, r.GainDb[1] / 20);
            Run(m, x, micA, micB, pos => { }, (pos, sa, sb) =>
            {
                int s = Fs / 2;
                // The doubled copy against the voice: other mic's share x its gain x the bleed, over the talker's.
                if (pos >= quiet + s && pos < quiet + talk) aOnly.Add(sb * gb * Math.Sqrt(rAB) / Math.Max(1e-6, sa * ga));
                if (pos >= quiet + talk + s && pos < quiet + 2 * talk) bOnly.Add(sa * ga * Math.Sqrt(rBA) / Math.Max(1e-6, sb * gb));
                if (pos >= quiet + 2 * talk + s) both.Add(Math.Min(sa, sb));
            });
            double dA = 20 * Math.Log10(aOnly.Average()), dB = 20 * Math.Log10(bOnly.Average()), keep = both.Average();
            Console.WriteLine("  " + (calibrated ? "calibrated" : "plain     ") + ": person 1 doubled at " + dA.ToString("0") + " dB, person 2 at " + dB.ToString("0") + " dB, both talking: the quieter share " + (keep * 100).ToString("0") + "%");
            if (calibrated)
            {
                Check(Math.Max(dA, dB) < -20, "calibrated: one voice is never doubled louder than -20 dB");
                Check(keep > 0.25, "calibrated: two people talking at once both stay in the mix (" + (keep * 100).ToString("0") + "% each at least)");
            }
            else Check(Math.Max(dA, dB) > -15, "plain auto mix doubles noticeably with mics this close (" + Math.Max(dA, dB).ToString("0") + " dB) - what calibration fixes");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
