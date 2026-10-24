using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using StreamLite;
using StreamLite.Audio;

// The mic chain with DeepFilterNet, paced like the live mic (one 10 ms block every 10 ms): RNNoise covers while the
// model loads, GPU <-> CPU switching, no late frames, voice detection, spectra, and back to RNNoise.
class DfnChainTest
{
    const int Fs = 48000, F = 480;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Tts(string text)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            var b = ms.ToArray(); var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f * 2.5f;
            return f;
        }
    }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        Check(DeepFilter.Installed && NoiseEngines.Available().Contains(NoiseEngines.DfnGpu), "DeepFilterNet files found, offered in the engine list");
        var voice = Tts("Testing one two three. Can you hear me clearly over this noisy fan?");
        var rnd = new Random(3);
        int lead = Fs * 4;
        int len = lead + voice.Length + Fs;
        var mix = new float[len]; double lp = 0;
        for (int i = 0; i < len; i++) { lp = 0.97 * lp + 0.03 * (rnd.NextDouble() * 2 - 1); mix[i] = (float)(lp * 0.25); }
        for (int i = 0; i < voice.Length; i++) mix[lead + i] += voice[i];

        var ns = new NoiseSuppressor { UseAi = true, Strength = 0.7f, RawSpectrum = new SpectrumAnalyzer(Fs), NoiseSpectrum = new SpectrumAnalyzer(Fs) };
        foreach (int mode in new[] { 2, 1 })
        {
            ns.Dfn = mode;
            var blk = new float[F]; var sw = Stopwatch.StartNew(); long next = 0;
            int readyAt = -1; float maxVad = 0, pauseVad = 1; double eIn = 0, eOut = 0, worstBlockMs = 0;
            int frames = len / F; int lateBefore = 0;
            for (int f = 0; f < frames; f++)
            {
                while (sw.ElapsedTicks < next) Thread.SpinWait(50);
                next += Stopwatch.Frequency / 100;
                Array.Copy(mix, f * F, blk, 0, F);
                var t0 = sw.Elapsed.TotalMilliseconds;
                ns.Process(blk, F);
                double took = sw.Elapsed.TotalMilliseconds - t0; if (took > 3) Console.WriteLine("    slow block " + f + ": " + took.ToString("0.0") + " ms" + (ns.DfnEngine != null && ns.DfnEngine.Ready ? " (dfn ready)" : " (loading)"));
                worstBlockMs = Math.Max(worstBlockMs, took);
                var d = ns.DfnEngine;
                if (readyAt < 0 && d != null && d.Ready) readyAt = f;
                if (f * F > lead + 8000 && f * F < lead + voice.Length) maxVad = Math.Max(maxVad, ns.SpeechPresence);
                if (f * F > Fs * 2 && f * F < lead - 4800) { pauseVad = Math.Min(pauseVad, ns.SpeechPresence); for (int i = 0; i < F; i++) { eIn += mix[f * F + i] * mix[f * F + i]; eOut += blk[i] * blk[i]; } }
            }
            var dfn = ns.DfnEngine;
            string name = mode == 2 ? "GPU" : "CPU";
            Check(dfn != null && dfn.Gpu == (mode == 2) && dfn.Error == null, name + ": engine running (" + (dfn != null && dfn.Error != null ? dfn.Error : "ok") + ")");
            Check(readyAt >= 0 && readyAt < 300, name + ": ready after " + (readyAt * 10) + " ms (RNNoise covered until then)");
            Check(dfn != null && dfn.Late <= (mode == 2 ? 3 : 0), name + ": late frames " + (dfn == null ? -1 : dfn.Late) + ", model " + (dfn == null ? 0 : dfn.AvgMs).ToString("0.00") + " ms per frame, mic thread worst " + worstBlockMs.ToString("0.00") + " ms");
            Check(maxVad > 0.6 && pauseVad < 0.3, name + ": voice detected while talking (" + maxVad.ToString("0.00") + "), not in the fan-only part (" + pauseVad.ToString("0.00") + ")");
            Check(10 * Math.Log10(eIn / (eOut + 1e-20)) > 25, name + ": fan in pauses turned down " + (10 * Math.Log10(eIn / (eOut + 1e-20))).ToString("0") + " dB");
        }
        var spec = ns.RawSpectrum.Snapshot();
        Check(spec.Max() > -90, "input spectrum for the graph is filled");
        ns.Dfn = 0; ns.Process(new float[F], F);
        Check(ns.DfnEngine == null, "switching back to RNNoise unloads DeepFilterNet");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
