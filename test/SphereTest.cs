using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// The sphere test through the real 3D processor: per position (2 steps each) the ear time/level differences and the
// 4-16 kHz spectrum (where height is heard); above/level/below must differ, sides must be on the right side, and the
// footsteps must fade out (no cut-off). Writes sphere_test.wav (binaural, for headphones).
class SphereTest
{
    const int B = SpatialProcessor.B, Fs = SpatialProcessor.Fs;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static double[] Bands(float[] x, int from, int len)
    {
        // 1/3-octave energies 4-16 kHz via Goertzel-free FFT on 4096 windows.
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
        foreach (var kind in new[] { HrtfSet.Kemar, HrtfSet.Ku100 })
        {
            var c = new SpatialConfig { HrtfKind = kind, Room = 0.15, CueSharpen = 0.35, FootstepBoost = 0, LoudTamer = 0, Limiter = true, Render360 = true };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { AutoPreamp = false }; c.DirGainDb[d] = 0; }
            var p = new SpatialProcessor(); p.Configure(c);
            p.StartSphereTest();
            var L = new List<float>(); var R = new List<float>(); var labels = new List<string>(); var azel = new List<float[]>();
            var l = new float[B]; var r = new float[B];
            var slots = new float[8][]; for (int i = 0; i < 8; i++) slots[i] = new float[B];
            while (p.TestRunning && L.Count < Fs * 30)
            {
                p.Process(slots, SpatialProcessor.Source.Silent, l, r);
                L.AddRange(l); R.AddRange(r);
                labels.Add(p.TestLabel); azel.Add(new[] { p.Radar.TestAz, p.Radar.TestEl });
            }
            Console.WriteLine("== " + kind + ": " + (L.Count / (double)Fs).ToString("0.0") + " s");
            var Lf = L.ToArray(); var Rf = R.ToArray();
            int seg = 2 * 21600;
            var spectra = new Dictionary<string, double[]>();
            for (int s = 0; s + seg <= Lf.Length; s += seg)
            {
                string lab = labels[Math.Min(labels.Count - 1, (s + seg / 2) / B)];
                double el = 0, er = 0; for (int i = s; i < s + seg; i++) { el += Lf[i] * Lf[i]; er += Rf[i] * Rf[i]; }
                double ild = 10 * Math.Log10(el / er);
                var bl = Bands(Lf, s, seg); var br = Bands(Rf, s, seg);
                spectra[lab] = bl.Zip(br, (a, b) => 10 * Math.Log10((Math.Pow(10, a / 10) + Math.Pow(10, b / 10)) / 2)).ToArray();
                Console.WriteLine("  " + lab.PadRight(32) + " L-R " + ild.ToString("+0.0;-0.0").PadLeft(6) + " dB   4-16 kHz: " + string.Join(" ", spectra[lab].Select(v => (v - spectra[lab].Max()).ToString("0").PadLeft(4))));
                if (lab.Contains("to your left")) Check(ild > 3, lab + ": louder in the left ear");
                if (lab.Contains("to your right")) Check(ild < -3, lab + ": louder in the right ear");
            }
            foreach (var side in new[] { "in front", "behind you", "to your left" })
            {
                var lv = spectra.First(kv => kv.Key.StartsWith("Level, " + side)).Value;
                var up = spectra.First(kv => kv.Key.StartsWith("Above, " + side)).Value;
                var dn = spectra.First(kv => kv.Key.StartsWith("Below, " + side)).Value;
                double dUp = lv.Zip(up, (a, b) => Math.Abs((a - lv.Max()) - (b - up.Max()))).Max(), dDn = lv.Zip(dn, (a, b) => Math.Abs((a - lv.Max()) - (b - dn.Max()))).Max();
                Check(dUp > 3 && dDn > 3, side + ": above / level / below have different height cues (" + dUp.ToString("0.0") + " / " + dDn.ToString("0.0") + " dB)");
            }
            Check(!Lf.Any(float.IsNaN) && Lf.Max(v => Math.Abs(v)) <= 1f, "no NaN or clipping (peak " + Lf.Max(v => Math.Abs(v)).ToString("0.00") + ")");
            if (kind == HrtfSet.Kemar)
            {
                string wav = args.Length > 0 ? args[0] : "sphere_test.wav";
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
        // The footstep itself: body below 150 Hz, a natural decay (no cut-off).
        var steps = typeof(TestSignal).GetField("Steps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null) as float[][];
        var st = steps[0];
        double peak = st.Max(v => Math.Abs((double)v)), tail = 0; for (int i = st.Length - 2400; i < st.Length; i++) tail = Math.Max(tail, Math.Abs(st[i]));
        Check(20 * Math.Log10(tail / peak) < -45, "step decays naturally: last 50 ms at " + (20 * Math.Log10(tail / peak)).ToString("0") + " dB below the peak");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
