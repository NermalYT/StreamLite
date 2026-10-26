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
    /// Short-time Fourier processing: 512-point frames, 75% overlap: adds 10.7 ms.
    /// </summary>
    internal sealed class StudioVoice
    {
        public const int Fs = 48000, N = 512, R = 128, K = N / 2 + 1;
        public const float DelayMs = N * 1000f / Fs;

        public volatile bool Enabled = true;
        /// <summary>The stage runs: adds DelayMs.</summary>
        public bool Active { get { return Enabled; } }
        /// <summary>0..1: how far the mic is corrected towards the broadcast voice (and how much treble is restored).</summary>
        public volatile float Strength = 0.75f;
        /// <summary>A/B compare: everything passes (same delay, so switching doesn't jump).</summary>
        public volatile bool Bypass;

        // 1/3-octave bands, 80 Hz .. 20 kHz.
        static readonly double[] Centres = { 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };
        static readonly int B = Centres.Length;
        /// <summary>Speech through a good close mic, band levels in dB (only the shape counts).</summary>
        static readonly double[] Target = MakeTarget();

        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], outBuf = new float[N], outQ = new float[R], re = new float[N], im = new float[N], yr = new float[K], yi = new float[K];
        readonly int[] bandOf = new int[K];
        readonly double[] bandPow = new double[B], ltas = new double[B], corrDb = new double[B];
        // Scratch for Update (no allocations on the audio thread) and the two halves of MatchedDb.
        readonly double[] um = new double[B], ud = new double[B], ug = new double[B], usm = new double[B], shownA = new double[B], shownB = new double[B];
        bool shownFlip;
        readonly float[] gainBin = new float[K], speechLin = new float[K];
        int pos; long frame; bool primed;
        double floorDb = -60, speechFrames;
        int cutBin = K;               // first bin the mic doesn't deliver (K = none missing)
        readonly object profileLock = new object();

        // Plosives: the rumble below 200 Hz against the voice band, per block.
        readonly Biquad lp1 = Biquad.LowPass(180, Fs), lp2 = Biquad.LowPass(180, Fs), hp1 = Biquad.HighPass(180, Fs), hp2 = Biquad.HighPass(180, Fs), bp1 = Biquad.HighPass(300, Fs), bp2 = Biquad.LowPass(3000, Fs);
        double lfRecent = 1, popGain = 1;
        bool popping;
        float[] low = new float[480], high = new float[480];
        /// <summary>Plosives caught so far (status).</summary>
        public int Pops;

        public StudioVoice()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < K; k++)
            {
                double f = Math.Max(1, k * (double)Fs / N), best = double.MaxValue;
                for (int b = 0; b < B; b++) { double d = Math.Abs(Math.Log(f / Centres[b])); if (d < best) { best = d; bandOf[k] = b; } }
                gainBin[k] = 1;
                speechLin[k] = (float)Math.Pow(10, TargetAt(Math.Max(Centres[0], Math.Min(Centres[B - 1], k * (double)Fs / N))) / 20);
            }
            for (int b = 0; b < B; b++) ltas[b] = double.NaN;
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

        /// <summary>In place, in any block size. Off: untouched, no delay.</summary>
        public void Process(float[] buf, int n)
        {
            bool on = Active;
            if (on != primed) { primed = on; Array.Clear(inBuf, 0, N); Array.Clear(outBuf, 0, N); Array.Clear(outQ, 0, R); pos = 0; }
            if (!on) return;
            Plosives(buf, n);
            for (int i = 0; i < n; i++)
            {
                inBuf[N - R + pos] = buf[i];
                buf[i] = outQ[pos];
                if (++pos == R) { pos = 0; Frame(); }
            }
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
            double speech = Math.Max(0, Math.Min(1, (vDb - floorDb - 12) / 8));
            if (speech > 0.5 && vDb > -95) Learn();
            if (!Bypass) Shape(speech);
            for (int k = 1; k < N / 2; k++) { re[N - k] = re[k]; im[N - k] = -im[k]; }
            im[0] = 0; im[N / 2] = 0;
            fft.Transform(re, im, true);
            float scale = 0.5f / N;   // sqrt-Hann analysis and synthesis at 75% overlap sum to 2
            for (int i = 0; i < N; i++) outBuf[i] += re[i] * scale * win[i];
            Array.Copy(outBuf, 0, outQ, 0, R);
            Array.Copy(outBuf, R, outBuf, 0, N - R);
            Array.Clear(outBuf, N - R, R);
            Array.Copy(inBuf, R, inBuf, 0, N - R);
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
                cutBin = cut < B ? Math.Min(K, (int)Math.Round(cutHz * N / Fs)) : K;
                CutoffHz = cut < B ? cutHz : 0;
            }
            // Per bin, interpolated on a log-frequency scale.
            for (int k = 0; k < K; k++)
            {
                double f = Math.Max(Centres[0], Math.Min(Centres[B - 1], k * (double)Fs / N));
                int hi = 1; while (hi < B - 1 && Centres[hi] < f) hi++;
                double t = Math.Log(f / Centres[hi - 1]) / Math.Log(Centres[hi] / Centres[hi - 1]);
                t = Math.Max(0, Math.Min(1, t));
                double db = corrDb[hi - 1] + t * (corrDb[hi] - corrDb[hi - 1]);
                gainBin[k] = (float)Math.Pow(10, db / 20);
            }
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

        /// <summary>One frame: the correction, then the missing treble rebuilt from the octave below the cut.</summary>
        void Shape(double speech)
        {
            for (int k = 0; k < K; k++) { re[k] *= gainBin[k]; im[k] *= gainBin[k]; }
            int cb = cutBin;
            if (cb >= K - 4 || cb < 40) return;
            double st = Strength;
            // The octave below the cut, shifted up by half the cut (and again for the next stretch) - single sideband,
            // with the phase advanced every hop so the shifted sound stays continuous. Its loudness follows this very
            // frame (an "s" brings bright air, a vowel a little), shaped like speech continues, faded in across the cut.
            int shift = cb / 2;
            for (int k = cb; k < K; k++) { yr[k] = 0; yi[k] = 0; }
            for (int j = 1; cb + (j - 1) * shift < K && j <= 4; j++)
            {
                int d = j * shift;
                double ph = 2 * Math.PI * d * R / N * frame;
                double c = Math.Cos(ph), s = Math.Sin(ph);
                for (int k = cb + (j - 1) * shift; k < Math.Min(K - 1, cb + j * shift); k++)
                {
                    int src = k - d; if (src < 1) continue;
                    double a = re[src], b = im[src];
                    double env = speechLin[k] / speechLin[src];   // how much quieter speech is up here than where it came from
                    yr[k] = (float)((a * c - b * s) * env); yi[k] = (float)((a * s + b * c) * env);
                }
            }
            float amt = (float)(st * (0.35 + 0.65 * speech));
            int fadeW = Math.Max(2, cb / 12);
            for (int k = cb; k < K - 1; k++)
            {
                float w = amt * Math.Min(1f, (k - cb + 1) / (float)fadeW);
                re[k] += yr[k] * w; im[k] += yi[k] * w;
            }
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
                for (int k = 0; k < K; k++) gainBin[k] = 1;
                cutBin = K;
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
