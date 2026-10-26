using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// "Studio voice": makes any microphone sound like a broadcast mic. While you talk it learns the long-term spectrum
    /// of your voice through this mic (1/3-octave bands, after noise removal) and compares it with the spectrum of
    /// speech through a good close mic (the standard long-term speech spectrum, ANSI S3.5, with a broadcast mic's
    /// warmth, presence and air). The difference is the mic's (and the room's) colouring - thin, boxy, harsh, dull - and
    /// it's taken out: a smooth correction, at most -10 / +8 dB, learned in seconds and then adjusted slowly.
    /// A mic that cuts the treble off (headsets, laptops, Bluetooth, cheap USB mics stop at 7-13 kHz) gets it back:
    /// the octave below the cut is shifted up (single-sideband, phase-continuous) and shaped to how speech continues,
    /// following every syllable - "s" and "t" get their air back. Plosive pops ("p", "b" into the capsule) are caught
    /// and their rumble taken out for that moment. The learned profile is kept per mic.
    /// No added delay: the sound only goes through filters (a 25-band graphic EQ solved to the correction, a Hilbert
    /// pair for the shift, a crossover for the pops). The short-time spectrum (512-point frames, 75% overlap) is only
    /// listened to, on the side.
    /// </summary>
    internal sealed class StudioVoice
    {
        public const int Fs = 48000, N = 512, R = 128, K = N / 2 + 1;
        /// <summary>Delay it adds: none (filters only; the spectrum is analysed on the side).</summary>
        public const float DelayMs = 0;

        public volatile bool Enabled = true;
        /// <summary>The stage runs.</summary>
        public bool Active { get { return Enabled; } }
        /// <summary>0..1: how far the mic is corrected towards the broadcast voice (and how much treble is restored).</summary>
        public volatile float Strength = 0.75f;
        /// <summary>A/B compare: everything passes untouched (it keeps learning).</summary>
        public volatile bool Bypass;

        // 1/3-octave bands, 80 Hz .. 20 kHz.
        static readonly double[] Centres = { 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };
        static readonly int B = Centres.Length;
        /// <summary>Speech through a good close mic, band levels in dB (only the shape counts).</summary>
        static readonly double[] Target = MakeTarget();

        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], re = new float[N], im = new float[N];
        readonly int[] bandOf = new int[K];
        readonly double[] bandPow = new double[B], ltas = new double[B], corrDb = new double[B];
        // Scratch for Update (no allocations on the audio thread) and the two halves of MatchedDb.
        readonly double[] um = new double[B], ud = new double[B], ug = new double[B], usm = new double[B], shownA = new double[B], shownB = new double[B];
        bool shownFlip;
        int pos; long frame;
        double floorDb = -60, speechFrames, speechNow;
        readonly object profileLock = new object();

        // Plosives: the rumble below 200 Hz against the voice band, per block.
        readonly Biquad lp1 = Biquad.LowPass(180, Fs), lp2 = Biquad.LowPass(180, Fs), hp1 = Biquad.HighPass(180, Fs), hp2 = Biquad.HighPass(180, Fs), bp1 = Biquad.HighPass(300, Fs), bp2 = Biquad.LowPass(3000, Fs);
        double lfRecent = 1, popGain = 1;
        bool popping;
        float[] low = new float[480], high = new float[480], src = new float[480], qd = new float[480], add = new float[480];
        /// <summary>Plosives caught so far (status).</summary>
        public int Pops;

        public StudioVoice()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < K; k++)
            {
                double f = Math.Max(1, k * (double)Fs / N), best = double.MaxValue;
                for (int b = 0; b < B; b++) { double d = Math.Abs(Math.Log(f / Centres[b])); if (d < best) { best = d; bandOf[k] = b; } }
            }
            for (int b = 0; b < B; b++) { ltas[b] = double.NaN; eqB0[b] = 1; }
        }

        static double[] MakeTarget()
        {
            // ANSI S3.5-1997 standard speech spectrum level (normal effort, dB/Hz) at 160 Hz .. 8 kHz, as band levels
            // (+ 10 log of each 1/3-octave band's width), continued below and above as speech continues.
            double[] f = { 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };
            double[] s = { 32.41, 34.48, 34.75, 33.98, 34.59, 34.27, 32.06, 28.30, 25.01, 23.00, 20.15, 17.32, 13.18, 11.55, 9.33, 5.31, 2.59, 1.13 };
            var t = new double[B];
            for (int b = 0; b < B; b++)
            {
                double c = Centres[b]; int i = Array.IndexOf(f, c);
                if (i >= 0) t[b] = s[i] + 10 * Math.Log10(0.2316 * c);
            }
            double t160 = t[3], t8k = t[20];
            t[2] = t160 - 1.5; t[1] = t160 - 4.5; t[0] = t160 - 9;                     // below the voice's lowest notes
            t[21] = t8k - 2.5; t[22] = t8k - 5.5; t[23] = t8k - 9.5; t[24] = t8k - 15;    // the air above 8 kHz
            // A broadcast mic up close: a little warmth, presence and air.
            for (int b = 0; b < B; b++)
            {
                double c = Centres[b];
                if (c >= 125 && c <= 200) t[b] += 2;
                if (c >= 2500 && c <= 5000) t[b] += 1.5;
                if (c >= 10000 && c <= 16000) t[b] += 2;
            }
            return t;
        }

        /// <summary>In place, in any block size. Off: untouched.</summary>
        public void Process(float[] buf, int n)
        {
            if (!Active) return;
            if (src.Length < n) { src = new float[n]; qd = new float[n]; add = new float[n]; }
            Plosives(buf, n);
            // Listened to on the side: the mic as it is (after the pops), every 128 samples.
            for (int i = 0; i < n; i++)
            {
                inBuf[N - R + pos] = buf[i];
                if (++pos == R) { pos = 0; Frame(); }
            }
            if (Bypass) return;
            Equalize(buf, n);
            Rebuild(buf, n);
        }

        /// <summary>
        /// A pop is a sudden burst of rumble (12 dB above the last 50 ms), four times the voice band or more
        /// (a deep vowel isn't: its voice band is strong too). While it lasts the rumble is pushed down to the voice's
        /// level. The rumble is split off with a Linkwitz-Riley crossover (both halves add back up exactly), so turning
        /// it down really takes it out.
        /// </summary>
        void Plosives(float[] buf, int n)
        {
            double lf = 0, mf = 0;
            if (low.Length < n) { low = new float[n]; high = new float[n]; }
            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                float l = lp2.Process(lp1.Process(x)); low[i] = l; lf += l * (double)l;
                high[i] = hp2.Process(hp1.Process(x));
                float m = bp2.Process(bp1.Process(x)); mf += m * (double)m;
            }
            lf /= n; mf /= n;
            double ms = n * 1000.0 / Fs;
            if (Bypass) { popping = false; popGain = 1; lfRecent = Math.Max(lf, lfRecent * Math.Exp(-ms / 50)); return; }   // A/B: untouched (not even the crossover's phase)
            bool burst = lf > lfRecent * 16 && lf > mf * 4 && lf > 1e-7;
            if (!Bypass && burst && !popping) { popping = true; Pops++; }
            if (popping && lf < mf * 2) popping = false;
            double want = popping && !Bypass ? Math.Max(0.06, Math.Sqrt(mf * 1.5 / lf)) : 1;
            if (want < popGain) popGain = want;
            else popGain += (want - popGain) * (1 - Math.Exp(-ms / 40));
            lfRecent = Math.Max(lf, lfRecent * Math.Exp(-ms / 50));
            float g = (float)popGain;
            for (int i = 0; i < n; i++) buf[i] = high[i] + g * low[i];
        }

        void Frame()
        {
            frame++;
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
            Array.Copy(inBuf, R, inBuf, 0, N - R);
            fft.Transform(re, im, false);
            Array.Clear(bandPow, 0, B);
            double voice = 0;
            for (int k = 1; k < K; k++)
            {
                double p = re[k] * (double)re[k] + im[k] * (double)im[k];
                bandPow[bandOf[k]] += p;
                double f = k * (double)Fs / N; if (f >= 300 && f <= 3400) voice += p;
            }
            // Talking? The voice band well above its quiet level (the noise remover ran before this).
            double vDb = 10 * Math.Log10(voice + 1e-20);
            floorDb = vDb < floorDb ? floorDb + (vDb - floorDb) * 0.1 : floorDb + 0.004;
            speechNow = Math.Max(0, Math.Min(1, (vDb - floorDb - 12) / 8));
            if (speechNow > 0.5 && vDb > -95) Learn();
        }

        /// <summary>Your voice's long-term spectrum through this mic: quick at first (~5 s of talking), then over ~30 s.</summary>
        void Learn()
        {
            speechFrames++;
            double a = speechFrames < 2000 ? 1.0 / speechFrames : 1.0 / (Fs / R * 30.0);
            lock (profileLock)
                for (int b = 0; b < B; b++)
                {
                    double l = 10 * Math.Log10(bandPow[b] + 1e-20);
                    ltas[b] = double.IsNaN(ltas[b]) ? l : ltas[b] + a * (l - ltas[b]);
                }
            if (frame % 16 == 0) Update();
        }

        /// <summary>The correction (per band) and where the mic's treble stops, from the learned spectrum.</summary>
        void Update()
        {
            var m = um;
            lock (profileLock) Array.Copy(ltas, m, B);
            for (int b = 0; b < B; b++) if (double.IsNaN(m[b])) return;
            // Level: matched over the core of the voice (300 Hz - 3 kHz), so loudness is left to auto level.
            double off = 0; int c = 0;
            for (int b = 0; b < B; b++) if (Centres[b] >= 315 && Centres[b] <= 3150) { off += Target[b] - m[b]; c++; }
            off /= c;
            var d = ud;
            for (int b = 0; b < B; b++) d[b] = Target[b] - (m[b] + off);   // + = the mic is weak there
            // Where the treble stops: the first band from 5 kHz on more than 15 dB short of speech - from there the
            // treble is rebuilt instead of boosted (boosting what isn't there only boosts noise).
            int cut = B;
            for (int b = 0; b < B; b++) if (Centres[b] >= 5000 && d[b] > 15) { cut = b; break; }
            double cutHz = 24000;
            if (cut < B)
            {
                // The cut itself: where the shortfall passes 6 dB, between bands on a log-frequency scale.
                int hi = cut; while (hi > 1 && Centres[hi - 1] >= 4000 && d[hi - 1] >= 6) hi--;
                double d0 = d[hi - 1], d1 = d[hi];
                double t = d1 > d0 ? Math.Max(0, Math.Min(1, (6 - d0) / (d1 - d0))) : 0;
                cutHz = Centres[hi - 1] * Math.Pow(Centres[hi] / Centres[hi - 1], t);
            }
            // Low end: never boost below the voice itself (no content there, only rumble).
            double core = 0; for (int b = 0; b < B; b++) if (Centres[b] >= 250 && Centres[b] <= 500) core = Math.Max(core, m[b]);
            var g = ug;
            for (int b = 0; b < B; b++)
            {
                double v = d[b];
                if (Centres[b] >= cutHz) v = 0;
                else if (Centres[b] < 160) v = Math.Min(v, m[b] > core - 25 ? 4 : 0);
                g[b] = Math.Max(-10, Math.Min(8, v));
            }
            // Smooth across bands (no narrow notches or peaks: it's the mic's character, not the voice's).
            var sm = usm;
            for (int b = 0; b < B; b++) { double s = 0, w = 0; for (int j = -1; j <= 1; j++) { int q = b + j; if (q < 0 || q >= B || Centres[q] >= cutHz) continue; double ww = j == 0 ? 2 : 1; s += g[q] * ww; w += ww; } sm[b] = w > 0 ? s / w : 0; }
            // Fade in over the first 5 s of talking, then follow slowly.
            double ramp = Math.Min(1, speechFrames / (Fs / R * 5.0)), st = Strength;
            lock (profileLock)
            {
                for (int b = 0; b < B; b++) corrDb[b] = sm[b] * ramp * st;
                CutoffHz = cut < B ? cutHz : 0;
            }
            SolveEq(corrDb);
            SetCut(cut < B ? cutHz : 0);
            var shown = shownFlip ? shownA : shownB; shownFlip = !shownFlip;
            Array.Copy(corrDb, shown, B);
            MatchedDb = shown;
        }

        static double TargetAt(double f)
        {
            if (f <= Centres[0]) return Target[0];
            for (int b = 1; b < B; b++)
                if (f <= Centres[b]) { double t = Math.Log(f / Centres[b - 1]) / Math.Log(Centres[b] / Centres[b - 1]); return Target[b - 1] + t * (Target[b] - Target[b - 1]); }
            return Target[B - 1];
        }

        // ------------------------------------------------------------------ the correction: a 25-band graphic EQ

        // One peaking filter per band (a high shelf for the top one), their gains solved so the whole chain lands on
        // the correction at every band centre (neighbouring filters overlap, so each band's gain isn't simply its
        // correction). Coefficients per band; the filters run band by band over each block.
        const double EqQ = 2.5;
        static readonly double[] eqCos = new double[B], eqSin = new double[B];
        static readonly double[] atC1, atS1, atC2, atS2;   // [band * B + centre]: cos/sin of w and 2w at each centre
        static readonly double[,] eqInv;                   // per dB of each filter's gain -> dB at each centre, inverted
        readonly double[] eqB0 = new double[B], eqB1 = new double[B], eqB2 = new double[B], eqA1 = new double[B], eqA2 = new double[B], eqZ1 = new double[B], eqZ2 = new double[B];
        readonly double[] solveG = new double[B], solveR = new double[B], solveD = new double[B];
        bool eqFlat = true;

        static StudioVoice()
        {
            for (int b = 0; b < B; b++) { double w0 = 2 * Math.PI * EqFreq(b) / Fs; eqCos[b] = Math.Cos(w0); eqSin[b] = Math.Sin(w0); }
            atC1 = new double[B]; atS1 = new double[B]; atC2 = new double[B]; atS2 = new double[B];
            for (int i = 0; i < B; i++) { double w = 2 * Math.PI * Centres[i] / Fs; atC1[i] = Math.Cos(w); atS1[i] = Math.Sin(w); atC2[i] = Math.Cos(2 * w); atS2[i] = Math.Sin(2 * w); }
            // The interaction: each filter at +6 dB, its response at every centre (per dB), then inverted.
            var m = new double[B, B]; var c = new double[5];
            for (int j = 0; j < B; j++)
            {
                Design(j, 6, c);
                for (int i = 0; i < B; i++) m[i, j] = ResponseDb(c, i) / 6;
            }
            eqInv = Invert(m);
        }

        static double EqFreq(int b) { return b == B - 1 ? 17000 : Centres[b]; }

        /// <summary>Band b's filter at gain dB: b0 b1 b2 a1 a2 (normalized).</summary>
        static void Design(int b, double db, double[] c)
        {
            double A = Math.Pow(10, db / 40), cs = eqCos[b], s = eqSin[b];
            double n0, n1, n2, d0, d1, d2;
            if (b == B - 1)
            {
                double alpha = s / (2 * 0.707), sa = 2 * Math.Sqrt(A) * alpha;
                n0 = A * ((A + 1) + (A - 1) * cs + sa); n1 = -2 * A * ((A - 1) + (A + 1) * cs); n2 = A * ((A + 1) + (A - 1) * cs - sa);
                d0 = (A + 1) - (A - 1) * cs + sa; d1 = 2 * ((A - 1) - (A + 1) * cs); d2 = (A + 1) - (A - 1) * cs - sa;
            }
            else
            {
                double alpha = s / (2 * EqQ);
                n0 = 1 + alpha * A; n1 = -2 * cs; n2 = 1 - alpha * A; d0 = 1 + alpha / A; d1 = -2 * cs; d2 = 1 - alpha / A;
            }
            c[0] = n0 / d0; c[1] = n1 / d0; c[2] = n2 / d0; c[3] = d1 / d0; c[4] = d2 / d0;
        }

        static double ResponseDb(double[] c, int at)
        {
            double c1 = atC1[at], s1 = atS1[at], c2 = atC2[at], s2 = atS2[at];
            double nr = c[0] + c[1] * c1 + c[2] * c2, ni = -(c[1] * s1 + c[2] * s2);
            double dr = 1 + c[3] * c1 + c[4] * c2, di = -(c[3] * s1 + c[4] * s2);
            return 10 * Math.Log10(Math.Max(1e-18, (nr * nr + ni * ni) / (dr * dr + di * di)));
        }

        static double[,] Invert(double[,] a)
        {
            int n = a.GetLength(0);
            var m = (double[,])a.Clone(); var inv = new double[n, n];
            for (int i = 0; i < n; i++) inv[i, i] = 1;
            for (int col = 0; col < n; col++)
            {
                int p = col; for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[p, col])) p = r;
                if (p != col) for (int k = 0; k < n; k++) { double t = m[col, k]; m[col, k] = m[p, k]; m[p, k] = t; t = inv[col, k]; inv[col, k] = inv[p, k]; inv[p, k] = t; }
                double d = m[col, col];
                for (int k = 0; k < n; k++) { m[col, k] /= d; inv[col, k] /= d; }
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col]; if (f == 0) continue;
                    for (int k = 0; k < n; k++) { m[r, k] -= f * m[col, k]; inv[r, k] -= f * inv[col, k]; }
                }
            }
            return inv;
        }

        readonly double[] coef = new double[5];

        /// <summary>The filters' gains for this correction: solved, then corrected twice by what the chain really does.</summary>
        void SolveEq(double[] want)
        {
            bool flat = true; for (int b = 0; b < B; b++) if (Math.Abs(want[b]) > 0.05) flat = false;
            if (flat) { if (!eqFlat) { for (int b = 0; b < B; b++) { eqB0[b] = 1; eqB1[b] = eqB2[b] = eqA1[b] = eqA2[b] = 0; } eqFlat = true; } return; }
            var g = solveG; var r = solveR;
            for (int i = 0; i < B; i++) { double s = 0; for (int j = 0; j < B; j++) s += eqInv[i, j] * want[j]; g[i] = s; }
            for (int pass = 0; pass < 2; pass++)
            {
                Array.Clear(r, 0, B);
                for (int j = 0; j < B; j++) { Design(j, g[j], coef); for (int i = 0; i < B; i++) r[i] += ResponseDb(coef, i); }
                for (int i = 0; i < B; i++) { double s = 0; for (int j = 0; j < B; j++) s += eqInv[i, j] * (want[j] - r[j]); solveD[i] = s; }
                for (int i = 0; i < B; i++) g[i] = Math.Max(-24, Math.Min(24, g[i] + solveD[i]));
            }
            for (int b = 0; b < B; b++) { Design(b, g[b], coef); eqB0[b] = coef[0]; eqB1[b] = coef[1]; eqB2[b] = coef[2]; eqA1[b] = coef[3]; eqA2[b] = coef[4]; }
            eqFlat = false;
        }

        void Equalize(float[] buf, int n)
        {
            if (eqFlat) return;
            for (int b = 0; b < B; b++)
            {
                double b0 = eqB0[b], b1 = eqB1[b], b2 = eqB2[b], a1 = eqA1[b], a2 = eqA2[b], z1 = eqZ1[b], z2 = eqZ2[b];
                for (int i = 0; i < n; i++)
                {
                    double x = buf[i], y = b0 * x + z1;
                    z1 = b1 * x - a1 * y + z2;
                    z2 = b2 * x - a2 * y;
                    buf[i] = (float)y;
                }
                // (denormals: the noise remover's silence is exact zeros, which decay to zeros)
                eqZ1[b] = Math.Abs(z1) < 1e-25 ? 0 : z1; eqZ2[b] = Math.Abs(z2) < 1e-25 ? 0 : z2;
            }
        }

        // ------------------------------------------------------------------ the missing treble, rebuilt

        // The octave below the cut (band-passed), made analytic by a pair of allpass chains 90 degrees apart (Olli
        // Niemitalo's Hilbert transformer, ±0.7° over 20 Hz - 23.9 kHz), then shifted up by half the cut, the cut, ...
        // (single sideband: each copy lands one half-cut higher), each copy as loud as speech is up there, all of it
        // following every syllable, high-passed at the cut.
        internal static readonly double[] HilA = Sq(0.6923878, 0.9360654322959, 0.9882295226860, 0.9987488452737);
        internal static readonly double[] HilB = Sq(0.4021921162426, 0.8561710882420, 0.9722909545651, 0.9952884791278);
        static double[] Sq(params double[] v) { return v.Select(x => x * x).ToArray(); }
        readonly double[] hA = new double[16], hB = new double[16];   // per section: x[n-1], x[n-2], y[n-1], y[n-2]
        double hAPrev;
        Biquad sHp1, sHp2, sLp1, sLp2, oHp1, oHp2;
        double cutNow;
        int copies;
        readonly double[] copyRe = new double[4], copyIm = new double[4], stepRe = new double[4], stepIm = new double[4], copyGain = new double[4];
        double amtNow;

        void SetCut(double cutHz)
        {
            if (cutHz > 0 && (cutHz < 3700 || cutHz > 20000)) cutHz = 0;   // (the STFT bins it worked in: 40 .. K-4)
            if (Math.Abs(cutHz - cutNow) < 1) return;
            bool was = cutNow > 0;
            cutNow = cutHz;
            if (cutHz <= 0) { copies = 0; return; }
            var nhp1 = Biquad.HighPass(cutHz / 2, Fs); var nhp2 = Biquad.HighPass(cutHz / 2, Fs);
            var nlp1 = Biquad.LowPass(cutHz, Fs); var nlp2 = Biquad.LowPass(cutHz, Fs);
            var ohp1 = Biquad.HighPass(cutHz, Fs); var ohp2 = Biquad.HighPass(cutHz, Fs);
            if (was) { nhp1.CopyState(sHp1); nhp2.CopyState(sHp2); nlp1.CopyState(sLp1); nlp2.CopyState(sLp2); ohp1.CopyState(oHp1); ohp2.CopyState(oHp2); }
            sHp1 = nhp1; sHp2 = nhp2; sLp1 = nlp1; sLp2 = nlp2; oHp1 = ohp1; oHp2 = ohp2;
            double shift = cutHz / 2;
            int n = 0;
            for (int j = 1; j <= 4 && cutHz + (j - 1) * shift < 20000 && cutHz + j * shift <= 27000; j++, n++)   // (above 24 kHz it folds back, but only to 21 kHz and up)
            {
                double w = 2 * Math.PI * j * shift / Fs;
                stepRe[n] = Math.Cos(w); stepIm[n] = Math.Sin(w);
                if (!was || n >= copies) { copyRe[n] = 1; copyIm[n] = 0; }
                // How much quieter speech is up there than where it came from (at the middles of the two stretches).
                double from = Math.Sqrt(shift * cutHz), to = Math.Sqrt((shift + j * shift) * (cutHz + j * shift));
                copyGain[n] = Math.Pow(10, (TargetAt(Math.Min(20000, to)) - TargetAt(from)) / 20);
            }
            copies = n;
        }

        internal static double Allpass(double[] c, double[] s, double x)
        {
            for (int k = 0, o = 0; k < c.Length; k++, o += 4)
            {
                double y = c[k] * (x + s[o + 3]) - s[o + 1];
                s[o + 1] = s[o]; s[o] = x;
                s[o + 3] = s[o + 2]; s[o + 2] = y;
                x = y;
            }
            return x;
        }

        void Rebuild(float[] buf, int n)
        {
            int nc = copies;
            if (nc == 0) return;
            double amt = Strength * (0.35 + 0.65 * speechNow);
            double a0 = amtNow, da = (amt - amtNow) / n; amtNow = amt;
            for (int i = 0; i < n; i++) src[i] = sLp2.Process(sLp1.Process(sHp2.Process(sHp1.Process(buf[i]))));
            for (int i = 0; i < n; i++)
            {
                double x = src[i];
                double ire = hAPrev; hAPrev = Allpass(HilA, hA, x);   // (chain A one sample later: then B leads it by 90 degrees)
                double q = Allpass(HilB, hB, x);
                double y = 0;
                for (int j = 0; j < nc; j++)
                {
                    double cr = copyRe[j], ci = copyIm[j];
                    y += (ire * cr + q * ci) * copyGain[j];   // the upper sideband
                    copyRe[j] = cr * stepRe[j] - ci * stepIm[j];
                    copyIm[j] = cr * stepIm[j] + ci * stepRe[j];
                }
                add[i] = (float)y;
            }
            for (int j = 0; j < nc; j++) { double m = 1 / Math.Sqrt(copyRe[j] * copyRe[j] + copyIm[j] * copyIm[j]); copyRe[j] *= m; copyIm[j] *= m; }
            for (int i = 0; i < n; i++) buf[i] += (float)(oHp2.Process(oHp1.Process(add[i])) * (a0 + da * i));
        }

        // ------------------------------------------------------------------ for the interface and status

        /// <summary>The correction per band right now (dB), null until learned.</summary>
        public volatile double[] MatchedDb;
        /// <summary>Where this mic's treble stops (Hz; 0 = it doesn't, nothing rebuilt).</summary>
        public volatile float CutoffHzF;
        double CutoffHz { set { CutoffHzF = (float)value; } }
        /// <summary>Seconds of your talking learned so far.</summary>
        public double LearnedSeconds { get { return speechFrames * R / Fs; } }

        /// <summary>What it's doing, in words ("learning your mic", "warmer, less boxy, treble rebuilt above 11 kHz").</summary>
        public string Describe()
        {
            if (!Enabled) return "Off";
            var m = MatchedDb;
            if (m == null || LearnedSeconds < 5) return "Learning your mic - just talk (" + LearnedSeconds.ToString("0") + " of 5 s)";
            var parts = new System.Collections.Generic.List<string>();
            Func<double, double, double> avg = (lo, hi) => { double s = 0; int c = 0; for (int b = 0; b < B; b++) if (Centres[b] >= lo && Centres[b] <= hi) { s += m[b]; c++; } return c > 0 ? s / c : 0; };
            double low = avg(100, 250), box = avg(315, 800), pres = avg(2000, 5000), air = avg(6300, 12500);
            if (low > 1.5) parts.Add("warmer"); else if (low < -1.5) parts.Add("less boomy");
            if (box < -1.5) parts.Add("less boxy"); else if (box > 1.5) parts.Add("fuller");
            if (pres < -1.5) parts.Add("less harsh"); else if (pres > 1.5) parts.Add("clearer");
            if (air > 1.5) parts.Add("brighter"); else if (air < -1.5) parts.Add("smoother treble");
            float cut = CutoffHzF;
            if (cut > 0) parts.Add("treble rebuilt above " + (cut / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
            return parts.Count == 0 ? "Your mic already sounds like a broadcast mic - nothing to correct" : char.ToUpper(parts[0][0]) + string.Join(", ", parts).Substring(1);
        }

        // ------------------------------------------------------------------ the learned profile, per mic

        /// <summary>The learned spectrum as text (kept per mic so it's ready at the next start).</summary>
        public string SaveProfile()
        {
            lock (profileLock)
            {
                if (ltas.Any(double.IsNaN) || speechFrames < 300) return null;
                return Math.Min(speechFrames, 1e6).ToString("0", CultureInfo.InvariantCulture) + ";" + string.Join(",", ltas.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)));
            }
        }

        public void LoadProfile(string s)
        {
            lock (profileLock)
            {
                for (int b = 0; b < B; b++) ltas[b] = double.NaN;
                speechFrames = 0; MatchedDb = null; CutoffHzF = 0;
                Array.Clear(corrDb, 0, B); SolveEq(corrDb); SetCut(0);
                if (string.IsNullOrEmpty(s)) return;
                try
                {
                    var p = s.Split(';'); var v = p[1].Split(',');
                    if (v.Length != B) return;
                    for (int b = 0; b < B; b++) ltas[b] = double.Parse(v[b], CultureInfo.InvariantCulture);
                    // Kept: counts as learned (ready at once), still adjusting slowly to the room as it is today.
                    speechFrames = Math.Max(Fs / R * 6.0, Math.Min(2000, double.Parse(p[0], CultureInfo.InvariantCulture)));
                }
                catch { for (int b = 0; b < B; b++) ltas[b] = double.NaN; speechFrames = 0; return; }
            }
            Update();
        }
    }
}
