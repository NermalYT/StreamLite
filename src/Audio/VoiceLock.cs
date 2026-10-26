using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// "Only my voice": a speaker-recognition model (3D-Speaker's CAM++, trained on VoxCeleb, Apache-2.0) turns a second
    /// or so of voice into a 512-number voiceprint. You record yours once (Learn my voice); while you talk, the mic's
    /// last 1.5 s of voice (after noise removal) is compared with it a few times a second, and when it's clearly someone
    /// else - your girlfriend, your brother, a TV host - the mic is turned down until it's you again. When you both talk
    /// at once you stay on. Someone else gets through for the first half second or so of each sentence after a pause
    /// (it takes that much voice to tell), never you: after a pause the mic starts open.
    /// The model runs on the CPU on its own thread (about 10 ms per look); the audio thread only moves samples and gain.
    /// </summary>
    internal sealed class VoiceLock : IDisposable
    {
        public const string ModelFile = "speaker_campplus.onnx";
        public const int Fs16 = 16000, Dim = 512;
        public static string ModelPath { get { return Path.Combine(OnnxSession.Folder, ModelFile); } }
        public static bool ModelInstalled { get { return OnnxSession.Available && File.Exists(ModelPath); } }

        /// <summary>On (with a learned voiceprint), how far another voice is turned down (dB, negative).</summary>
        public volatile bool Enabled;
        public volatile float CutDb = -40f;
        /// <summary>Similarity to your voiceprint at or above which it's you, and below which it's someone else.</summary>
        // Measured on 0.35-1 s pieces (test/VoicePrintProbe.cs): you alone mostly 0.6-0.85, someone else alone mostly
        // under 0.45, both at once around 0.55-0.65. In between, the last decision stands (test/VoiceLockTest.cs).
        public volatile float OpenAt = 0.52f, CloseBelow = 0.46f;
        /// <summary>With their voice learned too: at or above this it's you whatever theirs says (both talking).</summary>
        public volatile float SureAt = 0.58f;
        float[] print, block;
        public bool HasPrint { get { return print != null; } }
        /// <summary>A voice learned to keep out (optional): then whichever of the two a piece is closer to decides.</summary>
        public bool HasBlockPrint { get { return block != null; } }
        public void SetBlockPrint(float[] p) { block = p; }
        /// <summary>For the window and Health: the last similarity, whether another voice is being turned down now, and why.</summary>
        public volatile float LastSimilarity = float.NaN;
        public volatile bool Blocking;
        public volatile string Error;
        public long Looks, Blocks;

        // 48 kHz -> 16 kHz, into a ring of the last 4 s, with a "voice" flag per 10 ms.
        const int Ring = Fs16 * 4, Hop = 160;
        readonly float[] ring = new float[Ring];
        readonly bool[] voiced = new bool[Ring / Hop];
        long written;   // 16 kHz samples written in total
        readonly float[] fir; readonly float[] hist; int histPos, phase;
        float hopEnergy; int hopFill; float floorDb = -70f;
        long lastVoiceAt = -1;

        // Gain the worker asks for, and the one applied (ramped).
        volatile float target = 1f;
        float gain = 1f;
        /// <summary>The gain applied right now (1 = on).</summary>
        public float CurrentGain { get { return gain; } }

        // Learning your voice: 16 kHz voice collected while it's on.
        volatile List<float> learning;
        readonly object learnLock = new object();

        Thread worker; volatile bool stop;
        OnnxSession session;
        readonly object sessionLock = new object();

        public VoiceLock()
        {
            // Low-pass at 7.2 kHz (63-tap Hamming-windowed sinc) before taking every 3rd sample.
            const int taps = 63; fir = new float[taps]; hist = new float[taps];
            double fc = 7200.0 / 48000, sum = 0;
            for (int i = 0; i < taps; i++)
            {
                double m = i - (taps - 1) / 2.0;
                double s = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
                fir[i] = (float)(s * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1)))); sum += fir[i];
            }
            for (int i = 0; i < taps; i++) fir[i] = (float)(fir[i] / sum);
        }

        public void SetPrint(float[] p) { print = p; if (p == null) { target = 1f; Blocking = false; } }

        // ------------------------------------------------------------------ audio thread

        /// <summary>A block of the mic after noise removal (48 kHz): taken in, and turned down if it's someone else.</summary>
        public void Process(float[] buf, int n)
        {
            bool on = Enabled && print != null;
            if (!on && learning == null) { if (gain != 1f) gain = 1f; return; }
            for (int i = 0; i < n; i++)
            {
                hist[histPos] = buf[i]; histPos = (histPos + 1) % hist.Length;
                if (++phase < 3) continue;
                phase = 0;
                double y = 0; int k = histPos;
                for (int t = 0; t < fir.Length; t++) { y += fir[t] * hist[k]; k = k + 1 == hist.Length ? 0 : k + 1; }
                Put((float)y);
            }
            if (on) EnsureWorker();
            float want = on ? target : 1f;
            if (want == 1f && gain == 1f) return;
            // Down over 60 ms, back up over 15 ms (never clip your first word).
            float g0 = gain, step = want < gain ? n / (0.06f * 48000) : n / (0.015f * 48000);
            float g1 = want < gain ? Math.Max(want, gain - step) : Math.Min(want, gain + step);
            for (int i = 0; i < n; i++) buf[i] *= g0 + (g1 - g0) * (i + 1) / n;
            gain = g1;
        }

        void Put(float v)
        {
            ring[written % Ring] = v;
            written++;
            hopEnergy += v * v;
            if (++hopFill < Hop) return;
            float db = (float)(10 * Math.Log10(hopEnergy / Hop + 1e-12));
            floorDb = db < floorDb ? db : Math.Min(db, floorDb + 0.03f);   // rises 3 dB/s
            bool v1 = db > -52f && db > floorDb + 12f;
            voiced[(written / Hop - 1) % voiced.Length] = v1;
            if (v1) lastVoiceAt = written;
            var l = learning;
            if (l != null && v1) lock (learnLock) { long s0 = written - Hop; for (long j = s0; j < written; j++) l.Add(ring[j % Ring]); }
            hopEnergy = 0; hopFill = 0;
        }

        // ------------------------------------------------------------------ learning your voice

        public void StartLearning() { learning = new List<float>(Fs16 * 30); }
        public double LearnedSeconds { get { var l = learning; if (l == null) return 0; lock (learnLock) return l.Count / (double)Fs16; } }
        public bool Learning { get { return learning != null; } }
        public void CancelLearning() { learning = null; }

        /// <summary>
        /// The voice collected since StartLearning becomes your voiceprint: the average of its 1.5 s pieces. consistency =
        /// how alike the pieces are (0..1; a second voice in the recording shows as low). Null when there's under 4 s of voice.
        /// </summary>
        public float[] FinishLearning(out double consistency, out double seconds)
        {
            var l = learning; learning = null;
            consistency = 0; seconds = 0;
            if (l == null) return null;
            float[] all; lock (learnLock) all = l.ToArray();
            seconds = all.Length / (double)Fs16;
            if (all.Length < Fs16 * 4) return null;
            return PrintOf(all, all.Length, out consistency);
        }

        /// <summary>A voiceprint from 16 kHz voice: the normalized average of its 1.5 s pieces (0.75 s apart).</summary>
        public float[] PrintOf(float[] x16, int n, out double consistency)
        {
            consistency = 0;
            var pieces = new List<float[]>();
            int win = Fs16 * 3 / 2, hop = Fs16 * 3 / 4;
            for (int s = 0; s + win <= n; s += hop) { var e = Embed(x16, s, win); if (e != null) pieces.Add(e); }
            if (pieces.Count == 0) return null;
            var mean = new float[Dim];
            foreach (var e in pieces) for (int i = 0; i < Dim; i++) mean[i] += e[i];
            Normalize(mean);
            double c = 0; foreach (var e in pieces) c += Cos(e, mean);
            consistency = c / pieces.Count;
            return mean;
        }

        // ------------------------------------------------------------------ the model

        /// <summary>The voiceprint of n samples of 16 kHz audio from start (normalized), or null if the model can't run.</summary>
        public float[] Embed(float[] x16, int start, int n)
        {
            // The model pools over 1 s segments and is unsteady on other lengths: every look is exactly 1.6 s - a
            // shorter piece repeated up to it, a longer one as the average of its 1.6 s pieces.
            const int Len = Fs16 * 8 / 5;
            if (n < Fs16 / 4) return null;
            if (n < Len)
            {
                var rep = new float[Len];
                for (int i = 0; i < Len; i++) rep[i] = x16[start + i % n];
                return EmbedExact(rep, 0, Len);
            }
            if (n == Len) return EmbedExact(x16, start, Len);
            var sum = new float[Dim]; int count = 0;
            for (int s0 = 0; ; s0 += Len / 2)
            {
                int at = Math.Min(s0, n - Len);
                var e = EmbedExact(x16, start + at, Len);
                if (e == null) return null;
                for (int i = 0; i < Dim; i++) sum[i] += e[i];
                count++;
                if (at == n - Len) break;
            }
            Normalize(sum);
            return sum;
        }

        float[] EmbedExact(float[] x16, int start, int n)
        {
            int frames;
            var feats = Fbank.Compute(x16, start, n, out frames);
            if (frames < 20) return null;
            var o = new float[Dim];
            lock (sessionLock)
            {
                if (session == null)
                {
                    if (!ModelInstalled) { Error = "the voice model isn't installed"; return null; }
                    try { session = new OnnxSession(ModelPath, false, 1); }
                    catch (Exception ex) { Error = ex.Message; return null; }
                }
                session.Run(new[] { feats }, new[] { new long[] { 1, frames, Fbank.Mels } }, new[] { o });
            }
            Normalize(o);
            return o;
        }

        public static float Cos(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * (double)b[i]; return (float)s; }
        static void Normalize(float[] v) { double s = 0; foreach (var x in v) s += x * (double)x; float k = (float)(1 / Math.Sqrt(s + 1e-12)); for (int i = 0; i < v.Length; i++) v[i] *= k; }

        // ------------------------------------------------------------------ deciding (its own thread)

        /// <summary>Tests: no thread - they call DecideNow on audio time instead.</summary>
        internal bool NoWorker;

        void EnsureWorker()
        {
            if (worker != null || NoWorker) return;
            stop = false;
            worker = new Thread(Work) { IsBackground = true, Name = "Voice lock", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        /// <summary>Tests: one decision now, on the voice so far (as the worker does every 100 ms).</summary>
        internal void DecideNow() { Decide(); }
        long decidedAt = -1; bool other;

        void Work()
        {
            while (!stop)
            {
                Thread.Sleep(100);
                if (!Enabled || print == null) { target = 1f; Blocking = false; continue; }
                try { Decide(); } catch (Exception ex) { Error = ex.Message; }
            }
        }

        void Decide()
        {
            long now = written;
            var p = print; if (p == null) return;
            // After half a second of quiet the mic starts open: it's most likely you, and your first word must never be cut.
            bool quiet = lastVoiceAt < 0 || now - lastVoiceAt > Fs16 / 2;
            if (quiet) { other = false; target = 1f; Blocking = false; return; }
            if (now - decidedAt < Hop * 8) return;   // nothing new
            // The voiced 10 ms pieces since the last pause (0.3 s), up to the newest 1 s of them: whoever talks now, not
            // whoever talked before the pause.
            int take = 100, gap = 0;
            var picked = new List<long>();
            for (long h = now / Hop - 1; h >= 0 && now / Hop - h < voiced.Length && picked.Count < take; h--)
            {
                if (voiced[h % voiced.Length]) { picked.Add(h); gap = 0; }
                else if (++gap >= 30 && picked.Count > 0) break;
            }
            if (picked.Count < 35) return;   // under 0.35 s of voice: can't tell yet
            picked.Reverse();
            var x = new float[picked.Count * Hop];
            for (int i = 0; i < picked.Count; i++) { long s0 = picked[i] * Hop; for (int j = 0; j < Hop; j++) x[i * Hop + j] = ring[(s0 + j) % Ring]; }
            var e = Embed(x, 0, x.Length);
            if (e == null) return;
            decidedAt = now;
            float sim = Cos(e, p);
            LastSimilarity = sim;
            Interlocked.Increment(ref Looks);
            var b = block;
            if (b != null)
            {
                // You and the voice to keep out both known: the closer one (you on a near tie).
                float simB = Cos(e, b);
                if (sim >= simB - 0.02f || sim >= SureAt) other = false;
                else if (simB > sim + 0.05f) other = true;
            }
            else if (sim >= OpenAt) other = false;
            else if (sim < CloseBelow) other = true;
            if (other) Interlocked.Increment(ref Blocks);
            Blocking = other;
            target = other ? (float)Math.Pow(10, CutDb / 20) : 1f;
        }

        public void Dispose()
        {
            stop = true;
            var w = worker; if (w != null) w.Join(1000);
            worker = null;
            lock (sessionLock) { if (session != null) { session.Dispose(); session = null; } }
        }

        // ------------------------------------------------------------------ voiceprint file

        public static void Save(string path, float[] p, double consistency, double seconds)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x4B434F4C); w.Write(1); w.Write(consistency); w.Write(seconds); w.Write(p.Length);
                foreach (var v in p) w.Write(v);
            }
        }

        public static float[] Load(string path, out double consistency, out double seconds)
        {
            consistency = 0; seconds = 0;
            try
            {
                if (!File.Exists(path)) return null;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != 0x4B434F4C || r.ReadInt32() != 1) return null;
                    consistency = r.ReadDouble(); seconds = r.ReadDouble();
                    int n = r.ReadInt32(); if (n != Dim) return null;
                    var p = new float[n]; for (int i = 0; i < n; i++) p[i] = r.ReadSingle();
                    return p;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Kaldi's log mel filterbank, as the speaker model was trained on: 16 kHz, 25 ms frames every 10 ms, DC removed,
    /// pre-emphasis 0.97, Povey window, 512-point power spectrum, 80 triangular mel bands from 20 Hz to 8 kHz, natural
    /// log; then each band's mean over the clip taken off.
    /// </summary>
    internal static class Fbank
    {
        public const int Mels = 80;
        const int Len = 400, Shift = 160, NFft = 512;
        static float[] window; static float[][] bands; static int[] bandStart;
        static readonly object gate = new object();

        static void Init()
        {
            lock (gate)
            {
                if (window != null) return;
                var w = new float[Len];
                for (int i = 0; i < Len; i++) w[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Len - 1)), 0.85);
                Func<double, double> mel = f => 1127 * Math.Log(1 + f / 700);
                double lo = mel(20), hi = mel(8000), d = (hi - lo) / (Mels + 1);
                var b = new float[Mels][]; var st = new int[Mels];
                for (int m = 0; m < Mels; m++)
                {
                    double left = lo + m * d, center = left + d, right = center + d;
                    var wt = new List<float>(); int first = -1;
                    for (int k = 0; k < NFft / 2; k++)
                    {
                        double mm = mel(k * 16000.0 / NFft);
                        if (mm > left && mm < right)
                        {
                            if (first < 0) first = k;
                            wt.Add((float)(mm <= center ? (mm - left) / (center - left) : (right - mm) / (right - center)));
                        }
                        else if (first >= 0) break;
                    }
                    b[m] = wt.ToArray(); st[m] = Math.Max(0, first);
                }
                bands = b; bandStart = st; window = w;
            }
        }

        /// <summary>Features [frames x 80] (row-major) for n samples from start.</summary>
        public static float[] Compute(float[] x, int start, int n, out int frames)
        {
            Init();
            frames = n < Len ? 0 : 1 + (n - Len) / Shift;
            var o = new float[Math.Max(0, frames) * Mels];
            if (frames <= 0) return o;
            var fft = new Fft(NFft);
            var re = new float[NFft]; var im = new float[NFft]; var fr = new double[Len];
            for (int f = 0; f < frames; f++)
            {
                int s = start + f * Shift; double mean = 0;
                for (int i = 0; i < Len; i++) { fr[i] = x[s + i]; mean += fr[i]; }
                mean /= Len;
                for (int i = 0; i < Len; i++) fr[i] -= mean;
                for (int i = Len - 1; i > 0; i--) fr[i] -= 0.97 * fr[i - 1];
                fr[0] -= 0.97 * fr[0];
                Array.Clear(re, 0, NFft); Array.Clear(im, 0, NFft);
                for (int i = 0; i < Len; i++) re[i] = (float)(fr[i] * window[i]);
                fft.Transform(re, im, false);
                for (int m = 0; m < Mels; m++)
                {
                    double e = 0; var w = bands[m]; int k0 = bandStart[m];
                    for (int j = 0; j < w.Length; j++) { int k = k0 + j; e += w[j] * ((double)re[k] * re[k] + (double)im[k] * im[k]); }
                    o[f * Mels + m] = (float)Math.Log(Math.Max(e, 1.1920929e-07));
                }
            }
            for (int m = 0; m < Mels; m++)
            {
                double mean = 0; for (int f = 0; f < frames; f++) mean += o[f * Mels + m];
                mean /= frames;
                for (int f = 0; f < frames; f++) o[f * Mels + m] -= (float)mean;
            }
            return o;
        }
    }
}
