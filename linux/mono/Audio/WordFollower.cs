using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// Tune to my voice's teleprompter on Linux (4.1.4): an offline speech recognizer (Vosk, with its small English model,
    /// in the app's speech folder) listens to the raw mic, and every word it hears in the script's order counts - the
    /// way Windows' own recognizer does it. Nothing leaves the PC and nothing is kept. Without the recognizer the words
    /// count up as you talk (about 2.6 a second), as on a Windows PC without speech.
    /// </summary>
    internal sealed class WordFollower : IDisposable
    {
        readonly string[] words;
        int heard, utteranceBase;
        readonly object gate = new object();
        double voiceSeconds;
        // 48 -> 16 kHz for the recognizer.
        readonly float[] hist = new float[Fir.Length]; int histPos, phase;
        IntPtr recognizer;
        Thread worker;
        readonly Queue<short[]> chunks = new Queue<short[]>();
        volatile bool stopping;

        public int Heard { get { lock (gate) return heard; } }
        public int Count { get { return words.Length; } }
        /// <summary>No recognizer: the count follows talking time instead.</summary>
        public bool Estimating { get; private set; }
        /// <summary>Why the recognizer couldn't start (Estimating), or null.</summary>
        public string Problem { get; private set; }
        /// <summary>The recognizer's last guess (for the status line), or null.</summary>
        public volatile string LastHeard;

        public WordFollower(string script)
        {
            words = Words(script);
            try { Start(); }
            catch (Exception ex) { Problem = ex.Message; Estimating = true; Stop(); }
        }

        public static string[] Words(string text)
        {
            return Regex.Split((text ?? "").ToLowerInvariant().Replace("’", "'"), @"[^a-z0-9']+").Where(w => w.Trim('\'').Length > 0).Select(w => w.Trim('\'')).ToArray();
        }

        void Start()
        {
            IntPtr model = Vosk.Acquire();
            recognizer = Vosk.NewRecognizer(model, 16000f);
            if (recognizer == IntPtr.Zero) { Vosk.Release(); throw new InvalidOperationException("the speech recognizer didn't start"); }
            worker = new Thread(Listen) { IsBackground = true, Name = "WordFollower" };
            worker.Start();
        }

        /// <summary>The recognizer's thread: audio in 50 ms pieces, what it heard so far after each.</summary>
        void Listen()
        {
            var block = new List<short>(1600);
            while (true)
            {
                short[] c;
                lock (chunks)
                {
                    while (chunks.Count == 0 && !stopping) Monitor.Wait(chunks, 200);
                    if (stopping) return;
                    c = chunks.Dequeue();
                }
                block.AddRange(c);
                if (block.Count < 800) continue;
                var a = block.ToArray(); block.Clear();
                int r = Vosk.Accept(recognizer, a, a.Length);
                if (r < 0) continue;
                string json = r == 1 ? Vosk.Result(recognizer) : Vosk.Partial(recognizer);
                var m = Regex.Match(json ?? "", "\"(?:text|partial)\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success && m.Groups[1].Value.Length > 0) Align(m.Groups[1].Value, r == 1);
                else if (r == 1) lock (gate) utteranceBase = heard;
            }
        }

        /// <summary>
        /// What the recognizer heard in this utterance, matched to the script from where the utterance began: each word
        /// found among the next three expected ones moves on (a skipped or swallowed word doesn't block the rest), and two in a
        /// row further on catch up after a misheard stretch.
        /// </summary>
        void Align(string text, bool final)
        {
            var h = Words(text);
            lock (gate)
            {
                int i = utteranceBase;
                for (int p = 0; p < h.Length; p++)
                {
                    int j = i;
                    for (; j < Math.Min(words.Length, i + 3); j++)
                        if (Same(h[p], words[j])) break;
                    if (j < Math.Min(words.Length, i + 3)) { i = j + 1; continue; }
                    // Lost for longer (a misheard stretch): two words in a row that match two of the script's in a row,
                    // a little further on, pick it up from there.
                    if (p + 1 < h.Length)
                        for (j = i + 3; j + 1 < Math.Min(words.Length, i + 12); j++)
                            if (Same(h[p], words[j]) && Same(h[p + 1], words[j + 1])) { i = j + 1; break; }
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
            if (worker == null) return;
            var o = new short[n / 3 + 2]; int m = 0;
            for (int i = 0; i < n; i++)
            {
                hist[histPos] = b[i]; histPos = (histPos + 1) % hist.Length;
                if (++phase < 3) continue;
                phase = 0; double y = 0; int k = histPos;
                for (int t = 0; t < Fir.Length; t++) { y += Fir[t] * hist[k]; k = k + 1 == hist.Length ? 0 : k + 1; }
                if (m < o.Length) o[m++] = (short)Math.Max(-32768, Math.Min(32767, y * 32768));
            }
            if (m < o.Length) Array.Resize(ref o, m);
            lock (chunks) { if (chunks.Count < 500) chunks.Enqueue(o); Monitor.PulseAll(chunks); }
        }

        /// <summary>For tests: a whole recording (16 kHz mono 16-bit WAV) at once, then the result.</summary>
        internal int FollowWave(string wavPath)
        {
            if (recognizer == IntPtr.Zero) return 0;
            var b = File.ReadAllBytes(wavPath);
            int data = 12; while (data + 8 < b.Length && System.Text.Encoding.ASCII.GetString(b, data, 4) != "data") data += 8 + BitConverter.ToInt32(b, data + 4);
            int n = Math.Min(BitConverter.ToInt32(b, data + 4), b.Length - data - 8) / 2;
            var s = new short[n]; Buffer.BlockCopy(b, data + 8, s, 0, n * 2);
            lock (chunks) { chunks.Enqueue(s); chunks.Enqueue(new short[16000]); Monitor.PulseAll(chunks); }
            var until = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < until) { lock (chunks) if (chunks.Count == 0) break; Thread.Sleep(20); }
            Thread.Sleep(200);
            return Heard;
        }

        void Stop()
        {
            lock (chunks) { stopping = true; Monitor.PulseAll(chunks); }
            var w = worker; worker = null;
            if (w != null) w.Join(2000);
            var r = recognizer; recognizer = IntPtr.Zero;
            if (r != IntPtr.Zero) { Vosk.FreeRecognizer(r); Vosk.Release(); }
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

        /// <summary>
        /// Vosk (libvosk.so and its model in the app's speech folder), loaded on first use. The model (about 70 MB) stays
        /// loaded while the teleprompter runs and is let go a minute after the last line.
        /// </summary>
        static class Vosk
        {
            [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
            [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr handle, string name);
            [DllImport("libdl.so.2")] static extern IntPtr dlerror();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ModelNewFn([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr p);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr RecognizerNewFn(IntPtr model, float rate);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AcceptFn(IntPtr rec, short[] data, int n);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr TextFn(IntPtr rec);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LogLevelFn(int level);

            static ModelNewFn modelNew; static FreeFn modelFree, recFree;
            static RecognizerNewFn recNew; static AcceptFn accept; static TextFn result, partial;
            static IntPtr model; static int users;
            static Timer letGo;
            static readonly object g = new object();

            public static string Folder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "speech"); } }

            static T Fn<T>(IntPtr lib, string name) where T : class
            {
                IntPtr p = dlsym(lib, name);
                if (p == IntPtr.Zero) throw new InvalidOperationException("libvosk.so has no " + name);
                return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }

            static void Load()
            {
                if (modelNew != null) return;
                string so = Path.Combine(Folder, "libvosk.so");
                if (!File.Exists(so)) throw new FileNotFoundException("the speech recognizer isn't installed");
                IntPtr lib = dlopen(so, 2);   // RTLD_NOW
                if (lib == IntPtr.Zero) { IntPtr e = dlerror(); throw new InvalidOperationException("the speech recognizer didn't load: " + (e != IntPtr.Zero ? Marshal.PtrToStringAnsi(e) : "?")); }
                Fn<LogLevelFn>(lib, "vosk_set_log_level")(-1);
                modelFree = Fn<FreeFn>(lib, "vosk_model_free"); recFree = Fn<FreeFn>(lib, "vosk_recognizer_free");
                recNew = Fn<RecognizerNewFn>(lib, "vosk_recognizer_new"); accept = Fn<AcceptFn>(lib, "vosk_recognizer_accept_waveform_s");
                result = Fn<TextFn>(lib, "vosk_recognizer_result"); partial = Fn<TextFn>(lib, "vosk_recognizer_partial_result");
                modelNew = Fn<ModelNewFn>(lib, "vosk_model_new");
            }

            public static IntPtr Acquire()
            {
                lock (g)
                {
                    Load();
                    if (letGo != null) { letGo.Dispose(); letGo = null; }
                    if (model == IntPtr.Zero)
                    {
                        string dir = Path.Combine(Folder, "model");
                        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("the speech model isn't installed");
                        model = modelNew(dir);
                        if (model == IntPtr.Zero) throw new InvalidOperationException("the speech model didn't load");
                    }
                    users++;
                    return model;
                }
            }

            public static void Release()
            {
                lock (g)
                {
                    if (users > 0) users--;
                    if (users > 0 || model == IntPtr.Zero || letGo != null) return;
                    letGo = new Timer(_ =>
                    {
                        lock (g)
                        {
                            if (users > 0 || model == IntPtr.Zero) return;
                            modelFree(model); model = IntPtr.Zero;
                            if (letGo != null) { letGo.Dispose(); letGo = null; }
                        }
                    }, null, 60000, Timeout.Infinite);
                }
            }

            public static IntPtr NewRecognizer(IntPtr m, float rate) { return recNew(m, rate); }
            public static void FreeRecognizer(IntPtr r) { recFree(r); }
            public static int Accept(IntPtr r, short[] a, int n) { return accept(r, a, n); }
            public static string Result(IntPtr r) { return Utf8(result(r)); }
            public static string Partial(IntPtr r) { return Utf8(partial(r)); }

            static string Utf8(IntPtr p)
            {
                if (p == IntPtr.Zero) return null;
                int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
                var b = new byte[n]; Marshal.Copy(p, b, 0, n);
                return System.Text.Encoding.UTF8.GetString(b);
            }
        }
    }
}
