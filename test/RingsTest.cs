using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// The Test footsteps button through the real 3D processor: three full circles (ear level, floor above, floor below),
// 16 steps each, clockwise from the front. Per step: the ear level difference (left steps louder left, right steps
// louder right, front/behind balanced) and the 4-16 kHz spectrum (where height is heard): the same angle on the three
// floors must sound different. Writes rings_test.wav (binaural, for headphones).
class RingsTest
{
    const int B = SpatialProcessor.B, Fs = SpatialProcessor.Fs, StepLen = 21600;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static double[] Bands(float[] x, int from, int len)
    {
        int n = 4096; var fft = new Fft(n); var acc = new double[n / 2];
        var re = new float[n]; var im = new float[n];
        for (int s = from; s + n <= from + len; s += n / 2)
        {
            for (int i = 0; i < n; i++) { re[i] = x[s + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)); im[i] = 0; }
            fft.Transform(re, im, false);
            for (int k = 0; k < n / 2; k++) acc[k] += re[k] * re[k] + im[k] * im[k];
        }
        var centers = new[] { 4000, 5000, 6300, 8000, 10000, 12500, 16000 };
        return centers.Select(c => { int k0 = (int)(c / 1.12 * n / Fs), k1 = (int)(c * 1.12 * n / Fs); double e = 0; for (int k = k0; k <= k1; k++) e += acc[k]; return 10 * Math.Log10(e + 1e-20); }).ToArray();
    }

    static void Main(string[] args)
    {
        foreach (var kind in new[] { HrtfSet.Ku100, HrtfSet.Kemar })
        {
            var c = new SpatialConfig { HrtfKind = kind, Room = 0.2, CueSharpen = 0.3, FootstepBoost = 0, LoudTamer = 0, Limiter = true, Render360 = true, Focus360 = 1 };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { AutoPreamp = false }; c.DirGainDb[d] = 0; }
            var p = new SpatialProcessor(); p.Configure(c);
            var L = new List<float>(); var R = new List<float>(); var labels = new List<string>(); var els = new List<float>(); var azs = new List<float>();
            var l = new float[B]; var r = new float[B];
            var slots = new float[8][]; for (int i = 0; i < 8; i++) slots[i] = new float[B];
            p.Process(slots, SpatialProcessor.Source.Silent, l, r);   // the processor knows its head model, as in the app
            p.StartRingsTest();
            Check(p.TestMode == TestSignal.Mode.Rings, kind + ": the footsteps button runs the rings (true elevations)");
            while (p.TestRunning && L.Count < Fs * 40)
            {
                p.Process(slots, SpatialProcessor.Source.Silent, l, r);
                L.AddRange(l); R.AddRange(r);
                labels.Add(p.TestLabel); azs.Add(p.Radar.TestAz); els.Add(p.Radar.TestEl);
            }
            var Lf = L.ToArray(); var Rf = R.ToArray();
            int steps = Lf.Length / StepLen;
            Console.WriteLine("== " + kind + ": " + (Lf.Length / (double)Fs).ToString("0.0") + " s, " + steps + " steps");
            Check(steps == TestSignal.RingLevels.Length * TestSignal.RingSteps, "48 steps: 3 floors x 16 around");
            var spec = new Dictionary<string, double[]>();
            var stepAz = new List<float>(); var stepEl = new List<float>();
            for (int k = 0; k < steps; k++)
            {
                int s = k * StepLen, mid = Math.Min(labels.Count - 1, (s + StepLen / 2) / B);
                float az = azs[mid], el = els[mid];
                stepAz.Add(az); stepEl.Add(el);
                double eL = 0, eR = 0; for (int i = s; i < s + StepLen; i++) { eL += Lf[i] * Lf[i]; eR += Rf[i] * Rf[i]; }
                double ild = 10 * Math.Log10(eL / eR);
                var bl = Bands(Lf, s, StepLen); var br = Bands(Rf, s, StepLen);
                var sp = bl.Zip(br, (a, b) => 10 * Math.Log10((Math.Pow(10, a / 10) + Math.Pow(10, b / 10)) / 2)).ToArray();
                spec[el + "/" + az] = sp;
                Console.WriteLine("  " + labels[mid].PadRight(30) + " L-R " + ild.ToString("+0.0;-0.0").PadLeft(6) + " dB   4-16 kHz: " + string.Join(" ", sp.Select(v => (v - sp.Max()).ToString("0").PadLeft(4))));
                // 45° up or down a sound is nearer the middle of the head, so the ears differ less than at ear level.
                double minIld = el == 0 ? 3 : 2;
                if (az > 45 && az < 135) Check(ild > minIld, labels[mid] + ": louder in the left ear");
                if (az > 225 && az < 315) Check(ild < -minIld, labels[mid] + ": louder in the right ear");
                if (az == 0 || az == 180) Check(Math.Abs(ild) < 3, labels[mid] + ": balanced between the ears");
            }
            // Order: ear level, then above, then below; clockwise from the front (front, then to the right).
            Check(stepEl.Take(16).All(e => e == 0) && stepEl.Skip(16).Take(16).All(e => e > 0) && stepEl.Skip(32).All(e => e < 0), "ear level, then the floor above, then the floor below");
            Check(stepAz[0] == 0 && stepAz[1] > 270 && stepAz[4] == 270 && stepAz[8] == 180 && stepAz[12] == 90, "each circle goes front, right, behind, left");
            foreach (var az in new[] { 0f, 90f, 180f, 270f })
            {
                var lv = spec["0/" + az]; var up = spec["45/" + az]; var dn = spec["-45/" + az];
                Func<double[], double[], double> diff = (a, b) => a.Zip(b, (x, y) => Math.Abs((x - a.Max()) - (y - b.Max()))).Max();
                double dUp = diff(lv, up), dDn = diff(lv, dn), dUD = diff(up, dn);
                Check(dUp > 3 && dDn > 3 && dUD > 3, "azimuth " + az + ": above / level / below have different height cues (" + dUp.ToString("0.0") + " / " + dDn.ToString("0.0") + " / " + dUD.ToString("0.0") + " dB)");
            }
            Check(!Lf.Any(float.IsNaN) && Lf.Max(v => Math.Abs(v)) <= 1f, "no NaN or clipping (peak " + Lf.Max(v => Math.Abs(v)).ToString("0.00") + ")");
            if (kind == HrtfSet.Ku100)
            {
                string wav = args.Length > 0 ? args[0] : "rings_test.wav";
                using (var w = new BinaryWriter(File.Create(wav)))
                {
                    int n = Lf.Length;
                    w.Write("RIFF".ToCharArray()); w.Write(36 + n * 4); w.Write("WAVEfmt ".ToCharArray());
                    w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Fs); w.Write(Fs * 4); w.Write((short)4); w.Write((short)16);
                    w.Write("data".ToCharArray()); w.Write(n * 4);
                    for (int i = 0; i < n; i++) { w.Write((short)(Lf[i] * 32767)); w.Write((short)(Rf[i] * 32767)); }
                }
                Console.WriteLine("  wrote " + Path.GetFullPath(wav));
            }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
