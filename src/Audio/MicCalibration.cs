using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// Multi-mic calibration: everyone is quiet for a moment, then each person talks in turn. From each mic's level in
    /// every packet (MicMixer.PowerTap) it measures each person's voice level, the background noise, and how much of
    /// each person every other mic hears (the bleed). From that it suggests per-mic volumes that even out the voices
    /// and the bleed table for the smart auto mix, and estimates how much doubling is left before / after.
    /// </summary>
    internal sealed class MicCalibration
    {
        public readonly int Channels;
        readonly List<float[]>[] phases;   // 0 = quiet, 1..Channels = person on mic k talking
        readonly object gate = new object();
        public volatile int Phase = -1;     // where incoming packets go (-1 = nowhere)

        public MicCalibration(int channels)
        {
            Channels = channels;
            phases = new List<float[]>[channels + 1];
            for (int i = 0; i <= channels; i++) phases[i] = new List<float[]>();
        }

        /// <summary>One packet's levels (power per mic, main mic first) - MicMixer.PowerTap.</summary>
        public void Add(float[] powers)
        {
            int p = Phase;
            if (p < 0 || p > Channels || powers.Length != Channels) return;
            lock (gate) phases[p].Add(powers);
        }

        public int Count(int phase) { lock (gate) return phases[phase].Count; }

        public sealed class Result
        {
            public double[] VoiceDb, NoiseDb, GainDb;
            /// <summary>[from person k, to mic j]: how much quieter mic j hears person k than k's own mic does (dB).</summary>
            public double[,] BleedDb;
            /// <summary>[from, to] power ratios for MicMixer.Bleed.</summary>
            public float[,] BleedRatio;
            /// <summary>The worst doubled voice left (dB under the voice itself), with plain auto mix and calibrated.</summary>
            public double DoublingBeforeDb, DoublingAfterDb;
            /// <summary>Two people talking at once at equal level: the share each one keeps (calibrated mix).</summary>
            public double TogetherShare;
            public List<string> Problems = new List<string>();
        }

        static double Db(double p) { return 10 * Math.Log10(Math.Max(p, 1e-12)); }

        public Result Analyze()
        {
            int n = Channels;
            List<float[]>[] ph;
            lock (gate) ph = phases.Select(l => l.ToList()).ToArray();
            var r = new Result { VoiceDb = new double[n], NoiseDb = new double[n], GainDb = new double[n], BleedDb = new double[n, n], BleedRatio = new float[n, n] };
            // Background noise per mic: the quiet part (or, without it, the quietest 20% of everything).
            var noise = new double[n];
            for (int c = 0; c < n; c++)
            {
                var vals = (ph[0].Count >= 20 ? ph[0] : ph.SelectMany(l => l).ToList()).Select(f => (double)f[c]).OrderBy(v => v).ToList();
                noise[c] = vals.Count == 0 ? 1e-10 : vals[ph[0].Count >= 20 ? vals.Count / 2 : vals.Count / 5];
                r.NoiseDb[c] = Db(noise[c]);
            }
            var voice = new double[n];
            for (int k = 0; k < n; k++)
            {
                var frames = ph[k + 1];
                if (frames.Count < 50) { r.Problems.Add("Microphone " + (k + 1) + " wasn't recorded long enough."); voice[k] = noise[k]; r.VoiceDb[k] = Db(noise[k]); continue; }
                double peak = frames.Max(f => (double)f[k]);
                // Packets where person k is clearly talking on their own mic: 10 dB over its noise, within 30 dB of their loudest.
                var talk = frames.Where(f => f[k] > 10 * noise[k] && f[k] > peak * 1e-3).ToList();
                if (talk.Count < frames.Count / 6)
                {
                    r.Problems.Add("Microphone " + (k + 1) + " hardly heard its person - was the right person talking, and is that mic switched on?");
                    if (talk.Count == 0) { voice[k] = noise[k]; r.VoiceDb[k] = Db(noise[k]); continue; }
                }
                voice[k] = talk.Average(f => f[k] - noise[k]);
                r.VoiceDb[k] = Db(voice[k]);
                for (int j = 0; j < n; j++)
                {
                    if (j == k) continue;
                    double heard = talk.Average(f => Math.Max(0, f[j] - noise[j]));
                    double ratio = Math.Min(1.0, heard / Math.Max(voice[k], 1e-12));
                    r.BleedRatio[k, j] = (float)ratio;
                    r.BleedDb[k, j] = Db(ratio);
                    if (ratio > 0.5) r.Problems.Add("Microphone " + (j + 1) + " hears person " + (k + 1) + " almost as loud as their own mic (" + Db(ratio).ToString("0") + " dB). Point each mic at its person and keep it close to their mouth.");
                }
            }
            // Volumes: everyone at the average voice level (within +-12 dB).
            var ok = Enumerable.Range(0, n).Where(c => voice[c] > noise[c] * 3).ToList();
            double target = ok.Count > 0 ? ok.Average(c => r.VoiceDb[c]) : 0;
            for (int c = 0; c < n; c++) r.GainDb[c] = ok.Contains(c) ? Math.Round(Math.Max(-12, Math.Min(12, target - r.VoiceDb[c]))) : 0;
            r.DoublingBeforeDb = Doubling(r, false);
            r.DoublingAfterDb = Doubling(r, true);
            r.TogetherShare = Together(r);
            return r;
        }

        /// <summary>Shares the mixer would give with these levels (same rule as MicMixer.Mix).</summary>
        static double[] Shares(Result r, double[] env, bool calibrated)
        {
            int n = env.Length;
            var own = new double[n];
            double sum = 0;
            for (int c = 0; c < n; c++)
            {
                double o = env[c];
                if (calibrated)
                    for (int j = 0; j < n; j++)
                        if (j != c) { double t = Math.Pow(10, (r.GainDb[c] - r.GainDb[j]) / 20); o -= r.BleedRatio[j, c] * t * t * env[j]; }
                own[c] = calibrated ? Math.Max(o, 0.02 * env[c]) : env[c];
                sum += own[c];
            }
            return own.Select(o => o / sum).ToArray();
        }

        /// <summary>Worst case over who talks: the other mics' copy of the voice against the voice itself (dB).</summary>
        static double Doubling(Result r, bool calibrated)
        {
            int n = r.GainDb.Length; double worst = -120;
            for (int k = 0; k < n; k++)
            {
                var env = new double[n];
                for (int c = 0; c < n; c++) env[c] = Math.Pow(10, r.GainDb[c] / 10) * (c == k ? 1 : r.BleedRatio[k, c]) * Math.Pow(10, r.VoiceDb[k] / 10);
                var s = Shares(r, env, calibrated);
                double direct = s[k] * Math.Pow(10, r.GainDb[k] / 20), copy = 0;
                for (int j = 0; j < n; j++) if (j != k) copy += Math.Pow(s[j] * Math.Pow(10, r.GainDb[j] / 20), 2) * r.BleedRatio[k, j];
                worst = Math.Max(worst, Db(copy / Math.Max(direct * direct, 1e-12)));
            }
            return worst;
        }

        /// <summary>Two people (1 and 2) talking at once at the same level: the smaller share either keeps.</summary>
        static double Together(Result r)
        {
            int n = r.GainDb.Length; if (n < 2) return 1;
            var env = new double[n];
            // Levels after each mic's volume: both voices equally loud on their own mics, plus what leaks across.
            for (int c = 0; c < n; c++)
            {
                double e = c < 2 ? 1 : 0;
                for (int k = 0; k < 2; k++) if (k != c) { double t = Math.Pow(10, (r.GainDb[c] - r.GainDb[k]) / 20); e += r.BleedRatio[k, c] * t * t; }
                env[c] = e + 1e-9;
            }
            var s = Shares(r, env, true);
            return Math.Min(s[0], s[1]);
        }
    }
}
