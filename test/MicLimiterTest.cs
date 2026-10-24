using System;
using StreamLite;
using StreamLite.Audio;

// The mic's output limiter (what Discord hears when you shout): voice-pitch tones driven 6-12 dB into it with every
// other effect off. Prints the harmonic distortion it adds.
class MicLimiterTest
{
    const int Fs = 48000;

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; } }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    static void Main()
    {
        foreach (double f in new[] { 93.75, 140.625, 234.375 })     // exact FFT bins, typical voice pitches
            foreach (int vol in new[] { 0, 6, 12 })
            {
                var s = new AppSettings { NoiseRemoval = false, Gate = false, Compressor = false, AutoLevel = false, HighPassHz = 0, DeEsser = false, VolumeDb = vol, Limiter = true };
                s.MicEq.Enabled = false;
                var c = new VoiceChain(); c.Configure(s);
                int total = Fs * 3; var y = new float[total]; var blk = new float[480];
                for (int pos = 0; pos + 480 <= total; pos += 480)
                {
                    for (int i = 0; i < 480; i++) blk[i] = (float)(0.7 * Math.Sin(2 * Math.PI * f * (pos + i) / Fs));
                    c.Process(blk, 480, false);
                    Array.Copy(blk, 0, y, pos, 480);
                }
                const int N = 65536;
                var re = new double[N]; var im = new double[N];
                for (int i = 0; i < N; i++) re[i] = y[total - N + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
                Fft(re, im);
                int k0 = (int)Math.Round(f * N / Fs);
                double fund = 0, harm = 0;
                for (int k = k0 - 3; k <= k0 + 3; k++) fund += re[k] * re[k] + im[k] * im[k];
                for (int h = 2; h <= 15; h++) for (int k = k0 * h - 3; k <= k0 * h + 3 && k < N / 2; k++) harm += re[k] * re[k] + im[k] * im[k];
                Console.WriteLine(string.Format("{0,7:0.#} Hz  drive {1,3} dB  harmonics {2,6:0.000}%", f, vol + 20 * Math.Log10(0.7 / 0.89), 100 * Math.Sqrt(harm / fund)));
            }
    }
}
