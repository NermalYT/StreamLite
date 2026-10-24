using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;

// Objective A/B of noise suppressors. The suppressor runs on speech+noise; its per-frame gains are then applied
// separately to the clean speech and to the noise ("shadow filtering"), so we can measure exactly how much voice
// was damaged and how much noise was removed.
class NsEval
{
    const int Fs = 48000;

    // Old suppressor, kept verbatim for comparison (minimum statistics + Wiener, hop 512).
    class OldNs
    {
        const int N = 1024, Hop = N / 2, Bins = N / 2 + 1;
        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], outBuf = new float[N], outQueue = new float[Hop];
        readonly float[] re = new float[N], im = new float[N];
        readonly float[] smooth = new float[Bins], minimum = new float[Bins], prevGain = new float[Bins], prevPost = new float[Bins], gains = new float[Bins];
        int pos, frames;
        public float Strength = 0.7f, AdaptSpeed = 0.5f;
        public Action<float[]> GainTap;
        public OldNs() { for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N))); for (int k = 0; k < Bins; k++) prevGain[k] = 1; }
        public void Process(float[] buf, int count) { for (int i = 0; i < count; i++) { inBuf[N - Hop + pos] = buf[i]; buf[i] = outQueue[pos]; if (++pos == Hop) { pos = 0; Frame(); } } }
        void Frame()
        {
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            float rise = 1.0005f + 0.006f * AdaptSpeed, floor = (float)Math.Pow(10, -(6 + 30 * Strength) / 20), over = 1f + 1.5f * Strength;
            for (int k = 0; k < Bins; k++)
            {
                float p = re[k] * re[k] + im[k] * im[k];
                smooth[k] = frames == 0 ? p : 0.75f * smooth[k] + 0.25f * p;
                if (frames == 0) minimum[k] = smooth[k]; else if (smooth[k] < minimum[k]) minimum[k] = smooth[k]; else if (frames >= 8) minimum[k] = minimum[k] * rise + 1e-12f;
                float noise = minimum[k] * 2.0f * over + 1e-10f, post = p / noise;
                float xi = Math.Max(1e-4f, 0.98f * prevGain[k] * prevGain[k] * prevPost[k] + 0.02f * Math.Max(post - 1f, 0f));
                float g = Math.Max(floor, xi / (1f + xi)); prevGain[k] = g; prevPost[k] = post; gains[k] = g;
            }
            frames++;
            var sm = new float[Bins];
            for (int k = 0; k < Bins; k++) sm[k] = k > 0 && k < Bins - 1 ? 0.25f * gains[k - 1] + 0.5f * gains[k] + 0.25f * gains[k + 1] : gains[k];
            if (GainTap != null) GainTap(sm);
            for (int k = 0; k < Bins; k++) { re[k] *= sm[k]; im[k] *= sm[k]; if (k > 0 && k < N / 2) { re[N - k] = re[k]; im[N - k] = -im[k]; } }
            fft.Transform(re, im, true);
            for (int i = 0; i < N; i++) outBuf[i] += re[i] / N * win[i];
            Array.Copy(outBuf, 0, outQueue, 0, Hop); Array.Copy(outBuf, Hop, outBuf, 0, N - Hop); Array.Clear(outBuf, N - Hop, Hop); Array.Copy(inBuf, Hop, inBuf, 0, N - Hop);
        }
    }

    // Applies recorded per-frame gains to another signal with identical framing.
    class Shadow
    {
        readonly int n, hop; readonly float scale;
        readonly Fft fft; readonly float[] win, inBuf, outBuf, outQueue, re, im;
        int pos;
        readonly Queue<float[]> gains;
        public readonly List<float> Out = new List<float>();
        public readonly List<float[]> Spec = new List<float[]>();
        public Shadow(int n, int hop, float scale, Queue<float[]> g)
        {
            this.n = n; this.hop = hop; this.scale = scale; gains = g; fft = new Fft(n);
            win = new float[n]; inBuf = new float[n]; outBuf = new float[n]; outQueue = new float[hop]; re = new float[n]; im = new float[n];
            for (int i = 0; i < n; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / n)));
        }
        public void Run(float[] x)
        {
            foreach (var s in x)
            {
                inBuf[n - hop + pos] = s; Out.Add(outQueue[pos]);
                if (++pos == hop)
                {
                    pos = 0;
                    for (int i = 0; i < n; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
                    fft.Transform(re, im, false);
                    var g = gains.Count > 0 ? gains.Dequeue() : null;
                    var spec = new float[n / 2 + 1];
                    for (int k = 0; k <= n / 2; k++)
                    {
                        float gk = g != null ? g[k] : 1f;
                        re[k] *= gk; im[k] *= gk; spec[k] = re[k] * re[k] + im[k] * im[k];
                        if (k > 0 && k < n / 2) { re[n - k] = re[k]; im[n - k] = -im[k]; }
                    }
                    Spec.Add(spec);
                    fft.Transform(re, im, true);
                    for (int i = 0; i < n; i++) outBuf[i] += re[i] * scale * win[i];
                    Array.Copy(outBuf, 0, outQueue, 0, hop); Array.Copy(outBuf, hop, outBuf, 0, n - hop); Array.Clear(outBuf, n - hop, hop); Array.Copy(inBuf, hop, inBuf, 0, n - hop);
                }
            }
        }
    }

    static void Main()
    {
        var rnd = new Random(7);
        int len = Fs * 14;
        var speech = new float[len]; var noise = new float[len]; var isSpeech = new bool[len]; var isFric = new bool[len];
        // Noise: pink noise + hum and the tones measured on the user's Yeti, slowly wobbling.
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < len; i++)
        {
            double w = rnd.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11; b6 = w * 0.115926;
            double t = (double)i / Fs, wob = 1 + 0.15 * Math.Sin(2 * Math.PI * 0.3 * t);
            double tones = 0.004 * Math.Sin(2 * Math.PI * 79 * t) + 0.003 * Math.Sin(2 * Math.PI * 108 * t) + 0.0015 * Math.Sin(2 * Math.PI * 1014 * t) + 0.001 * Math.Sin(2 * Math.PI * 1307 * t);
            noise[i] = (float)((pink * 0.06 + tones) * wob);
        }
        // Speech: 2 s silence (learning), then syllables with formants and some "s" sounds.
        int p = Fs * 2; double phase = 0;
        while (p < len - Fs / 2)
        {
            int syl = (int)(Fs * (0.12 + rnd.NextDouble() * 0.15));
            double f0 = 110 + rnd.NextDouble() * 60, f1 = 450 + rnd.NextDouble() * 400, f2 = 1100 + rnd.NextDouble() * 1000, f3 = 2500 + rnd.NextDouble() * 500;
            bool fric = rnd.NextDouble() < 0.25;
            for (int i = 0; i < syl && p + i < len; i++)
            {
                double t = (double)i / syl, envA = Math.Sin(Math.PI * t);
                if (fric)
                {
                    // "s": band-limited noise 4-9 kHz.
                    double w = rnd.NextDouble() * 2 - 1;
                    speech[p + i] = (float)(w * 0.12 * envA); isFric[p + i] = true;
                }
                else
                {
                    double f = f0 * (1 + 0.04 * Math.Sin(2 * Math.PI * 5 * i / (double)Fs) - 0.1 * t);
                    phase += 2 * Math.PI * f / Fs;
                    double s = 0;
                    for (int h = 1; h * f < 8000; h++)
                    {
                        double hf = h * f;
                        double amp = 1.0 / h * (1.0 / (1 + Math.Pow((hf - f1) / 120, 2)) + 0.6 / (1 + Math.Pow((hf - f2) / 180, 2)) + 0.3 / (1 + Math.Pow((hf - f3) / 250, 2)) + 0.05);
                        s += amp * Math.Sin(h * phase);
                    }
                    speech[p + i] = (float)(s * 0.45 * envA);
                }
                isSpeech[p + i] = true;
            }
            p += syl + (int)(Fs * (rnd.NextDouble() < 0.2 ? 0.25 + rnd.NextDouble() * 0.4 : 0.03 + rnd.NextDouble() * 0.08));
        }
        // High-pass the fricatives into the 4-9 kHz range.
        var hp = Biquad.Create(new EqFilter { Type = "HP", Freq = 4000, Q = 0.7 }, Fs);
        for (int i = 0; i < len; i++) if (isFric[i]) speech[i] = hp.Process(speech[i]);

        Console.WriteLine("Input: voice " + Db(speech, isSpeech, true).ToString("0.0") + " dB, noise " + Db(noise, null, true).ToString("0.0") + " dB (SNR " + (Db(speech, isSpeech, true) - Db(noise, null, true)).ToString("0") + " dB)");
        Console.WriteLine(string.Format("{0,-34}{1,10}{2,12}{3,12}{4,12}{5,12}", "", "noise", "voice", "voice", "'s' sounds", "residual"));
        Console.WriteLine(string.Format("{0,-34}{1,10}{2,12}{3,12}{4,12}{5,12}", "suppressor", "removed", "lost", "distortion", "lost", "chirpiness"));
        RunOld("old: strength 75, adapt 100 (yours)", 0.75f, 1.0f, speech, noise, isSpeech, isFric);
        RunOld("old: strength 55, adapt 30", 0.55f, 0.3f, speech, noise, isSpeech, isFric);
        RunNew("new: defaults (strength 50)", 0.5f, 0.3f, 0.5f, 0.6f, speech, noise, isSpeech, isFric);
        RunNew("new: strong (strength 90)", 0.9f, 0.3f, 0.5f, 0.6f, speech, noise, isSpeech, isFric);
        RunNew("new: gentle (strength 35)", 0.35f, 0.3f, 0.5f, 0.6f, speech, noise, isSpeech, isFric);
        RunNew("new: no voice protect/smoothing", 0.6f, 0.3f, 0f, 0f, speech, noise, isSpeech, isFric);
    }

    static void RunOld(string name, float strength, float adapt, float[] s, float[] n, bool[] sp, bool[] fr)
    {
        var q = new Queue<float[]>();
        var ns = new OldNs { Strength = strength, AdaptSpeed = adapt, GainTap = g => q.Enqueue((float[])g.Clone()) };
        var mix = s.Zip(n, (a, b) => a + b).ToArray(); ns.Process(mix, mix.Length);
        Report(name, 1024, 512, 1f / 1024, q, s, n, sp, fr);
    }

    static void RunNew(string name, float strength, float adapt, float smooth, float protect, float[] s, float[] n, bool[] sp, bool[] fr)
    {
        var q = new Queue<float[]>();
        var ns = new NoiseSuppressor { Strength = strength, AdaptSpeed = adapt, Smoothing = smooth, VoiceProtect = protect };
        ns.GainTap = g => q.Enqueue(g == null ? null : (float[])g.Clone());
        var mix = s.Zip(n, (a, b) => a + b).ToArray();
        for (int i = 0; i < mix.Length; i += 480) { int c = Math.Min(480, mix.Length - i); var blk = new float[c]; Array.Copy(mix, i, blk, 0, c); ns.Process(blk, c); }
        Report(name, 1024, 256, 1f / 2048, q, s, n, sp, fr);
    }

    static void Report(string name, int N, int hop, float scale, Queue<float[]> gains, float[] s, float[] n, bool[] sp, bool[] fr)
    {
        var gs = new Queue<float[]>(gains); var gn = new Queue<float[]>(gains);
        var shS = new Shadow(N, hop, scale, gs); shS.Run(s);
        var shN = new Shadow(N, hop, scale, gn); shN.Run(n);
        var refS = new Shadow(N, hop, scale, new Queue<float[]>()); refS.Run(s);       // same framing, no gains = reference
        var refN = new Shadow(N, hop, scale, new Queue<float[]>()); refN.Run(n);
        int lat = N, start = Fs * 3;                                                      // skip learning period
        double nIn = 0, nOut = 0, sIn = 0, sOut = 0, fIn = 0, fOut = 0;
        for (int i = start; i < s.Length - lat; i++)
        {
            float so = shS.Out[i + lat], ro = refS.Out[i + lat], no = shN.Out[i + lat], rn = refN.Out[i + lat];
            nIn += rn * rn; nOut += no * no;
            if (sp[i] && !fr[i]) { sIn += ro * ro; sOut += so * so; }
            if (fr[i]) { fIn += ro * ro; fOut += so * so; }
        }
        // Log-spectral distortion of the voice (frames with voice), and residual-noise flicker.
        double lsd = 0; int lsdN = 0; double flick = 0, flickIn = 0; int fN = 0;
        int f0 = start / hop + 4;
        for (int f = f0; f < shS.Spec.Count; f++)
        {
            int center = Math.Min(s.Length - 1, f * hop);
            if (sp[center] && !fr[center])
            {
                double acc = 0; int c = 0;
                double mx = 0; for (int k = 4; k < 350; k++) mx = Math.Max(mx, refS.Spec[f][k]);
                for (int k = 4; k < 350; k++) { double a = refS.Spec[f][k], b = shS.Spec[f][k]; if (a > mx * 1e-3) { double d = 10 * Math.Log10((b + 1e-12) / (a + 1e-12)); acc += d * d; c++; } }
                if (c > 0) { lsd += Math.Sqrt(acc / c); lsdN++; }
            }
        }
        for (int k = 20; k < 400; k += 4)
        {
            var o = new List<double>(); var r = new List<double>();
            for (int f = f0; f < shN.Spec.Count; f++) { o.Add(10 * Math.Log10(shN.Spec[f][k] + 1e-14)); r.Add(10 * Math.Log10(refN.Spec[f][k] + 1e-14)); }
            flick += Std(o); flickIn += Std(r); fN++;
        }
        Console.WriteLine(string.Format("{0,-34}{1,8:0.0}dB{2,10:0.0}dB{3,10:0.0}dB{4,10:0.0}dB{5,10:+0.0;-0.0}dB",
            name, 10 * Math.Log10(nIn / nOut), 10 * Math.Log10(sIn / sOut), lsd / Math.Max(1, lsdN), 10 * Math.Log10(fIn / fOut), (flick - flickIn) / fN));
    }

    static double Std(List<double> v) { double m = v.Average(); return Math.Sqrt(v.Average(x => (x - m) * (x - m))); }
    static double Db(float[] x, bool[] mask, bool rms)
    {
        double s = 0; int c = 0;
        for (int i = 0; i < x.Length; i++) if (mask == null || mask[i]) { s += x[i] * x[i]; c++; }
        return 10 * Math.Log10(s / Math.Max(1, c) + 1e-20);
    }
}


