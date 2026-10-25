using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// Output sync check: clicks played on one output at a time are timed with the microphone (the headphones held
    /// against it, or a speaker it can hear). The time from a click going into that output's buffer to the mic hearing
    /// it is that output's delay; the mic's own delay is the same for every output, so the differences are exact.
    /// </summary>
    internal sealed class OutputSyncMeter
    {
        const int Fs = 48000;
        readonly List<float[]> packets = new List<float[]>();
        readonly List<long> ends = new List<long>();     // Stopwatch ticks when each packet reached StreamLite
        readonly object gate = new object();

        /// <summary>AudioEngine.RawTap.</summary>
        public void Feed(float[] buf, int n, long ticks)
        {
            var copy = new float[n]; Array.Copy(buf, copy, n);
            lock (gate) { packets.Add(copy); ends.Add(ticks); }
        }

        public void Clear() { lock (gate) { packets.Clear(); ends.Clear(); } }

        public sealed class Result
        {
            /// <summary>Median delay from click to microphone, ms (null = not heard).</summary>
            public double? DelayMs;
            public int Heard, Played;
            /// <summary>How far the single clicks scattered around the median (ms): small = a trustworthy number.</summary>
            public double SpreadMs;
            public string Problem;
        }

        /// <summary>Finds each click (sent at clickTicks) in what the mic recorded since Clear.</summary>
        public Result Measure(IList<long> clickTicks)
        {
            float[][] ps; long[] es;
            lock (gate) { ps = packets.ToArray(); es = ends.ToArray(); }
            var res = new Result { Played = clickTicks.Count };
            int total = ps.Sum(p => p.Length);
            if (total < Fs / 2) { res.Problem = "The microphone didn't deliver any sound - is the virtual mic on?"; return res; }
            // One timeline: every sample's time, from its packet's arrival.
            var x = new float[total]; var t = new double[total];
            double tickMs = 1000.0 / Stopwatch.Frequency;
            int o = 0;
            for (int k = 0; k < ps.Length; k++)
            {
                int n = ps[k].Length;
                for (int i = 0; i < n; i++) { x[o + i] = ps[k][i]; t[o + i] = es[k] * tickMs - (n - 1 - i) * 1000.0 / Fs; }
                o += n;
            }
            // Clicks are short and bright: the difference of neighbouring samples (a high-pass), then a 0.5 ms peak envelope.
            var env = new float[total];
            for (int i = 1; i < total; i++) env[i] = Math.Abs(x[i] - x[i - 1]);
            const int W = 24;
            var pk = new float[total];
            for (int i = 0; i < total; i++) { float m = 0; for (int j = Math.Max(0, i - W); j <= i; j++) if (env[j] > m) m = env[j]; pk[i] = m; }
            // Background: the quiet before the first click.
            double first = clickTicks.Count > 0 ? clickTicks[0] * tickMs : t[total - 1];
            var before = Enumerable.Range(0, total).Where(i => t[i] < first).Select(i => pk[i]).OrderBy(v => v).ToList();
            float floor = before.Count > 100 ? before[before.Count / 2] : pk.OrderBy(v => v).ElementAt(total / 4);
            var delays = new List<double>();
            foreach (var c in clickTicks)
            {
                double c0 = c * tickMs;
                int a = Array.FindIndex(t, v => v >= c0);
                if (a < 0) continue;
                int b = Array.FindIndex(t, v => v >= c0 + 700); if (b < 0) b = total;
                float max = 0; for (int i = a; i < b; i++) if (pk[i] > max) max = pk[i];
                if (max < floor * 8 || max < 1e-4f) continue;             // not 18 dB above the room: not heard
                float thr = Math.Max(floor * 4, max * 0.3f);
                for (int i = a; i < b; i++) if (env[i] >= thr) { delays.Add(t[i] - c0); break; }
            }
            res.Heard = delays.Count;
            if (delays.Count < Math.Max(2, clickTicks.Count / 2 + 1))
            {
                res.Problem = "The microphone didn't hear the clicks clearly - hold the headphones right against the mic, or turn this output up.";
                return res;
            }
            delays.Sort();
            double med = delays[delays.Count / 2];
            res.DelayMs = med;
            res.SpreadMs = delays.Max(d => Math.Abs(d - med));
            if (res.SpreadMs > 15) res.Problem = "The clicks came back unevenly (" + res.SpreadMs.ToString("0") + " ms apart) - try again in a quieter moment.";
            return res;
        }

        /// <summary>Delays that bring every output in line with the slowest one, ms.</summary>
        public static int[] Align(IList<double?> measured)
        {
            double slowest = measured.Where(m => m.HasValue).Select(m => m.Value).DefaultIfEmpty(0).Max();
            return measured.Select(m => m.HasValue ? (int)Math.Round(slowest - m.Value) : 0).ToArray();
        }
    }
}
