// Two people, two mics, one virtual mic (MicMixer): each mic hears its own person plus the other one a desk away
// (-12 dB, 4 ms later) and its own noise. The main mic comes in 3 ms packets (exclusive), the second mic in 10 ms
// packets (shared) through its drift-corrected ring - like the app. Checks: whoever talks gets the mix, both can talk
// at once, idle noise of two open mics doesn't add up, no clicks, a silent / unplugged second mic changes nothing,
// and Auto mix off simply sums. Usage: run.ps1 MultiMicTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;

class MultiMicTest
{
    const int Fs = 48000, Pk = 144, Extra = 480;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Voice(int len, double f0, int seed)
    {
        var x = new float[len]; var r = new Random(seed);
        for (int i = 0; i < len; i++)
        {
            double t = (double)i / Fs, env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t);   // syllables
            double v = 0; for (int k = 1; k < 10; k++) v += Math.Sin(2 * Math.PI * k * f0 * t + k) / k;
            x[i] = (float)(0.15 * env * v);
        }
        return x;
    }

    static double Db(double p) { return 10 * Math.Log10(p + 1e-20); }

    static void Main()
    {
        int seg = 2 * Fs, total = 4 * seg;            // A talks, B talks, both, nobody
        var a = Voice(total, 120, 1); var b = Voice(total, 210, 2);
        var talkA = new bool[total]; var talkB = new bool[total];
        for (int i = 0; i < total; i++) { int s = i / seg; talkA[i] = s == 0 || s == 2; talkB[i] = s == 1 || s == 2; }
        var rnd = new Random(3);
        var micA = new float[total]; var micB = new float[total];
        int lag = Fs * 4 / 1000; float bleed = 0.25f;  // -12 dB
        for (int i = 0; i < total; i++)
        {
            float va = talkA[i] ? a[i] : 0, vb = talkB[i] ? b[i] : 0;
            float va2 = i >= lag && talkA[i - lag] ? a[i - lag] : 0, vb2 = i >= lag && talkB[i - lag] ? b[i - lag] : 0;
            micA[i] = va + bleed * vb2 + (float)(0.001 * (rnd.NextDouble() * 2 - 1));
            micB[i] = vb + bleed * va2 + (float)(0.001 * (rnd.NextDouble() * 2 - 1));
        }

        foreach (bool auto in new[] { true, false })
        {
            var mixer = new MicMixer { AutoMix = auto };
            var extra = new ExtraMic("test", 0);
            mixer.Use(extra);
            var output = new float[total]; var shareMain = new float[total / Pk]; var shareB = new float[total / Pk];
            int fed = 0; var pkt = new float[Extra]; var buf = new float[Pk];
            for (int p = 0, pos = 0; pos + Pk <= total; p++, pos += Pk)
            {
                // The second mic delivers 10 ms packets as the time comes (ahead of the reader by up to one packet).
                while (fed + Extra <= Math.Min(total, pos + Pk + Extra)) { Array.Copy(micB, fed, pkt, 0, Extra); extra.Feed(pkt, Extra); fed += Extra; }
                Array.Copy(micA, pos, buf, 0, Pk);
                mixer.Mix(buf, Pk);
                Array.Copy(buf, 0, output, pos, Pk);
                shareMain[p] = mixer.MainShare; shareB[p] = extra.Share;
            }
            Func<int, int, double> avgMain = (s0, s1) => shareMain.Skip(s0 / Pk).Take((s1 - s0) / Pk).Average();
            Func<int, int, double> avgB = (s0, s1) => shareB.Skip(s0 / Pk).Take((s1 - s0) / Pk).Average();
            Func<float[], int, int, double> pw = (x, s0, s1) => { double s = 0; for (int i = s0; i < s1; i++) s += x[i] * x[i]; return s / (s1 - s0); };
            int settle = Fs / 5;   // skip the first 200 ms of each part (the second mic's ring starts; voices take over)
            Console.WriteLine("== Auto mix " + (auto ? "on" : "off"));
            if (auto)
            {
                double m0 = avgMain(settle, seg), b0 = avgB(settle, seg), m1 = avgMain(seg + settle, 2 * seg), b1 = avgB(seg + settle, 2 * seg), m2 = avgMain(2 * seg + settle, 3 * seg);
                Console.WriteLine("  shares: A talks main " + m0.ToString("0.00") + " / B " + b0.ToString("0.00") + ", B talks main " + m1.ToString("0.00") + " / B " + b1.ToString("0.00") + ", both main " + m2.ToString("0.00"));
                Check(m0 > 0.85, "A talks: A's mic gets the mix (" + (m0 * 100).ToString("0") + "%)");
                Check(b1 > 0.85, "B talks: B's mic gets the mix (" + (b1 * 100).ToString("0") + "%)");
                Check(m2 > 0.2 && m2 < 0.8, "both talk: both mics are in the mix");
                double inA = pw(micA, settle, seg), outA = pw(output, settle, seg);
                Check(Math.Abs(Db(outA) - Db(inA)) < 2.5, "A's voice comes through at its level (" + (Db(outA) - Db(inA)).ToString("+0.0;-0.0") + " dB)");
                double inB = pw(micB, seg + settle, 2 * seg), outB = pw(output, seg + settle, 2 * seg);
                Check(Math.Abs(Db(outB) - Db(inB)) < 2.5, "B's voice comes through at its level (" + (Db(outB) - Db(inB)).ToString("+0.0;-0.0") + " dB)");
                double nIn = pw(micA, 3 * seg + settle, total) + pw(micB, 3 * seg + settle, total), nOut = pw(output, 3 * seg + settle, total);
                Check(Db(nOut) < Db(nIn) - 2, "nobody talks: the two mics' noise doesn't add up (" + (Db(nOut) - Db(nIn)).ToString("0.0") + " dB vs both mics summed)");
                // Clicks: no sample jumps far beyond what the inputs themselves do.
                double maxIn = 0, maxOut = 0;
                for (int i = 1; i < total; i++) { maxIn = Math.Max(maxIn, Math.Max(Math.Abs(micA[i] - micA[i - 1]), Math.Abs(micB[i] - micB[i - 1]))); maxOut = Math.Max(maxOut, Math.Abs(output[i] - output[i - 1])); }
                Check(maxOut <= maxIn * 1.5 && !output.Any(float.IsNaN), "no clicks or NaN (largest step " + maxOut.ToString("0.000") + " vs input " + maxIn.ToString("0.000") + ")");
                Check(extra.Dropouts == 0, "second mic's ring: no dropouts");
            }
            else
            {
                double sumIn = pw(micA, settle, seg) + pw(micB, settle, seg), outP = pw(output, settle, seg);
                Check(Math.Abs(Db(outP) - Db(sumIn)) < 1.5, "off: both mics simply added (" + (Db(outP) - Db(sumIn)).ToString("+0.0;-0.0") + " dB)");
            }
        }

        // A second mic that delivers nothing (unplugged, or still starting): the main mic is untouched.
        {
            var mixer = new MicMixer(); var gone = new ExtraMic("gone", 0); mixer.Use(gone);
            var buf = new float[Pk]; double diff = 0, sig = 0;
            for (int pos = 0; pos + Pk <= seg; pos += Pk)
            {
                Array.Copy(micA, pos, buf, 0, Pk); mixer.Mix(buf, Pk);
                if (pos > Fs / 10) for (int i = 0; i < Pk; i++) { diff += (buf[i] - micA[pos + i]) * (buf[i] - micA[pos + i]); sig += micA[pos + i] * micA[pos + i]; }
            }
            Check(Db(diff) - Db(sig) < -30, "a silent / unplugged second mic leaves the main mic as it is (" + (Db(diff) - Db(sig)).ToString("0") + " dB difference)");
        }
        // No extra mics: nothing at all happens.
        {
            var mixer = new MicMixer(); var buf = (float[])micA.Take(Pk).ToArray().Clone(); var before = (float[])buf.Clone();
            mixer.Mix(buf, Pk);
            Check(buf.SequenceEqual(before), "no extra mics: the mic is passed through untouched");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
