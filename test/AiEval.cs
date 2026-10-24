using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite.Audio;

// Objective A/B of the classic suppressor vs the RNNoise AI engine, on real (TTS) speech plus noise.
// The suppressor's per-frame gains are replayed on the clean voice and on the noise separately
// ("shadow filtering"), so voice damage and noise removal are measured exactly.
class AiEval
{
    const int Fs = 48000;

    class Shadow
    {
        readonly int n, hop; readonly float[] win; readonly float scale;
        readonly MixedFft fft;
        readonly float[] inBuf, outBuf, outQueue, xr, xi, yr, yi;
        int pos;
        readonly Queue<float[]> gains;
        public readonly List<float> Out = new List<float>();
        public readonly List<float[]> Spec = new List<float[]>();
        public Shadow(int n, int hop, float[] win, Queue<float[]> g)
        {
            this.n = n; this.hop = hop; this.win = win; gains = g; fft = new MixedFft(n);
            inBuf = new float[n]; outBuf = new float[n]; outQueue = new float[hop]; xr = new float[n]; xi = new float[n]; yr = new float[n]; yi = new float[n];
            double c = 0; for (int j = 0; j < n; j += hop) c += win[j] * win[j];
            scale = (float)(1.0 / (n * c));
        }
        public void Run(float[] x)
        {
            foreach (var s in x)
            {
                inBuf[n - hop + pos] = s; Out.Add(outQueue[pos]);
                if (++pos < hop) continue;
                pos = 0;
                for (int i = 0; i < n; i++) { xr[i] = inBuf[i] * win[i]; xi[i] = 0; }
                fft.Transform(xr, xi, yr, yi);
                var g = gains.Count > 0 ? gains.Dequeue() : null;
                var spec = new float[n / 2 + 1];
                for (int k = 0; k <= n / 2; k++)
                {
                    float gk = g != null ? g[k] : 1f;
                    yr[k] *= gk; yi[k] *= gk; spec[k] = yr[k] * yr[k] + yi[k] * yi[k];
                }
                for (int k = n / 2 + 1; k < n; k++) { yr[k] = yr[n - k]; yi[k] = -yi[n - k]; }
                Spec.Add(spec);
                for (int k = 0; k < n; k++) { xr[k] = yr[k]; xi[k] = -yi[k]; }   // inverse via conjugate
                fft.Transform(xr, xi, yr, yi);
                for (int i = 0; i < n; i++) outBuf[i] += yr[i] * scale * win[i];
                Array.Copy(outBuf, 0, outQueue, 0, hop); Array.Copy(outBuf, hop, outBuf, 0, n - hop); Array.Clear(outBuf, n - hop, hop); Array.Copy(inBuf, hop, inBuf, 0, n - hop);
            }
        }
    }

    static float[] Tts(string text)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Rate = 1;
            synth.Speak(text);
            var b = ms.ToArray();
            var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            return f;
        }
    }

    static void Main(string[] args)
    {
        var rnd = new Random(7);
        {
            // FFT check against a direct DFT.
            int n = 960; var fft = new MixedFft(n);
            var ar = new float[n]; var ai = new float[n]; var br = new float[n]; var bi = new float[n];
            for (int i = 0; i < n; i++) { ar[i] = (float)(rnd.NextDouble() - .5); ai[i] = (float)(rnd.NextDouble() - .5); }
            fft.Transform(ar, ai, br, bi);
            double err = 0, mag = 0;
            for (int k = 0; k < n; k++)
            {
                double sr = 0, si = 0;
                for (int t = 0; t < n; t++) { double ph = -2 * Math.PI * k * t / n; sr += ar[t] * Math.Cos(ph) - ai[t] * Math.Sin(ph); si += ar[t] * Math.Sin(ph) + ai[t] * Math.Cos(ph); }
                err = Math.Max(err, Math.Abs(sr - br[k]) + Math.Abs(si - bi[k])); mag = Math.Max(mag, Math.Abs(sr));
            }
            Console.WriteLine("FFT960 max error " + err.ToString("E2") + " (peak " + mag.ToString("0.0") + ")");
        }
        var voice = Tts("So yesterday we were playing ranked and honestly the whole match was insane. " +
                        "She said she'd push the left side, but six of them showed up at the same time. " +
                        "Can you hear me okay? Is my mic still sounding weird, or is it fixed now? " +
                        "Seriously, the sound should be crisp, clear, and smooth without any hissing.");
        int lead = Fs * 2;
        int len = lead + voice.Length + Fs;
        var speech = new float[len];
        // Normalize voice to about -20 dBFS RMS on active parts.
        double e = 0; int c = 0; foreach (var v in voice) if (Math.Abs(v) > 0.01) { e += v * v; c++; }
        float gain = (float)(0.1 / Math.Sqrt(e / Math.Max(1, c)));
        for (int i = 0; i < voice.Length; i++) speech[lead + i] = voice[i] * gain;

        float[] room = RoomNoise(len, rnd, 1.0), keys = Keyboard(len, rnd), fan = Fan(len, rnd);
        var scenes = new List<Tuple<string, float[]>>
        {
            Tuple.Create("your room (-45 dB hiss + tones)", room),
            Tuple.Create("room + keyboard clicks", room.Zip(keys, (a, b) => a + b).ToArray()),
            Tuple.Create("loud fan (-30 dB)", fan),
        };

        // Speech / fricative masks per 10 ms block from the clean voice.
        var sp = new bool[len]; var fr = new bool[len];
        var hpS = Biquad.Create(new EqFilter { Type = "HP", Freq = 4000, Q = 0.7 }, Fs);
        var hiOnly = speech.Select(v => hpS.Process(v)).ToArray();
        for (int b = 0; b + 480 <= len; b += 480)
        {
            double all = 0, hi = 0;
            for (int i = b; i < b + 480; i++) { all += speech[i] * speech[i]; hi += hiOnly[i] * hiOnly[i]; }
            double db = 10 * Math.Log10(all / 480 + 1e-20);
            bool s = db > -45, f = s && hi > 0.5 * all;
            for (int i = b; i < b + 480; i++) { sp[i] = s; fr[i] = f; }
        }

        foreach (var sc in scenes)
        {
            var noise = sc.Item2;
            Console.WriteLine();
            Console.WriteLine("== " + sc.Item1 + ": voice " + Db(speech, sp).ToString("0.0") + " dB, noise " + Db(noise, null).ToString("0.0") + " dB");
            // Column order matches Run(): noise removed overall, in pauses, voice lost, distortion, 's' lost, chirpiness.
            Console.WriteLine(string.Format("{0,-36}{1,10}{2,10}{3,10}{4,12}{5,10}{6,12}", "", "noise", "pauses", "voice", "voice", "'s'", "residual"));
            Console.WriteLine(string.Format("{0,-36}{1,10}{2,10}{3,10}{4,12}{5,10}{6,12}", "engine", "removed", "removed", "lost", "distortion", "lost", "chirpiness"));
            Run("classic (strength 50)", false, 0.5f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 30", true, 0.3f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 50 (stock RNNoise)", true, 0.5f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 70", true, 0.7f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 85", true, 0.85f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 100", true, 1.0f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("AI strength 85, protect 0", true, 0.85f, 0.5f, 0f, speech, noise, sp, fr);
            Run("style Natural (60/50/60)", true, 0.6f, 0.5f, 0.6f, speech, noise, sp, fr);
            Run("style Noisy room (90/60/50)", true, 0.9f, 0.6f, 0.5f, speech, noise, sp, fr);
            Run("style Late night (70/50/80)", true, 0.7f, 0.5f, 0.8f, speech, noise, sp, fr);
            Run("your tuning (50/100/100)", true, 0.5f, 1.0f, 1.0f, speech, noise, sp, fr);
        }

        // CPU cost of the AI engine.
        var ns = new NoiseSuppressor { UseAi = true };
        var blk = new float[480];
        var sw = Stopwatch.StartNew();
        int secs = 0;
        for (int i = 0; i + 480 <= len; i += 480) { Array.Copy(speech, i, blk, 0, 480); ns.Process(blk, 480); }
        secs = len / Fs;
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("AI engine CPU: " + (sw.Elapsed.TotalMilliseconds / secs).ToString("0.0") + " ms per second of audio (" + (sw.Elapsed.TotalMilliseconds / secs / 10).ToString("0.00") + "% of one core)");
        GC.Collect();
        Console.WriteLine("Managed heap: " + (GC.GetTotalMemory(true) / 1024) + " KB, working set " + (Process.GetCurrentProcess().WorkingSet64 / 1048576) + " MB");

        if (args.Length > 0) Dump(args[0], speech, scenes[1].Item2);
    }

    static void Dump(string dir, float[] speech, float[] noise)
    {
        Directory.CreateDirectory(dir);
        var mix = speech.Zip(noise, (a, b) => a + b).ToArray();
        WriteWav(Path.Combine(dir, "input.wav"), mix);
        foreach (var ai in new[] { false, true })
        {
            var ns = new NoiseSuppressor { UseAi = ai, Strength = ai ? 0.7f : 0.5f };
            var o = (float[])mix.Clone();
            for (int i = 0; i + 480 <= o.Length; i += 480) { var b = new float[480]; Array.Copy(o, i, b, 0, 480); ns.Process(b, 480); Array.Copy(b, 0, o, i, 480); }
            WriteWav(Path.Combine(dir, ai ? "ai.wav" : "classic.wav"), o);
        }
    }

    static void WriteWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + x.Length * 2); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, v * 32768)));
        }
    }

    static float[] RoomNoise(int len, Random rnd, double scale)
    {
        var noise = new float[len];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < len; i++)
        {
            double w = rnd.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11; b6 = w * 0.115926;
            double t = (double)i / Fs, wob = 1 + 0.15 * Math.Sin(2 * Math.PI * 0.3 * t);
            double tones = 0.002 * Math.Sin(2 * Math.PI * 79 * t) + 0.0015 * Math.Sin(2 * Math.PI * 108 * t) + 0.001 * Math.Sin(2 * Math.PI * 1014 * t) + 0.0007 * Math.Sin(2 * Math.PI * 1307 * t);
            noise[i] = (float)((pink * 0.03 + tones) * wob * scale);
        }
        return noise;
    }

    static float[] Keyboard(int len, Random rnd)
    {
        var k = new float[len];
        int p = Fs / 2;
        while (p < len - Fs / 10)
        {
            double amp = 0.05 + rnd.NextDouble() * 0.08;
            int dur = (int)(Fs * 0.012);
            for (int i = 0; i < dur; i++) k[p + i] += (float)((rnd.NextDouble() * 2 - 1) * amp * Math.Exp(-i / (Fs * 0.002)));
            p += (int)(Fs * (0.08 + rnd.NextDouble() * 0.25));
        }
        return k;
    }

    static float[] Fan(int len, Random rnd)
    {
        var f = new float[len];
        double lp = 0, lp2 = 0;
        for (int i = 0; i < len; i++)
        {
            double w = rnd.NextDouble() * 2 - 1;
            lp = 0.97 * lp + 0.03 * w; lp2 = 0.7 * lp2 + 0.3 * w;
            double t = (double)i / Fs;
            f[i] = (float)(lp * 0.25 + lp2 * 0.02 + 0.004 * Math.Sin(2 * Math.PI * 120 * t) + 0.003 * Math.Sin(2 * Math.PI * 240 * t));
        }
        return f;
    }

    static void Run(string name, bool ai, float strength, float smooth, float protect, float[] s, float[] n, bool[] sp, bool[] fr)
    {
        var q = new Queue<float[]>();
        var ns = new NoiseSuppressor { UseAi = ai, Strength = strength, AdaptSpeed = 0.3f, Smoothing = smooth, VoiceProtect = protect };
        var vads = new List<float>();
        ns.GainTap = g => { q.Enqueue(g == null ? null : (float[])g.Clone()); vads.Add(ns.SpeechPresence); };
        var mix = s.Zip(n, (a, b) => a + b).ToArray();
        for (int i = 0; i < mix.Length; i += 480) { int c = Math.Min(480, mix.Length - i); var blk = new float[c]; Array.Copy(mix, i, blk, 0, c); ns.Process(blk, c); }

        int N = ai ? 960 : 1024, hop = ai ? 480 : 256;
        var win = new float[N];
        for (int i = 0; i < N; i++)
        {
            if (ai) { int j = i < 480 ? i : N - 1 - i; double v = Math.Sin(.5 * Math.PI * (j + .5) / 480); win[i] = (float)Math.Sin(.5 * Math.PI * v * v); }
            else win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
        }
        var shS = new Shadow(N, hop, win, new Queue<float[]>(q)); shS.Run(s);
        var shN = new Shadow(N, hop, win, new Queue<float[]>(q)); shN.Run(n);
        var refS = new Shadow(N, hop, win, new Queue<float[]>()); refS.Run(s);
        var refN = new Shadow(N, hop, win, new Queue<float[]>()); refN.Run(n);
        int lat = N - hop, start = Fs * 1;
        double pIn = 0, pOut = 0, nIn = 0, nOut = 0, sIn = 0, sOut = 0, fIn = 0, fOut = 0;
        for (int i = start; i < s.Length - lat; i++)
        {
            float so = shS.Out[i + lat], ro = refS.Out[i + lat], no = shN.Out[i + lat], rn = refN.Out[i + lat];
            nIn += rn * rn; nOut += no * no; if (!sp[i]) { pIn += rn * rn; pOut += no * no; }
            if (sp[i] && !fr[i]) { sIn += ro * ro; sOut += so * so; }
            if (fr[i]) { fIn += ro * ro; fOut += so * so; }
        }
        int k0 = 100 * N / Fs, k1 = 8000 * N / Fs;
        double lsd = 0; int lsdN = 0, f0 = start / hop + 2;
        for (int f = f0; f < shS.Spec.Count; f++)
        {
            int center = Math.Min(s.Length - 1, Math.Max(0, f * hop - hop));
            if (!sp[center] || fr[center]) continue;
            double acc = 0; int c = 0, mxk = 0; double mx = 0;
            for (int k = k0; k < k1; k++) if (refS.Spec[f][k] > mx) { mx = refS.Spec[f][k]; mxk = k; }
            for (int k = k0; k < k1; k++) { double a = refS.Spec[f][k], b = shS.Spec[f][k]; if (a > mx * 1e-3) { double d = 10 * Math.Log10((b + 1e-12) / (a + 1e-12)); acc += d * d; c++; } }
            if (c > 0) { lsd += Math.Sqrt(acc / c); lsdN++; }
        }
        double flick = 0, flickIn = 0; int fN = 0;
        for (int k = 200 * N / Fs; k < 16000 * N / Fs; k += 4)
        {
            var o = new List<double>(); var r = new List<double>();
            for (int f = f0; f < shN.Spec.Count; f++) { if (sp[Math.Min(s.Length - 1, Math.Max(0, f * hop - hop))]) continue; o.Add(10 * Math.Log10(shN.Spec[f][k] + 1e-14)); r.Add(10 * Math.Log10(refN.Spec[f][k] + 1e-14)); }
            flick += Std(o); flickIn += Std(r); fN++;
        }
        Console.WriteLine(string.Format("{0,-36}{1,8:0.0}dB{6,8:0.0}dB{2,8:0.0}dB{3,10:0.0}dB{4,8:0.0}dB{5,10:+0.0;-0.0}dB",
            name, 10 * Math.Log10(nIn / nOut), 10 * Math.Log10(sIn / sOut), lsd / Math.Max(1, lsdN), fIn > 0 ? 10 * Math.Log10(fIn / fOut) : 0, (flick - flickIn) / fN, 10 * Math.Log10(pIn / pOut)));
        double vs = 0, vn = 0; int cs = 0, cn = 0;
        for (int f = 0; f < vads.Count; f++) { int ci = Math.Max(0, Math.Min(s.Length - 1, f * hop)); if (ci < start) continue; if (sp[ci]) { vs += vads[f]; cs++; } else { vn += vads[f]; cn++; } }
        Console.WriteLine("      voice detected: talking " + (100 * vs / Math.Max(1, cs)).ToString("0") + "%, pauses " + (100 * vn / Math.Max(1, cn)).ToString("0") + "%");
    }

    public static string VadInfo = "";
    static double Std(List<double> v) { double m = v.Average(); return Math.Sqrt(v.Average(x => (x - m) * (x - m))); }
    static double Db(float[] x, bool[] mask)
    {
        double s = 0; int c = 0;
        for (int i = 0; i < x.Length; i++) if (mask == null || mask[i]) { s += x[i] * x[i]; c++; }
        return 10 * Math.Log10(s / Math.Max(1, c) + 1e-20);
    }
}
