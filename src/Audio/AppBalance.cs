using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// What StreamLite hears of one profile's apps (4.2, the listening EQ): the long-term average spectrum of their
    /// sound in the fine EQ's 31 bands. Level-free - every moment with sound counts by its shape, not by how loud it is -
    /// and it forgets slowly (sound from an hour of listening ago counts half), so it follows what you play now.
    /// Nothing of the sound itself is kept: only these 31 numbers.
    /// </summary>
    internal sealed class AppSpectrum
    {
        public const int Rate = 48000, Size = 8192;              // a look every 171 ms, 5.9 Hz between bins
        const double HalfSeconds = 3600;
        /// <summary>Looks quieter than this (RMS, -60 dBFS) are skipped. Tests lower it to listen to inaudible sound.</summary>
        internal static double Silent = 0.001;
        static readonly int[] lo = new int[31], hi = new int[31];
        static readonly int refLo = Array.FindIndex(EqProfile.FineFreqs, f => f >= 100), refHi = Array.FindLastIndex(EqProfile.FineFreqs, f => f <= 5000);
        readonly Fft fft = new Fft(Size);
        readonly float[] buf = new float[Size], re = new float[Size], im = new float[Size], win = new float[Size];
        readonly double[] pow = new double[31], look = new double[31];
        int fill;
        /// <summary>Seconds of the apps' sound (with sound in it) heard so far.</summary>
        public double Seconds;

        static AppSpectrum()
        {
            double bin = (double)Rate / Size;
            for (int i = 0; i < 31; i++)
            {
                double fc = EqProfile.FineFreqs[i];
                lo[i] = Math.Max(1, (int)Math.Ceiling(fc * Math.Pow(2, -1.0 / 6) / bin));
                hi[i] = Math.Min(Size / 2 - 1, (int)Math.Floor(fc * Math.Pow(2, 1.0 / 6) / bin));
                if (hi[i] < lo[i]) hi[i] = lo[i];
            }
        }

        /// <summary>Carries on from what was heard before (dB per band from Db(), and its seconds), or starts afresh.</summary>
        public AppSpectrum(double[] heardDb = null, double seconds = 0)
        {
            for (int i = 0; i < Size; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)));
            if (heardDb != null && heardDb.Length == 31 && seconds > 0)
            {
                for (int i = 0; i < 31; i++) pow[i] = Math.Pow(10, heardDb[i] / 10);
                Seconds = seconds;
            }
        }

        /// <summary>Interleaved stereo at 48 kHz (frames of two floats): how many looks it made of them.</summary>
        public int Feed(float[] stereo, int frames)
        {
            int looks = 0;
            for (int f = 0; f < frames; f++)
            {
                buf[fill++] = 0.5f * (stereo[2 * f] + stereo[2 * f + 1]);
                if (fill == Size) { fill = 0; if (Look()) looks++; }
            }
            return looks;
        }

        bool Look()
        {
            double sum = 0;
            for (int i = 0; i < Size; i++) sum += buf[i] * buf[i];
            if (Math.Sqrt(sum / Size) < Silent) return false;
            for (int i = 0; i < Size; i++) { re[i] = buf[i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            // Each band's energy, relative to the look's own energy between 100 Hz and 5 kHz.
            double refE = 0;
            for (int b = 0; b < 31; b++)
            {
                double e = 0;
                for (int k = lo[b]; k <= hi[b]; k++) e += (double)re[k] * re[k] + (double)im[k] * im[k];
                look[b] = e;
                if (b >= refLo && b <= refHi) refE += e;
            }
            refE /= refHi - refLo + 1;
            if (refE <= 1e-12) return false;
            double dt = (double)Size / Rate;
            Seconds += dt;
            // A plain average at first, then an exponential one that halves an old look's weight every HalfSeconds.
            double a = Math.Max(dt / Seconds, dt * Math.Log(2) / HalfSeconds);
            for (int b = 0; b < 31; b++) pow[b] += a * (look[b] / refE - pow[b]);
            return true;
        }

        /// <summary>The average spectrum so far: dB per band (0 dB = the average between 100 Hz and 5 kHz).</summary>
        public double[] Db()
        {
            var d = new double[31];
            for (int b = 0; b < 31; b++) d[b] = 10 * Math.Log10(pow[b] + 1e-12);
            return d;
        }
    }

    /// <summary>
    /// The listening EQ: how far an app's average sound (AppSpectrum) is from a balanced one of its kind - music, games
    /// and films, or voice chat - turned into a few gentle, wide filters (a low shelf, six octave-wide bells, a high
    /// shelf). Only broad tilts and bumps are corrected, never more than a few dB, and only part of the way (strength);
    /// it grows in over the first ten minutes of sound, so a few seconds of one scene don't decide it.
    /// </summary>
    internal static class AppBalance
    {
        public const double FullAfterSeconds = 600;

        sealed class Kind { public double Lo, Hi, MaxBoost, MaxCut; public Func<double, double> Target; }

        // Long-term spectra of balanced material, in band energy per third of an octave (pink noise is flat): music,
        // games and films are about flat from the low bass to the low mids and fall about 4.5 dB an octave above; speech
        // sits between 150 and 600 Hz and falls about 6 dB an octave above. Below the range the target rolls off, so a
        // thin app isn't pumped with sub-bass.
        static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            { "music", new Kind { Lo = 40, Hi = 12500, MaxBoost = 4, MaxCut = 6, Target = f => f < 50 ? -6 * Log2(50 / f) : f <= 500 ? 0 : -4.5 * Log2(f / 500) } },
            { "game", new Kind { Lo = 50, Hi = 12500, MaxBoost = 3, MaxCut = 4, Target = f => f < 50 ? -6 * Log2(50 / f) : f <= 500 ? 0 : -4.5 * Log2(f / 500) } },
            { "speech", new Kind { Lo = 125, Hi = 8000, MaxBoost = 3, MaxCut = 5, Target = f => f < 150 ? -12 * Log2(150 / f) : f <= 600 ? 0 : -6 * Log2(f / 600) } },
        };

        static double Log2(double x) { return Math.Log(x, 2); }

        /// <summary>Which balanced sound a profile's apps are held up against.</summary>
        public static string KindOf(GameProfile p) { return p.ChatApp ? "speech" : p.WhilePlaying ? "music" : "game"; }

        // The filters it fits (gains found per app): a low shelf, octave-wide bells from 200 Hz to 6.4 kHz, a high shelf.
        static readonly EqFilter[] Shape =
        {
            new EqFilter { Type = "LSC", Freq = 100, Q = 0.7 },
            new EqFilter { Type = "PK", Freq = 200, Q = 1.41 }, new EqFilter { Type = "PK", Freq = 400, Q = 1.41 },
            new EqFilter { Type = "PK", Freq = 800, Q = 1.41 }, new EqFilter { Type = "PK", Freq = 1600, Q = 1.41 },
            new EqFilter { Type = "PK", Freq = 3200, Q = 1.41 }, new EqFilter { Type = "PK", Freq = 6400, Q = 1.41 },
            new EqFilter { Type = "HSC", Freq = 10000, Q = 0.7 },
        };

        /// <summary>
        /// The EQ for what was heard (AppSpectrum.Db and its seconds) of this kind of app, strength 0..1: the change
        /// wanted per band (the balanced target minus what was heard, centred on the mids, smoothed over an octave,
        /// kept within the kind's limits), then the filter gains that come closest to it (least squares). Empty when
        /// there's nothing worth changing.
        /// </summary>
        public static List<EqFilter> Fit(double[] heardDb, double seconds, string kind, double strength)
        {
            var result = new List<EqFilter>();
            if (heardDb == null || heardDb.Length != 31 || seconds <= 0 || strength <= 0) return result;
            Kind k; if (!Kinds.TryGetValue(kind ?? "", out k)) k = Kinds["game"];
            var F = EqProfile.FineFreqs;
            var want = Want(heardDb, k);
            double grow = Math.Min(1, seconds / FullAfterSeconds) * Math.Min(1, strength);
            for (int b = 0; b < 31; b++) want[b] *= grow;

            // Least squares over the bands (weight 1 inside the kind's range, 0.3 outside where the change should stay 0),
            // with a little pull towards 0 dB so no two filters fight each other.
            int n = Shape.Length;
            var basis = new double[31, n];
            for (int j = 0; j < n; j++)
            {
                var one = new List<EqFilter> { new EqFilter { Type = Shape[j].Type, Freq = Shape[j].Freq, Q = Shape[j].Q, Gain = 1 } };
                for (int b = 0; b < 31; b++) basis[b, j] = Biquad.ResponseDb(one, F[b], 48000);
            }
            var A = new double[n, n + 1];
            for (int b = 0; b < 31; b++)
            {
                double w = F[b] >= k.Lo && F[b] <= k.Hi ? 1 : 0.3;
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) A[i, j] += w * basis[b, i] * basis[b, j];
                    A[i, n] += w * basis[b, i] * want[b];
                }
            }
            for (int i = 0; i < n; i++) A[i, i] += 0.05;
            var g = Solve(A, n);
            if (g == null) return result;

            var filters = new List<EqFilter>();
            for (int j = 0; j < n; j++) filters.Add(new EqFilter { Type = Shape[j].Type, Freq = Shape[j].Freq, Q = Shape[j].Q, Gain = g[j] });
            // The real curve within the limits (bells and shelves add up a little differently from the 1 dB guesses).
            double over = 1;
            for (int b = 0; b < 31; b++)
            {
                double r = Biquad.ResponseDb(filters, F[b], 48000);
                if (r > k.MaxBoost) over = Math.Min(over, k.MaxBoost / r);
                if (r < -k.MaxCut) over = Math.Min(over, k.MaxCut / -r);
            }
            foreach (var f in filters)
            {
                f.Gain = Math.Round(f.Gain * over * 10) / 10;
                if (Math.Abs(f.Gain) >= 0.3) result.Add(f);
            }
            return result;
        }

        /// <summary>The change wanted per band, before strength: target minus heard inside the kind's range (0 outside),
        /// centred on 200 Hz - 4 kHz, smoothed over about an octave and kept within the kind's limits.</summary>
        internal static double[] Want(double[] heardDb, string kind)
        {
            Kind k; if (!Kinds.TryGetValue(kind ?? "", out k)) k = Kinds["game"];
            return Want(heardDb, k);
        }

        static double[] Want(double[] heardDb, Kind k)
        {
            var F = EqProfile.FineFreqs;
            var d = new double[31];
            double mid = 0; int nm = 0;
            for (int b = 0; b < 31; b++)
            {
                if (F[b] < k.Lo || F[b] > k.Hi) continue;
                d[b] = k.Target(F[b]) - heardDb[b];
                if (F[b] >= 200 && F[b] <= 4000) { mid += d[b]; nm++; }
            }
            mid = nm > 0 ? mid / nm : 0;
            for (int b = 0; b < 31; b++) if (F[b] >= k.Lo && F[b] <= k.Hi) d[b] -= mid;
            // Two passes of a three-band average inside the range (the edges repeat): bumps narrower than about an octave
            // are the content's (one song's note), not the app's sound.
            for (int pass = 0; pass < 2; pass++)
            {
                var s = (double[])d.Clone();
                for (int b = 0; b < 31; b++)
                {
                    if (F[b] < k.Lo || F[b] > k.Hi) continue;
                    double a = b > 0 && F[b - 1] >= k.Lo ? d[b - 1] : d[b], c = b < 30 && F[b + 1] <= k.Hi ? d[b + 1] : d[b];
                    s[b] = (a + d[b] + c) / 3;
                }
                d = s;
            }
            for (int b = 0; b < 31; b++) d[b] = Math.Max(-k.MaxCut, Math.Min(k.MaxBoost, d[b]));
            return d;
        }

        static double[] Solve(double[,] A, int n)
        {
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
                if (Math.Abs(A[p, c]) < 1e-12) return null;
                if (p != c) for (int j = c; j <= n; j++) { double t = A[c, j]; A[c, j] = A[p, j]; A[p, j] = t; }
                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double m = A[r, c] / A[c, c];
                    for (int j = c; j <= n; j++) A[r, j] -= m * A[c, j];
                }
            }
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = A[i, n] / A[i, i];
            return x;
        }

        /// <summary>"bass +1.5 dB, 3.2 kHz -1 dB": the listening EQ in a few words (the biggest changes first).</summary>
        public static string Describe(List<EqFilter> filters)
        {
            if (filters == null || filters.Count == 0) return "no change needed";
            return string.Join(", ", filters.OrderByDescending(f => Math.Abs(f.Gain)).Take(3).Select(f =>
                (f.Type == "LSC" ? "bass" : f.Type == "HSC" ? "treble" : f.Freq >= 1000 ? (f.Freq / 1000).ToString("0.#") + " kHz" : f.Freq.ToString("0") + " Hz") +
                " " + (f.Gain > 0 ? "+" : "") + f.Gain.ToString("0.#") + " dB"));
        }

        /// <summary>The largest difference (dB) between two EQs' curves - a new fit is applied only once it really differs.</summary>
        public static double Difference(List<EqFilter> a, List<EqFilter> b)
        {
            double worst = 0;
            foreach (var f in EqProfile.FineFreqs)
                worst = Math.Max(worst, Math.Abs(Biquad.ResponseDb(a ?? new List<EqFilter>(), f, 48000) - Biquad.ResponseDb(b ?? new List<EqFilter>(), f, 48000)));
            return worst;
        }
    }
}
