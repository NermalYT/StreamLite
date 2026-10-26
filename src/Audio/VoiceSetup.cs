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
            /// <summary>Your voice after noise removal, 16 kHz (from VoiceLock's learning).</summary>
            public float[] Voice16;
            public bool Done, Skipped;
            float acc; int fill; float peak;

            /// <summary>A block of the raw mic (48 kHz); silence = the room before you talk.</summary>
            public void FeedRaw(float[] b, int n, bool silence)
            {
                for (int i = 0; i < n; i++)
                {
                    float v = b[i]; acc += v * v; float a = Math.Abs(v); if (a > peak) peak = a;
                    if (++fill < 480) continue;
                    float db = (float)(10 * Math.Log10(acc / 480 + 1e-12));
                    lock (RawDb) { if (silence) QuietDb.Add(db); else { RawDb.Add(db); PeakDb = Math.Max(PeakDb, (float)(20 * Math.Log10(peak + 1e-9))); } }
                    acc = 0; fill = 0; peak = 0;
                }
            }

            /// <summary>Your voice's level: the louder 20% of the 10 ms pieces above the room (dBFS RMS), NaN if none.</summary>
            public double VoiceDb(double roomDb)
            {
                List<float> v; lock (RawDb) v = RawDb.Where(d => d > roomDb + 10).OrderBy(d => d).ToList();
                return v.Count < 20 ? double.NaN : v[(int)(v.Count * 0.8)];
            }
        }

        public static Step[] Steps()
        {
            return new[]
            {
                new Step { Kind = Kind.Normal, Seconds = 10, Title = "Right at your mic, your normal voice",
                    Say = "Sit where you usually do and talk at your normal volume. First stay quiet for 2 seconds (StreamLite listens to the room), then read this aloud:",
                    Line = "\"Hey, can you hear me okay? I'm setting up my mic so it knows how I sound. Push left, there's one behind the boxes - I'll cover you from the stairs.\"" },
                new Step { Kind = Kind.Far, Seconds = 7, Optional = true, Title = "From further away",
                    Say = "Go where you sometimes talk from further away - your bed, the sofa, across the room - and talk the way you would there. Skip this if you always sit at your mic.",
                    Line = "\"Can you still hear me from over here? I'm lying on my bed, the mic is on my desk.\"" },
                new Step { Kind = Kind.Loud, Seconds = 5, Title = "Loud, like a callout",
                    Say = "Back at your mic: as loud as you get in a game (no need to hurt your ears).",
                    Line = "\"He's on the stairs! Behind you, behind you! Watch out!\"" },
                new Step { Kind = Kind.Quiet, Seconds = 6, Title = "Quiet, like late at night",
                    Say = "As quietly as you talk when someone's asleep next door.",
                    Line = "\"Okay, one more game and then I'm going to bed, I'm so tired.\"" },
                new Step { Kind = Kind.Silly, Seconds = 6, Optional = true, Title = "Your silly voice",
                    Say = "Do you put on a voice for fun - a high-pitched Elmo voice when you mock someone, a deep movie-trailer voice, a character? Do it now, so it's never mistaken for someone else. Skip this if you don't.",
                    Line = "Say anything in that voice." },
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

        /// <summary>What the setup changed, in words, and what it measured.</summary>
        public sealed class Result
        {
            public readonly List<string> Lines = new List<string>();
            public double PitchHz = double.NaN, RoomDb = double.NaN, NormalDb = double.NaN, FarDb = double.NaN, QuietDb = double.NaN;
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
                r.Lines.Add("When you're loud your mic clips (too loud before StreamLite gets it): turn its gain down a little on the mic or in Windows Sound settings > Recording.");
            }
            else if (loud != null) r.Lines.Add("Loud is fine: no clipping (" + loud.PeakDb.ToString("0.0") + " dBFS at the loudest).");

            // Your voiceprint: normal, loud and quiet together; far and silly as extra versions of you.
            var main = new List<float>();
            foreach (var st in new[] { normal, loud, quiet }) if (st != null && st.Voice16 != null) main.AddRange(st.Voice16);
            if (lk != null && main.Count >= VoiceLock.Fs16 * 4)
            {
                var arr = main.ToArray(); double cons;
                r.Print = lk.PrintOf(arr, arr.Length, out cons);
                r.Consistency = cons; r.PrintSeconds = arr.Length / (double)VoiceLock.Fs16;
                var nv = normal.Voice16 ?? new float[0];
                lk.Calibrate(nv.Length >= VoiceLock.Fs16 * 6 ? nv : arr, nv.Length >= VoiceLock.Fs16 * 6 ? nv.Length : arr.Length, out r.P10, out r.P25);
                foreach (var st in new[] { far, silly })
                    if (st != null && st.Voice16 != null && st.Voice16.Length >= VoiceLock.Fs16 * 2)
                    {
                        double c2; var p = lk.PrintOf(st.Voice16, st.Voice16.Length, out c2);
                        if (p != null) r.AlsoYou.Add(p);
                    }
                if (r.Print != null)
                    r.Lines.Add("Your voiceprint: " + r.PrintSeconds.ToString("0") + " s of your voice" + (r.AlsoYou.Count > 0 ? ", plus your " + string.Join(" and ", new[] { far != null && far.Voice16 != null ? "far" : null, silly != null && silly.Voice16 != null ? "silly" : null }.Where(x => x != null)) + " voice" : "") + " - Only my voice knows it's you.");
            }
            return r;
        }
    }
}
