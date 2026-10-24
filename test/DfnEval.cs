using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite.Audio;

// RNNoise vs DeepFilterNet 3 (CPU and GPU) on TTS speech + noise. Black-box: the output is aligned to the clean voice,
// then: SI-SDR (overall quality, higher = better), noise left in pauses, voice level change, delay, speed, memory.
// Usage: DfnEval [wavDir]
class DfnEval
{
    const int Fs = 48000, F = 480;

    static float[] Tts(string text, int voice)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            var vs = synth.GetInstalledVoices().Where(v => v.Enabled).ToList();
            if (vs.Count > 0) synth.SelectVoice(vs[voice % vs.Count].VoiceInfo.Name);
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Rate = 1;
            synth.Speak(text);
            var b = ms.ToArray();
            var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            return f;
        }
    }

    static float[] Room(int len, Random rnd)
    {
        var noise = new float[len];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < len; i++)
        {
            double w = rnd.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11; b6 = w * 0.115926;
            double t = (double)i / Fs;
            double tones = 0.002 * Math.Sin(2 * Math.PI * 79 * t) + 0.0015 * Math.Sin(2 * Math.PI * 108 * t) + 0.001 * Math.Sin(2 * Math.PI * 1014 * t);
            noise[i] = (float)(pink * 0.03 + tones);
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

    static float[] Add(params float[][] xs) { var o = new float[xs[0].Length]; foreach (var x in xs) for (int i = 0; i < o.Length; i++) o[i] += x[i]; return o; }
    static float[] Scale(float[] x, double g) { return x.Select(v => (float)(v * g)).ToArray(); }

    sealed class Score { public double SiSdr, Pauses, VoiceDb; public int Delay; }

    static Score Measure(float[] clean, float[] noise, float[] y, bool[] speech, int fixedDelay = -1)
    {
        int best = 0; double bv = double.MinValue;
        if (fixedDelay >= 0) best = fixedDelay;
        else
            for (int d = 0; d <= 4000; d += 1)
            {
                double s = 0;
                for (int i = Fs; i < clean.Length - 4000; i += 3) s += clean[i] * y[i + d];
                if (s > bv) { bv = s; best = d; }
            }
        int n = clean.Length - 4000;
        double ss = 0, sy = 0;
        for (int i = 0; i < n; i++) { ss += clean[i] * clean[i]; sy += clean[i] * y[i + best]; }
        double a = sy / ss, er = 0;
        for (int i = 0; i < n; i++) { double r = y[i + best] - a * clean[i]; er += r * r; }
        double pn = 0, po = 0; int pc = 0;
        for (int i = 0; i < n; i++) if (!speech[i]) { pn += noise[i] * noise[i]; po += y[i + best] * y[i + best]; pc++; }
        return new Score { SiSdr = 10 * Math.Log10(a * a * ss / er), Pauses = 10 * Math.Log10(pn / (po + 1e-20)), VoiceDb = 20 * Math.Log10(Math.Abs(a)), Delay = best };
    }

    static float[] RunRnnoise(float[] x, float strength)
    {
        var ns = new NoiseSuppressor { UseAi = true, Strength = strength };
        var o = (float[])x.Clone(); var b = new float[F];
        for (int i = 0; i + F <= o.Length; i += F) { Array.Copy(o, i, b, 0, F); ns.Process(b, F); Array.Copy(b, 0, o, i, F); }
        return o;
    }

    static float[] RunDfn(DeepFilter d, float[] x, float lim, out int late)
    {
        d.AttenLimitDb = lim;
        var o = new float[x.Length]; var b = new float[F]; var y = new float[F];
        int before = d.Late;
        for (int i = 0; i + F <= x.Length; i += F)
        {
            Array.Copy(x, i, b, 0, F);
            d.Process(b, y);
            d.WaitIdle(2000);
            Array.Copy(y, 0, o, i, F);
        }
        late = d.Late - before;
        return o;
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

    static long PrivateMb() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / 1048576; }
    static long WorkingMb() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64 / 1048576; }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string wavDir = args.Length > 0 ? args[0] : null;
        var rnd = new Random(11);
        var voice = Tts("So yesterday we were playing ranked and honestly the whole match was insane. " +
                        "She said she'd push the left side, but six of them showed up at the same time. " +
                        "Can you hear me okay? Is my mic still sounding weird, or is it fixed now? " +
                        "Seriously, the sound should be crisp, clear, and smooth without any hissing.", 0);
        int lead = Fs * 2, len = lead + voice.Length + Fs * 2;
        var clean = new float[len];
        double e = 0; int c = 0; foreach (var v in voice) if (Math.Abs(v) > 0.01) { e += v * v; c++; }
        float gain = (float)(0.1 / Math.Sqrt(e / Math.Max(1, c)));
        for (int i = 0; i < voice.Length; i++) clean[lead + i] = voice[i] * gain;
        var speech = new bool[len];
        for (int b = 0; b + F <= len; b += F)
        {
            double en = 0; for (int i = b; i < b + F; i++) en += clean[i] * clean[i];
            bool s = 10 * Math.Log10(en / F + 1e-20) > -50;
            // pauses = at least 150 ms away from speech
            for (int i = b; i < b + F; i++) speech[i] = s;
        }
        var sp2 = (bool[])speech.Clone();
        for (int i = 0; i < len; i++) if (speech[i]) for (int j = Math.Max(0, i - 7200); j < Math.Min(len, i + 7200); j += 480) sp2[j] = true;
        speech = sp2;

        var tv = Tts("And in other news tonight, the weather will stay mild with light rain moving in from the west by the weekend, and temperatures around twenty degrees.", 1);
        var tvFull = new float[len]; for (int i = 0; i < Math.Min(tv.Length, len - Fs / 2); i++) tvFull[Fs / 2 + i] = tv[i] * gain * 0.25f;
        var scenes = new List<Tuple<string, float[]>>
        {
            Tuple.Create("room hiss + hum (-45 dB)", Room(len, rnd)),
            Tuple.Create("keyboard clicks + room", Add(Keyboard(len, rnd), Room(len, rnd))),
            Tuple.Create("loud fan (-30 dB)", Fan(len, rnd)),
            Tuple.Create("very loud fan (-22 dB)", Scale(Fan(len, rnd), 2.5)),
            Tuple.Create("TV talking behind you (-12 dB)", Add(tvFull, Room(len, rnd))),
        };

        long memStart = PrivateMb();
        Console.WriteLine("process private memory at start: " + memStart + " MB");
        var engines = new List<Tuple<string, DeepFilter>>();
        foreach (bool gpu in new[] { false, true })
        {
            var sw = Stopwatch.StartNew();
            var d = new DeepFilter(gpu);
            while (!d.Ready && d.Error == null && sw.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(10);
            Console.WriteLine("DeepFilterNet " + (gpu ? "GPU" : "CPU") + ": " + (d.Ready ? "ready in " + sw.ElapsedMilliseconds + " ms" : "FAILED " + d.Error) +
                "; process private " + PrivateMb() + " MB, working set " + WorkingMb() + " MB");
            if (d.Ready) engines.Add(Tuple.Create(gpu ? "DeepFilterNet 3 GPU" : "DeepFilterNet 3 CPU", d));
        }

        foreach (var sc in scenes)
        {
            var noise = sc.Item2;
            var mix = Add(clean, noise);
            Console.WriteLine();
            Console.WriteLine("== " + sc.Item1);
            Console.WriteLine(string.Format("{0,-34}{1,9}{2,12}{3,10}{4,8}{5,6}", "engine", "SI-SDR", "pauses -dB", "voice dB", "delay", "late"));
            var inS = Measure(clean, noise, mix, speech, 0);
            Console.WriteLine(string.Format("{0,-34}{1,9:0.0}{2,12:0.0}{3,10:0.0}{4,8}{5,6}", "input (no processing)", inS.SiSdr, inS.Pauses, inS.VoiceDb, "", ""));
            foreach (var st in new[] { 0.5f, 0.7f })
            {
                var y = RunRnnoise(mix, st); var s = Measure(clean, noise, y, speech);
                Console.WriteLine(string.Format("{0,-34}{1,9:0.0}{2,12:0.0}{3,10:0.0}{4,8}{5,6}", "RNNoise strength " + (int)(st * 100), s.SiSdr, s.Pauses, s.VoiceDb, s.Delay, ""));
                if (wavDir != null && st == 0.5f) { Directory.CreateDirectory(wavDir); WriteWav(Path.Combine(wavDir, sc.Item1.Split(' ')[0] + "-rnnoise.wav"), y); WriteWav(Path.Combine(wavDir, sc.Item1.Split(' ')[0] + "-input.wav"), mix); }
            }
            foreach (var en in engines)
                foreach (var clean2 in new[] { 0f, 15f, 30f })
                {
                    int late; float lim = 100f;
                    en.Item2.CleanupDb = clean2;
                    var y = RunDfn(en.Item2, mix, lim, out late); var s = Measure(clean, noise, y, speech);
                    Console.WriteLine(string.Format("{0,-34}{1,9:0.0}{2,12:0.0}{3,10:0.0}{4,8}{5,6}", en.Item1 + " +" + clean2 + " dB pauses", s.SiSdr, s.Pauses, s.VoiceDb, s.Delay, late));
                    if (wavDir != null && clean2 == 15f) WriteWav(Path.Combine(wavDir, sc.Item1.Split(' ')[0] + "-dfn-" + (en.Item2.Gpu ? "gpu" : "cpu") + ".wav"), y);
                }
        }
        Console.WriteLine();
        foreach (var en in engines)
            Console.WriteLine(en.Item1 + ": " + en.Item2.AvgMs.ToString("0.00") + " ms per 10 ms frame on average (max " + en.Item2.MaxMs.ToString("0.0") + " ms), " +
                (en.Item2.Gpu ? "" : (en.Item2.AvgMs / 10 * 100).ToString("0.0") + "% of one core, ") + "buffer " + en.Item2.Buffer + " frame(s)");
        Console.WriteLine("process private memory at end: " + PrivateMb() + " MB, working set " + WorkingMb() + " MB");
        foreach (var en in engines) en.Item2.Dispose();
    }
}
