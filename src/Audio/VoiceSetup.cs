using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// "Tune StreamLite to your voice" (3.5.2): five short recordings - right at your mic, from further away, loud, quiet,
    /// and the silly voice you put on for fun - and what they tune: the low cut and de-esser from your pitch, how far auto
    /// level may lift you, Across the room, clipping, and your voiceprint for Only my voice (your far and silly voices count
    /// as you too). The recordings themselves are only kept as levels and voiceprints.
    /// Everything is measured on the RAW mic (3.5.6): what the microphone itself picks up, before any StreamLite setting -
    /// noise removal, gate, EQ, mute or push to talk, even with the virtual mic switched off - so the settings it makes
    /// don't depend on what was set before.
    /// </summary>
    internal sealed class VoiceSetup
    {
        public enum Kind { Normal, Far, Loud, Quiet, Silly }

        public sealed class Step
        {
            public Kind Kind; public string Title, Say, Line; public bool Optional; public double Seconds;
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
                        voiced = db > floorDb + 10f || (prob > 0.6f && db > floorDb + 2f);
                    }
                    lock (RawDb)
                    {
                        if (silence) QuietDb.Add(db); else { RawDb.Add(db); PeakDb = Math.Max(PeakDb, (float)(20 * Math.Log10(peak + 1e-9))); }
                        if (voiced) { for (int j = 0; j < piece16Fill; j++) voice16.Add(piece16[j]); Voice48.AddRange(piece48); }
                    }
                    acc = 0; fill = 0; peak = 0; piece16Fill = 0;
                }
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

        /// <summary>
        /// A noise-removed copy of raw voice (48 kHz in, 16 kHz out) for the voiceprint: Only my voice listens after noise
        /// removal, so your print is made the way it will hear you - from the raw recording, at the strength just tuned.
        /// </summary>
        public static float[] Cleaned16(List<float> raw48, AppSettings s)
        {
            if (raw48 == null || raw48.Count < 480) return new float[0];
            var nn = new Rnnoise();
            double st = s.Strength / 100.0;
            double att = st <= 0.5 ? 10 + 40 * st : 30 + 40 * (st - 0.5);
            float sharpen = st <= 0.5 ? 0f : (float)(3 * (st - 0.5));
            float minGain = (float)Math.Pow(10, -att / 20);
            float voiceFloor = (float)Math.Pow(minGain, 1 - 0.7 * (s.NsVoiceProtect / 100.0));
            float decay = (float)(0.3 + 0.55 * (s.NsSmoothing / 100.0));
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
                new Step { Kind = Kind.Normal, Seconds = 10, Title = "Right at your mic, your normal voice",
                    Say = "Sit where you usually do and talk at your normal volume. First stay quiet for 2 seconds (StreamLite listens to the room), then read the text aloud.",
                    Line = "Hey, can you hear me okay? I'm setting up my mic so it knows how I sound. Push left, there's one behind the boxes. I'll cover you from the stairs, so take it slow." },
                new Step { Kind = Kind.Far, Seconds = 7, Optional = true, Title = "From further away",
                    Say = "Go where you sometimes talk from further away - your bed, the sofa, across the room - and talk the way you would there. Skip this if you always sit at your mic.",
                    Line = "Can you still hear me from over here? I'm lying on my bed, and the mic is all the way over on my desk." },
                new Step { Kind = Kind.Loud, Seconds = 5, Title = "Loud, like a callout",
                    Say = "Back at your mic: as loud as you get in a game (no need to hurt your ears).",
                    Line = "He's on the stairs! Behind you, behind you! Watch out, he's coming around the corner right now!" },
                new Step { Kind = Kind.Quiet, Seconds = 6, Title = "Quiet, like late at night",
                    Say = "As quietly as you talk when someone's asleep next door.",
                    Line = "Okay, one more game and then I'm going to bed. I'm so tired, so let's keep it down for a bit." },
                new Step { Kind = Kind.Silly, Seconds = 6, Optional = true, Title = "Your silly voice",
                    Say = "Do you put on a voice for fun - a high-pitched Elmo voice when you mock someone, a deep movie-trailer voice, a character? Say anything in it, so it's never mistaken for someone else. Skip this if you don't.",
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
    }
}
