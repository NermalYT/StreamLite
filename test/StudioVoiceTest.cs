// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// Studio voice: a good recording of a voice through simulated cheap mics (headset, laptop, cheap USB, dull lavalier)
// with and without Studio voice. Measured: how far each mic's tone is from the good mic's (1/3-octave long-term
// spectrum, level-matched over 300 Hz - 3 kHz) before and after - Studio voice should make the mics sound alike and
// like a broadcast voice; the treble a mic cuts off coming back; DNSMOS where its model is installed; a plosive pop
// caught while normal talking isn't touched; the delay; the CPU.
// The voice: your own recording from the Microphone page's voice test (voice-sample.wav, never leaves this PC), else
// Windows' speech synthesizer (its voices stop around 11 kHz, so the treble checks then only run on the mics).
// Usage: run.ps1 StudioVoiceTest [wav folder]
class StudioVoiceTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static readonly double[] Bands = { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

    /// <summary>1/3-octave long-term spectrum (dB) of the parts where someone talks.</summary>
    static double[] Ltas(float[] x)
    {
        const int N = 4096; var fft = new Fft(N); var w = new float[N];
        for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
        var frames = new List<double[]>(); var energy = new List<double>();
        for (int at = 0; at + N <= x.Length; at += N / 2)
        {
            var re = new float[N]; var im = new float[N];
            for (int i = 0; i < N; i++) re[i] = x[at + i] * w[i];
            fft.Transform(re, im, false);
            var p = new double[N / 2 + 1]; double e = 0;
            for (int k = 0; k <= N / 2; k++) { p[k] = re[k] * (double)re[k] + im[k] * (double)im[k]; e += p[k]; }
            frames.Add(p); energy.Add(e);
        }
        double thr = energy.OrderBy(e => e).ElementAt(energy.Count * 3 / 10) * 3;   // well above the quiet 30%
        if (Environment.GetEnvironmentVariable("SV_DEBUG") != null) Console.WriteLine("frames " + energy.Count + " thr " + thr + " max " + energy.Max() + " above " + energy.Count(e => e > thr));
        var sum = new double[Bands.Length];
        foreach (var p in frames.Where((p, i) => energy[i] > thr))
            for (int k = 1; k <= N / 2; k++)
            {
                double f = k * (double)Fs / N;
                for (int b = 0; b < Bands.Length; b++) if (f >= Bands[b] / 1.1225 && f < Bands[b] * 1.1225) sum[b] += p[k];
            }
        return sum.Select(s => 10 * Math.Log10(s + 1e-20)).ToArray();
    }

    /// <summary>How different two tones are: RMS dB difference over 100 Hz - 16 kHz, after matching 300 Hz - 3 kHz.</summary>
    static double Dist(double[] a, double[] b, double maxHz = 16000)
    {
        double off = 0; int c = 0;
        for (int i = 0; i < Bands.Length; i++) if (Bands[i] >= 315 && Bands[i] <= 3150) { off += a[i] - b[i]; c++; }
        off /= c; double s = 0; int n = 0;
        for (int i = 0; i < Bands.Length; i++) if (Bands[i] <= maxHz) { double d = a[i] - b[i] - off; s += d * d; n++; }
        return Math.Sqrt(s / n);
    }

    sealed class Mic { public string Name; public EqFilter[] F; public double LowPass; }

    static readonly Mic[] Mics =
    {
        new Mic { Name = "headset (thin, stops at 7 kHz)", LowPass = 7000, F = new[] { new EqFilter { Type = "HP", Freq = 220, Q = 0.7 }, new EqFilter { Type = "PK", Freq = 2600, Gain = 4, Q = 1.2 } } },
        new Mic { Name = "laptop (boxy, harsh, stops at 10 kHz)", LowPass = 10000, F = new[] { new EqFilter { Type = "HP", Freq = 320, Q = 0.7 }, new EqFilter { Type = "PK", Freq = 500, Gain = 5, Q = 1.4 }, new EqFilter { Type = "PK", Freq = 4000, Gain = 5, Q = 2 } } },
        new Mic { Name = "cheap USB (harsh peaks, stops at 13 kHz)", LowPass = 13000, F = new[] { new EqFilter { Type = "LS", Freq = 160, Gain = -5, Q = 0.7 }, new EqFilter { Type = "PK", Freq = 3000, Gain = 6, Q = 1.5 }, new EqFilter { Type = "PK", Freq = 6500, Gain = 4, Q = 2 } } },
        new Mic { Name = "dull lavalier (muffled)", LowPass = 9000, F = new[] { new EqFilter { Type = "HS", Freq = 3000, Gain = -7, Q = 0.7 }, new EqFilter { Type = "PK", Freq = 250, Gain = 3, Q = 1 } } },
    };

    static float[] Through(Mic m, float[] x)
    {
        var y = (float[])x.Clone();
        foreach (var f in m.F) { var q = Biquad.Create(f, Fs); for (int i = 0; i < y.Length; i++) y[i] = q.Process(y[i]); }
        // A steep cut (8th order) where the mic stops.
        for (int s = 0; s < 4; s++) { var q = Biquad.LowPass(m.LowPass, Fs); for (int i = 0; i < y.Length; i++) y[i] = q.Process(y[i]); }
        return y;
    }

    /// <summary>Studio voice on a clip, twice through (the first pass learns the mic), the second pass returned.</summary>
    static float[] Studio(float[] x, out StudioVoice sv, float strength = 0.75f)
    {
        sv = new StudioVoice { Strength = strength };
        var warm = (float[])x.Clone();
        for (int at = 0; at < warm.Length; at += 480) { int n = Math.Min(480, warm.Length - at); var b = new float[n]; Array.Copy(warm, at, b, 0, n); sv.Process(b, n); }
        var y = (float[])x.Clone();
        for (int at = 0; at < y.Length; at += 480) { int n = Math.Min(480, y.Length - at); var b = new float[n]; Array.Copy(y, at, b, 0, n); sv.Process(b, n); Array.Copy(b, 0, y, at, n); }
        return y;
    }


    /// <summary>DeepFilterNet loaded (it takes a moment - offline the clip would otherwise run on RNNoise meanwhile), then 2 s of the clip so everything has adapted, like a mic that's been on.</summary>
    static void Warm(VoiceChain c, float[] x)
    {
        var blk = new float[480];
        for (int k = 0; k < 4000 && c.Suppressor.Dfn != 0; k++)
        {
            Array.Clear(blk, 0, 480); c.Process(blk, 480, false);
            var d = c.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
            System.Threading.Thread.Sleep(2);
        }
        for (int at = 0; at + 480 <= Math.Min(x.Length, 48000 * 2); at += 480) { Array.Copy(x, at, blk, 0, 480); c.Process(blk, 480, false); }
    }

    static float[] Clean(float[] x)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var s = new AppSettings { NoiseRemoval = true, NsEngine = NoiseEngines.DfnGpu, NsRealtime = false, Gate = false, Compressor = false, DeEsser = false, AutoLevel = false, Limiter = false, StudioVoice = false, MicEq = new EqProfile { Enabled = false } };
        var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s);
        Warm(c, x);
        var y = (float[])x.Clone();
        for (int at = 0; at + 480 <= y.Length; at += 480) { var b = new float[480]; Array.Copy(y, at, b, 0, 480); c.Process(b, 480, false); Array.Copy(b, 0, y, at, 480); }
        c.Suppressor.ReleaseEngine();
        // DeepFilterNet's own delay: skip its first half second.
        var o = new float[y.Length - Fs / 2]; Array.Copy(y, Fs / 2, o, 0, o.Length);
        return o;
    }

    static float[] Levelled(float[] x)
    {
        double s = 0; int n = 0; for (int i = 0; i < x.Length; i++) { s += x[i] * (double)x[i]; n++; }
        double g = Math.Pow(10, -22 / 20.0) / Math.Sqrt(s / n + 1e-20);
        return x.Select(v => (float)Math.Max(-1, Math.Min(1, v * g))).ToArray();
    }

    static double Band(float[] x, double lo, double hi) { var l = Ltas(x); double s = 0; for (int i = 0; i < Bands.Length; i++) if (Bands[i] >= lo && Bands[i] <= hi) s += Math.Pow(10, l[i] / 10); return 10 * Math.Log10(s + 1e-20); }

    static void Main(string[] args)
    {
        // Your own recording (CaptureRaw mic: test\shots\live\mic-*-mono48.wav, stays on this PC), cleaned by
        // DeepFilterNet first - Studio voice comes after the noise remover.
        string liveDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\live"));
        string own = Directory.Exists(liveDir) ? Directory.GetFiles(liveDir, "mic-*-mono48.wav").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
        float[] voice; bool fullBand;
        if (own != null) { voice = Clean(DnsmosRun.ReadWav(own)); fullBand = true; Console.WriteLine("Voice: your recording (" + (voice.Length / (double)Fs).ToString("0.0") + " s, stays on this PC), noise removed"); }
        else
        {
            fullBand = false;
            using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.SetOutputToAudioStream(ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(Fs, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                synth.Speak("Welcome back to the show. Today we're talking about microphones, and why some of them sound so much better than others. Sit back, relax, and enjoy the episode.");
                var b = ms.ToArray(); voice = new float[b.Length / 2];
                for (int i = 0; i < voice.Length; i++) voice[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            }
            Console.WriteLine("Voice: Windows' speech synthesizer (no recording of yours on this PC)");
        }
        voice = Levelled(voice);
        string outDir = args.Length > 0 ? args[0] : null;
        if (outDir != null) Directory.CreateDirectory(outDir);

        StudioVoice sv;
        var refOut = Studio(voice, out sv);
        var refL = Ltas(voice); var refOutL = Ltas(refOut);
        if (Environment.GetEnvironmentVariable("SV_DEBUG") != null) { Console.WriteLine(string.Join(" ", refL.Select(v => v.ToString("0")))); Console.WriteLine(string.Join(" ", Ltas(Through(Mics[0], voice)).Select(v => v.ToString("0")))); Console.WriteLine("peak " + voice.Max() + " len " + voice.Length); }
        Console.WriteLine("The good mic itself through Studio voice: " + sv.Describe() + "  (changed by " + Dist(refOutL, refL).ToString("0.0") + " dB)");

        Console.WriteLine("Cheap mics - how far from the good mic's tone (RMS dB over 100 Hz - " + (fullBand ? "16 kHz" : "8 kHz") + "):");
        double maxHz = fullBand ? 16000 : 8000;
        DnsmosRun.Scorer scorer = null;
        try { scorer = new DnsmosRun.Scorer(); } catch { Console.WriteLine("  (DNSMOS not installed: test\\dnsmos)"); }
        double sumBefore = 0, sumAfter = 0;
        foreach (var m in Mics)
        {
            var raw = Through(m, voice);
            var outp = Studio(raw, out sv);
            double before = Dist(Ltas(raw), refL, maxHz), after = Dist(Ltas(outp), refOutL, maxHz);
            sumBefore += before; sumAfter += after;
            string treble = "";
            if (fullBand)
            {
                double lost = Band(voice, m.LowPass * 1.25, 16000) - Band(raw, m.LowPass * 1.25, 16000);
                double back = Band(outp, m.LowPass * 1.25, 16000) - Band(raw, m.LowPass * 1.25, 16000);
                treble = ", treble above " + (m.LowPass * 1.25 / 1000).ToString("0.#") + " kHz: " + lost.ToString("0") + " dB lost, " + back.ToString("0") + " dB back";
            }
            string mos = "";
            if (scorer != null)
            {
                var a = scorer.Score(Levelled(raw)); var b = scorer.Score(Levelled(outp));
                mos = ", DNSMOS SIG " + a[0].ToString("0.00") + " -> " + b[0].ToString("0.00") + ", OVRL " + a[2].ToString("0.00") + " -> " + b[2].ToString("0.00");
            }
            Console.WriteLine("  " + m.Name + ": " + before.ToString("0.0") + " dB -> " + after.ToString("0.0") + " dB" + treble + mos);
            Console.WriteLine("      " + sv.Describe());
            if (outDir != null)
            {
                string tag = m.Name.Split(' ')[0];
                Write(Path.Combine(outDir, tag + "-before.wav"), raw); Write(Path.Combine(outDir, tag + "-studio.wav"), outp);
            }
            if (fullBand) Check(after < before * 0.55, m.Name + ": at least 45% closer to the good mic");
        }
        if (outDir != null) { Write(Path.Combine(outDir, "good-mic.wav"), voice); Write(Path.Combine(outDir, "good-mic-studio.wav"), refOut); }
        Console.WriteLine("  average: " + (sumBefore / Mics.Length).ToString("0.0") + " dB -> " + (sumAfter / Mics.Length).ToString("0.0") + " dB");
        Check(sumAfter < sumBefore * 0.6, "the cheap mics sound much more like the good one (average " + (sumBefore / Mics.Length).ToString("0.0") + " -> " + (sumAfter / Mics.Length).ToString("0.0") + " dB)");
        if (scorer != null) scorer.Dispose();

        Console.WriteLine("Treble rebuilt:");
        {
            var raw = Through(Mics[0], voice); Studio(raw, out sv);
            Check(sv.CutoffHzF > 5000 && sv.CutoffHzF < 8500, "the headset's cut found at " + (sv.CutoffHzF / 1000).ToString("0.0") + " kHz (it stops at 7 kHz)");
            if (fullBand) { Studio(voice, out sv); Check(sv.CutoffHzF == 0, "the good mic: nothing missing, nothing rebuilt"); }
        }

        Console.WriteLine("Plosives:");
        {
            var x = (float[])voice.Clone();
            var popAt = new List<int>();
            var rnd = new Random(3);
            for (int at = Fs; at + Fs / 10 < x.Length; at += 2 * Fs)
            {
                popAt.Add(at);
                double f0 = 40 + rnd.Next(40);
                for (int i = 0; i < Fs * 6 / 100; i++) { double t = i / (double)Fs; x[at + i] += (float)(0.6 * Math.Sin(2 * Math.PI * f0 * t) * Math.Exp(-t / 0.02) * (1 - Math.Exp(-t / 0.002))); }
            }
            var y = Studio(x, out sv);
            Func<float[], int, int, double> lowE = (s, a, len) => { var q1 = Biquad.LowPass(150, Fs); var q2 = Biquad.LowPass(150, Fs); double e = 0; for (int i = a; i < a + len && i < s.Length; i++) { double v = q2.Process(q1.Process(s[i])); e += v * v; } return e; };
            double cut = 0; foreach (int a in popAt) cut += 10 * Math.Log10(lowE(x, a, Fs / 15) / (lowE(y, a + StudioVoice.N, Fs / 15) + 1e-20));
            cut /= popAt.Count;
            Check(cut > 8, "pops' rumble taken down " + cut.ToString("0") + " dB (" + popAt.Count + " pops)");
            Studio(voice, out sv);
            int perPass = sv.Pops / 2, secs = voice.Length / Fs;
            Check(perPass <= Math.Max(1, secs / 6), "normal talking: at most one catch per 6 s (" + perPass + " in " + secs + " s - a real 'p' or 'b' into the mic counts too)");
        }

        Console.WriteLine("Delay and cost:");
        {
            var s2 = new StudioVoice();
            var imp = new float[4800]; imp[100] = 1;
            for (int at = 0; at < imp.Length; at += 480) { var b = new float[480]; Array.Copy(imp, at, b, 0, 480); s2.Process(b, 480); Array.Copy(b, 0, imp, at, 480); }
            int peak = 0; for (int i = 1; i < imp.Length; i++) if (Math.Abs(imp[i]) > Math.Abs(imp[peak])) peak = i;
            Check(peak - 100 == StudioVoice.N, "delay " + (peak - 100) + " samples = " + StudioVoice.DelayMs.ToString("0.0") + " ms");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var big = new float[Fs * 10]; Array.Copy(voice, big, Math.Min(voice.Length, big.Length));
            Studio(big, out sv);
            double perSec = sw.Elapsed.TotalMilliseconds / 20;
            Check(perSec < 15, "costs " + (perSec / 10).ToString("0.00") + "% of one core (" + perSec.ToString("0.0") + " ms per second of sound)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    static void Write(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + x.Length * 2); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
        }
    }
}
