using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// Fills in sound the mic's driver lost (a packet that never arrived): the voice's last pitch period repeated for
    /// exactly as long as the hole, then blended into the next real packet. A 3 ms hole that way is inaudible, where
    /// running dry means a fade to silence - and no buffer could have bridged it, because that sound doesn't exist.
    /// </summary>
    internal sealed class PacketConcealer
    {
        const int Hist = 2048, Window = 512, MinPeriod = 96, MaxPeriod = 768, Blend = 32, SeamFix = 16;
        readonly float[] h = new float[Hist];
        int filled, period = MinPeriod, cont;
        float endGain = 1;

        /// <summary>The latest real sound (call for every packet, before it's processed).</summary>
        public void Remember(float[] x, int n)
        {
            if (n >= Hist) { Array.Copy(x, n - Hist, h, 0, Hist); filled = Hist; return; }
            Array.Copy(h, n, h, 0, Hist - n);
            Array.Copy(x, 0, h, Hist - n, n);
            filled = Math.Min(Hist, filled + n);
        }

        /// <summary><paramref name="n"/> samples continuing the last sound (its strongest repeat period).</summary>
        public void Fill(float[] dst, int n)
        {
            if (filled < Hist) { Array.Clear(dst, 0, n); period = MinPeriod; cont = 0; endGain = 0; return; }
            // The period that best predicts the last 10.7 ms from the sound before it (normalized correlation).
            double best = double.MinValue; int bestT = MinPeriod;
            for (int t = MinPeriod; t <= MaxPeriod; t++)
            {
                double xy = 0, yy = 0;
                for (int i = Hist - Window; i < Hist; i++) { xy += h[i] * h[i - t]; yy += h[i - t] * h[i - t]; }
                double c = yy > 1e-12 ? xy / Math.Sqrt(yy) : 0;
                if (c > best) { best = c; bestT = t; }
            }
            period = bestT;
            // Repeat it; a long hole fades a little (a held sound that never changes sounds artificial). The first
            // samples take out any step against where the sound was heading (no click even when the period is a guess).
            float seam = h[Hist - period] - (2 * h[Hist - 1] - h[Hist - 2]);
            for (int i = 0; i < n; i++)
            {
                float g = 1f - 0.3f * i / (SampleRateHz / 50f);
                dst[i] = h[Hist - period + (i % period)] * g - (i < SeamFix ? seam * (SeamFix - i) / SeamFix : 0);
            }
            cont = n % period;
            endGain = 1f - 0.3f * n / (SampleRateHz / 50f);
        }

        /// <summary>Blends the start of the next real packet out of the filled-in sound (no click at the seam).</summary>
        public void BlendInto(float[] x, int n)
        {
            int m = Math.Min(Blend, n);
            for (int i = 0; i < m; i++)
            {
                float w = (i + 1f) / (m + 1f);
                float c = h[Hist - period + ((cont + i) % period)] * endGain;
                x[i] = w * x[i] + (1 - w) * c;
            }
        }

        const int SampleRateHz = 48000;
    }
}
