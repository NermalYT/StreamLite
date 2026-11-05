using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// "Set up your mic" (3.5.2, Re-Setup Mic from 4.4): three required recordings - right at your mic, loud, quiet - and,
    /// now or any time later, optional sections each aimed at one kind of sound: hums and thinking sounds (mhm, mmm, ooh,
    /// umm, ahh), S sounds, pops and hard sounds, soft sounds and breaths, further away, and the silly voice you put on for
    /// fun. What they tune: the low cut and de-esser from your pitch and your S, how far auto level may lift you, noise
    /// removal and the gate so nothing you do gets cut off, Across the room, clipping, and your voiceprint for Only my voice
    /// (your far and silly voices count as you too). The recordings themselves are only kept as levels and voiceprints.
    /// Everything is measured on the RAW mic (3.5.6): what the microphone itself picks up, before any StreamLite setting -
    /// noise removal, gate, EQ, mute or push to talk, even with the virtual mic switched off - so the settings it makes
    /// don't depend on what was set before.
    /// </summary>
    internal sealed class VoiceSetup
    {
        public enum Kind { Normal, Far, Loud, Quiet, Silly, Hums, Esses, Pops, Soft }

        public sealed class Step
        {
            public Kind Kind; public string Title, Say, Line; public bool Optional; public double Seconds;
            /// <summary>The section's one line on the menu: what it tunes.</summary>
            public string Tunes;
            /// <summary>Words the recognizer can follow (false: sounds that aren't words - its progress goes by the voice heard).</summary>
            public bool Follow = true;
            /// <summary>Sounds quieter than talking count too (a hum in a noisy room).</summary>
            public bool Faint;
            /// <summary>The raw mic, 10 ms at a time (RMS dB), and its loudest sample (dBFS).</summary>
            public readonly List<float> RawDb = new List<float>();
            public float PeakDb = -120f;
            /// <summary>The room before you talk (Normal only: the first 2 seconds, quiet).</summary>
            public readonly List<float> QuietDb = new List<float>();
            /// <summary>Your voice straight from the mic, 16 kHz: the 10 ms pieces with voice in them (TakeVoice).</summary>
            public float[] Voice16;
            /// <summary>The same voice pieces at 48 kHz, raw - the voiceprint is built from a noise-removed copy of them.</summary>
            public readonly List<float> Voice48 = new List<float>();
            readonly List<float> voice16 = new List<float>();
            readonly float[] piece48 = new float[480], piece16 = new float[160];
            readonly float[] firHist = new float[Fir.Length]; int firPos, firPhase, piece16Fill;
            float floorDb = float.NaN;
            Rnnoise vad; readonly float[] vadIn = new float[480], vadOut = new float[480];
            /// <summary>Seconds of voice recorded so far in this step.</summary>
            public double VoiceSeconds { get { lock (RawDb) return voice16.Count / 16000.0; } }
            /// <summary>The room's level from the first step (dBFS), so a later step knows voice from room at once.</summary>
            public void SetRoom(double roomDb) { if (!double.IsNaN(roomDb)) floorDb = (float)roomDb; }
            /// <summary>This step's voice, 16 kHz (also kept in Voice16).</summary>
            public float[] TakeVoice() { lock (RawDb) Voice16 = voice16.ToArray(); return Voice16; }
            public bool Done, Skipped;
            /// <summary>The room itself (Normal only: the first 2 s, 48 kHz) - for hum.</summary>
            public readonly List<float> Room48 = new List<float>();
            float acc; int fill; float peak;

            /// <summary>A block of the raw mic (48 kHz); silence = the room before you talk.</summary>
            public void FeedRaw(float[] b, int n, bool silence)
            {
                for (int i = 0; i < n; i++)
                {
                    float v = b[i]; acc += v * v; float a = Math.Abs(v); if (a > peak) peak = a;
                    if (silence && Room48.Count < 96000) lock (RawDb) Room48.Add(v);
                    // 48 -> 16 kHz (low-pass, every third sample) for the voice analysis and the voiceprint.
                    firHist[firPos] = v; firPos = (firPos + 1) % firHist.Length;
                    if (++firPhase == 3)
                    {
                        firPhase = 0; double y = 0; int k = firPos;
                        for (int t = 0; t < Fir.Length; t++) { y += Fir[t] * firHist[k]; k = k + 1 == firHist.Length ? 0 : k + 1; }
                        if (piece16Fill < piece16.Length) piece16[piece16Fill++] = (float)y;
                    }
                    piece48[fill] = v;
                    if (++fill < 480) continue;
                    float db = (float)(10 * Math.Log10(acc / 480 + 1e-12));
                    // Voice: well above the room (the room's level follows the quietest pieces, rising 3 dB a second), or a
                    // little above it and speech to RNNoise's ear (analysis only - the audio isn't changed): a quiet voice in
                    // a noisy room is barely louder than the room.
                    floorDb = float.IsNaN(floorDb) || db < floorDb ? db : Math.Min(db, floorDb + 0.03f);
                    bool voiced = false;
                    if (!silence && db > -65f)
                    {
                        if (vad == null) vad = new Rnnoise();
                        for (int j = 0; j < 480; j++) vadIn[j] = piece48[j] * 32768f;
                        float prob = vad.ProcessFrame(vadIn, vadOut, 1f, 1f, 0.6f, 0f, false);
                        voiced = db > floorDb + (Faint ? 6f : 10f) || (prob > 0.6f && db > floorDb + 2f);
                    }
                    lock (RawDb)
                    {
                        if (silence) QuietDb.Add(db); else { RawDb.Add(db); PeakDb = Math.Max(PeakDb, (float)(20 * Math.Log10(peak + 1e-9))); }
                        if (voiced) { for (int j = 0; j < piece16Fill; j++) voice16.Add(piece16[j]); Voice48.AddRange(piece48); }
                    }
                    acc = 0; fill = 0; peak = 0; piece16Fill = 0;
                }
            }

            /// <summary>The room from this step's own quiet seconds (dBFS), NaN when it had none.</summary>
            public double OwnRoomDb()
            {
                List<float> q; lock (RawDb) q = QuietDb.OrderBy(d => d).ToList();
                return q.Count > 10 ? q[q.Count / 2] : double.NaN;
            }

            /// <summary>Your voice's level: the louder 20% of the 10 ms pieces above the room (dBFS RMS), NaN if none.</summary>
            public double VoiceDb(double roomDb)
            {
                List<float> v; lock (RawDb) v = RawDb.Where(d => d > roomDb + 10).OrderBy(d => d).ToList();
                return v.Count < 20 ? double.NaN : v[(int)(v.Count * 0.8)];
            }
        }

        /// <summary>Low-pass for 48 -> 16 kHz: 63-tap windowed sinc at 7 kHz.</summary>
        static readonly float[] Fir = MakeFir(63, 7000.0 / 48000);
        static float[] MakeFir(int taps, double fc)
        {
            var h = new float[taps]; double sum = 0; int m = taps / 2;
            for (int i = 0; i < taps; i++)
            {
                int x = i - m;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
                h[i] = (float)(sinc * w); sum += h[i];
            }
            for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / sum);
            return h;
        }

        /// <summary>What noise removal does at these settings, as RNNoise's numbers (the same on every engine's scale).</summary>
        static void NsNumbers(AppSettings s, out float minGain, out float voiceFloor, out float decay, out float sharpen)
        {
            double st = s.Strength / 100.0;
            double att = st <= 0.5 ? 10 + 40 * st : 30 + 40 * (st - 0.5);
            sharpen = st <= 0.5 ? 0f : (float)(3 * (st - 0.5));
            minGain = (float)Math.Pow(10, -att / 20);
            voiceFloor = (float)Math.Pow(minGain, 1 - 0.7 * (s.NsVoiceProtect / 100.0));
            decay = (float)(0.3 + 0.55 * (s.NsSmoothing / 100.0));
        }

        /// <summary>How much quieter noise removal leaves this sound at these settings (dB, 0 = untouched). Measured with the built-in remover.</summary>
        internal static double LossDb(List<float> raw48, AppSettings s)
        {
            if (raw48 == null || raw48.Count < 4800) return 0;
            float minGain, voiceFloor, decay, sharpen; NsNumbers(s, out minGain, out voiceFloor, out decay, out sharpen);
            var nn = new Rnnoise();
            var inF = new float[Rnnoise.FrameSize]; var outF = new float[Rnnoise.FrameSize];
            double before = 0, after = 0;
            for (int f = 0; f + Rnnoise.FrameSize <= raw48.Count; f += Rnnoise.FrameSize)
            {
                for (int i = 0; i < Rnnoise.FrameSize; i++) { inF[i] = raw48[f + i] * 32768f; before += inF[i] * (double)inF[i]; }
                nn.ProcessFrame(inF, outF, minGain, voiceFloor, decay, sharpen, true);
                for (int i = 0; i < Rnnoise.FrameSize; i++) after += outF[i] * (double)outF[i];
            }
            return 10 * Math.Log10((before + 1) / (after + 1));
        }

        /// <summary>
        /// A noise-removed copy of raw voice (48 kHz in, 16 kHz out) for the voiceprint: Only my voice listens after noise
        /// removal, so your print is made the way it will hear you - from the raw recording, at the strength just tuned.
        /// </summary>
        public static float[] Cleaned16(List<float> raw48, AppSettings s)
        {
            if (raw48 == null || raw48.Count < 480) return new float[0];
            var nn = new Rnnoise();
            float minGain, voiceFloor, decay, sharpen; NsNumbers(s, out minGain, out voiceFloor, out decay, out sharpen);
            var inF = new float[Rnnoise.FrameSize]; var outF = new float[Rnnoise.FrameSize];
            var hist = new float[Fir.Length]; int pos = 0, ph = 0;
            var o = new List<float>(raw48.Count / 3 + 16);
            for (int f = 0; f + Rnnoise.FrameSize <= raw48.Count; f += Rnnoise.FrameSize)
            {
                for (int i = 0; i < Rnnoise.FrameSize; i++) inF[i] = raw48[f + i] * 32768f;
                nn.ProcessFrame(inF, outF, minGain, voiceFloor, decay, sharpen, true);
                for (int i = 0; i < Rnnoise.FrameSize; i++)
                {
                    hist[pos] = outF[i] / 32768f; pos = (pos + 1) % hist.Length;
                    if (++ph < 3) continue;
                    ph = 0; double y = 0; int k = pos;
                    for (int t = 0; t < Fir.Length; t++) { y += Fir[t] * hist[k]; k = k + 1 == hist.Length ? 0 : k + 1; }
                    o.Add((float)y);
                }
            }
            return o.ToArray();
        }

        public static Step[] Steps()
        {
            return new[]
            {
                // The lines are read from the teleprompter (4.1): plain words (no hyphens or numbers), so each word on the
                // screen is one word the recognizer hears. Line = "" = say anything (no script).
                // 4.4: aimed. The callouts carry every sound of English once; the optional sections are built around one
                // kind of sound each, and what they measure tunes what that sound goes through.
                // Required first (the order the dialog walks them): nothing else is needed for a good mic.
                new Step { Kind = Kind.Normal, Seconds = 10, Title = "Right at your mic, your normal voice",
                    Say = "Sit where you usually do and talk at your normal volume. First stay quiet for 2 seconds (StreamLite listens to the room), then read the text aloud.",
                    Line = "Hey, can you hear me okay? I'm tuning my mic so it knows exactly how I sound. Push left, there's one behind the big boxes. Watch the bridge, I'll cover you from the stairs, so take it slow." },
                new Step { Kind = Kind.Loud, Seconds = 5, Title = "Loud, like a callout",
                    Say = "Still at your mic: as loud as you get in a game (no need to hurt your ears).",
                    Line = "He's on the stairs! Behind you, behind you! Push them now, shoot the last guy! Watch out, he's coming around the corner!" },
                new Step { Kind = Kind.Quiet, Seconds = 6, Title = "Quiet, like late at night",
                    Say = "As quietly as you talk when someone's asleep next door.",
                    Line = "Okay, one more game and then I'm going to bed. I'm so tired, so let's keep it down for a bit." },
                // Optional, any of them, now or later.
                new Step { Kind = Kind.Hums, Seconds = 8, Optional = true, Follow = false, Faint = true, Title = "Hums and thinking sounds",
                    Tunes = "Mhm, mmm, ooh, umm, ahh: noise removal and the gate stop cutting them off.",
                    Say = "The sounds you make without words, the ones noise removal and gates cut off most. Hold each one for about a second, at your normal volume.",
                    Line = "Mmm... mhm... umm... hmm... ooooh... ahhh... uh huh... mmm..." },
                new Step { Kind = Kind.Esses, Seconds = 7, Optional = true, Title = "S sounds",
                    Tunes = "Finds where your S really sits, with a lisp or without: the de-esser goes there, or stays out.",
                    Say = "Lines full of S and SH sounds. Say them the way you always do: a lisp or a soft S is fine, that's what it's listening for.",
                    Line = "Sister Suzy sat on the seesaw. She sells sea shells by the sea shore. Six slippery snakes slide past the sunny side of the stairs." },
                new Step { Kind = Kind.Pops, Seconds = 6, Optional = true, Title = "Pops and hard sounds",
                    Tunes = "P, B, T and K: how hard they hit your mic sets the low cut, and the gate opens fast enough for them.",
                    Say = "Lines full of P, B, T and K. Don't hold back on them: this is about how hard they hit your mic.",
                    Line = "Peter Piper picked a peck of peppers. Big bad bears bite back. Two tiny tigers tap the table. Kick the cat toy, go get the good gear." },
                new Step { Kind = Kind.Soft, Seconds = 6, Optional = true, Faint = true, Title = "Soft sounds and breaths",
                    Tunes = "F, TH and H, the ends of your words: noise removal and the gate let them through.",
                    Say = "The soft sounds noise removal takes for noise. Let the ends of the words fade out the way they do when you talk.",
                    Line = "Fifty fluffy feathers fall softly from the roof. Think through these three things with me. How high he hopes, huh. Both of them breathe with faith." },
                new Step { Kind = Kind.Far, Seconds = 7, Optional = true, Title = "From further away",
                    Tunes = "Across the room, and your voiceprint knows you from there too.",
                    Say = "Go where you sometimes talk from further away - your bed, the sofa, across the room - and talk the way you would there.",
                    Line = "Can you still hear me from over here? I'm lying on my bed, and the mic is all the way over on my desk." },
                new Step { Kind = Kind.Silly, Seconds = 6, Optional = true, Title = "Your silly voice",
                    Tunes = "Your voiceprint knows it's still you, so Only my voice never turns it down.",
                    Say = "Do you put on a voice for fun - a high-pitched Elmo voice when you mock someone, a deep movie-trailer voice, a character? Say anything in it, so it's never mistaken for someone else.",
                    Line = "" },
            };
        }

        /// <summary>Your voice's pitch (Hz): the median of the voiced 40 ms pieces (autocorrelation, 70-400 Hz); NaN if too little voice.</summary>
        public static double Pitch(float[] x16)
        {
            if (x16 == null || x16.Length < 16000) return double.NaN;
            const int Fs = 16000, W = 640, Hop = 320;
            var f0 = new List<double>(); var fr = new double[W];
            for (int s = 0; s + W <= x16.Length; s += Hop)
            {
                double mean = 0, e = 0;
                for (int i = 0; i < W; i++) { fr[i] = x16[s + i]; mean += fr[i]; }
                mean /= W;
                for (int i = 0; i < W; i++) { fr[i] -= mean; e += fr[i] * fr[i]; }
                if (e / W < 1e-6) continue;
                int lo = Fs / 400, hi = Fs / 70; double best = 0; int bk = 0;
                for (int k = lo; k <= hi; k++)
                {
                    double c = 0; for (int i = 0; i + k < W; i++) c += fr[i] * fr[i + k];
                    if (c > best) { best = c; bk = k; }
                }
                if (bk > 0 && best > 0.4 * e) f0.Add((double)Fs / bk);
            }
            if (f0.Count < 10) return double.NaN;
            f0.Sort();
            return f0[f0.Count / 2];
        }

        /// <summary>The level of your voice (16 kHz voiced pieces from the raw mic, RMS dBFS): noise removal keeps your voice at
        /// this level, so it's what the gate and compressor see of you.</summary>
        public static double CleanDb(float[] x16)
        {
            if (x16 == null || x16.Length < 1600) return double.NaN;
            double e = 0; foreach (var v in x16) e += v * (double)v;
            return 10 * Math.Log10(e / x16.Length + 1e-12);
        }

        /// <summary>How sharp your S sounds are: 4-8 kHz against 300 Hz-3 kHz in your voice (dB).</summary>
        public static double Sibilance(float[] x16)
        {
            if (x16 == null || x16.Length < 16000) return double.NaN;
            const int N = 512; var fft = new Fft(N); var re = new float[N]; var im = new float[N];
            double hi = 0, lo = 0;
            for (int s = 0; s + N <= x16.Length; s += N / 2)
            {
                for (int i = 0; i < N; i++) { re[i] = x16[s + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
                fft.Transform(re, im, false);
                for (int k = 1; k < N / 2; k++)
                {
                    double f = k * 16000.0 / N, p = re[k] * (double)re[k] + im[k] * (double)im[k];
                    if (f >= 4000 && f < 8000) hi += p; else if (f >= 300 && f < 3000) lo += p;
                }
            }
            return 10 * Math.Log10((hi + 1e-12) / (lo + 1e-12));
        }

        /// <summary>Mains hum in the room: 50 or 60 (Hz) when its harmonics stand 12 dB over their surroundings, else 0.</summary>
        public static int Hum(List<float> room48)
        {
            if (room48 == null || room48.Count < 48000) return 0;
            Func<double, double> power = f =>
            {
                double w = 2 * Math.PI * f / 48000, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                foreach (var x in room48) { double s0 = x + c * s1 - s2; s2 = s1; s1 = s0; }
                return s1 * s1 + s2 * s2 - c * s1 * s2;
            };
            foreach (int mains in new[] { 50, 60 })
            {
                double peak = 0, side = 0;
                for (int h = 1; h <= 3; h++) { peak += power(mains * h); side += (power(mains * h - 7) + power(mains * h + 7)) / 2; }
                if (10 * Math.Log10((peak + 1e-18) / (side + 1e-18)) > 12) return mains;
            }
            return 0;
        }

        /// <summary>What the setup changed, in words, and what it measured.</summary>
        public sealed class Result
        {
            public readonly List<string> Lines = new List<string>();
            public double PitchHz = double.NaN, RoomDb = double.NaN, NormalDb = double.NaN, FarDb = double.NaN, QuietDb = double.NaN, SibilanceDb = double.NaN;
            public int HumHz;
            public bool Clipping;
            public float[] Print; public List<float[]> AlsoYou = new List<float[]>();
            public double PrintSeconds, Consistency; public float P10 = float.NaN, P25 = float.NaN;
        }

        /// <summary>
        /// Tunes the settings from the steps (and builds the voiceprints with the model: about a second of CPU).
        /// Changes only what the recordings say something about; everything else stays as you set it.
        /// </summary>
        public static Result Apply(Step[] steps, AppSettings s, VoiceLock lk)
        {
            var r = new Result();
            Func<Kind, Step> get = k => steps.FirstOrDefault(x => x.Kind == k && x.Done && !x.Skipped);
            var normal = get(Kind.Normal); var far = get(Kind.Far); var loud = get(Kind.Loud); var quiet = get(Kind.Quiet); var silly = get(Kind.Silly);
            if (normal == null) { r.Lines.Add("Nothing changed: the first recording is needed."); return r; }
            List<float> room; lock (normal.RawDb) room = normal.QuietDb.OrderBy(d => d).ToList();
            r.RoomDb = room.Count > 10 ? room[room.Count / 2] : -70;
            r.NormalDb = normal.VoiceDb(r.RoomDb);

            // Your pitch: the low cut sits under it (a low voice keeps its chest, a high one loses the rumble), the de-esser
            // where your S sounds are (higher with a higher voice).
            r.PitchHz = Pitch(normal.Voice16);
            if (!double.IsNaN(r.PitchHz))
            {
                s.HighPassHz = r.PitchHz < 125 ? 80 : r.PitchHz < 170 ? 100 : 120;
                s.DeEssFreq = r.PitchHz < 160 ? 6000 : 7000;
                r.Lines.Add("Your voice sits around " + r.PitchHz.ToString("0") + " Hz (" + (r.PitchHz < 125 ? "low" : r.PitchHz < 170 ? "medium" : r.PitchHz < 230 ? "high" : "very high") + "): low cut at " + s.HighPassHz + " Hz, de-esser at " + (s.DeEssFreq / 1000.0).ToString("0") + " kHz.");
            }

            // The room: hum (de-hum on it), and how much noise there is next to your voice (how hard noise removal works).
            List<float> roomCopy; lock (normal.RawDb) roomCopy = new List<float>(normal.Room48);
            r.HumHz = Hum(roomCopy);
            s.DeHumHz = r.HumHz;
            if (r.HumHz > 0) r.Lines.Add("Your room hums at " + r.HumHz + " Hz (power lines, a cable or a fan): de-hum is on.");
            if (!double.IsNaN(r.NormalDb))
            {
                double snr = r.NormalDb - r.RoomDb;
                s.Strength = snr >= 40 ? 55 : snr >= 25 ? 70 : snr >= 15 ? 85 : 95;
                // 4.1: how hard it may work around your voice too - a noisy room needs more taken out between and under
                // your words, a quiet one keeps your voice at its fullest.
                s.NsVoiceProtect = snr >= 40 ? 70 : snr >= 25 ? 62 : snr >= 15 ? 52 : 45;
                s.NsSmoothing = snr >= 25 ? 50 : 60;
                r.Lines.Add("Your room is " + snr.ToString("0") + " dB under your voice (" + (snr >= 40 ? "very quiet" : snr >= 25 ? "quiet" : snr >= 15 ? "noisy" : "very noisy") + "): noise removal at " + s.Strength + "%, voice protection " + s.NsVoiceProtect + "%.");
            }

            // The gate between the room and your quiet voice; the compressor from how far your loud and quiet voice lie
            // apart (your voice's raw level - noise removal leaves it there).
            double cleanNormal = CleanDb(normal.Voice16), cleanQuiet = quiet != null ? CleanDb(quiet.Voice16) : double.NaN, cleanLoud = loud != null ? CleanDb(loud.Voice16) : double.NaN;
            if (!double.IsNaN(cleanQuiet) || !double.IsNaN(cleanNormal))
            {
                double low = !double.IsNaN(cleanQuiet) ? cleanQuiet : cleanNormal - 12;
                s.GateThreshold = (int)Math.Max(-70, Math.Min(-35, Math.Round(low - 15)));
                s.Gate = !double.IsNaN(r.NormalDb) && r.NormalDb - r.RoomDb < 25;   // a quiet room needs none: noise removal leaves silence
                s.GateExpander = true;
                r.Lines.Add(s.Gate ? "Noise gate on, at " + s.GateThreshold + " dB - 15 dB under your quietest voice, so it never cuts you." : "Noise gate off: your room is quiet enough (its threshold is at " + s.GateThreshold + " dB if you turn it on).");
            }
            if (!double.IsNaN(cleanNormal))
            {
                s.Compressor = true;
                s.CompThreshold = (int)Math.Max(-40, Math.Min(-12, Math.Round(cleanNormal - 2)));
                double range = !double.IsNaN(cleanLoud) && !double.IsNaN(cleanQuiet) ? cleanLoud - cleanQuiet : double.NaN;
                s.CompRatio = double.IsNaN(range) ? 25 : range > 24 ? 35 : range > 14 ? 25 : 20;
                r.Lines.Add("Compressor from " + s.CompThreshold + " dB at " + (s.CompRatio / 10.0).ToString("0.0") + ":1" + (double.IsNaN(range) ? "." : " - your loud and quiet voice are " + range.ToString("0") + " dB apart, it evens them out."));
            }

            // Sharp S sounds: the de-esser.
            var sib = new List<float>(); foreach (var st in new[] { normal, loud }) if (st != null && st.Voice16 != null) sib.AddRange(st.Voice16);
            r.SibilanceDb = Sibilance(sib.ToArray());
            if (!double.IsNaN(r.SibilanceDb))
            {
                s.DeEsser = r.SibilanceDb > -12;
                s.DeEssAmount = r.SibilanceDb > -8 ? 8 : 6;
                r.Lines.Add(s.DeEsser ? "Your S sounds are sharp (" + r.SibilanceDb.ToString("0") + " dB): de-esser on, " + s.DeEssAmount + " dB." : "Your S sounds are soft (" + r.SibilanceDb.ToString("0") + " dB): de-esser off.");
            }

            // Quiet: how far auto level may lift you (to its target from your quiet voice, a little headroom).
            if (quiet != null) r.QuietDb = quiet.VoiceDb(r.RoomDb);
            if (!double.IsNaN(r.QuietDb))
            {
                int need = (int)Math.Ceiling(s.AutoLevelTarget - r.QuietDb + 3);
                s.AutoLevelMaxBoost = Math.Max(20, Math.Min(40, need));
                r.Lines.Add("Your quiet voice reaches the mic at " + r.QuietDb.ToString("0") + " dB: auto level may lift it up to " + s.AutoLevelMaxBoost + " dB.");
            }

            // Far: a lot quieter from there -> Across the room.
            if (far != null) r.FarDb = far.VoiceDb(r.RoomDb);
            if (!double.IsNaN(r.FarDb) && !double.IsNaN(r.NormalDb))
            {
                double drop = r.NormalDb - r.FarDb;
                s.FarVoice = drop >= 8;
                r.Lines.Add(s.FarVoice ? "From further away you're " + drop.ToString("0") + " dB quieter: Across the room is on (it lifts a far voice up to 40 dB)."
                                       : "From further away you're only " + Math.Max(0, drop).ToString("0") + " dB quieter: your mic picks you up fine, Across the room stays off.");
            }

            // Loud: clipping can't be repaired after the mic.
            if (loud != null && loud.PeakDb > -0.5f)
            {
                r.Clipping = true;
                r.Lines.Add("When you're loud your mic clips (too loud before StreamLite gets it): turn its gain down a little on the mic or in " + Os.InputLevelPlace + ".");
            }
            else if (loud != null) r.Lines.Add("Loud is fine: no clipping (" + loud.PeakDb.ToString("0.0") + " dBFS at the loudest).");

            // Your voiceprint: normal, loud and quiet together; far and silly as extra versions of you. Made from the raw
            // recordings with noise removed the way Only my voice will hear you (at the strength set above).
            var clean = new Dictionary<Step, float[]>();
            foreach (var st in new[] { normal, far, loud, quiet, silly }) if (st != null) { List<float> v48; lock (st.RawDb) v48 = new List<float>(st.Voice48); clean[st] = Cleaned16(v48, s); }
            var main = new List<float>();
            foreach (var st in new[] { normal, loud, quiet }) if (st != null && clean[st].Length > 0) main.AddRange(clean[st]);
            if (lk != null && main.Count >= VoiceLock.Fs16 * 4)
            {
                var arr = main.ToArray(); double cons;
                r.Print = lk.PrintOf(arr, arr.Length, out cons);
                r.Consistency = cons; r.PrintSeconds = arr.Length / (double)VoiceLock.Fs16;
                var nv = clean[normal];
                lk.Calibrate(nv.Length >= VoiceLock.Fs16 * 6 ? nv : arr, nv.Length >= VoiceLock.Fs16 * 6 ? nv.Length : arr.Length, out r.P10, out r.P25);
                foreach (var st in new[] { far, silly })
                    if (st != null && clean[st].Length >= VoiceLock.Fs16 * 2)
                    {
                        double c2; var p = lk.PrintOf(clean[st], clean[st].Length, out c2);
                        if (p != null) r.AlsoYou.Add(p);
                    }
                if (r.Print != null)
                    r.Lines.Add("Your voiceprint: " + r.PrintSeconds.ToString("0") + " s of your voice" + (r.AlsoYou.Count > 0 ? ", plus your " + string.Join(" and ", new[] { far != null && far.Voice16 != null ? "far" : null, silly != null && silly.Voice16 != null ? "silly" : null }.Where(x => x != null)) + " voice" : "") + " - Only my voice knows it's you.");
            }
            return r;
        }

        // ------------------------------------------------------------------ the optional sections (4.4)

        /// <summary>
        /// One optional section, now or long after the rest: tunes only what its sound says something about, and only in the
        /// direction that keeps more of you (a gate only goes lower, voice protection only goes up). normalDb = your normal
        /// voice's level, NaN when this run hasn't heard it.
        /// </summary>
        public static Result TuneOptional(Step st, AppSettings s, VoiceLock lk, double normalDb)
        {
            var r = new Result();
            List<float> v48; lock (st.RawDb) v48 = new List<float>(st.Voice48);
            if (v48.Count < 48000 && st.Kind != Kind.Silly) { r.Lines.Add("Too little was heard to tune anything: nothing changed."); return r; }
            switch (st.Kind)
            {
                case Kind.Hums: TuneHums(st, v48, s, r); break;
                case Kind.Esses: TuneEsses(v48, s, r); break;
                case Kind.Pops: TunePops(v48, s, r); break;
                case Kind.Soft: TuneSoft(v48, s, r); break;
                case Kind.Far:
                {
                    double room = st.OwnRoomDb(); if (double.IsNaN(room)) room = -70;
                    r.FarDb = st.VoiceDb(room);
                    double normal = !double.IsNaN(normalDb) ? normalDb : s.CompThreshold + 2;
                    if (!double.IsNaN(r.FarDb))
                    {
                        double drop = normal - r.FarDb;
                        s.FarVoice = drop >= 8;
                        r.Lines.Add(s.FarVoice ? "From further away you're about " + drop.ToString("0") + " dB quieter: Across the room is on." : "From further away you're only about " + Math.Max(0, drop).ToString("0") + " dB quieter: your mic picks you up fine, Across the room stays off.");
                    }
                    Also(v48, s, lk, r, "far");
                    break;
                }
                case Kind.Silly: Also(v48, s, lk, r, "silly"); break;
            }
            if (st.PeakDb > -0.5f) { r.Clipping = true; r.Lines.Add("Your mic clipped during this one (too loud before StreamLite gets it): turn its gain down a little on the mic or in " + Os.InputLevelPlace + "."); }
            return r;
        }

        static void Also(List<float> v48, AppSettings s, VoiceLock lk, Result r, string which)
        {
            var clean = Cleaned16(v48, s);
            if (lk == null || clean.Length < VoiceLock.Fs16 * 2) return;
            double c; var p = lk.PrintOf(clean, clean.Length, out c);
            if (p == null) return;
            r.AlsoYou.Add(p);
            r.Lines.Add("Your voiceprint knows your " + which + " voice too.");
        }

        /// <summary>Each 10 ms piece's level (dB), sorted.</summary>
        static List<double> PieceLevels(List<float> x)
        {
            var l = new List<double>(x.Count / 480 + 1);
            for (int f = 0; f + 480 <= x.Count; f += 480)
            {
                double e = 0; for (int i = 0; i < 480; i++) e += x[f + i] * (double)x[f + i];
                l.Add(10 * Math.Log10(e / 480 + 1e-12));
            }
            l.Sort();
            return l;
        }

        /// <summary>The gate stays under the quietest of this sound, and stays open long enough for it to fade.</summary>
        static void GateUnder(List<float> v48, AppSettings s, Result r, int margin, int hold, int release, string what)
        {
            var lv = PieceLevels(v48);
            if (lv.Count < 20) return;
            int thr = (int)Math.Max(-70, Math.Min(-35, Math.Round(lv[lv.Count / 5] - margin)));
            bool moved = s.GateThreshold > thr;
            if (moved) s.GateThreshold = thr;
            s.GateHold = Math.Max(s.GateHold, hold); s.GateRelease = Math.Max(s.GateRelease, release);
            s.GateExpander = true;
            r.Lines.Add(moved ? "The noise gate moved down to " + thr + " dB, under the quietest of your " + what + ", and it closes more slowly so their ends stay." : "The noise gate was already under your " + what + " (" + s.GateThreshold + " dB); it closes slowly enough for their ends to stay.");
        }

        /// <summary>Noise removal lets this sound through (at most 3 dB lost): voice protection up first, then less strength.</summary>
        static void KeepThrough(List<float> v48, AppSettings s, Result r, string what)
        {
            double first = LossDb(v48, s), loss = first;
            int lowest = Math.Max(40, s.Strength - 20);   // a noisy room still needs its noise removal
            for (int i = 0; i < 12 && loss > 3; i++)
            {
                if (s.NsVoiceProtect < 90) s.NsVoiceProtect = Math.Min(90, s.NsVoiceProtect + 12);
                else if (s.Strength > lowest) s.Strength = Math.Max(lowest, s.Strength - 10);
                else break;
                loss = LossDb(v48, s);
            }
            if (first <= 3) r.Lines.Add("Noise removal already leaves your " + what + " alone (" + first.ToString("0.0") + " dB lost).");
            else r.Lines.Add("Noise removal was taking " + first.ToString("0") + " dB off your " + what + ": voice protection " + s.NsVoiceProtect + "%, noise removal " + s.Strength + "%, now " + loss.ToString("0.0") + " dB.");
        }

        static void TuneHums(Step st, List<float> v48, AppSettings s, Result r)
        {
            GateUnder(v48, s, r, 12, 300, 250, "hums and thinking sounds");
            KeepThrough(v48, s, r, "hums and thinking sounds");
            // A long held "ahh" pushing into the limiter is what distorts: the compressor holds it down a little earlier.
            var lv = PieceLevels(v48);
            double top = lv[(int)(lv.Count * 0.95)];
            if (top > s.CompThreshold + 12 && s.CompRatio < 30) { s.CompRatio = 30; r.Lines.Add("Your held sounds come in loud: compressor at 3.0:1, so they don't push into the limiter and distort."); }
        }

        /// <summary>Where your S sits and how sharp it is: frames where the top of the voice outweighs its body.</summary>
        internal static void MeasureEsses(List<float> v48, out double peakHz, out double sharpDb, out int frames)
        {
            const int N = 1024; var fft = new Fft(N); var re = new float[N]; var im = new float[N];
            var sib = new double[N / 2 + 1];
            double body = 0; int bodyFrames = 0; frames = 0; peakHz = double.NaN; sharpDb = double.NaN;
            for (int p = 0; p + N <= v48.Count; p += N / 2)
            {
                for (int i = 0; i < N; i++) { re[i] = v48[p + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
                fft.Transform(re, im, false);
                double hi = 0, lo = 0;
                for (int k = 1; k <= N / 2; k++)
                {
                    double f = k * 48000.0 / N, e = re[k] * (double)re[k] + im[k] * (double)im[k];
                    if (f >= 3000 && f < 12000) hi += e; else if (f >= 200 && f < 3000) lo += e;
                }
                if (hi > lo * 0.7) { frames++; for (int k = 0; k <= N / 2; k++) sib[k] += re[k] * (double)re[k] + im[k] * (double)im[k]; }
                else if (lo > 0) { body += lo; bodyFrames++; }
            }
            if (frames < 8 || bodyFrames < 8) return;
            double best = 0; int at = 0, reach = (int)(350.0 * N / 48000) + 1, k0 = (int)(3000.0 * N / 48000), k1 = (int)(11000.0 * N / 48000);
            double top = 0; for (int k = k0; k <= k1; k++) top += sib[k];
            for (int k = k0; k <= k1; k++)
            {
                double sum = 0;
                for (int q = Math.Max(0, k - reach); q <= Math.Min(N / 2, k + reach); q++) sum += sib[q];
                if (sum > best) { best = sum; at = k; }
            }
            peakHz = at * 48000.0 / N;
            sharpDb = 10 * Math.Log10((top / frames + 1e-12) / (body / bodyFrames + 1e-12));
        }

        static void TuneEsses(List<float> v48, AppSettings s, Result r)
        {
            double hz, sharp; int frames; MeasureEsses(v48, out hz, out sharp, out frames);
            if (double.IsNaN(hz)) { r.Lines.Add("It heard too few S sounds to tell: the de-esser stays as it was."); return; }
            r.SibilanceDb = sharp;
            s.DeEssFreq = (int)Math.Max(3000, Math.Min(10000, Math.Round(hz / 100) * 100));
            s.DeEsser = sharp > -6;
            if (s.DeEsser) s.DeEssAmount = sharp > 2 ? 8 : sharp > -2 ? 6 : 4;
            string where = "Your S sits around " + (hz / 1000).ToString("0.0") + " kHz" + (hz < 4500 ? ", lower than most" : "");
            r.Lines.Add(s.DeEsser ? where + " and it's sharp (" + sharp.ToString("0") + " dB against your voice): the de-esser works right there, " + s.DeEssAmount + " dB." : where + ", and it's soft (" + sharp.ToString("0") + " dB against your voice): the de-esser stays off, so nothing dulls it further.");
        }

        /// <summary>How hard your P and B hit the mic: the loudest bass bursts against your voice (dB).</summary>
        internal static double PopDb(List<float> v48)
        {
            if (v48.Count < 4800) return double.NaN;
            double a = 1 - Math.Exp(-2 * Math.PI * 120 / 48000), lp = 0, all = 0;
            var bursts = new List<double>(v48.Count / 960 + 1);
            double e = 0; int n = 0;
            for (int i = 0; i < v48.Count; i++)
            {
                double x = v48[i]; lp += a * (x - lp); e += lp * lp; all += x * x;
                if (++n == 960) { bursts.Add(10 * Math.Log10(e / 960 + 1e-12)); e = 0; n = 0; }
            }
            bursts.Sort();
            // Bursts stand out from the bass the voice always has (a low voice's own pitch is down there too): no burst, no pop.
            double top = bursts[(int)(bursts.Count * 0.98)], usual = bursts[bursts.Count / 2];
            return top - usual < 8 ? -30 : top - 10 * Math.Log10(all / v48.Count + 1e-12);
        }

        static void TunePops(List<float> v48, AppSettings s, Result r)
        {
            double pop = PopDb(v48);
            if (double.IsNaN(pop)) return;
            int was = s.HighPassHz;
            if (pop > 0) s.HighPassHz = Math.Max(s.HighPassHz, 120);
            else if (pop > -6) s.HighPassHz = Math.Max(s.HighPassHz, 100);
            r.Lines.Add((pop > -6 ? "Your P and B sounds thump the mic (" + pop.ToString("0") + " dB against your voice)" : "Your P and B sounds don't thump the mic much (" + pop.ToString("0") + " dB against your voice)") +
                (s.HighPassHz != was ? ": low cut up to " + s.HighPassHz + " Hz." + (pop > 0 ? " Turning the mic a little to the side helps more than any filter." : "") : ": the low cut stays at " + s.HighPassHz + " Hz."));
            s.GateAttack = Math.Min(s.GateAttack, 2);
            r.Lines.Add("The noise gate opens within " + s.GateAttack + " ms, in time for the T and K at the start of your words.");
        }

        static void TuneSoft(List<float> v48, AppSettings s, Result r)
        {
            GateUnder(v48, s, r, 10, 250, 200, "soft sounds");
            KeepThrough(v48, s, r, "soft sounds");
        }
    }
}
