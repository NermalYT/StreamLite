// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite;
using StreamLite.Audio;

// A voice next to a wind turbine (a loud machine in the room): low rumble, the blades' whoosh pulsing about once a
// second, gearbox tones, generator hum - as loud as the voice, and louder. Through the built-in voice chain (DeepFilterNet
// on the graphics card, the default chain) at several strengths, with the sound learned (Record a sound to remove) and
// without: DNSMOS, how much of the machine is left in pauses, and how intact the voice stays (SI-SDR).
// Usage: run.ps1 WindTest
class WindTest
{
    const int Fs = 48000;

    static float[] Tts(string text, string voice)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            synth.SelectVoice(voice);
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            var b = ms.ToArray(); var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            return f;
        }
    }

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = Math.Max(0, a); i < Math.Min(x.Length, b); i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    static double Level(float[] x) { var l = new List<double>(); for (int i = 0; i + 2400 <= x.Length; i += 2400) l.Add(Rms(x, i, i + 2400)); l.Sort(); return l[l.Count * 9 / 10]; }
    static void Scale(float[] x, double g) { for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * g); }

    internal static float[] Turbine(int n, Random rnd)
    {
        var x = new float[n]; double a = 0, b = 0, c = 0, rum = 0, rum2 = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Fs, w = rnd.NextDouble() * 2 - 1;
            // The blades' whoosh: broadband, mostly 200-1500 Hz, swelling each time a blade passes (0.85 Hz).
            a = 0.9 * a + 0.1 * w; b = 0.7 * b + 0.3 * a; c = b - 0.95 * c * 0 + 0;   // pink-ish, gently low-passed
            double swell = 0.55 + 0.45 * Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 0.85 * t)), 2);
            double whoosh = (b * 3.0 - a * 0.8) * swell;
            // Low rumble (structure-borne, 25-70 Hz) and generator hum (60 Hz + 120 Hz).
            rum = 0.995 * rum + 0.005 * w; rum2 = 0.98 * rum2 + 0.02 * rum;
            double low = rum2 * 40 + 0.12 * Math.Sin(2 * Math.PI * 60 * t) + 0.06 * Math.Sin(2 * Math.PI * 120 * t);
            // Gearbox tones, wobbling a little with the load.
            double gear = 0.05 * Math.Sin(2 * Math.PI * (310 + 4 * Math.Sin(2 * Math.PI * 0.2 * t)) * t) + 0.03 * Math.Sin(2 * Math.PI * 620 * t);
            x[i] = (float)(whoosh + low + gear);
        }
        return x;
    }

    static float[] Run(AppSettings s, float[] x, float[] learnW)
    {
        var chain = new VoiceChain(); chain.Suppressor.Synchronous = true; chain.Configure(s);
        if (learnW != null) { chain.Suppressor.Print.Use(learnW); chain.Suppressor.Print.Enabled = true; }
        var blk = new float[144];
        for (int k = 0; k < 4000 && chain.Suppressor.Dfn != 0; k++)
        {
            Array.Clear(blk, 0, 144); chain.Process(blk, 144, false);
            var d = chain.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
            System.Threading.Thread.Sleep(2);
        }
        for (int i = 0; i + 144 <= Fs * 2; i += 144) { Array.Copy(x, i, blk, 0, 144); chain.Process(blk, 144, false); }   // adapted, like a mic that's been on
        var y = new float[x.Length];
        for (int i = 0; i + 144 <= x.Length; i += 144) { Array.Copy(x, i, blk, 0, 144); chain.Process(blk, 144, false); Array.Copy(blk, 0, y, i, 144); }
        chain.Suppressor.ReleaseEngine();
        return y;
    }

    static float[] Levelled(float[] x) { var y = (float[])x.Clone(); double l = Level(y); if (l > 1e-6) Scale(y, Math.Pow(10, -22 / 20.0) / l); for (int i = 0; i < y.Length; i++) y[i] = Math.Max(-1f, Math.Min(1f, y[i])); return y; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var rnd = new Random(9);
        var lines = new[] { "Okay so tomorrow we start at seven, don't be late.", "Rotate to B, they're stacking the site with four.", "Honestly that was the best round all night.", "Give me a second, I'm grabbing a drink." };
        var clean = new float[14 * Fs]; var spans = new List<int[]>(); int pos = Fs / 2, li = 0;
        while (pos < clean.Length - Fs) { var s = Tts(lines[li++ % lines.Length], "Microsoft David Desktop"); int len = Math.Min(s.Length, clean.Length - pos); Array.Copy(s, 0, clean, pos, len); spans.Add(new[] { pos, pos + len }); pos += s.Length + Fs / 2; }
        Scale(clean, Math.Pow(10, -30 / 20.0) / Level(clean));
        var machine = Turbine(24 * Fs, rnd);
        var basis = AppSettings.Load();
        basis.NsRealtime = false; basis.NoiseRemoval = true; basis.NsEngine = NoiseEngines.DfnGpu; basis.LowVram = false;
        // The sound learned from 8 s of it alone, after DeepFilterNet (as Record a sound to remove does).
        using (var sc = new DnsmosRun.Scorer())
            foreach (double snr in new[] { 0.0, -5.0 })
            {
                var nz = (float[])machine.Clone(); Scale(nz, Math.Pow(10, (-30 - snr) / 20.0) / Rms(nz, 0, nz.Length));
                var mix = new float[clean.Length]; for (int i = 0; i < mix.Length; i++) mix[i] = clean[i] + nz[8 * Fs + i];
                var rec = new float[8 * Fs]; Array.Copy(nz, rec, rec.Length);
                var sRec = AppSettings.Load(); sRec.NsRealtime = false; sRec.NoiseRemoval = true; sRec.NsEngine = NoiseEngines.DfnGpu; sRec.Gate = false; sRec.Compressor = false; sRec.AutoLevel = false; sRec.DeEsser = false; sRec.MicEq = new EqProfile { Enabled = false };
                var recAfter = Run(sRec, rec, null);
                var learned = NoisePrint.Learn(recAfter, recAfter.Length, rec);
                Console.WriteLine("Wind turbine " + (snr >= 0 ? "as loud as" : (-snr) + " dB louder than") + " the voice (input DNSMOS " + string.Join(" / ", sc.Score(Levelled(mix)).Select(v => v.ToString("0.00"))) + "); learned: " + learned.Character + (learned.AlreadyGone ? " (nothing left after DeepFilterNet)" : ", " + learned.AfterDb.ToString("0") + " dBFS left after DeepFilterNet"));
                foreach (var cfg in new[] { "54", "70", "85", "100", "70+learned", "100+learned" })
                {
                    var s = AppSettings.Load(); s.NsRealtime = false; s.NoiseRemoval = true; s.NsEngine = NoiseEngines.DfnGpu; s.LowVram = false;
                    s.Strength = int.Parse(cfg.Split('+')[0]);
                    var y = Run(s, mix, cfg.Contains("learned") && !learned.AlreadyGone ? learned.W : null);
                    // Delay against the clean voice, then: what's left in pauses, voice SI-SDR, DNSMOS.
                    int best = 0; double bc = double.MinValue; var sp = spans[0];
                    for (int lag = 0; lag < 6000; lag += 2) { double cc = 0; for (int i = sp[0]; i < sp[1] && i + lag < y.Length; i += 2) cc += clean[i] * (double)y[i + lag]; if (cc > bc) { bc = cc; best = lag; } }
                    double en = 0, ein = 0;
                    for (int i = Fs; i + best < y.Length; i++) { if (spans.Any(q => i >= q[0] - 4800 && i < q[1] + 4800)) continue; en += y[i + best] * (double)y[i + best]; ein += mix[i] * (double)mix[i]; }
                    double sy = 0, ss = 0; foreach (var q in spans) for (int i = q[0]; i < q[1] && i + best < y.Length; i++) { sy += clean[i] * (double)y[i + best]; ss += clean[i] * (double)clean[i]; }
                    double a = sy / ss, e = 0, tt = 0; foreach (var q in spans) for (int i = q[0]; i < q[1] && i + best < y.Length; i++) { double tg = a * clean[i], er = y[i + best] - tg; tt += tg * tg; e += er * er; }
                    var m = sc.Score(Levelled(y));
                    Console.WriteLine("  strength " + cfg.PadRight(12) + " left in pauses " + (10 * Math.Log10((en + 1e-20) / (ein + 1e-20))).ToString("0.0").PadLeft(6) + " dB   voice SI-SDR " + (10 * Math.Log10(tt / (e + 1e-20))).ToString("0.0").PadLeft(5) + " dB   DNSMOS " + m[0].ToString("0.00") + " / " + m[1].ToString("0.00") + " / " + m[2].ToString("0.00"));
                }
            }
    }
}
