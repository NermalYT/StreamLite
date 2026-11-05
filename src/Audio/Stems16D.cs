using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// True 16D (4.4): instead of one stereo picture circling your head, the song is pulled apart and every part gets its
    /// own place in a sphere of 16 virtual speakers (8 at ear level, 4 above, 4 below), each wandering slowly on its own:
    /// the boomy bass low behind you, the vocals in front, the drums off to one side, what the mix pans left and right on
    /// either side of you, the air above. Every frequency of every 20 ms is split by what it sounds like, by signal
    /// processing alone (no AI model, about 15 ms of delay): by pitch (bass, air), by whether it just hit (drums), by
    /// whether it's the same in both channels (what mixes put in the middle: vocals) and by which channel it's in. Not studio
    /// stems: a bass note with a guitar on it splits by frequency, not by instrument. The parts add back up to the song.
    /// </summary>
    internal sealed class Stems16D
    {
        const int Win = 960, Hop = SpatialProcessor.B, N = 2048, K = N / 2 + 1;
        public const int Latency = Win - Hop;
        public const int Bass = 0, Vocals = 1, Drums = 2, Left = 3, Right = 4, Air = 5, StemCount = 6;
        public const int Speakers = 16;
        const double Fs = SpatialProcessor.Fs;
        const int Near = 3;

        struct V3 { public double X, Y, Z; public double Dot(V3 b) { return X * b.X + Y * b.Y + Z * b.Z; } }
        static V3 Dir(double az, double el)
        {
            double a = az * Math.PI / 180, e = el * Math.PI / 180;
            return new V3 { X = Math.Cos(e) * Math.Cos(a), Y = Math.Cos(e) * Math.Sin(a), Z = Math.Sin(e) };
        }

        // The 16 virtual speakers: every 45 degrees at ear level, every 90 above and below.
        static readonly double[][] Layout = MakeLayout();
        static double[][] MakeLayout()
        {
            var l = new List<double[]>();
            for (int i = 0; i < 8; i++) l.Add(new double[] { i * 45, 0 });
            for (int i = 0; i < 4; i++) l.Add(new double[] { 45 + i * 90, 40 });
            for (int i = 0; i < 4; i++) l.Add(new double[] { 45 + i * 90, -35 });
            return l.ToArray();
        }
        static readonly V3[] SpeakerDir = Layout.Select(a => Dir(a[0], a[1])).ToArray();

        /// <summary>The head's response at each virtual speaker, as spectra, built once per head in the background.</summary>
        sealed class Table
        {
            public float[][] Re = new float[Speakers * 2][], Im = new float[Speakers * 2][];
            public float Scale;
        }
        static readonly Dictionary<HrtfSet, Table> tables = new Dictionary<HrtfSet, Table>();
        static readonly HashSet<HrtfSet> building = new HashSet<HrtfSet>();

        /// <summary>This head has the elevations a sphere needs (a custom 7-speaker file doesn't).</summary>
        public static bool Supports(HrtfSet set) { return set != null && !set.SpeakerSet; }
        public static bool IsReady(HrtfSet set) { lock (tables) return tables.ContainsKey(set); }
        public static void Prepare(HrtfSet set)
        {
            lock (tables) { if (tables.ContainsKey(set) || !building.Add(set)) return; }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Table t = null;
                try { t = Build(set); } catch { }
                lock (tables) { building.Remove(set); if (t != null) tables[set] = t; }
            });
        }

        static Table Build(HrtfSet set)
        {
            var t = new Table(); var fft = new Fft(N); var re = new float[N]; var im = new float[N];
            double power = 0; int count = 0;
            for (int s = 0; s < Speakers; s++)
            {
                float[] l, r; set.Build(Layout[s][0], Layout[s][1], 0, out l, out r);
                for (int e = 0; e < 2; e++)
                {
                    var ir = e == 0 ? l : r;
                    Array.Clear(re, 0, N); Array.Clear(im, 0, N);
                    Array.Copy(ir, re, Math.Min(ir.Length, N - Win));   // shorter than what a frame leaves free: no wrap-around
                    fft.Transform(re, im, false);
                    var hr = new float[K]; var hi = new float[K];
                    Array.Copy(re, hr, K); Array.Copy(im, hi, K);
                    t.Re[s * 2 + e] = hr; t.Im[s * 2 + e] = hi;
                    for (int k = 0; k < K; k++)
                    {
                        double f = k * Fs / N;
                        if (f >= 200 && f <= 8000) { power += hr[k] * (double)hr[k] + hi[k] * (double)hi[k]; count++; }
                    }
                }
            }
            // As loud as the song was (checked in test/Music16DTest.cs).
            t.Scale = (float)(1 / Math.Sqrt(power / Math.Max(1, count)));
            return t;
        }

        readonly Fft fft = new Fft(N);
        readonly float[] window = new float[Win], histL = new float[Win], histR = new float[Win];
        readonly float[] re = new float[N], im = new float[N];
        readonly float[] xLr = new float[K], xLi = new float[K], xRr = new float[K], xRi = new float[K];
        readonly float[] olaL = new float[N], olaR = new float[N];
        readonly float[] pL = new float[K], pR = new float[K], cr = new float[K];
        readonly float[][] zr = new float[Speakers][], zi = new float[Speakers][];
        readonly bool[] used = new bool[Speakers];
        readonly float[] yLr = new float[K], yLi = new float[K], yRr = new float[K], yRi = new float[K];
        readonly float[] bassW = new float[K], airW = new float[K];
        // Where a hit just happened, band by band: the drums.
        const int HitBands = 24;
        readonly int[] bandOf = new int[K];
        readonly double[] bandE = new double[HitBands], bandAvg = new double[HitBands], bandHit = new double[HitBands];
        // Where each part is: two sources (the song's left and right, a little apart), its nearest virtual speakers and gains.
        readonly int[,] spk = new int[StemCount * 2, Near];
        readonly float[,] gain = new float[StemCount * 2, Near];
        readonly double[] partAz = new double[StemCount], partEl = new double[StemCount];
        readonly float[] w = new float[StemCount];
        double time;
        float bassLeft = 0.7f, bassRight = 0.7f; double bassDelayL, bassDelayR;

        public Stems16D()
        {
            for (int i = 0; i < Win; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Win));
            for (int s = 0; s < Speakers; s++) { zr[s] = new float[K]; zi[s] = new float[K]; }
            for (int k = 0; k < K; k++)
            {
                double f = k * Fs / N;
                bassW[k] = (float)SmoothFall(f, 120, 200);
                airW[k] = (float)(1 - SmoothFall(f, 7000, 10000));
                double pos = Math.Log(Math.Max(150, Math.Min(16000, f)) / 150) / Math.Log(16000.0 / 150);
                bandOf[k] = Math.Min(HitBands - 1, (int)(pos * HitBands));
            }
        }

        /// <summary>1 below a, 0 above b, smooth between.</summary>
        static double SmoothFall(double x, double a, double b)
        {
            double t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return 1 - t * t * (3 - 2 * t);
        }

        static float Ramp(float x, float a, float b)
        {
            float t = (x - a) / (b - a); t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        /// <summary>Where a part is right now (azimuth anticlockwise, elevation; degrees).</summary>
        public void PartAt(int part, out double az, out double el) { az = partAz[part]; el = partEl[part]; }

        /// <summary>
        /// Each part's slow path: its own area, its own wandering, and the whole picture turning very slowly, so it never
        /// settles and never becomes one circle. seconds = 16D's speed (the old "one full circle").
        /// </summary>
        void Place(double seconds)
        {
            double p = 2 * Math.PI * time / Math.Max(2, seconds), turn = 360 * time / (Math.Max(2, seconds) * 8);
            At(Bass, 200 + 40 * Math.Sin(p * 0.13) + turn, -20);
            At(Vocals, 28 * Math.Sin(p * 0.31) + turn * 0.25, 8 + 8 * Math.Sin(p * 0.17));
            At(Drums, 290 + 55 * Math.Sin(p * 0.23) + turn, 10 * Math.Sin(p * 0.19));
            At(Left, 100 + 35 * Math.Sin(p * 0.29) + turn, 15 * Math.Sin(p * 0.11));
            At(Right, 255 + 35 * Math.Sin(p * 0.37 + 1) + turn, 15 * Math.Sin(p * 0.13 + 2));
            At(Air, 150 + turn * 2, 45);
            for (int s = 0; s < StemCount; s++)
            {
                if (s == Bass) continue;
                double spread = s == Vocals ? 8 : s == Left || s == Right ? 12 : 20;
                Aim(s * 2, partAz[s] + spread, partEl[s]);
                Aim(s * 2 + 1, partAz[s] - spread, partEl[s]);
            }
            // The bass, which no head response can place (its wave is longer than a head): the time and level between the ears.
            double a = partAz[Bass] * Math.PI / 180, side = Math.Sin(a);   // + = left
            bassLeft = (float)Math.Sqrt(0.5 * (1 + 0.72 * side)); bassRight = (float)Math.Sqrt(0.5 * (1 - 0.72 * side));
            bassDelayL = side < 0 ? -side * 0.0006 : 0; bassDelayR = side > 0 ? side * 0.0006 : 0;
        }

        void At(int part, double az, double el) { partAz[part] = ((az % 360) + 360) % 360; partEl[part] = el; }

        /// <summary>A source's nearest three virtual speakers, with gains of constant power.</summary>
        void Aim(int src, double az, double el)
        {
            var d = Dir(az, el);
            int b0 = -1, b1 = -1, b2 = -1; double v0 = -1, v1 = -1, v2 = -1;
            for (int s = 0; s < Speakers; s++)
            {
                double c = d.Dot(SpeakerDir[s]);
                if (c > v0) { v2 = v1; b2 = b1; v1 = v0; b1 = b0; v0 = c; b0 = s; }
                else if (c > v1) { v2 = v1; b2 = b1; v1 = c; b1 = s; }
                else if (c > v2) { v2 = c; b2 = s; }
            }
            // The third speaker's share is 0 where the source would leave it for a fourth: nothing jumps as it moves on.
            double x0 = Math.Max(0, v0 - v2 + 1e-3), x1 = Math.Max(0, v1 - v2 + 1e-3);
            double g0 = x0 * x0, g1 = x1 * x1;
            double norm = Math.Sqrt(g0 * g0 + g1 * g1) + 1e-9;
            spk[src, 0] = b0; spk[src, 1] = b1; spk[src, 2] = b2;
            gain[src, 0] = (float)(g0 / norm); gain[src, 1] = (float)(g1 / norm); gain[src, 2] = 0;
        }

        /// <summary>One block (Hop samples) of the song -> the two ears, Latency samples later.</summary>
        public void Process(HrtfSet set, float[] l, float[] r, float[] lfe, float[] outL, float[] outR, double seconds)
        {
            Table t; lock (tables) if (!tables.TryGetValue(set, out t)) { Array.Copy(l, outL, Hop); Array.Copy(r, outR, Hop); return; }
            Array.Copy(histL, Hop, histL, 0, Win - Hop); Array.Copy(histR, Hop, histR, 0, Win - Hop);
            const float c = 0.7071f;
            for (int i = 0; i < Hop; i++) { histL[Win - Hop + i] = l[i] + c * lfe[i]; histR[Win - Hop + i] = r[i] + c * lfe[i]; }
            for (int i = 0; i < Win; i++) { re[i] = histL[i] * window[i]; im[i] = histR[i] * window[i]; }
            Array.Clear(re, Win, N - Win); Array.Clear(im, Win, N - Win);
            fft.Transform(re, im, false);
            for (int k = 0; k < K; k++)
            {
                int m = (N - k) & (N - 1);
                float ar = re[k], ai = im[k], br = re[m], bi = im[m];
                xLr[k] = 0.5f * (ar + br); xLi[k] = 0.5f * (ai - bi);
                xRr[k] = 0.5f * (ai + bi); xRi[k] = 0.5f * (br - ar);
            }
            time += Hop / Fs;
            Place(seconds);
            Hits();
            Split();
            Hear(t);
            for (int i = 0; i < Hop; i++) { outL[i] = olaL[i]; outR[i] = olaR[i]; }
            Array.Copy(olaL, Hop, olaL, 0, N - Hop); Array.Clear(olaL, N - Hop, Hop);
            Array.Copy(olaR, Hop, olaR, 0, N - Hop); Array.Clear(olaR, N - Hop, Hop);
        }

        /// <summary>Band by band: how much more is there than a moment ago.</summary>
        void Hits()
        {
            Array.Clear(bandE, 0, HitBands);
            for (int k = 0; k < K; k++) bandE[bandOf[k]] += xLr[k] * xLr[k] + xLi[k] * xLi[k] + xRr[k] * xRr[k] + xRi[k] * xRi[k];
            for (int b = 0; b < HitBands; b++)
            {
                double ratio = bandE[b] / (bandAvg[b] + 1e-12);
                double h = Math.Max(0, Math.Min(1, (ratio - 2.2) / 3));
                bandHit[b] = Math.Max(h, bandHit[b] * 0.82);   // about 25 ms
                bandAvg[b] += (bandE[b] > bandAvg[b] ? 0.04 : 0.12) * (bandE[b] - bandAvg[b]);
            }
        }

        /// <summary>Every frequency to its parts (they add up to 1), into the virtual speakers those parts are at.</summary>
        void Split()
        {
            for (int s = 0; s < Speakers; s++) used[s] = false;
            for (int src = 2; src < StemCount * 2; src++)
                for (int j = 0; j < Near; j++)
                    if (gain[src, j] > 0 && !used[spk[src, j]]) { used[spk[src, j]] = true; Array.Clear(zr[spk[src, j]], 0, K); Array.Clear(zi[spk[src, j]], 0, K); }
            Array.Clear(yLr, 0, K); Array.Clear(yLi, 0, K); Array.Clear(yRr, 0, K); Array.Clear(yRi, 0, K);
            const float a = 0.35f;
            for (int k = 0; k < K; k++)
            {
                float lr = xLr[k], li = xLi[k], rr = xRr[k], ri = xRi[k];
                float el = lr * lr + li * li, er = rr * rr + ri * ri;
                pL[k] += a * (el - pL[k]); pR[k] += a * (er - pR[k]); cr[k] += a * (lr * rr + li * ri - cr[k]);
                float sum = pL[k] + pR[k] + 1e-18f;
                float coh = 2 * cr[k] / sum, pan = (pL[k] - pR[k]) / sum;
                float bass = bassW[k], air = airW[k], hit = (float)bandHit[bandOf[k]];
                float rest = 1 - bass - air;
                float drum = (rest + air) * hit;
                float restNoHit = rest * (1 - hit), airNoHit = air * (1 - hit);
                float absPan = pan < 0 ? -pan : pan;
                float centre = Ramp(coh, 0.55f, 0.88f) * (1 - Ramp(absPan, 0.15f, 0.42f));
                float sides = restNoHit * (1 - centre);
                float leftShare = 0.5f + 0.5f * Math.Max(-1f, Math.Min(1f, pan * 2.2f));
                w[Vocals] = restNoHit * centre; w[Drums] = drum; w[Left] = sides * leftShare; w[Right] = sides * (1 - leftShare); w[Air] = airNoHit;
                for (int s = 1; s < StemCount; s++)
                {
                    float m = w[s];
                    if (m < 1e-4f) continue;
                    for (int j = 0; j < Near; j++)
                    {
                        float g = gain[s * 2, j] * m;
                        if (g > 0) { var ar = zr[spk[s * 2, j]]; var ai = zi[spk[s * 2, j]]; ar[k] += g * lr; ai[k] += g * li; }
                        g = gain[s * 2 + 1, j] * m;
                        if (g > 0) { var ar = zr[spk[s * 2 + 1, j]]; var ai = zi[spk[s * 2 + 1, j]]; ar[k] += g * rr; ai[k] += g * ri; }
                    }
                }
                if (bass > 1e-4f)
                {
                    // The bass in one piece, straight to both ears at its own time and level.
                    float mr = bass * (lr + rr) * 0.7071f, mi = bass * (li + ri) * 0.7071f;
                    double wt = 2 * Math.PI * k / N * Fs;
                    float cl = (float)Math.Cos(wt * bassDelayL), sl = (float)Math.Sin(wt * bassDelayL), c2 = (float)Math.Cos(wt * bassDelayR), s2 = (float)Math.Sin(wt * bassDelayR);
                    yLr[k] += bassLeft * (mr * cl + mi * sl); yLi[k] += bassLeft * (mi * cl - mr * sl);
                    yRr[k] += bassRight * (mr * c2 + mi * s2); yRi[k] += bassRight * (mi * c2 - mr * s2);
                }
            }
        }

        /// <summary>The virtual speakers through the head, both ears in one inverse transform, overlapped into the output.</summary>
        void Hear(Table t)
        {
            float scale = t.Scale;
            for (int s = 0; s < Speakers; s++)
            {
                if (!used[s]) continue;
                float[] zre = zr[s], zim = zi[s], hLr = t.Re[s * 2], hLi = t.Im[s * 2], hRr = t.Re[s * 2 + 1], hRi = t.Im[s * 2 + 1];
                for (int k = 0; k < K; k++)
                {
                    float a = zre[k], b = zim[k];
                    if (a == 0 && b == 0) continue;
                    yLr[k] += scale * (a * hLr[k] - b * hLi[k]); yLi[k] += scale * (a * hLi[k] + b * hLr[k]);
                    yRr[k] += scale * (a * hRr[k] - b * hRi[k]); yRi[k] += scale * (a * hRi[k] + b * hRr[k]);
                }
            }
            for (int k = 0; k < K; k++) { re[k] = yLr[k] - yRi[k]; im[k] = yLi[k] + yRr[k]; }
            for (int k = K; k < N; k++) { int m = N - k; re[k] = yLr[m] + yRi[m]; im[k] = -yLi[m] + yRr[m]; }
            fft.Transform(re, im, true);
            const float sc = 0.5f / N;   // the window adds up to 2 at this overlap
            for (int i = 0; i < N; i++) { olaL[i] += re[i] * sc; olaR[i] += im[i] * sc; }
        }
    }
}
