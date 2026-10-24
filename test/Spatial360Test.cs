using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamLite.Audio;

// 360° mode vs 7 virtual speakers. For sounds panned between speakers (the way games pan), how close is each to
// the real head response at that angle: time difference between the ears (ITD), level difference per band (ILD)
// and tone colour per ear. Plus: single-speaker sounds must come out the same, wide ambience must stay wide, CPU.
class Spatial360Test
{
    const int B = SpatialProcessor.B, Fs = SpatialProcessor.Fs;
    const int Len = 1 << 16;                       // analysed samples (1.4 s)
    const int Warm = 8 * B;

    static SpatialConfig Cfg(bool r360, double focus, double room)
    {
        var c = new SpatialConfig { Room = room, CueSharpen = 0, FootstepBoost = 0, LoudTamer = 0, Limiter = false, Render360 = r360, Focus360 = focus };
        foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { AutoPreamp = false }; c.DirGainDb[d] = 0; }
        return c;
    }

    static float[] Noise(int n, int seed) { var r = new Random(seed); var x = new float[n]; for (int i = 0; i < n; i++) x[i] = (float)((r.NextDouble() * 2 - 1) * 0.1); return x; }

    /// <summary>Feeds per-slot signals (null = silent) through a processor; returns the ears, latency removed.</summary>
    static void Render(SpatialConfig c, float[][] input, SpatialProcessor.Source src, out float[] L, out float[] R)
    {
        var p = new SpatialProcessor(); p.Configure(c);
        int total = Warm + Len + 4 * B;
        var oL = new List<float>(); var oR = new List<float>();
        var l = new float[B]; var r = new float[B];
        int lat = -1;
        for (int b = 0; b * B < total; b++)
        {
            var s = new float[8][];
            for (int k = 0; k < 8; k++) { s[k] = new float[B]; if (input[k] != null) for (int i = 0; i < B; i++) { int t = b * B + i; s[k][i] = t < input[k].Length ? input[k][t] : 0; } }
            p.Process(s, src, l, r);
            if (lat < 0) lat = p.ExtraLatency;
            oL.AddRange(l); oR.AddRange(r);
        }
        if (c.Render360 && !p.Rendering360) Console.WriteLine("  (360 not active: " + c.HrtfKind + ")");
        L = oL.Skip(lat + Warm).Take(Len).ToArray(); R = oR.Skip(lat + Warm).Take(Len).ToArray();
    }

    static float[] Conv(float[] x, float[] h)
    {
        var y = new float[x.Length];
        for (int n = 0; n < x.Length; n++) { double s = 0; for (int k = 0; k < h.Length && k <= n; k++) s += h[k] * x[n - k]; y[n] = (float)s; }
        return y;
    }

    static readonly double[] Bands = Enumerable.Range(0, 20).Select(i => 200 * Math.Pow(2, i / 3.0)).Where(f => f < 16000).ToArray();

    sealed class Spec { public double[] Re, Im; }
    static Spec FftOf(float[] x)
    {
        int n = Len * 2; var re = new float[n]; var im = new float[n];
        Array.Copy(x, re, Math.Min(x.Length, Len));
        new Fft(n).Transform(re, im, false);
        return new Spec { Re = re.Select(v => (double)v).ToArray(), Im = im.Select(v => (double)v).ToArray() };
    }

    static double[] BandDb(Spec s)
    {
        int n = Len * 2; var o = new double[Bands.Length];
        for (int b = 0; b < Bands.Length; b++)
        {
            int k0 = (int)(Bands[b] / Math.Pow(2, 1 / 6.0) * n / Fs), k1 = (int)(Bands[b] * Math.Pow(2, 1 / 6.0) * n / Fs);
            double e = 0; for (int k = k0; k <= k1; k++) e += s.Re[k] * s.Re[k] + s.Im[k] * s.Im[k];
            o[b] = 10 * Math.Log10(e / (k1 - k0 + 1) + 1e-30);
        }
        return o;
    }

    /// <summary>Interaural time difference in microseconds (+ = left ear first), from the cross-correlation below 1.5 kHz.</summary>
    static double Itd(Spec a, Spec b)
    {
        int n = Len * 2; var re = new float[n]; var im = new float[n];
        int kMax = 1500 * n / Fs;
        for (int k = 1; k < kMax; k++)
        {
            double cr = a.Re[k] * b.Re[k] + a.Im[k] * b.Im[k], ci = a.Im[k] * b.Re[k] - a.Re[k] * b.Im[k];   // A conj(B)
            re[k] = (float)cr; im[k] = (float)ci; re[n - k] = (float)cr; im[n - k] = (float)-ci;
        }
        new Fft(n).Transform(re, im, true);
        int best = 0; double bv = double.MinValue;
        for (int lag = -48; lag <= 48; lag++) { double v = re[(lag + n) % n]; if (v > bv) { bv = v; best = lag; } }
        double y0 = re[(best - 1 + n) % n], y1 = re[(best + n) % n], y2 = re[(best + 1 + n) % n];
        double frac = 0.5 * (y0 - y2) / (y0 - 2 * y1 + y2 + 1e-30);
        return -(best + frac) * 1e6 / Fs;    // peak at negative lag = right ear later = sound from the left
    }

    /// <summary>Interaural coherence (|cross| / sqrt(EL ER)) per band, averaged over bands.</summary>
    static double Coherence(Spec a, Spec b, double fLo, double fHi)
    {
        int n = Len * 2; double sum = 0; int cnt = 0;
        for (int bi = 0; bi < Bands.Length; bi++)
        {
            if (Bands[bi] < fLo || Bands[bi] > fHi) continue;
            int k0 = (int)(Bands[bi] / Math.Pow(2, 1 / 6.0) * n / Fs), k1 = (int)(Bands[bi] * Math.Pow(2, 1 / 6.0) * n / Fs);
            double cr = 0, ci = 0, ea = 0, eb = 0;
            for (int k = k0; k <= k1; k++)
            {
                cr += a.Re[k] * b.Re[k] + a.Im[k] * b.Im[k]; ci += a.Im[k] * b.Re[k] - a.Re[k] * b.Im[k];
                ea += a.Re[k] * a.Re[k] + a.Im[k] * a.Im[k]; eb += b.Re[k] * b.Re[k] + b.Im[k] * b.Im[k];
            }
            sum += Math.Sqrt(cr * cr + ci * ci) / Math.Sqrt(ea * eb + 1e-30); cnt++;
        }
        return sum / cnt;
    }

    sealed class Err { public double Itd, Ild, Tone; }

    static Err Compare(float[] L, float[] R, float[] refL, float[] refR)
    {
        Spec sl = FftOf(L), sr = FftOf(R), rl = FftOf(refL), rr = FftOf(refR);
        double itd = Itd(sl, sr) - Itd(rl, rr);
        var bl = BandDb(sl); var br = BandDb(sr); var gl = BandDb(rl); var gr = BandDb(rr);
        double ild = 0, tone = 0; int ni = 0;
        for (int b = 0; b < Bands.Length; b++)
        {
            tone += Math.Abs(bl[b] - gl[b]) + Math.Abs(br[b] - gr[b]);
            if (Bands[b] >= 1500) { ild += Math.Abs((bl[b] - br[b]) - (gl[b] - gr[b])); ni++; }
        }
        return new Err { Itd = itd, Ild = ild / ni, Tone = tone / (2 * Bands.Length) };
    }

    static void Main()
    {
        int fails = 0;
        Action<bool, string> check = (ok, what) => { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; };
        foreach (var kind in new[] { HrtfSet.Kemar, HrtfSet.Ku100 })
        {
            var set = HrtfSet.Get(kind, null);
            Console.WriteLine("\n=== " + set.Name + " ===");
            var swT = Stopwatch.StartNew();
            ContinuousRenderer.Prepare(set);
            while (!ContinuousRenderer.IsReady(set)) System.Threading.Thread.Sleep(5);
            Console.WriteLine("  0.25 degree table built in " + swT.ElapsedMilliseconds + " ms (background)");
            var src = SpatialProcessor.Source.Surround71;
            var radar = new SoundRadar(); radar.SetLayout(src, Cfg(false, 1, 0));
            var cfgSpk = Cfg(false, 1, 0); cfgSpk.HrtfKind = kind;
            var cfg360 = Cfg(true, 1, 0); cfg360.HrtfKind = kind;

            // 1) One speaker only: 360 mode must equal speaker mode.
            {
                var x = Noise(Warm + Len + 8 * B, 7);
                var inp = new float[8][]; inp[SpatialProcessor.SL] = x;
                float[] aL, aR, bL, bR;
                Render(cfgSpk, inp, src, out aL, out aR); Render(cfg360, inp, src, out bL, out bR);
                double e = 0, d = 0;
                for (int i = 0; i < Len; i++) { e += aL[i] * aL[i] + aR[i] * aR[i]; d += (aL[i] - bL[i]) * (aL[i] - bL[i]) + (aR[i] - bR[i]) * (aR[i] - bR[i]); }
                double db = 10 * Math.Log10(d / e + 1e-30);
                check(db < -30, "single speaker (Left) identical in both modes: difference " + db.ToString("0.0") + " dB");
            }

            // 2) Panned sources every 5° (between speakers): error vs the real response at that angle.
            Console.WriteLine("  angle   ITD err us (spk / 360)   ILD err dB (spk / 360)   tone err dB (spk / 360)");
            var spkAz = new HashSet<int> { 0, 30, 90, 145, 215, 270, 330 };
            double sumItdS = 0, sumItd3 = 0, sumIldS = 0, sumIld3 = 0, sumToneS = 0, sumTone3 = 0; int cnt = 0;
            double worstItd3 = 0, worstIld3 = 0;
            for (int az = 0; az < 360; az += 5)
            {
                if (spkAz.Contains(az)) continue;
                int a, b; float ga, gb;
                TestSignal.Pan(az, radar.SlotAz, out a, out b, out ga, out gb);
                var x = Noise(Warm + Len + 8 * B, 100 + az);
                var inp = new float[8][];
                inp[a] = x.Select(v => v * ga).ToArray();
                inp[b] = x.Select(v => v * gb).ToArray();
                float[] sL, sR, cL, cR;
                Render(cfgSpk, inp, src, out sL, out sR); Render(cfg360, inp, src, out cL, out cR);
                int hi = set.Nearest(az, 0);
                var seg = x.Skip(Warm).Take(Len + set.Taps).ToArray();
                // Reference: the same noise from the measured response at that exact angle (aligned like the renders).
                var full = Conv(x.Take(Warm + Len).ToArray(), set.L[hi]); var fullR = Conv(x.Take(Warm + Len).ToArray(), set.R[hi]);
                var refL = full.Skip(Warm).Take(Len).ToArray(); var refR = fullR.Skip(Warm).Take(Len).ToArray();
                var es = Compare(sL, sR, refL, refR); var e3 = Compare(cL, cR, refL, refR);
                Console.WriteLine("  " + az.ToString().PadLeft(4) + "    " + es.Itd.ToString("0").PadLeft(6) + " / " + e3.Itd.ToString("0").PadLeft(5) +
                    "           " + es.Ild.ToString("0.0").PadLeft(5) + " / " + e3.Ild.ToString("0.0").PadLeft(4) +
                    "             " + es.Tone.ToString("0.0").PadLeft(5) + " / " + e3.Tone.ToString("0.0").PadLeft(4));
                sumItdS += Math.Abs(es.Itd); sumItd3 += Math.Abs(e3.Itd); sumIldS += es.Ild; sumIld3 += e3.Ild; sumToneS += es.Tone; sumTone3 += e3.Tone; cnt++;
                worstItd3 = Math.Max(worstItd3, Math.Abs(e3.Itd)); worstIld3 = Math.Max(worstIld3, e3.Ild);
            }
            Console.WriteLine("  mean    " + (sumItdS / cnt).ToString("0").PadLeft(6) + " / " + (sumItd3 / cnt).ToString("0").PadLeft(5) +
                "           " + (sumIldS / cnt).ToString("0.0").PadLeft(5) + " / " + (sumIld3 / cnt).ToString("0.0").PadLeft(4) +
                "             " + (sumToneS / cnt).ToString("0.0").PadLeft(5) + " / " + (sumTone3 / cnt).ToString("0.0").PadLeft(4));
            check(sumItd3 < 0.5 * sumItdS, "360 mode halves the time-difference error between speakers (" + (sumItd3 / cnt).ToString("0") + " vs " + (sumItdS / cnt).ToString("0") + " us)");
            check(sumIld3 < 0.7 * sumIldS, "360 mode cuts the level-difference error (" + (sumIld3 / cnt).ToString("0.0") + " vs " + (sumIldS / cnt).ToString("0.0") + " dB)");
            check(sumTone3 < sumToneS, "360 mode tone is closer to the real response (" + (sumTone3 / cnt).ToString("0.0") + " vs " + (sumToneS / cnt).ToString("0.0") + " dB)");
            check(worstItd3 < 120, "worst 360 time-difference error " + worstItd3.ToString("0") + " us");

            // 2a) In-between angles (2.5° off the measured grid): time difference lies between its two neighbours.
            {
                int bad = 0; double maxDev = 0;
                for (int az = 5; az < 360; az += 10)
                {
                    Func<double, double> itdAt = ang =>
                    {
                        int a, b; float ga, gb;
                        TestSignal.Pan(ang, radar.SlotAz, out a, out b, out ga, out gb);
                        var x = Noise(Warm + Len + 8 * B, 900);
                        var inp = new float[8][]; inp[a] = x.Select(v => v * ga).ToArray(); inp[b] = x.Select(v => v * gb).ToArray();
                        float[] cL, cR; Render(cfg360, inp, src, out cL, out cR);
                        return Itd(FftOf(cL), FftOf(cR));
                    };
                    if (az % 30 != 5 && az % 30 != 25) continue;
                    double lo = itdAt(az - 2.5), mid = itdAt(az), hi = itdAt(az + 2.5);
                    double mn = Math.Min(lo, hi) - 5, mx = Math.Max(lo, hi) + 5;   // the curve peaks near 90° / 270°
                    if (mid < mn || mid > mx) { bad++; Console.WriteLine("    at " + az + ": " + lo.ToString("0") + " / " + mid.ToString("0") + " / " + hi.ToString("0") + " us"); }
                    maxDev = Math.Max(maxDev, Math.Abs(mid - (lo + hi) / 2));
                }
                check(bad == 0, "1-degree steps: time difference moves smoothly between measured angles (max " + maxDev.ToString("0") + " us from the midpoint)");
            }

            // 2b) Partial focus (the default 80%) must not lose loudness or move the sound.
            {
                var cfg80 = Cfg(true, 0.8, 0); cfg80.HrtfKind = kind;
                double worst = 0, worstItd = 0;
                foreach (int az in new[] { 15, 60, 120, 175, 240, 300 })
                {
                    int a, b; float ga, gb;
                    TestSignal.Pan(az, radar.SlotAz, out a, out b, out ga, out gb);
                    var x = Noise(Warm + Len + 8 * B, 700 + az);
                    var inp = new float[8][]; inp[a] = x.Select(v => v * ga).ToArray(); inp[b] = x.Select(v => v * gb).ToArray();
                    float[] fL, fR, pL, pR;
                    Render(cfg360, inp, src, out fL, out fR); Render(cfg80, inp, src, out pL, out pR);
                    var e = Compare(pL, pR, fL, fR);
                    worst = Math.Max(worst, e.Tone); worstItd = Math.Max(worstItd, Math.Abs(e.Itd));
                }
                check(worst < 1.0 && worstItd < 25, "80% focus vs 100%: tone within " + worst.ToString("0.0") + " dB, timing within " + worstItd.ToString("0") + " us");
            }

            // 2c) A sound moving around the head (a 1 kHz tone circling once in 1.4 s): no frame artefacts - the energy
            // away from 1 kHz must stay as low as the speaker rendering's.
            {
                var inp = new float[8][]; for (int k = 0; k < 8; k++) inp[k] = new float[Warm + Len + 8 * B];
                int total = Warm + Len + 8 * B;
                for (int t = 0; t < total; t++)
                {
                    double az = Math.Max(0, t - Warm) * 360.0 / Len;
                    int a, b; float ga, gb;
                    TestSignal.Pan(az, radar.SlotAz, out a, out b, out ga, out gb);
                    float v = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * t / Fs));
                    inp[a][t] += v * ga; inp[b][t] += v * gb;
                }
                float[] sL, sR, cL, cR;
                Render(cfgSpk, inp, src, out sL, out sR); Render(cfg360, inp, src, out cL, out cR);
                Func<float[], double> spur = y =>
                {
                    var f = FftOf(y); int n = Len * 2; double inB = 0, all = 0;
                    for (int k = 1; k < n / 2; k++) { double e = f.Re[k] * f.Re[k] + f.Im[k] * f.Im[k]; all += e; double hz = k * (double)Fs / n; if (Math.Abs(hz - 1000) < 60) inB += e; }
                    return 10 * Math.Log10((all - inB) / all + 1e-30);
                };
                double s1 = Math.Max(spur(sL), spur(sR)), s3 = Math.Max(spur(cL), spur(cR));
                check(s3 < Math.Max(s1 + 3, -40), "moving sound: energy away from the tone " + s3.ToString("0.0") + " dB (speakers " + s1.ToString("0.0") + " dB)");
            }

            // 3) Wide ambience (independent noise in every speaker): stays wide, same loudness.
            {
                var inp = new float[8][];
                foreach (int s in new[] { 0, 1, 2, 4, 5, 6, 7 }) inp[s] = Noise(Warm + Len + 8 * B, 300 + s);
                float[] sL, sR, cL, cR;
                Render(cfgSpk, inp, src, out sL, out sR); Render(cfg360, inp, src, out cL, out cR);
                Spec a1 = FftOf(sL), a2 = FftOf(sR), b1 = FftOf(cL), b2 = FftOf(cR);
                double cohS = Coherence(a1, a2, 500, 12000), coh3 = Coherence(b1, b2, 500, 12000);
                double lvS = 10 * Math.Log10(sL.Concat(sR).Sum(v => (double)v * v)), lv3 = 10 * Math.Log10(cL.Concat(cR).Sum(v => (double)v * v));
                check(coh3 < cohS + 0.1, "ambience width: interaural coherence " + coh3.ToString("0.00") + " (speakers " + cohS.ToString("0.00") + ")");
                check(Math.Abs(lv3 - lvS) < 1.0, "ambience loudness " + (lv3 - lvS).ToString("+0.00;-0.00") + " dB vs speakers");
            }

            // 4) Two sources at once in different pairs (gunfire left-behind, steps front-right): both keep their place.
            {
                var x1 = Noise(Warm + Len + 8 * B, 501); var x2 = Noise(Warm + Len + 8 * B, 502);
                var inp = new float[8][];
                int a, b; float ga, gb;
                TestSignal.Pan(120, radar.SlotAz, out a, out b, out ga, out gb);
                inp[a] = x1.Select(v => v * ga).ToArray(); inp[b] = x1.Select(v => v * gb).ToArray();
                TestSignal.Pan(300, radar.SlotAz, out a, out b, out ga, out gb);
                inp[a] = inp[a] ?? new float[x2.Length]; inp[b] = inp[b] ?? new float[x2.Length];
                for (int i = 0; i < x2.Length; i++) { inp[a][i] += x2[i] * ga; inp[b][i] += x2[i] * gb; }
                float[] cL, cR, sL, sR;
                Render(cfg360, inp, src, out cL, out cR); Render(cfgSpk, inp, src, out sL, out sR);
                Func<float[], float[], double> lvl = (u, v) => 10 * Math.Log10(u.Concat(v).Sum(q => (double)q * q));
                check(Math.Abs(lvl(cL, cR) - lvl(sL, sR)) < 1.0, "two simultaneous sources: loudness " + (lvl(cL, cR) - lvl(sL, sR)).ToString("+0.00;-0.00") + " dB vs speakers");
            }

            // 5) 5.1 layout works too.
            {
                var r51 = new SoundRadar(); r51.SetLayout(SpatialProcessor.Source.Surround51, cfgSpk);
                var x = Noise(Warm + Len + 8 * B, 9);
                var inp = new float[8][];
                inp[SpatialProcessor.FL] = x.Select(v => v * 0.7071f).ToArray(); inp[SpatialProcessor.BL] = x.Select(v => v * 0.7071f).ToArray();
                float[] cL, cR; Render(cfg360, inp, SpatialProcessor.Source.Surround51, out cL, out cR);
                double l = cL.Sum(v => (double)v * v), r = cR.Sum(v => (double)v * v);
                check(l > 2 * r && !cL.Any(float.IsNaN), "5.1: sound between front-left and rear-left lands on the left (" + (10 * Math.Log10(l / r)).ToString("0.0") + " dB)");
            }
        }

        // 6) CPU: 7.1 noise in every speaker, room on.
        {
            var p = new SpatialProcessor(); var c = Cfg(true, 0.8, 0.2); c.HrtfKind = HrtfSet.Kemar; p.Configure(c);
            var rnd = new Random(5); var l = new float[B]; var r = new float[B];
            var s = new float[8][]; for (int k = 0; k < 8; k++) s[k] = new float[B];
            for (int w = 0; w < 200; w++) { for (int k = 0; k < 8; k++) for (int i = 0; i < B; i++) s[k][i] = (float)(rnd.NextDouble() - 0.5); p.Process(s, SpatialProcessor.Source.Surround71, l, r); }
            var sw = Stopwatch.StartNew(); int blocks = 4000;
            for (int bk = 0; bk < blocks; bk++) { for (int k = 0; k < 8; k++) for (int i = 0; i < B; i++) s[k][i] = (float)(rnd.NextDouble() - 0.5); p.Process(s, SpatialProcessor.Source.Surround71, l, r); }
            double pct = sw.Elapsed.TotalSeconds / (blocks * (double)B / Fs) * 100;
            check(p.Rendering360, "360 mode active for 7.1");
            check(pct < 8, "CPU " + pct.ToString("0.0") + "% of one core (whole 3D chain, 360 mode, room on)");
            var ring = p.Radar.Ring;
            check(ring != null && ring.Length == ContinuousRenderer.Sectors && ring.Max() > -40, "radar ring filled (max " + (ring == null ? 0 : ring.Max()).ToString("0") + " dB)");
        }
        Console.WriteLine(fails == 0 ? "\nALL PASS" : "\n" + fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
