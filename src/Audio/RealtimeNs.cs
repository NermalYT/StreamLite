using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// Realtime noise removal: your voice goes through a 2.7 ms filter instead of the noise remover's own 20-50 ms
    /// window and look-ahead. The noise remover (RNNoise, Classic or DeepFilterNet) still listens - on a copy - and
    /// steers this filter band by band with the gains it would have applied. Its gains come 10-50 ms late, so word
    /// starts would lose their first moments: a fast onset detector opens the voice bands at once when a voiced sound
    /// rises well above the background (and not for broadband clicks), until the noise remover's gains catch up.
    /// </summary>
    internal sealed class RealtimeNs
    {
        /// <summary>128-sample window (2.7 ms), 32-sample hop (0.67 ms): the delay is the window.</summary>
        public const int N = 128, R = 32, K = N / 2 + 1;
        public const float DelayMs = N * 1000f / 48000;
        const double BinHz = 48000.0 / N;
        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], outBuf = new float[N], outQ = new float[R], re = new float[N], im = new float[N];
        readonly float[] target = new float[K], applied = new float[K], floor = new float[K], pw = new float[K], ps = new float[K];
        int pos, hops, onsetRun, onsetHold;

        /// <summary>Lowest gain (the strength's attenuation limit) - the onset path never goes below the noise remover's floor either.</summary>
        public volatile float MinGain = 0.03f;
        /// <summary>The noise remover's voice probability right now (0..1).</summary>
        public volatile float Vad;
        /// <summary>How far behind the noise remover's gains are (ms): the onset path holds the voice bands open that long.</summary>
        public volatile float LagMs = 20;
        /// <summary>Pass everything (gain 1) with the same delay, so switching A/B doesn't jump.</summary>
        public volatile bool Bypass;
        /// <summary>Hops the onset path opened the voice bands (tests).</summary>
        public int OnsetHops;

        // Bins: voiced speech 300-3400 Hz, the "click" band 5-14 kHz, and how far up the onset path may open (7.5 kHz).
        static readonly int SpLo = (int)Math.Round(300 / BinHz), SpHi = (int)Math.Round(3400 / BinHz), HiLo = (int)Math.Round(5000 / BinHz), HiHi = (int)Math.Round(14000 / BinHz), OpenHi = (int)Math.Round(7500 / BinHz);

        public RealtimeNs()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < K; k++) { target[k] = applied[k] = 1; floor[k] = 1e-9f; }
        }

        /// <summary>
        /// The noise remover's latest gains (per bin of its own FFT, binHz apart): each of this filter's 375 Hz bins
        /// gets the power average of the gains it covers.
        /// </summary>
        public void SetGains(float[] gains, int count, double binHz)
        {
            if (gains == null) { for (int k = 0; k < K; k++) target[k] = 1; return; }
            for (int k = 0; k < K; k++)
            {
                double lo = (k - 0.5) * BinHz, hi = (k + 0.5) * BinHz, sum = 0; int n = 0;
                int a = Math.Max(0, (int)Math.Ceiling(lo / binHz)), b = Math.Min(count - 1, (int)Math.Floor(hi / binHz));
                for (int i = a; i <= b; i++) { sum += gains[i] * gains[i]; n++; }
                if (n == 0) { int i = Math.Max(0, Math.Min(count - 1, (int)Math.Round(k * BinHz / binHz))); sum = gains[i] * gains[i]; n = 1; }
                target[k] = (float)Math.Sqrt(sum / n);
            }
        }

        readonly float[] pauseTarget = new float[K], keep = new float[K];
        bool havePause; int sinceSpeech = 1000;
        /// <summary>The deeper remover's floor in pauses.</summary>
        public volatile float PauseMinGain = 0.001f;

        /// <summary>
        /// A deeper but slower noise remover's gains (DeepFilterNet, 50 ms behind): used only in pauses - once the
        /// steering remover has heard no voice for 80 ms - to make them as quiet as it does. While you talk, the
        /// timely gains steer alone.
        /// </summary>
        public void SetPauseGains(float[] gains, int count, double binHz)
        {
            if (gains == null) { havePause = false; return; }
            Array.Copy(target, keep, K);
            SetGains(gains, count, binHz);
            Array.Copy(target, pauseTarget, K); Array.Copy(keep, target, K);
            havePause = true;
        }

        public void Process(float[] buf, int count)
        {
            for (int i = 0; i < count; i++)
            {
                inBuf[N - R + pos] = buf[i];
                buf[i] = outQ[pos];
                if (++pos == R) { pos = 0; Frame(); }
            }
        }

        void Frame()
        {
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            for (int k = 0; k < K; k++) pw[k] = re[k] * re[k] + im[k] * im[k];
            hops++;

            // Smoothed power (about 2 ms), and the background's average per bin: learned while the noise remover says
            // nobody talks (and no word start is held), following a quieter room faster than a louder one.
            bool talking = Vad > 0.5f;
            for (int k = 0; k < K; k++)
            {
                ps[k] += (pw[k] - ps[k]) * 0.35f;
                if (hops <= 16) floor[k] += (ps[k] - floor[k]) / hops;
                else if (!talking && onsetHold == 0) floor[k] += (ps[k] - floor[k]) * (ps[k] < floor[k] ? 0.02f : 0.005f);
                floor[k] = Math.Max(floor[k], 1e-12f);
            }

            // A word starting: much of the voice band clearly (10 dB) above the background, voiced (more energy in
            // the voice band than up where keyboard and mouse clicks live), for two hops in a row. Held open as long
            // as the noise remover's gains are behind.
            int lit = 0; double es = 0, eh = 0, fs = 0;
            for (int k = SpLo; k <= SpHi; k++) { if (ps[k] > 10 * floor[k]) lit++; es += ps[k]; fs += floor[k]; }
            for (int k = HiLo; k <= HiHi; k++) eh += ps[k];
            bool onset = hops > 16 && lit * 2 >= SpHi - SpLo + 1 && es > 10 * fs && es > 2 * eh;
            onsetRun = onset ? onsetRun + 1 : 0;
            int holdHops = (int)(LagMs * 48 / R) + 15;
            if (onsetRun >= 2) onsetHold = holdHops;
            else if (onsetHold > 0) onsetHold--;
            if (onsetHold > 0) OnsetHops++;

            sinceSpeech = talking ? 0 : Math.Min(sinceSpeech + 1, 1000);
            bool pause = havePause && onsetHold == 0 && sinceSpeech > 80 * 48 / R;
            float minG = pause ? Math.Min(MinGain, PauseMinGain) : MinGain;
            for (int k = 0; k < K; k++)
            {
                float t = pause ? Math.Min(target[k], pauseTarget[k]) : target[k];
                float g = Math.Max(minG, Math.Min(1, t));
                if (onsetHold > 0 && k >= 1 && k <= OpenHi)
                {
                    // Wiener-like opening from the bin's own level over the background.
                    float p = ps[k];
                    float fast = p > 0 ? (float)Math.Sqrt(Math.Max(0, 1 - 2 * floor[k] / p)) : 0;
                    if (fast > g) g = fast;
                }
                if (Bypass) g = 1;
                // Open fast (1 ms), close a little slower (3 ms): no zipper noise as the gains step every 10 ms.
                applied[k] += (g - applied[k]) * (g > applied[k] ? 0.6f : 0.2f);
                float a = applied[k];
                re[k] *= a; im[k] *= a;
                if (k > 0 && k < N / 2) { re[N - k] = re[k]; im[N - k] = -im[k]; }
            }
            
            fft.Transform(re, im, true);
            // sqrt-Hann analysis + synthesis at 75% overlap sums to 2.
            float scale = 1f / (N * 2f);
            for (int i = 0; i < N; i++) outBuf[i] += re[i] * scale * win[i];
            Array.Copy(outBuf, 0, outQ, 0, R);
            Array.Copy(outBuf, R, outBuf, 0, N - R);
            Array.Clear(outBuf, N - R, R);
            Array.Copy(inBuf, R, inBuf, 0, N - R);
        }
    }
}
