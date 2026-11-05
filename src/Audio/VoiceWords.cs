using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// Voice hotkeys (4.4): a word or sentence you recorded, heard again in your mic, triggers a hotkey. Your takes are kept
    /// as numbers only (how the sound's spectrum moves, 50 times a second - never the sound), matched by dynamic time
    /// warping, so it works in any language with no speech recognizer and hears a phrase said faster, slower or inside a
    /// longer sentence. Runs only while a voice hotkey is bound, on its own low-priority thread; nothing leaves the PC.
    /// </summary>
    internal sealed class VoiceWords : IDisposable
    {
        public const int Fs = 48000;
        const int Bands = 26, Coeffs = 12;
        internal const int Dim = Coeffs * 2;

        /// <summary>A phrase to listen for: its takes (each frames x Dim numbers) and how close a match has to be.</summary>
        public sealed class Phrase
        {
            public object Tag;
            public string Text;
            public List<float[][]> Takes = new List<float[][]>();
            public double Limit = 1;
        }

        volatile Phrase[] phrases = new Phrase[0];
        /// <summary>A phrase was heard (its own thread). Also how close it was (0 = the same, 1 = at the limit).</summary>
        public event Action<Phrase, double> Heard;
        /// <summary>Tests and the phrase dialog: the closest any phrase came in the last speech (1 = at the limit, lower = closer).</summary>
        public volatile float LastScore = float.NaN;

        // The audio thread writes here; the worker reads.
        const int RingSize = 1 << 19;
        readonly float[] ring = new float[RingSize];
        long written;
        Thread worker;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        public void Set(IEnumerable<Phrase> list)
        {
            phrases = list.Where(p => p.Takes.Count > 0).ToArray();
            if (phrases.Length > 0 && worker == null)
            {
                stop = false;
                worker = new Thread(Work) { IsBackground = true, Name = "Voice hotkeys", Priority = ThreadPriority.BelowNormal };
                worker.Start();
            }
        }

        public bool Listening { get { return phrases.Length > 0; } }

        /// <summary>Audio thread: the raw mic, 48 kHz. Copies and returns at once.</summary>
        public void Feed(float[] b, int n, long t)
        {
            if (phrases.Length == 0) return;
            long w = Interlocked.Read(ref written);
            for (int i = 0; i < n; i++) ring[(w + i) & (RingSize - 1)] = b[i];
            Interlocked.Add(ref written, n);
            wake.Set();
        }

        void Work()
        {
            var det = new Detector(this);
            long read = Interlocked.Read(ref written);
            var chunk = new float[4800];
            while (!stop)
            {
                wake.WaitOne(200);
                if (stop) break;
                long w = Interlocked.Read(ref written);
                if (w - read > RingSize / 2) read = w - 4800;   // fell far behind (a busy PC): drop it, this isn't sound anyone hears
                while (w - read >= 480 && !stop)
                {
                    int n = (int)Math.Min(chunk.Length, w - read);
                    for (int i = 0; i < n; i++) chunk[i] = ring[(read + i) & (RingSize - 1)];
                    read += n;
                    try { det.Push(chunk, n); } catch { }
                }
            }
        }

        public void Dispose() { stop = true; wake.Set(); var w = worker; if (w != null) w.Join(500); worker = null; }

        // ------------------------------------------------------------------ numbers from sound

        /// <summary>48 kHz -> feature frames, one per 20 ms: 12 cepstra (the spectrum's shape, not its level) and how they move.</summary>
        internal sealed class Features
        {
            const int N = 512, Win = 400, Hop = 160;            // at 16 kHz: 25 ms windows, 10 ms apart
            static readonly float[] Fir = MakeFir();
            static readonly float[] Hann = Enumerable.Range(0, Win).Select(i => (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Win - 1)))).ToArray();
            static readonly float[][] Mel = MakeMel();
            static readonly float[][] Dct = MakeDct();
            readonly Fft fft = new Fft(N);
            readonly float[] re = new float[N], im = new float[N], hist = new float[Fir.Length], buf = new float[Win];
            readonly float[] bands = new float[Bands];
            int pos, phase, fill, skip;
            /// <summary>Called with each 10 ms frame's cepstra and level (dB).</summary>
            public Action<float[], float> Frame;

            public void Push(float[] x, int n)
            {
                for (int i = 0; i < n; i++)
                {
                    hist[pos] = x[i]; pos = (pos + 1) % hist.Length;
                    if (++phase < 3) continue;
                    phase = 0;
                    double y = 0; int k = pos;
                    for (int t = 0; t < Fir.Length; t++) { y += Fir[t] * hist[k]; k = k + 1 == hist.Length ? 0 : k + 1; }
                    Sample((float)y);
                }
            }

            void Sample(float v)
            {
                buf[fill++] = v;
                if (fill < Win) return;
                Analyse();
                Array.Copy(buf, Hop, buf, 0, Win - Hop);
                fill = Win - Hop;
            }

            void Analyse()
            {
                double e = 0;
                for (int i = 0; i < Win; i++) { e += buf[i] * (double)buf[i]; re[i] = buf[i] * Hann[i]; im[i] = 0; }
                Array.Clear(re, Win, N - Win); Array.Clear(im, Win, N - Win);
                fft.Transform(re, im, false);
                for (int b = 0; b < Bands; b++)
                {
                    double s = 0; var w = Mel[b];
                    for (int k = 0; k < w.Length; k++) if (w[k] > 0) s += w[k] * (re[k] * (double)re[k] + im[k] * (double)im[k]);
                    bands[b] = (float)Math.Log(s + 1e-7);
                }
                var c = new float[Coeffs];
                for (int q = 0; q < Coeffs; q++)
                {
                    double s = 0; var d = Dct[q];
                    for (int b = 0; b < Bands; b++) s += d[b] * bands[b];
                    c[q] = (float)s;
                }
                var f = Frame;
                if (f != null) f(c, (float)(10 * Math.Log10(e / Win + 1e-12)));
            }

            static float[] MakeFir()
            {
                const int taps = 47; double fc = 7000.0 / 48000, sum = 0; var h = new float[taps];
                for (int i = 0; i < taps; i++)
                {
                    int x = i - taps / 2;
                    double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                    h[i] = (float)(sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1)))); sum += h[i];
                }
                for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / sum);
                return h;
            }

            static float[][] MakeMel()
            {
                Func<double, double> mel = f => 2595 * Math.Log10(1 + f / 700), hz = m => 700 * (Math.Pow(10, m / 2595) - 1);
                double lo = mel(120), hi = mel(7000);
                var edges = Enumerable.Range(0, Bands + 2).Select(i => hz(lo + (hi - lo) * i / (Bands + 1)) * N / 16000.0).ToArray();
                var m2 = new float[Bands][];
                for (int b = 0; b < Bands; b++)
                {
                    m2[b] = new float[N / 2 + 1];
                    for (int k = 0; k <= N / 2; k++)
                    {
                        double up = (k - edges[b]) / (edges[b + 1] - edges[b]), down = (edges[b + 2] - k) / (edges[b + 2] - edges[b + 1]);
                        m2[b][k] = (float)Math.Max(0, Math.Min(up, down));
                    }
                }
                return m2;
            }

            // Cepstra 1 to 12: the first (overall level) is left out, so a louder or quieter take looks the same.
            static float[][] MakeDct()
            {
                return Enumerable.Range(1, Coeffs).Select(q => Enumerable.Range(0, Bands).Select(b => (float)(Math.Sqrt(2.0 / Bands) * Math.Cos(Math.PI * q * (b + 0.5) / Bands))).ToArray()).ToArray();
            }
        }

        /// <summary>10 ms frames -> frames of 20 ms with their movement: every second frame, with the slope around it.</summary>
        static float[][] WithMovement(List<float[]> c)
        {
            var o = new List<float[]>();
            for (int i = 0; i < c.Count; i += 2)
            {
                var f = new float[Dim];
                Array.Copy(c[i], f, Coeffs);
                var a = c[Math.Max(0, i - 4)]; var b = c[Math.Min(c.Count - 1, i + 4)];
                for (int q = 0; q < Coeffs; q++) f[Coeffs + q] = (b[q] - a[q]) * 0.5f;
                o.Add(f);
            }
            return o.ToArray();
        }

        // ------------------------------------------------------------------ a recorded take

        /// <summary>A take from a recording (48 kHz): only the part with voice in it. Null = no voice.</summary>
        public static float[][] TakeOf(float[] x48, int n)
        {
            var frames = new List<float[]>(); var levels = new List<float>();
            var fx = new Features { Frame = (c, db) => { frames.Add(c); levels.Add(db); } };
            fx.Push(x48, n);
            if (levels.Count < 10) return null;
            var sorted = levels.OrderBy(v => v).ToList();
            float floor = sorted[sorted.Count / 10], top = sorted[sorted.Count * 95 / 100];
            if (top < -60 || top - floor < 8) return null;
            float on = Math.Max(floor + 8, top - 30);
            int first = levels.FindIndex(v => v > on), last = levels.FindLastIndex(v => v > on);
            if (first < 0 || last - first < 12) return null;   // under 120 ms: a click, not a word
            first = Math.Max(0, first - 3); last = Math.Min(levels.Count - 1, last + 3);
            return WithMovement(frames.GetRange(first, last - first + 1));
        }

        /// <summary>
        /// How far another take may be: from how far your own takes are from each other, with a cap and a floor, so a very
        /// even or very uneven set of takes can't make it trigger on anything or on nothing.
        /// </summary>
        public static double LimitFor(List<float[][]> takes, double sensitivity = 1)
        {
            double worst = 0;
            for (int i = 0; i < takes.Count; i++)
                for (int j = 0; j < takes.Count; j++)
                    if (i != j) worst = Math.Max(worst, Distance(takes[j], takes[i]));
            return Math.Max(MinLimit, Math.Min(MaxLimit, worst * 1.15)) * sensitivity;
        }

        // On text-to-speech voices the phrase said faster, slower or in a sentence came in at 2.6-3.9, and any other talk at
        // over 7 (test/VoiceWordsTest.cs): room for a real voice, which varies more, on both sides.
        internal const double MinLimit = 4.8, MaxLimit = 6.6;

        // ------------------------------------------------------------------ matching

        /// <summary>
        /// How far the template is from the best stretch of the speech: dynamic time warping free to start and end anywhere in
        /// the speech, cost per template frame. Steps stay near diagonal, so a phrase said up to twice as fast or slow still
        /// matches but not a fraction of it.
        /// </summary>
        public static double Distance(float[][] template, float[][] speech)
        {
            int m = template.Length, n = speech.Length;
            if (m == 0 || n == 0) return double.MaxValue;
            var prev = new double[m + 1]; var cur = new double[m + 1]; var prev2 = new double[m + 1];
            for (int j = 1; j <= m; j++) prev[j] = prev2[j] = double.MaxValue / 4;
            double best = double.MaxValue;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = 0;   // may start at any frame of the speech
                var s = speech[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    double d = Dist(template[j - 1], s);
                    double diag = prev[j - 1] + d;                                  // (1, 1)
                    double fast = j >= 2 ? prev[j - 2] + d + Dist(template[j - 2], s) : double.MaxValue;   // (1, 2): speech faster
                    double slow = i >= 2 ? prev2[j - 1] + d : double.MaxValue;     // (2, 1): speech slower
                    cur[j] = Math.Min(diag, Math.Min(fast, slow));
                }
                if (cur[m] < best) best = cur[m];
                var t = prev2; prev2 = prev; prev = cur; cur = t;
            }
            return best / m;
        }

        static double Dist(float[] a, float[] b)
        {
            double s = 0;
            for (int q = 0; q < Coeffs; q++) { double d = a[q] - b[q]; s += d * d; }
            for (int q = Coeffs; q < Dim; q++) { double d = a[q] - b[q]; s += 0.5 * d * d; }
            return Math.Sqrt(s);
        }

        // ------------------------------------------------------------------ listening

        /// <summary>Finds speech in the mic and asks every phrase, a few times a second while you talk, whether it just ended.</summary>
        internal sealed class Detector
        {
            const int MaxFrames = 900, CheckEvery = 25, Hangover = 40;   // 10 ms frames
            readonly VoiceWords owner;
            readonly Features fx = new Features();
            readonly List<float[]> speech = new List<float[]>();
            readonly List<float[]> pre = new List<float[]>();
            float floor = float.NaN;
            int quiet = Hangover + 1, sinceCheck, refractory;
            bool talking;

            public Detector(VoiceWords o) { owner = o; fx.Frame = Frame; }
            public void Push(float[] x, int n) { fx.Push(x, n); }

            void Frame(float[] c, float db)
            {
                floor = float.IsNaN(floor) || db < floor ? db : Math.Min(db, floor + 0.02f);
                if (refractory > 0) { refractory--; speech.Clear(); talking = false; return; }
                bool voice = db > floor + 9 && db > -62;
                if (voice) quiet = 0; else quiet++;
                if (!talking)
                {
                    pre.Add(c); if (pre.Count > 12) pre.RemoveAt(0);
                    if (!voice) return;
                    talking = true; speech.Clear(); speech.AddRange(pre); sinceCheck = 0;
                }
                speech.Add(c);
                if (speech.Count > MaxFrames) speech.RemoveRange(0, speech.Count - MaxFrames);
                if (++sinceCheck >= CheckEvery || quiet == Hangover) { sinceCheck = 0; Check(); }
                if (talking && quiet > Hangover) { talking = false; speech.Clear(); pre.Clear(); }
            }

            void Check()
            {
                var ps = owner.phrases;
                if (ps.Length == 0 || speech.Count < 20) return;
                float best = float.MaxValue;
                foreach (var p in ps)
                    foreach (var t in p.Takes)
                    {
                        // Only the end of the speech: it fires right as the phrase ends, even in mid-sentence.
                        int want = Math.Min(speech.Count, (t.Length * 2 + 30) * 2);
                        var tail = WithMovement(speech.GetRange(speech.Count - want, want));
                        double score = EndDistance(t, tail) / p.Limit;
                        if (score < best) best = (float)score;
                        if (score <= 1)
                        {
                            owner.LastScore = (float)score;
                            refractory = 150;
                            var h = owner.Heard; if (h != null) h(p, score);
                            return;
                        }
                    }
                owner.LastScore = best;
            }
        }

        /// <summary>The same warping, but the match has to end close to the end of the speech (what was just said).</summary>
        internal static double EndDistance(float[][] template, float[][] speech)
        {
            int m = template.Length, n = speech.Length;
            if (m == 0 || n == 0) return double.MaxValue;
            var prev = new double[m + 1]; var cur = new double[m + 1]; var prev2 = new double[m + 1];
            for (int j = 1; j <= m; j++) prev[j] = prev2[j] = double.MaxValue / 4;
            double best = double.MaxValue;
            int lastFew = Math.Max(0, n - 25);   // up to half a second of tail after the phrase (its hangover)
            for (int i = 1; i <= n; i++)
            {
                cur[0] = 0;
                var s = speech[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    double d = Dist(template[j - 1], s);
                    double diag = prev[j - 1] + d;
                    double fast = j >= 2 ? prev[j - 2] + d + Dist(template[j - 2], s) : double.MaxValue;
                    double slow = i >= 2 ? prev2[j - 1] + d : double.MaxValue;
                    cur[j] = Math.Min(diag, Math.Min(fast, slow));
                }
                if (i >= lastFew && cur[m] < best) best = cur[m];
                var t = prev2; prev2 = prev; prev = cur; cur = t;
            }
            return best / m;
        }

        // ------------------------------------------------------------------ takes in the settings file

        /// <summary>A take as text for the settings (numbers rounded to 0.1, as 16-bit values, base 64).</summary>
        public static string Pack(float[][] take)
        {
            var b = new byte[4 + take.Length * Dim * 2];
            BitConverter.GetBytes(take.Length).CopyTo(b, 0);
            int o = 4;
            foreach (var f in take)
                foreach (var v in f)
                {
                    short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v * 100)));
                    b[o++] = (byte)s; b[o++] = (byte)(s >> 8);
                }
            return Convert.ToBase64String(b);
        }

        /// <summary>Null when it isn't one.</summary>
        public static float[][] Unpack(string text)
        {
            try
            {
                var b = Convert.FromBase64String(text);
                int n = BitConverter.ToInt32(b, 0);
                if (n <= 0 || b.Length != 4 + n * Dim * 2) return null;
                var t = new float[n][]; int o = 4;
                for (int i = 0; i < n; i++)
                {
                    t[i] = new float[Dim];
                    for (int q = 0; q < Dim; q++) { t[i][q] = (short)(b[o] | (b[o + 1] << 8)) / 100f; o += 2; }
                }
                return t;
            }
            catch { return null; }
        }
    }
}
