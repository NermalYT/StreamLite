// Realtime noise removal vs each noise remover's own path, on the same recording: spoken callouts (Windows' voice)
// over a fan, with pauses and keyboard clicks. Per engine and mode: the delay (measured), how much fan noise is
// removed in pauses, how much click noise, how intact the voice stays (signal-to-distortion), and how much of each
// word's first 30 ms is kept. Usage: run.ps1 NsRealtimeTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite;
using StreamLite.Audio;

class NsRealtimeTest
{
    const int Fs = 48000;
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

    static double Db(double x) { return 10 * Math.Log10(x + 1e-20); }
    static double E(float[] x, int a, int b) { double s = 0; for (int i = Math.Max(0, a); i < Math.Min(x.Length, b); i++) s += x[i] * (double)x[i]; return s; }

    sealed class Result { public string Name; public double DelayMs, FanDb, ClickDb, Sdr, OnsetDb; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        // Lead-in (fan only, the removers learn it), callouts with pauses, a stretch of keyboard clicks, more callouts.
        var parts = new[] { "Enemy behind the wall, push left.", "I need ammo, cover me.", "Nice shot, let's go again." };
        var voices = parts.Select(Tts).ToList();
        int lead = 3 * Fs, gap = Fs, clicksLen = 2 * Fs;
        int len = lead + voices.Sum(v => v.Length) + gap * 3 + clicksLen + Fs;
        var clean = new float[len]; var mix = new float[len];
        var speech = new List<int[]>(); int pos = lead;
        int clickStart = 0;
        for (int p = 0; p < voices.Count; p++)
        {
            Array.Copy(voices[p], 0, clean, pos, voices[p].Length); speech.Add(new[] { pos, pos + voices[p].Length }); pos += voices[p].Length + gap;
            if (p == 0) { clickStart = pos; pos += clicksLen + gap; }
        }
        var rnd = new Random(7); double lp = 0, lp2 = 0;
        for (int i = 0; i < len; i++)
        {
            lp = 0.97 * lp + 0.03 * (rnd.NextDouble() * 2 - 1); lp2 = 0.6 * lp2 + 0.4 * lp;
            mix[i] = clean[i] + (float)(lp2 * 0.25);
        }
        // Keyboard: a click every 120 ms (a 3 ms burst, broadband, fast decay).
        for (int c = clickStart; c + 400 < clickStart + clicksLen; c += 5760)
            for (int i = 0; i < 400; i++) mix[c + i] += (float)((rnd.NextDouble() * 2 - 1) * 0.35 * Math.Exp(-i / 72.0));
        int fanA = Fs, fanB = lead - Fs / 4;   // a fan-only stretch after the removers have learned it

        // Word starts in the clean voice: 5 ms frames rising from quiet to clearly speaking.
        var onsets = new List<int>();
        double peak = 0; for (int i = 0; i < len; i += 240) peak = Math.Max(peak, E(clean, i, i + 240));
        bool quiet = true;
        for (int i = lead; i + 240 < len; i += 240)
        {
            double e = E(clean, i, i + 240);
            if (quiet && e > peak * 1e-2) { onsets.Add(i); quiet = false; }
            else if (!quiet && e < peak * 1e-5) quiet = true;
        }

        var results = new List<Result>();
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai, NoiseEngines.DfnCpu })
            foreach (bool realtime in new[] { false, true })
            {
                var ns = new NoiseSuppressor { UseAi = engine != NoiseEngines.Classic, Dfn = engine == NoiseEngines.DfnCpu ? 1 : 0, Strength = 0.7f, Realtime = realtime, Synchronous = true };
                var blk = new float[144];
                // DeepFilterNet loads in the background: wait for it on fan noise.
                for (int k = 0; k < 4000 && ns.Dfn != 0; k++)
                {
                    Array.Copy(mix, (k * 144) % (lead - 144), blk, 0, 144); ns.Process(blk, 144);
                    var d = ns.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
                    System.Threading.Thread.Sleep(2);
                }
                var y = new float[len];
                for (int i = 0; i + 144 <= len; i += 144) { Array.Copy(mix, i, blk, 0, 144); ns.Process(blk, 144); Array.Copy(blk, 0, y, i, 144); }
                // Delay: where the output lines up best with the clean voice (first callout).
                int best = 0; double bestC = double.MinValue;
                for (int lag = 0; lag < 4000; lag += 4)
                {
                    double c = 0; for (int i = speech[0][0]; i < speech[0][1]; i += 2) c += clean[i] * (double)y[i + lag];
                    if (c > bestC) { bestC = c; best = lag; }
                }
                for (int lag = Math.Max(0, best - 4); lag < best + 4; lag++)
                {
                    double c = 0; for (int i = speech[0][0]; i < speech[0][1]; i++) c += clean[i] * (double)y[i + lag];
                    if (c > bestC) { bestC = c; best = lag; }
                }
                Func<int, int, double> Ey = (a, b) => E(y, a + best, b + best);
                var r = new Result { Name = engine + (realtime ? " realtime" : ""), DelayMs = best * 1000.0 / Fs };
                r.FanDb = Db(E(mix, fanA, fanB)) - Db(Ey(fanA, fanB));
                r.ClickDb = Db(E(mix, clickStart, clickStart + clicksLen)) - Db(Ey(clickStart, clickStart + clicksLen));
                double sig = 0, err = 0;
                foreach (var s in speech) for (int i = s[0]; i < s[1]; i++) { double v = clean[i], d = y[i + best] - v; sig += v * v; err += d * d; }
                r.Sdr = Db(sig) - Db(err);
                double on = 0; foreach (var o in onsets) on += Db(Ey(o, o + 1440)) - Db(E(clean, o, o + 1440));
                r.OnsetDb = on / Math.Max(1, onsets.Count);
                results.Add(r);
                Console.WriteLine("  " + r.Name.PadRight(18) + "delay " + r.DelayMs.ToString("0.0").PadLeft(5) + " ms   fan -" + r.FanDb.ToString("0.0").PadLeft(4) + " dB   clicks -" + r.ClickDb.ToString("0.0").PadLeft(4) +
                    " dB   voice SDR " + r.Sdr.ToString("0.0").PadLeft(5) + " dB   word starts " + r.OnsetDb.ToString("+0.0;-0.0") + " dB" + (realtime ? "   onset hops " + ns.RealtimeFilter.OnsetHops : ""));
                ns.ReleaseEngine();
            }
        Console.WriteLine("  (" + onsets.Count + " word starts)");
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai, NoiseEngines.DfnCpu })
        {
            var n = results.First(r => r.Name == engine); var rt = results.First(r => r.Name == engine + " realtime");
            Check(rt.DelayMs <= 3.0, engine + " realtime: " + rt.DelayMs.ToString("0.0") + " ms delay (its own path: " + n.DelayMs.ToString("0.0") + " ms)");
            Check(rt.FanDb >= Math.Min(n.FanDb - 4, 45), engine + " realtime: fan noise down " + rt.FanDb.ToString("0") + " dB (its own path " + n.FanDb.ToString("0") + " dB)");
            Check(rt.Sdr >= n.Sdr - 2, engine + " realtime: voice as intact (SDR " + rt.Sdr.ToString("0.0") + " vs " + n.Sdr.ToString("0.0") + " dB)");
            Check(rt.OnsetDb >= -3, engine + " realtime: word starts kept (" + rt.OnsetDb.ToString("+0.0;-0.0") + " dB in their first 30 ms; its own path " + n.OnsetDb.ToString("+0.0;-0.0") + " dB)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
