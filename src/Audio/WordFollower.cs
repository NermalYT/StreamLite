using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// Follows a script you read out loud, word by word (4.1, the teleprompter in Tune to my voice): Windows' own
    /// offline speech recognizer listens to the raw mic, and every word it hears in the script's order counts. It knows
    /// the script (any point of it, so a restart mid-sentence still matches) and plain dictation besides, so a misread
    /// word or a skipped one doesn't stop it. Nothing leaves the PC. Without a recognizer (a Windows language without
    /// speech) it estimates from how long you've been talking.
    /// </summary>
    internal sealed class WordFollower : IDisposable
    {
        readonly string[] words;          // the script's words, lower case, no punctuation
        SpeechRecognitionEngine engine;
        PcmPipe pipe;
        int heard, utteranceBase;
        readonly object gate = new object();
        // 48 -> 16 kHz for the recognizer.
        readonly float[] hist = new float[Fir.Length]; int histPos, phase;
        /// <summary>Words of the script heard so far (0..Count), in order.</summary>
        public int Heard { get { lock (gate) return heard; } }
        public int Count { get { return words.Length; } }
        /// <summary>No recognizer on this PC: the count follows talking time instead.</summary>
        public bool Estimating { get; private set; }
        /// <summary>Why the recognizer couldn't start (Estimating), or null.</summary>
        public string Problem { get; private set; }
        /// <summary>The recognizer's last guess (for the status line), or null.</summary>
        public volatile string LastHeard;
        double voiceSeconds;

        public WordFollower(string script)
        {
            words = Words(script);
            try { Start(script); }
            catch (Exception ex) { Problem = ex.Message; Estimating = true; Stop(); }
        }

        /// <summary>A script's words as they're compared: lower case, letters, digits and apostrophes only.</summary>
        public static string[] Words(string text)
        {
            return Regex.Split((text ?? "").ToLowerInvariant().Replace("’", "'"), @"[^a-z0-9']+").Where(w => w.Trim('\'').Length > 0).Select(w => w.Trim('\'')).ToArray();
        }

        static RecognizerInfo Recognizer()
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            var ui = Thread.CurrentThread.CurrentUICulture;
            return all.FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == ui.TwoLetterISOLanguageName) ?? all.FirstOrDefault();
        }

        void Start(string script)
        {
            var info = Recognizer();
            if (info == null) throw new InvalidOperationException("no speech recognizer");
            engine = new SpeechRecognitionEngine(info);
            // The script from any word on (a restart mid-sentence matches too), and dictation for everything else.
            var choices = new Choices();
            var spoken = Regex.Split(script, @"\s+").Where(w => Words(w).Length > 0).ToArray();
            for (int i = 0; i < spoken.Length; i++) choices.Add(string.Join(" ", spoken.Skip(i).Select(w => string.Join(" ", Words(w)))));
            engine.LoadGrammar(new Grammar(new GrammarBuilder(choices)) { Name = "script", Weight = 1f });
            engine.LoadGrammar(new DictationGrammar { Name = "free", Weight = 0.3f });
            engine.SpeechHypothesized += (s, e) => Align(e.Result.Text, false);
            engine.SpeechRecognized += (s, e) => Align(e.Result.Text, true);
            engine.SpeechRecognitionRejected += (s, e) => { if (e.Result != null && e.Result.Text.Length > 0) Align(e.Result.Text, true); else lock (gate) utteranceBase = heard; };
            pipe = new PcmPipe();
            engine.SetInputToAudioStream(pipe, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }

        /// <summary>
        /// What the recognizer heard in this utterance, matched to the script from where the utterance began: each word
        /// found among the next three expected ones moves on (a skipped or swallowed word doesn't block the rest).
        /// </summary>
        void Align(string text, bool final)
        {
            var h = Words(text);
            lock (gate)
            {
                int i = utteranceBase;
                foreach (var w in h)
                {
                    for (int j = i; j < Math.Min(words.Length, i + 3); j++)
                        if (Same(w, words[j])) { i = j + 1; break; }
                }
                if (i > heard) heard = i;
                if (final) utteranceBase = heard;
            }
            LastHeard = text;
        }

        /// <summary>The same word, give or take a letter or two (the recognizer's spelling of a name, a plural).</summary>
        internal static bool Same(string a, string b)
        {
            if (a == b) return true;
            if (a.Length < 3 || b.Length < 3) return false;
            int d = Distance(a, b);
            return d <= (Math.Max(a.Length, b.Length) >= 6 ? 2 : 1);
        }

        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// The raw mic (48 kHz), as it comes. voiced = this block has your voice in it (the estimate without a recognizer
        /// counts about 2.6 words a second of talking).
        /// </summary>
        public void Feed(float[] b, int n, bool voiced)
        {
            if (Estimating)
            {
                if (voiced) voiceSeconds += n / 48000.0;
                lock (gate) heard = Math.Max(heard, Math.Min(words.Length, (int)(voiceSeconds * 2.6)));
                return;
            }
            var p = pipe; if (p == null) return;
            var o = new byte[n / 3 * 2 + 4]; int m = 0;
            for (int i = 0; i < n; i++)
            {
                hist[histPos] = b[i]; histPos = (histPos + 1) % hist.Length;
                if (++phase < 3) continue;
                phase = 0; double y = 0; int k = histPos;
                for (int t = 0; t < Fir.Length; t++) { y += Fir[t] * hist[k]; k = k + 1 == hist.Length ? 0 : k + 1; }
                int v = (int)Math.Max(-32768, Math.Min(32767, y * 32768));
                if (m + 1 < o.Length) { o[m++] = (byte)v; o[m++] = (byte)(v >> 8); }
            }
            p.Write(o, m);
        }

        /// <summary>For tests: a whole recording (16 kHz mono WAV) at once, then the result.</summary>
        internal int FollowWave(string wavPath)
        {
            if (engine == null) return 0;
            engine.RecognizeAsyncCancel();
            engine.SetInputToWaveFile(wavPath);
            try { while (engine.Recognize(TimeSpan.FromSeconds(30)) != null) { } } catch { }
            return Heard;
        }

        void Stop()
        {
            var e = engine; engine = null;
            var p = pipe; pipe = null;
            if (p != null) p.Close();
            if (e != null) { try { e.RecognizeAsyncCancel(); } catch { } try { e.Dispose(); } catch { } }
        }

        public void Dispose() { Stop(); }

        static readonly float[] Fir = MakeFir(47, 7000.0 / 48000);
        static float[] MakeFir(int taps, double fc)
        {
            var h = new float[taps]; double sum = 0; int mid = taps / 2;
            for (int i = 0; i < taps; i++)
            {
                int x = i - mid;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
                h[i] = (float)(sinc * w); sum += h[i];
            }
            for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / sum);
            return h;
        }

        /// <summary>The recognizer's input: audio written as it comes, read by the recognizer (waiting until there is some).</summary>
        sealed class PcmPipe : Stream
        {
            readonly Queue<byte[]> chunks = new Queue<byte[]>();
            byte[] cur; int curPos;
            bool closed;
            readonly object g = new object();

            public void Write(byte[] data, int n)
            {
                var c = new byte[n]; Buffer.BlockCopy(data, 0, c, 0, n);
                lock (g) { if (closed) return; chunks.Enqueue(c); Monitor.PulseAll(g); }
            }

            /// <summary>Waits until the whole request is there: the recognizer takes a short read for the end of the audio.</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                int done = 0;
                lock (g)
                    while (done < count)
                    {
                        if (cur == null || curPos >= cur.Length)
                        {
                            if (chunks.Count == 0)
                            {
                                if (closed) break;
                                Monitor.Wait(g, 200);
                                continue;
                            }
                            cur = chunks.Dequeue(); curPos = 0;
                        }
                        int n = Math.Min(count - done, cur.Length - curPos);
                        Buffer.BlockCopy(cur, curPos, buffer, offset + done, n); curPos += n; done += n;
                    }
                return done;
            }

            public override void Close() { lock (g) { closed = true; Monitor.PulseAll(g); } base.Close(); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            // The recognizer asks a stream for its length and position: an endless live stream answers -1 / 0.
            public override long Length { get { return -1; } }
            public override long Position { get { return 0; } set { } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { return 0; }
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { var c = new byte[count]; Buffer.BlockCopy(buffer, offset, c, 0, count); Write(c, count); }
        }
    }
}
