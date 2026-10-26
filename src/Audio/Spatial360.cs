using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// True 360° rendering of surround game audio. Games pan each sound between two neighbouring speakers; for every
    /// frequency band this finds that pair, works out the angle the game meant (inverse of its panning) and plays the
    /// band with the measured head response for that exact angle (1° steps, interpolated from the 5° measurements),
    /// instead of as two separate virtual speakers. Ambience that is spread over many speakers (low coherence) keeps
    /// the speaker rendering, so it stays wide. Short-time Fourier processing: 160-sample Hann frames, 50% overlap,
    /// zero-padded to 512 so every frame is an exact (alias-free) convolution; adds 80 samples (1.7 ms) of delay. Hops of 80
/// fit the 3D blocks (240) and Windows' 10 ms packets (480) exactly, so no sound waits for the next packet.
    /// </summary>
    internal sealed class ContinuousRenderer
    {
        const int F = 160, Hop = 80, N = 512, K = N / 2 + 1, Slots = SpatialProcessor.Slots;   // hops of 80: three per 240-frame block
        public const int Latency = F - Hop;
        public const int Sectors = 72;                     // radar ring: 5° each
        const int Res = 4, TN = 360 * Res;                 // head-response table: every 0.25°

        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[F];
        readonly float[][] hist = new float[Slots][];
        readonly float[][] xr = new float[Slots][], xi = new float[Slots][];
        readonly float[][] pw = new float[Slots][];        // smoothed power per slot per bin
        readonly float[] re = new float[N], im = new float[N];
        readonly float[] olaL = new float[N], olaR = new float[N];
        readonly float[] yLr = new float[K], yLi = new float[K], yRr = new float[K], yRi = new float[K];
        readonly double[] ring = new double[Sectors], ringFrame = new double[Sectors];
        public readonly float[] RingDb = new float[Sectors];

        // Layout.
        int[] order = new int[0];                          // used slots sorted by azimuth (the ring)
        readonly int[] deg = new int[Slots];               // speaker positions as table indexes
        readonly float[] ux = new float[Slots], uy = new float[Slots];
        float[][] cRe, cIm;                                // smoothed cross-spectrum per ring pair per bin
        Table table;
        public volatile float Focus = 1f;

        public ContinuousRenderer()
        {
            for (int i = 0; i < F; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / F));   // sums to 1 at 50% overlap
            for (int s = 0; s < Slots; s++) { hist[s] = new float[F]; xr[s] = new float[K]; xi[s] = new float[K]; pw[s] = new float[K]; }
            for (int i = 0; i < Sectors; i++) RingDb[i] = -100;
        }

        public bool Active { get { return table != null && order.Length >= 2; } }

        /// <summary>True when this head-response set has the horizontal measurements 360° mode needs.</summary>
        public static bool Supports(HrtfSet set)
        {
            if (set == null || set.SpeakerSet) return false;
            return Enumerable.Range(0, set.Az.Length).Count(i => Math.Abs(set.El[i]) < 0.5) >= 24;
        }

        /// <summary>True once the head-response table for this set is built (Prepare builds it in the background).</summary>
        public static bool IsReady(HrtfSet set) { return Table.Ready(set); }
        public static void Prepare(HrtfSet set)
        {
            if (Table.Ready(set)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Table.Get(set); } catch { } });
        }

        /// <summary>Speakers in use and where they are (azimuth, anticlockwise degrees).</summary>
        public void Configure(HrtfSet set, int[] slots, double[] azimuths)
        {
            table = Table.Get(set);
            var o = slots.Select((s, i) => new { s, a = ((azimuths[i] % 360) + 360) % 360 }).OrderBy(x => x.a).ToList();
            foreach (var x in o)
            {
                deg[x.s] = (int)Math.Round(x.a * Res) % TN;
                double r = x.a * Math.PI / 180;
                ux[x.s] = (float)Math.Cos(r); uy[x.s] = (float)Math.Sin(r);
            }
            cRe = new float[o.Count][]; cIm = new float[o.Count][];
            for (int p = 0; p < o.Count; p++) { cRe[p] = new float[K]; cIm[p] = new float[K]; }
            order = o.Select(x => x.s).ToArray();
        }

        /// <summary>One block (3 hops) of the used slots -> direct binaural sound, delayed by Latency.</summary>
        public void Process(float[][] slots, float[] outL, float[] outR, int n)
        {
            for (int h = 0; h + Hop <= n; h += Hop)
            {
                foreach (int s in order)
                {
                    var hs = hist[s];
                    Array.Copy(hs, Hop, hs, 0, F - Hop);
                    Array.Copy(slots[s], h, hs, F - Hop, Hop);
                }
                Frame();
                for (int i = 0; i < Hop; i++) { outL[h + i] = olaL[i]; outR[h + i] = olaR[i]; }
                Array.Copy(olaL, Hop, olaL, 0, N - Hop); Array.Clear(olaL, N - Hop, Hop);
                Array.Copy(olaR, Hop, olaR, 0, N - Hop); Array.Clear(olaR, N - Hop, Hop);
            }
        }

        void Frame()
        {
            // Forward FFTs, two real channels per complex transform.
            for (int a = 0; a < order.Length; a += 2)
            {
                int s1 = order[a], s2 = a + 1 < order.Length ? order[a + 1] : -1;
                for (int i = 0; i < F; i++) { re[i] = hist[s1][i] * win[i]; im[i] = s2 >= 0 ? hist[s2][i] * win[i] : 0; }
                Array.Clear(re, F, N - F); Array.Clear(im, F, N - F);
                fft.Transform(re, im, false);
                for (int k = 0; k < K; k++)
                {
                    int m = (N - k) & (N - 1);
                    float zr = re[k], zi = im[k], mr = re[m], mi = im[m];
                    xr[s1][k] = 0.5f * (zr + mr); xi[s1][k] = 0.5f * (zi - mi);
                    if (s2 >= 0) { xr[s2][k] = 0.5f * (zi + mi); xi[s2][k] = 0.5f * (mr - zr); }
                }
            }

            const float aP = 0.8006f;                        // smoothing per hop (1.7 ms): ~7.5 ms
            float focus = Focus;
            int pairs = order.Length;
            var ringE = ringFrame; Array.Clear(ringE, 0, Sectors);
            for (int k = 0; k < K; k++)
            {
                // Speaker rendering of everything, plus the energies.
                float lr = 0, li = 0, rr = 0, ri = 0;
                foreach (int s in order)
                {
                    float a = xr[s][k], b = xi[s][k];
                    float e = a * a + b * b;
                    pw[s][k] = aP * pw[s][k] + (1 - aP) * e;
                    int d = deg[s];
                    float hlr = table.Re[d][k], hli = table.Im[d][k], hrr = table.Re[TN + d][k], hri = table.Im[TN + d][k];
                    lr += a * hlr - b * hli; li += a * hli + b * hlr;
                    rr += a * hrr - b * hri; ri += a * hri + b * hrr;
                }
                // Strongest neighbouring pair, coherence of the pair (smoothed cross-spectrum).
                int bp = -1; float be = 0;
                for (int p = 0; p < pairs; p++)
                {
                    int i = order[p], j = order[(p + 1) % pairs];
                    float ar = xr[i][k], ai = xi[i][k], br = xr[j][k], bi = xi[j][k];
                    cRe[p][k] = aP * cRe[p][k] + (1 - aP) * (ar * br + ai * bi);
                    cIm[p][k] = aP * cIm[p][k] + (1 - aP) * (ai * br - ar * bi);
                    float e = ar * ar + ai * ai + br * br + bi * bi;
                    if (e > be) { be = e; bp = p; }
                }
                if (bp >= 0 && be > 1e-14f && k >= 2 && focus > 0.001f)
                {
                    int i = order[bp], j = order[(bp + 1) % pairs];
                    float ar = xr[i][k], ai = xi[i][k], br = xr[j][k], bi = xi[j][k];
                    float ei = ar * ar + ai * ai, ej = br * br + bi * bi;
                    float gamma;
                    if (ei < 0.01f * ej || ej < 0.01f * ei) gamma = 1;     // essentially one speaker: nothing to decide
                    else
                    {
                        float c2 = cRe[bp][k] * cRe[bp][k] + cIm[bp][k] * cIm[bp][k];
                        gamma = c2 / (pw[i][k] * pw[j][k] + 1e-20f);
                        if (gamma > 1) gamma = 1;
                    }
                    float w = focus * gamma;
                    // Direction the game meant: inverse of its pairwise panning (vector of the pair's smoothed amplitudes).
                    float mi_ = (float)Math.Sqrt(pw[i][k]), mj = (float)Math.Sqrt(pw[j][k]);
                    double ang = Math.Atan2(mi_ * uy[i] + mj * uy[j], mi_ * ux[i] + mj * ux[j]) * 180 / Math.PI;
                    double pos = (ang < 0 ? ang + 360 : ang) * Res;
                    int d = (int)pos % TN, d2 = (d + 1) % TN; float fr = (float)(pos - Math.Floor(pos));
                    if (w > 0.01f)
                    {
                        // One coherent source with the pair's energy, placed at that angle.
                        float sr = ar + br, si = ai + bi, sm = sr * sr + si * si;
                        float scale;
                        if (sm > 1e-20f) scale = (float)Math.Sqrt(be / sm);
                        else { sr = ei >= ej ? ar : br; si = ei >= ej ? ai : bi; scale = (float)Math.Sqrt(be / (sr * sr + si * si + 1e-30f)); }
                        float mr = sr * scale, mii = si * scale;
                        // Replace w of the pair's speaker rendering with the placed source.
                        int di = deg[i], dj = deg[j];
                        float pLr = ar * table.Re[di][k] - ai * table.Im[di][k] + br * table.Re[dj][k] - bi * table.Im[dj][k];
                        float pLi = ar * table.Im[di][k] + ai * table.Re[di][k] + br * table.Im[dj][k] + bi * table.Re[dj][k];
                        float pRr = ar * table.Re[TN + di][k] - ai * table.Im[TN + di][k] + br * table.Re[TN + dj][k] - bi * table.Im[TN + dj][k];
                        float pRi = ar * table.Im[TN + di][k] + ai * table.Re[TN + di][k] + br * table.Im[TN + dj][k] + bi * table.Re[TN + dj][k];
                        float g0 = 1 - fr;
                        float hLr = g0 * table.Re[d][k] + fr * table.Re[d2][k], hLi = g0 * table.Im[d][k] + fr * table.Im[d2][k];
                        float hRr = g0 * table.Re[TN + d][k] + fr * table.Re[TN + d2][k], hRi = g0 * table.Im[TN + d][k] + fr * table.Im[TN + d2][k];
                        float qLr = mr * hLr - mii * hLi, qLi = mr * hLi + mii * hLr;
                        float qRr = mr * hRr - mii * hRi, qRi = mr * hRi + mii * hRr;
                        // Partly placed: the two renderings of the same sound can partly cancel, so each ear keeps the
                        // energy a plain crossfade of their energies would have.
                        float u = 1 - w;
                        float xLr = u * pLr + w * qLr, xLi = u * pLi + w * qLi, xRr = u * pRr + w * qRr, xRi = u * pRi + w * qRi;
                        float gL = 1, gR = 1;
                        if (u > 0.001f)
                        {
                            gL = Comp(u * (pLr * pLr + pLi * pLi) + w * (qLr * qLr + qLi * qLi), xLr * xLr + xLi * xLi);
                            gR = Comp(u * (pRr * pRr + pRi * pRi) + w * (qRr * qRr + qRi * qRi), xRr * xRr + xRi * xRi);
                        }
                        lr += gL * xLr - pLr; li += gL * xLi - pLi;
                        rr += gR * xRr - pRr; ri += gR * xRi - pRi;
                    }
                    ringE[d / (5 * Res)] += w * be;
                    ringE[deg[i] / (5 * Res)] += (1 - w) * ei; ringE[deg[j] / (5 * Res)] += (1 - w) * ej;
                    foreach (int s in order) if (s != i && s != j) ringE[deg[s] / (5 * Res)] += xr[s][k] * xr[s][k] + xi[s][k] * xi[s][k];
                }
                else foreach (int s in order) ringE[deg[s] / (5 * Res)] += xr[s][k] * xr[s][k] + xi[s][k] * xi[s][k];
                yLr[k] = lr; yLi[k] = li; yRr[k] = rr; yRi[k] = ri;
            }

            // Both ears in one inverse FFT (Z = YL + i*YR), then overlap-add the full 512-sample result.
            for (int k = 0; k < K; k++) { re[k] = yLr[k] - yRi[k]; im[k] = yLi[k] + yRr[k]; }
            for (int k = K; k < N; k++) { int m = N - k; re[k] = yLr[m] + yRi[m]; im[k] = -yLi[m] + yRr[m]; }
            fft.Transform(re, im, true);
            const float sc = 1f / N;
            for (int i = 0; i < N; i++) { olaL[i] += re[i] * sc; olaR[i] += im[i] * sc; }

            // Radar ring (fast attack, ~0.3 s release).
            for (int b = 0; b < Sectors; b++)
            {
                ring[b] = Math.Max(ringE[b], ring[b] * 0.99063);  // ~0.3 s release at 1.7 ms hops
                RingDb[b] = (float)(10 * Math.Log10(ring[b] / (F * F * 0.375) + 1e-12));
            }
        }

        static float Comp(float target, float actual)
        {
            if (actual < 1e-30f) return 1;
            float g = (float)Math.Sqrt(target / actual);
            return g < 0.5f ? 0.5f : g > 2f ? 2f : g;
        }

        /// <summary>Head responses at every whole degree (horizontal plane), as 512-point spectra. Built once per set.</summary>
        sealed class Table
        {
            public float[][] Re, Im;                         // [ear*TN + angle*Res][bin]
            static readonly Dictionary<HrtfSet, Table> cache = new Dictionary<HrtfSet, Table>();

            public static bool Ready(HrtfSet set) { lock (cache) return cache.ContainsKey(set); }

            public static Table Get(HrtfSet set)
            {
                Table t;
                lock (cache) if (cache.TryGetValue(set, out t)) return t;
                lock (building)                              // one build at a time; never holds the lock the audio thread checks
                {
                    lock (cache) if (cache.TryGetValue(set, out t)) return t;
                    t = Build(set);
                    lock (cache) cache[set] = t;
                    return t;
                }
            }
            static readonly object building = new object();

            static Table Build(HrtfSet set)
            {
                var idx = Enumerable.Range(0, set.Az.Length).Where(i => Math.Abs(set.El[i]) < 0.5).OrderBy(i => set.Az[i]).ToArray();
                var az = idx.Select(i => (double)set.Az[i]).ToArray();
                // Onset (fractional) of each measured response, and the response moved to a common onset.
                const double D = 24;
                var aligned = new float[2][][]; var onset = new double[2][];
                for (int e = 0; e < 2; e++)
                {
                    aligned[e] = new float[idx.Length][]; onset[e] = new double[idx.Length];
                    for (int m = 0; m < idx.Length; m++)
                    {
                        var ir = e == 0 ? set.L[idx[m]] : set.R[idx[m]];
                        onset[e][m] = Onset(ir);
                        aligned[e][m] = Shift(ir, D - onset[e][m], set.Taps + 64);
                    }
                }
                var t = new Table { Re = new float[2 * TN][], Im = new float[2 * TN][] };
                var f = new Fft(N); var r = new float[N]; var i2 = new float[N];
                for (int q = 0; q < TN; q++)
                {
                    // Neighbouring measurements around d (wrapping at 360).
                    double d = q / (double)Res;
                    int hi = Array.FindIndex(az, a => a >= d); if (hi < 0) hi = 0;
                    int lo = (hi - 1 + az.Length) % az.Length;
                    double a0 = az[lo], a1 = az[hi]; if (a1 < a0) a1 += 360;
                    double dd = d < a0 ? d + 360 : d;
                    double w = a1 > a0 ? (dd - a0) / (a1 - a0) : 0;
                    if (az[hi] == d) { lo = hi; w = 0; }
                    for (int e = 0; e < 2; e++)
                    {
                        var A = aligned[e][lo]; var B = aligned[e][hi];
                        var mix = new float[A.Length];
                        for (int k = 0; k < mix.Length; k++) mix[k] = (float)((1 - w) * A[k] + w * B[k]);
                        double on = (1 - w) * onset[e][lo] + w * onset[e][hi];
                        var ir = w == 0 ? (e == 0 ? set.L[idx[lo]] : set.R[idx[lo]]) : Fade(Shift(mix, on - D, set.Taps + 16));
                        Array.Clear(r, 0, N); Array.Clear(i2, 0, N);
                        Array.Copy(ir, r, Math.Min(N, ir.Length));
                        f.Transform(r, i2, false);
                        var hr = new float[K]; var hm = new float[K];
                        Array.Copy(r, hr, K); Array.Copy(i2, hm, K);
                        t.Re[e * TN + q] = hr; t.Im[e * TN + q] = hm;
                    }
                }
                return t;
            }

            /// <summary>Short fade at the end, so the response stays shorter than the alias-free length.</summary>
            static float[] Fade(float[] x)
            {
                for (int i = 0; i < 16; i++) x[x.Length - 16 + i] *= (float)(0.5 + 0.5 * Math.Cos(Math.PI * (i + 1) / 17.0));
                return x;
            }

            /// <summary>First crossing of 20% of the peak, interpolated between samples.</summary>
            static double Onset(float[] ir)
            {
                float pk = 0; foreach (var v in ir) pk = Math.Max(pk, Math.Abs(v));
                float th = 0.2f * pk;
                for (int i = 1; i < ir.Length; i++)
                    if (Math.Abs(ir[i]) >= th) { float a = Math.Abs(ir[i - 1]), b = Math.Abs(ir[i]); return i - 1 + (th - a) / Math.Max(1e-9f, b - a); }
                return 0;
            }

            /// <summary>Delays (positive) or advances a response by a fractional number of samples (windowed sinc).</summary>
            static float[] Shift(float[] x, double by, int len)
            {
                var y = new float[len];
                const int H = 16;
                for (int n = 0; n < len; n++)
                {
                    double src = n - by;
                    int c = (int)Math.Floor(src);
                    double s = 0;
                    for (int k = c - H + 1; k <= c + H; k++)
                    {
                        if (k < 0 || k >= x.Length) continue;
                        double t = src - k;
                        double sinc = Math.Abs(t) < 1e-9 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                        s += x[k] * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * t / H));
                    }
                    y[n] = (float)s;
                }
                return y;
            }
        }
    }
}
