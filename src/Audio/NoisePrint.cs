using System;
using System.IO;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// "Record a sound to remove": you stay quiet for a few seconds while the sound plays (TV, kids, a fan, music), and
    /// from then on that sound is taken out of your mic. It learns what the noise remover LEAVES of that sound (the
    /// recording is made after it), as a set of spectral patterns (non-negative matrix factorisation, KL divergence).
    /// While you talk, every 10.7 ms the sound is explained as a mix of those patterns and of voice patterns - a built-in
    /// set of harmonic voices, then your own voice, learned in the background from moments the noise remover calls
    /// speech and the recorded sound isn't there - and a soft mask keeps the voice part. Adds 21 ms when it's on.
    /// </summary>
    internal sealed class NoisePrint
    {
        public const int Fs = 48000, N = 1024, R = 512, K = N / 2 + 1;
        public const float DelayMs = N * 1000f / Fs;
        public const int NoiseAtoms = 32, VoiceAtoms = 48;
        const int Iter = 8;

        /// <summary>Learned sound: NoiseAtoms columns of K bins, each summing to 1 (atom-major).</summary>
        volatile float[] wn;
        /// <summary>Voice patterns (VoiceAtoms x K): the built-in set until your own voice has been learned.</summary>
        volatile float[] ws = Generic();
        public volatile bool Enabled;
        /// <summary>0..1: how hard the sound is pushed down (over-subtraction and floor).</summary>
        public volatile float Strength = 0.7f;
        /// <summary>The noise remover hears a voice right now (0..1); set by the chain each block.</summary>
        public volatile float Vad;
        public bool HasPrint { get { return wn != null; } }
        public bool OwnVoice { get { return ownVoice; } }
        volatile bool ownVoice;
        /// <summary>How much of the sound is being taken out right now (dB, for the interface).</summary>
        public volatile float RemovedDb;
        /// <summary>A/B compare: everything passes (same delay, so switching doesn't jump).</summary>
        public volatile bool Bypass;

        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], outBuf = new float[N], outQ = new float[R], re = new float[N], im = new float[N];
        readonly float[] mag = new float[K], lam = new float[K], ratio = new float[K], gain = new float[K], prevGain = new float[K], hS = new float[VoiceAtoms], hN = new float[NoiseAtoms];
        int pos;
        bool primed;
        double voiceShare = 1;

        public NoisePrint()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < K; k++) prevGain[k] = 1;
            for (int j = 0; j < VoiceAtoms; j++) hS[j] = 1e-3f;
            for (int j = 0; j < NoiseAtoms; j++) hN[j] = 1e-3f;
        }

        // ------------------------------------------------------------------ running

        /// <summary>
        /// In place. Off (or nothing recorded): untouched, no delay, and nothing is listened to (4.1.3: your voice is
        /// learned only while a sound is being removed - and only in memory, never saved).
        /// </summary>
        public void Process(float[] buf, int n)
        {
            bool on = Enabled && wn != null;
            if (on != primed) { primed = on; Array.Clear(inBuf, 0, N); Array.Clear(outBuf, 0, N); Array.Clear(outQ, 0, R); pos = 0; }
            if (!on) return;
            for (int i = 0; i < n; i++)
            {
                inBuf[N - R + pos] = buf[i];
                buf[i] = outQ[pos];
                if (++pos == R) { pos = 0; Frame(); }
            }
        }

        void Frame()
        {
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            double eIn = 0;
            for (int k = 0; k < K; k++) { mag[k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]); eIn += mag[k] * mag[k]; }
            var Wn = wn; var Ws = ws;
            if (Wn != null && eIn > 1e-9 && !Bypass)
            {
                Explain(mag, Ws, hS, VoiceAtoms, Wn, hN, NoiseAtoms, lam, ratio);
                // Voice and sound estimates per bin.
                double eV = 0, eN = 0;
                for (int k = 0; k < K; k++)
                {
                    double v = 0, nz = 0;
                    for (int j = 0; j < VoiceAtoms; j++) v += Ws[j * K + k] * hS[j];
                    for (int j = 0; j < NoiseAtoms; j++) nz += Wn[j * K + k] * hN[j];
                    lam[k] = (float)v; ratio[k] = (float)nz;
                    eV += v * v; eN += nz * nz;
                }
                // How much of this moment is your voice: where it's mostly the recorded sound it's pushed down hard,
                // while your voice carries the moment the mask stays gentle (pushing hard there is what damages a voice).
                double share = eV / (eV + eN + 1e-24);
                voiceShare = voiceShare * 0.6 + share * 0.4;
                float s = Strength;
                float over = (float)(1 + 6 * s * (1 - voiceShare) * (1 - voiceShare));
                float floor = (float)Math.Pow(10, -(8 + 30 * s * (1 - voiceShare)) / 20);
                double eOut = 0;
                for (int k = 0; k < K; k++)
                {
                    double v2 = lam[k] * (double)lam[k], n2 = over * ratio[k] * (double)ratio[k];
                    float g = (float)(v2 + n2 > 1e-24 ? v2 / (v2 + n2) : 1);
                    gain[k] = Math.Max(floor, g);
                }
                // Smoothing across frequency, and over time (opens at once, closes over ~4 frames): no warble.
                float prevK = gain[0];
                for (int k = 1; k < K - 1; k++) { float c = gain[k]; gain[k] = 0.25f * prevK + 0.5f * c + 0.25f * gain[k + 1]; prevK = c; }
                for (int k = 0; k < K; k++)
                {
                    float g = gain[k] >= prevGain[k] ? gain[k] : prevGain[k] + (gain[k] - prevGain[k]) * 0.35f;
                    prevGain[k] = g;
                    re[k] *= g; im[k] *= g; eOut += mag[k] * g * mag[k] * g;
                    if (k > 0 && k < N / 2) { re[N - k] = re[k]; im[N - k] = -im[k]; }
                }
                RemovedDb = RemovedDb * 0.9f + 0.1f * (float)(10 * Math.Log10((eIn + 1e-12) / (eOut + 1e-12)));
                // Your own voice: moments the noise remover calls speech while the recorded sound explains little.
                LearnVoice(Ws, Wn);
            }
            else { for (int k = 1; k < N / 2; k++) { re[N - k] = re[k]; im[N - k] = -im[k]; } }
            fft.Transform(re, im, true);
            float scale = 1f / N;   // sqrt-Hann analysis and synthesis at 50% overlap sum to 1
            for (int i = 0; i < N; i++) outBuf[i] += re[i] * scale * win[i];
            Array.Copy(outBuf, 0, outQ, 0, R);
            Array.Copy(outBuf, R, outBuf, 0, N - R);
            Array.Clear(outBuf, N - R, R);
            Array.Copy(inBuf, R, inBuf, 0, N - R);
        }

        /// <summary>
        /// Activations for one frame (KL multiplicative updates, warm-started from the last frame): v ~ Ws hs + Wn hn.
        /// Columns sum to 1, so each update is h *= W^T (v / (W h)).
        /// </summary>
        static void Explain(float[] v, float[] Ws, float[] hs, int js, float[] Wn, float[] hn, int jn, float[] lam, float[] ratio)
        {
            // Warm start, but let a sound that stopped fade fast and one that started come in (no atom stuck at 0).
            double tot = 0; for (int k = 0; k < K; k++) tot += v[k];
            float seed = (float)(tot / (js + jn) * 0.02 + 1e-9);
            for (int j = 0; j < js; j++) hs[j] = hs[j] * 0.7f + seed;
            for (int j = 0; j < jn; j++) hn[j] = hn[j] * 0.7f + seed;
            for (int it = 0; it < Iter; it++)
            {
                for (int k = 0; k < K; k++) lam[k] = 1e-12f;
                for (int j = 0; j < js; j++) { float h = hs[j]; int o = j * K; for (int k = 0; k < K; k++) lam[k] += Ws[o + k] * h; }
                for (int j = 0; j < jn; j++) { float h = hn[j]; int o = j * K; for (int k = 0; k < K; k++) lam[k] += Wn[o + k] * h; }
                for (int k = 0; k < K; k++) ratio[k] = v[k] / lam[k];
                for (int j = 0; j < js; j++) { double a = 0; int o = j * K; for (int k = 0; k < K; k++) a += Ws[o + k] * ratio[k]; hs[j] *= (float)a; }
                for (int j = 0; j < jn; j++) { double a = 0; int o = j * K; for (int k = 0; k < K; k++) a += Wn[o + k] * ratio[k]; hn[j] *= (float)a; }
            }
        }

        // ------------------------------------------------------------------ your voice, learned while you talk

        const int VoiceFrames = 2400;               // ~25 s of talking (every second frame kept)
        float[] voiceBank; int voiceN, voiceSkip;
        volatile bool fitting;

        void LearnVoice(float[] Ws, float[] Wn)
        {
            if (ownVoice || fitting || Vad < 0.7f) return;
            double s = 0, nz = 0;
            for (int j = 0; j < VoiceAtoms; j++) s += hS[j];
            for (int j = 0; j < NoiseAtoms; j++) nz += hN[j];
            if (nz > 0.15 * (s + nz)) return;        // the recorded sound is there: not a clean moment of your voice
            if ((++voiceSkip & 1) != 0) return;
            Keep(Ws);
        }

        double voicePeak;

        /// <summary>One moment of your voice (mag) for the bank; when it's full, your voice is learned in the background.</summary>
        void Keep(float[] Ws)
        {
            // Speech only: within 25 dB of your loud moments (the voice probability lags a little behind word ends).
            double e = 0; for (int k = 0; k < K; k++) e += mag[k] * mag[k];
            voicePeak = Math.Max(voicePeak * 0.9995, e);
            if (e < voicePeak * 0.003 || e < 1e-10) return;
            var keep = voiceBank ?? (voiceBank = new float[VoiceFrames * K]);   // Forget (another thread) may drop it meanwhile
            int at = Math.Min(voiceN, VoiceFrames - 1);
            Array.Copy(mag, 0, keep, at * K, K);
            if (++voiceN < VoiceFrames) return;
            // Enough: fit your voice's patterns in the background (from the built-in ones), keep them, switch over.
            fitting = true;
            var bank = voiceBank; int frames = voiceN; voiceBank = null; voiceN = 0;
            var start = (float[])Ws.Clone();
            new Thread(() =>
            {
                try
                {
                    var w = Fit(bank, frames, VoiceAtoms, 60, start);
                    ws = w; ownVoice = true;   // in memory only: gone when StreamLite closes or the sound is forgotten
                }
                catch { }
                finally { fitting = false; }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Noise print: your voice" }.Start();
        }


        /// <summary>Tests: voice patterns learned elsewhere.</summary>
        internal void UseVoice(float[] w) { ws = w; ownVoice = true; }


        // ------------------------------------------------------------------ the recorded sound

        /// <summary>What a recording taught (or why nothing): shown in the interface.</summary>
        public sealed class Learned
        {
            public float[] W;
            /// <summary>Level of the sound as recorded after the noise remover, and before it (dBFS).</summary>
            public float AfterDb, BeforeDb;
            /// <summary>Nothing left to learn: the noise remover already takes it out (or it was too quiet).</summary>
            public bool AlreadyGone;
            /// <summary>Where most of it sits, in words ("low hum", "voices", "hiss"...).</summary>
            public string Character;
            /// <summary>It changes like voices or music do: people talking are Only my voice's job (it knows your voice).</summary>
            public bool VoiceLike;
        }

        /// <summary>
        /// Learns the sound from a recording made after the noise remover (what it leaves), with the raw recording for
        /// comparison. Takes a few seconds: call it off the audio thread.
        /// </summary>
        public static Learned Learn(float[] after, int n, float[] before)
        {
            var r = new Learned();
            r.AfterDb = Db(after, n); r.BeforeDb = before != null ? Db(before, Math.Min(n, before.Length)) : r.AfterDb;
            var frames = Spectra(after, n);
            int f = frames.Length / K;
            // Nothing left: silence after the noise remover, or so little (25 dB down, under -60 dBFS) that it can't be heard.
            if (r.AfterDb < -75 || (r.AfterDb < -60 && r.AfterDb < r.BeforeDb - 25) || f < 20) { r.AlreadyGone = true; r.Character = "nothing left"; return r; }
            r.W = Fit(frames, f, NoiseAtoms, 120, null);
            r.Character = Describe(frames, f);
            r.VoiceLike = r.Character == "changing voices, music or clatter";
            return r;
        }

        public void Use(float[] w) { wn = w; for (int j = 0; j < NoiseAtoms; j++) hN[j] = 1e-3f; }
        /// <summary>Forgets the recorded sound and the voice learned while it was in use (4.1.3: both).</summary>
        public void Forget() { wn = null; ws = Generic(); ownVoice = false; voiceBank = null; voiceN = 0; }

        static float Db(float[] x, int n) { double s = 0; for (int i = 0; i < n; i++) s += x[i] * x[i]; return (float)(10 * Math.Log10(s / Math.Max(1, n) + 1e-20)); }

        /// <summary>Magnitude spectra of a recording, every R samples (frame-major).</summary>
        static float[] Spectra(float[] x, int n)
        {
            var w = new float[N]; for (int i = 0; i < N; i++) w[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            var fft = new Fft(N); var re = new float[N]; var im = new float[N];
            int f = Math.Max(0, (n - N) / R + 1); var o = new float[f * K];
            for (int t = 0; t < f; t++)
            {
                for (int i = 0; i < N; i++) { re[i] = x[t * R + i] * w[i]; im[i] = 0; }
                fft.Transform(re, im, false);
                for (int k = 0; k < K; k++) o[t * K + k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
            return o;
        }

        static string Describe(float[] v, int f)
        {
            double lo = 0, mid = 0, hi = 0, flux = 0, tot = 0;
            for (int t = 0; t < f; t++)
            {
                double e = 0;
                for (int k = 0; k < K; k++)
                {
                    double p = v[t * K + k] * v[t * K + k], hz = k * (double)Fs / N; e += p;
                    // Hum and rumble sit under 150 Hz; voices carry most of their power just above (their pitch, 100-400 Hz).
                    if (hz < 150) lo += p; else if (hz < 4000) mid += p; else hi += p;
                }
                tot += e;
                if (t > 0) { double e0 = 0; for (int k = 0; k < K; k++) e0 += v[(t - 1) * K + k] * v[(t - 1) * K + k]; flux += Math.Abs(10 * Math.Log10((e + 1e-20) / (e0 + 1e-20))); }
            }
            bool steady = flux / Math.Max(1, f - 1) < 1.5;
            string where = lo > mid && lo > hi ? "low hum or rumble" : hi > mid ? "hiss" : steady ? "a steady tone or whir" : "voices, music or clatter";
            return (steady ? "steady " : "changing ") + where;
        }

        /// <summary>KL-NMF of frame-major magnitude spectra: atoms columns (each summing to 1), from a start set or a spread of frames.</summary>
        internal static float[] Fit(float[] v, int f, int atoms, int iterations, float[] start)
        {
            var w = new float[atoms * K]; var h = new float[atoms * f];
            var rnd = new Random(17);
            if (start != null) Array.Copy(start, w, w.Length);
            else
                for (int j = 0; j < atoms; j++)
                {
                    // Start from frames spread over the recording (plus a little noise): every part of it is covered.
                    int t = (int)((j + 0.5) * f / atoms);
                    double s = 0; for (int k = 0; k < K; k++) { w[j * K + k] = v[t * K + k] + 1e-6f + (float)(rnd.NextDouble() * 1e-5); s += w[j * K + k]; }
                    for (int k = 0; k < K; k++) w[j * K + k] = (float)(w[j * K + k] / s);
                }
            for (int i = 0; i < h.Length; i++) h[i] = 1e-2f;
            var lam = new float[K]; var num = new double[atoms * K]; var den = new double[atoms];
            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(num, 0, num.Length); Array.Clear(den, 0, den.Length);
                for (int t = 0; t < f; t++)
                {
                    int vo = t * K, ho = t * atoms;
                    // h update for this frame
                    for (int k = 0; k < K; k++) lam[k] = 1e-12f;
                    for (int j = 0; j < atoms; j++) { float hj = h[ho + j]; int o = j * K; for (int k = 0; k < K; k++) lam[k] += w[o + k] * hj; }
                    for (int j = 0; j < atoms; j++) { double a = 0; int o = j * K; for (int k = 0; k < K; k++) a += w[o + k] * v[vo + k] / lam[k]; h[ho + j] *= (float)a; }
                    // w update statistics with the new h
                    for (int k = 0; k < K; k++) lam[k] = 1e-12f;
                    for (int j = 0; j < atoms; j++) { float hj = h[ho + j]; int o = j * K; for (int k = 0; k < K; k++) lam[k] += w[o + k] * hj; }
                    for (int j = 0; j < atoms; j++)
                    {
                        float hj = h[ho + j]; if (hj < 1e-12f) continue;
                        int o = j * K; den[j] += hj;
                        for (int k = 0; k < K; k++) num[o + k] += hj * v[vo + k] / lam[k];
                    }
                }
                for (int j = 0; j < atoms; j++)
                {
                    if (den[j] <= 0) continue;
                    int o = j * K; double s = 0;
                    for (int k = 0; k < K; k++) { w[o + k] = (float)(w[o + k] * num[o + k] / den[j]) + 1e-9f; s += w[o + k]; }
                    for (int k = 0; k < K; k++) w[o + k] = (float)(w[o + k] / s);
                    for (int t = 0; t < f; t++) h[t * atoms + j] *= (float)s;
                }
            }
            return w;
        }

        /// <summary>
        /// Built-in voices: voiced sounds as harmonic combs (pitches 85-330 Hz) under three vowel envelopes, plus
        /// unvoiced ones (s, sh, f, breath, plosive) - so voices are explained by these, not by the recorded sound.
        /// </summary>
        internal static float[] Generic()
        {
            var w = new float[VoiceAtoms * K];
            double[][] vowels = { new double[] { 730, 1090, 2440 }, new double[] { 300, 2300, 3000 }, new double[] { 350, 870, 2250 } };
            int a = 0;
            for (int p = 0; p < 12; p++)
            {
                double f0 = 85 * Math.Pow(330.0 / 85, p / 11.0);
                foreach (var fm in vowels)
                {
                    for (int k = 0; k < K; k++)
                    {
                        double hz = k * (double)Fs / N, v = 0;
                        for (int hm = 1; hm * f0 < 8000; hm++)
                        {
                            double d = (hz - hm * f0) / (Fs / (double)N);
                            v += Math.Exp(-d * d / 1.2);
                        }
                        double env = 0;
                        foreach (double F in fm) { double x = (hz - F) / (90 + 0.06 * F); env += 1 / (1 + x * x); }
                        env = env * Math.Exp(-hz / 3500) + 0.02;
                        w[a * K + k] = (float)(v * env + 1e-6);
                    }
                    a++;
                }
            }
            double[][] unvoiced = { new double[] { 6500, 2500 }, new double[] { 3500, 1200 }, new double[] { 8000, 6000 }, new double[] { 1500, 1500 }, new double[] { 5000, 4000 }, new double[] { 10000, 3000 } };
            int u = 0;
            while (a < VoiceAtoms)
            {
                var c = unvoiced[u % unvoiced.Length]; double shift = 1 + 0.15 * (u / unvoiced.Length);
                for (int k = 0; k < K; k++) { double hz = k * (double)Fs / N, x = (hz - c[0] * shift) / c[1]; w[a * K + k] = (float)(Math.Exp(-x * x) + 1e-4); }
                a++; u++;
            }
            for (int j = 0; j < VoiceAtoms; j++) { double s = 0; for (int k = 0; k < K; k++) s += w[j * K + k]; for (int k = 0; k < K; k++) w[j * K + k] = (float)(w[j * K + k] / s); }
            return w;
        }

        internal static void SaveDict(string path, float[] w, int atoms)
        {
            if (path == null || w == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var b = new byte[8 + w.Length * 4];
            BitConverter.GetBytes(atoms).CopyTo(b, 0); BitConverter.GetBytes(K).CopyTo(b, 4);
            Buffer.BlockCopy(w, 0, b, 8, w.Length * 4);
            File.WriteAllBytes(path + ".tmp", b);
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
        }

        internal static float[] LoadDict(string path, int atoms)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                var b = File.ReadAllBytes(path);
                if (b.Length != 8 + atoms * K * 4 || BitConverter.ToInt32(b, 0) != atoms || BitConverter.ToInt32(b, 4) != K) return null;
                var w = new float[atoms * K]; Buffer.BlockCopy(b, 8, w, 0, w.Length * 4);
                return w;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The seconds of the sound to remove, as the noise remover receives it (Raw) and what it leaves of it (After). The
    /// first 0.4 s are skipped: that's the click on the Record button.
    /// </summary>
    internal sealed class NoisePrintRecording
    {
        public const int Seconds = 8, Skip = NoisePrint.Fs * 2 / 5;
        public readonly float[] Raw = new float[NoisePrint.Fs * Seconds], After = new float[NoisePrint.Fs * Seconds];
        int rawSeen, afterSeen;
        public double Progress { get { return Math.Max(0, Math.Min(1.0, (afterSeen - Skip) / (double)After.Length)); } }
        public bool Full { get { return afterSeen - Skip >= After.Length; } }
        public void FeedRaw(float[] b, int n) { Put(Raw, ref rawSeen, b, n); }
        public void FeedAfter(float[] b, int n) { Put(After, ref afterSeen, b, n); }
        static void Put(float[] dst, ref int seen, float[] b, int n)
        {
            for (int i = 0; i < n; i++, seen++) { int at = seen - Skip; if (at >= 0 && at < dst.Length) dst[at] = b[i]; }
        }
    }
}