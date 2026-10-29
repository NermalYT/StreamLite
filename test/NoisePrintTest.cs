// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite.Audio;

// "Record a sound to remove", measured: a voice (Windows' David) over a TV, kids, music and a fan. The sound is recorded
// alone first (through DeepFilterNet, as in the app), then the voice talks over it. DeepFilterNet alone against
// DeepFilterNet + the learned sound (built-in voice patterns, and patterns learned from the same voice saying other
// lines): how much of the sound is left in pauses, how intact the voice stays (scale-invariant SDR against the clean
// voice), and DNSMOS. Usage: run.ps1 NoisePrintTest [noise filter]
class NoisePrintTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Tts(string text, string voice, string pitch = null, string rate = null)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            synth.SelectVoice(voice);
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            if (pitch == null) synth.Speak(text);
            else synth.SpeakSsml("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><prosody pitch='" + pitch + "' rate='" + (rate ?? "medium") + "'>" + text + "</prosody></speak>");
            var b = ms.ToArray(); var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            return f;
        }
    }

    static float[] Talk(string voice, string[] lines, int seconds, Random rnd, List<int[]> spans, string pitch = null, string rate = null)
    {
        var o = new float[seconds * Fs]; int pos = Fs / 2, li = 0;
        while (pos < o.Length - Fs)
        {
            var s = Tts(lines[li++ % lines.Length], voice, pitch, rate);
            int len = Math.Min(s.Length, o.Length - pos);
            Array.Copy(s, 0, o, pos, len);
            if (spans != null) spans.Add(new[] { pos, pos + len });
            pos += s.Length + Fs * (4 + rnd.Next(6)) / 10;
        }
        return o;
    }

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = Math.Max(0, a); i < Math.Min(x.Length, b); i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    static double Level(float[] x) { var l = new List<double>(); for (int i = 0; i + 2400 <= x.Length; i += 2400) l.Add(Rms(x, i, i + 2400)); l.Sort(); return l[l.Count * 9 / 10]; }
    static void Scale(float[] x, double g) { for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * g); }

    static float[] Noise(string kind, int seconds, Random rnd)
    {
        int n = seconds * Fs; var x = new float[n];
        if (kind == "TV")
        {
            var tv = Talk("Microsoft Zira Desktop", new[] { "And now the weather for the weekend, with sunshine in the north.", "Scientists say the discovery could change everything we know.", "Coming up after the break, the top ten moments of the season.", "Tickets are on sale now at all participating stores." }, seconds, rnd, null);
            double lp = 0;
            for (int i = 0; i < n; i++)
            {
                lp = 0.65 * lp + 0.35 * tv[i];
                double rev = (i > 2400 ? 0.3 * x[i - 2400] : 0) + (i > 4100 ? 0.2 * x[i - 4100] : 0);
                double mus = 0.05 * (Math.Sin(2 * Math.PI * 196 * i / Fs) + Math.Sin(2 * Math.PI * 247 * i / Fs) + Math.Sin(2 * Math.PI * 294 * i / Fs)) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.25 * i / Fs));
                x[i] = (float)(lp + rev + mus);
            }
        }
        else if (kind == "partner")
        {
            // Someone else in the room talking (a woman, across the room: quieter, with the room's echo), as a
            // girlfriend on the phone or chatting next to you.
            var talk = Talk("Microsoft Zira Desktop", new[] { "Yeah I know, I told her the same thing yesterday.", "Can you grab some milk on the way back?", "No way, that's so funny, wait let me show you.", "I'm going to order food, do you want anything?", "Honestly I think we should just leave at eight." }, seconds, rnd, null);
            double lp = 0;
            for (int i = 0; i < n; i++)
            {
                lp = 0.75 * lp + 0.25 * talk[i];   // distance takes the top off
                double room = (i > 1900 ? 0.35 * talk[i - 1900] : 0) + (i > 3700 ? 0.25 * talk[i - 3700] : 0) + (i > 6100 ? 0.15 * talk[i - 6100] : 0);
                x[i] = (float)(lp + room);
            }
        }
        else if (kind == "kids")
        {
            var kl = new[] { "Mom! He took my toy!", "Look at me, look at me!", "No it's my turn!", "Can we go outside now?", "Wheee!", "I'm telling!" };
            for (int pos = 0; pos < n - Fs; )
            {
                var s = Tts(kl[rnd.Next(kl.Length)], rnd.Next(2) == 0 ? "Microsoft Zira Desktop" : "Microsoft David Desktop", "x-high", "fast");
                float g = (float)(0.5 + rnd.NextDouble());
                for (int i = 0; i < s.Length && pos + i < n; i++) x[pos + i] += s[i] * g;
                pos += s.Length + rnd.Next(Fs / 2);
            }
        }
        else if (kind == "music")
        {
            double[] chord = { 220, 277.2, 329.6, 440, 164.8, 207.7, 246.9, 329.6 };
            for (int i = 0; i < n; i++)
            {
                int bar = (i / (Fs * 2)) % 2; double t = (double)i / Fs, v = 0;
                for (int c = 0; c < 4; c++) { double f = chord[bar * 4 + c]; v += Math.Sin(2 * Math.PI * f * t) + 0.4 * Math.Sin(4 * Math.PI * f * t) + 0.2 * Math.Sin(6 * Math.PI * f * t); }
                double beat = (i % (Fs / 2)) < 2400 ? (rnd.NextDouble() * 2 - 1) * Math.Exp(-(i % (Fs / 2)) / 500.0) * 3 : 0;
                x[i] = (float)(v * 0.1 + beat);
            }
        }
        else
        {
            double a = 0, b = 0;
            for (int i = 0; i < n; i++) { a = 0.98 * a + 0.02 * (rnd.NextDouble() * 2 - 1); b = 0.5 * b + 0.5 * a; x[i] = (float)(b * 6 + 0.02 * Math.Sin(2 * Math.PI * 120 * i / Fs)); }
        }
        return x;
    }

    static NoiseSuppressor Dfn()
    {
        var ns = new NoiseSuppressor { UseAi = true, Dfn = 2, Strength = 0.54f, Realtime = false, Synchronous = true };
        var blk = new float[480];
        for (int k = 0; k < 4000; k++)
        {
            Array.Clear(blk, 0, 480); ns.Process(blk, 480);
            var d = ns.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
            System.Threading.Thread.Sleep(2);
        }
        return ns;
    }

    static float[] Run(NoiseSuppressor ns, NoisePrint np, float[] x)
    {
        var y = new float[x.Length]; var blk = new float[480];
        for (int i = 0; i + 480 <= x.Length; i += 480)
        {
            Array.Copy(x, i, blk, 0, 480);
            if (ns != null) ns.Process(blk, 480);
            if (np != null) np.Process(blk, 480);
            Array.Copy(blk, 0, y, i, 480);
        }
        return y;
    }

    // Scale-invariant SDR of y (delayed by d) against the clean voice, over the spoken parts.
    static double SiSdr(float[] clean, float[] y, int d, List<int[]> spans)
    {
        double sy = 0, ss = 0;
        foreach (var s in spans) for (int i = s[0]; i < s[1] && i + d < y.Length; i++) { sy += clean[i] * (double)y[i + d]; ss += clean[i] * (double)clean[i]; }
        double a = sy / ss, e = 0, t = 0;
        foreach (var s in spans) for (int i = s[0]; i < s[1] && i + d < y.Length; i++) { double tg = a * clean[i], er = y[i + d] - tg; t += tg * tg; e += er * er; }
        return 10 * Math.Log10(t / (e + 1e-20));
    }

    static int Delay(float[] clean, float[] y, List<int[]> spans)
    {
        int best = 0; double bc = double.MinValue; var s = spans[0];
        for (int lag = 0; lag < 6000; lag += 2) { double c = 0; for (int i = s[0]; i < s[1] && i + lag < y.Length; i += 2) c += clean[i] * (double)y[i + lag]; if (c > bc) { bc = c; best = lag; } }
        return best;
    }

    static float[] Levelled(float[] x) { var y = (float[])x.Clone(); double l = Level(y); if (l > 1e-6) Scale(y, Math.Pow(10, -22 / 20.0) / l); for (int i = 0; i < y.Length; i++) y[i] = Math.Max(-1f, Math.Min(1f, y[i])); return y; }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string filter = args.Length > 0 ? args[0] : null;
        var rnd = new Random(3);
        var evalLines = new[] { "Enemy behind the wall, push left.", "I need ammo, can somebody cover me?", "Nice shot! Let's go again.", "Wait, there's two on the stairs." };
        var learnLines = new[] { "Okay so tomorrow we start at seven, don't be late.", "Did you hear that? Footsteps above us, on the roof.", "Rotate to B, they're stacking the site with four.", "Honestly that was the best round all night, well played.", "Give me a second, I'm grabbing a drink.", "Who has the bomb? Drop it for me please." };
        // The "user's" own voice patterns: other lines of the same voice (as the app learns them while you talk).
        var learnVoice = Talk("Microsoft David Desktop", learnLines, 30, rnd, null);
        var lv = (float[])learnVoice.Clone(); Scale(lv, Math.Pow(10, -30 / 20.0) / Level(lv));
        var spec = typeof(NoisePrint).GetMethod("Spectra", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var vs = (float[])spec.Invoke(null, new object[] { lv, lv.Length });
        // Only frames with voice in them (as the app only learns from speech).
        var keep = new List<float>(); int K = NoisePrint.K;
        for (int t = 0; t < vs.Length / K; t++) { double e = 0; for (int k = 0; k < K; k++) e += vs[t * K + k] * vs[t * K + k]; if (e > 1e-4) for (int k = 0; k < K; k++) keep.Add(vs[t * K + k]); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var own = NoisePrint.Fit(keep.ToArray(), keep.Count / K, NoisePrint.VoiceAtoms, 60, NoisePrint.Generic());
        Console.WriteLine("own voice learned from " + (keep.Count / K) + " frames in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");

        using (var sc = new DnsmosRun.Scorer())
        {
            Console.WriteLine("noise".PadRight(8) + "version".PadRight(26) + "left in pauses   voice SI-SDR   DNSMOS SIG/BAK/OVRL");
            foreach (var kind in new[] { "TV", "kids", "music", "fan", "partner" })
            {
                if (filter != null && kind != filter) continue;
                var spans = new List<int[]>();
                var clean = Talk("Microsoft David Desktop", evalLines, 14, rnd, spans);
                Scale(clean, Math.Pow(10, -30 / 20.0) / Level(clean));
                var noise = Noise(kind, 24, rnd);
                double gap = Environment.GetEnvironmentVariable("NP_GAP") != null ? double.Parse(Environment.GetEnvironmentVariable("NP_GAP"), System.Globalization.CultureInfo.InvariantCulture) : 5;
                Scale(noise, Math.Pow(10, (-30 - gap) / 20.0) / Rms(noise, 0, noise.Length));   // 5 dB below the voice (NP_GAP: another gap)
                // 1. The sound alone, 8 s, through DeepFilterNet (what the app records), then learned.
                var rec = new float[8 * Fs]; Array.Copy(noise, 0, rec, 0, rec.Length);
                var ns = Dfn(); var recAfter = Run(ns, null, rec); ns.ReleaseEngine();
                sw.Restart();
                var learned = NoisePrint.Learn(recAfter, recAfter.Length - Fs / 10, rec);
                double learnS = sw.Elapsed.TotalSeconds;
                // 2. The voice over the rest of the sound.
                var mix = new float[clean.Length]; for (int i = 0; i < mix.Length; i++) mix[i] = clean[i] + noise[8 * Fs + i];
                var results = new List<string>();
                double[] left = new double[3], sdr = new double[3];
                for (int v = 0; v < 3; v++)
                {
                    ns = Dfn();
                    NoisePrint np = null;
                    if (v > 0 && !learned.AlreadyGone) { np = new NoisePrint { Enabled = true, Strength = Environment.GetEnvironmentVariable("NP_STRENGTH") != null ? float.Parse(Environment.GetEnvironmentVariable("NP_STRENGTH"), System.Globalization.CultureInfo.InvariantCulture) : 0.7f }; np.Use(learned.W); if (v == 2) np.UseVoice(own); }
                    var y = Run(ns, np, mix); ns.ReleaseEngine();
                    int d = Delay(clean, y, spans);
                    // Pauses: everything outside the spoken parts (with 100 ms margins).
                    double en = 0, ein = 0;
                    for (int i = Fs; i + d < y.Length; i++)
                    {
                        bool inSpeech = spans.Any(s => i >= s[0] - 4800 && i < s[1] + 4800);
                        if (inSpeech) continue;
                        en += y[i + d] * (double)y[i + d]; ein += mix[i] * (double)mix[i];
                    }
                    left[v] = 10 * Math.Log10((en + 1e-20) / (ein + 1e-20));
                    sdr[v] = SiSdr(clean, y, d, spans);
                    var m = sc.Score(Levelled(y));
                    string name = v == 0 ? "DeepFilterNet alone" : v == 1 ? "+ sound (built-in voices)" : "+ sound (your voice)";
                    if (v > 0 && learned.AlreadyGone) name += " [nothing to learn]";
                    Console.WriteLine(kind.PadRight(8) + name.PadRight(26) + (left[v].ToString("0.0") + " dB").PadLeft(14) + (sdr[v].ToString("0.0") + " dB").PadLeft(15) + "   " + m[0].ToString("0.00") + " / " + m[1].ToString("0.00") + " / " + m[2].ToString("0.00"));
                    MicSampleWav(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\noiseprint\" + kind + "-" + v + ".wav"), y);
                }
                Console.WriteLine("        learned in " + learnS.ToString("0.0") + " s: " + learned.Character + (learned.AlreadyGone ? " (DeepFilterNet leaves nothing of it)" : "") + ", after DeepFilterNet " + learned.AfterDb.ToString("0") + " dBFS (before " + learned.BeforeDb.ToString("0") + ")");
                // Other people talking (kids, a partner) are Only my voice's job: the sound remover says so and keeps your voice intact.
                bool people = kind == "kids" || kind == "partner";
                if (people) Check(learned.VoiceLike, kind + ": recognised as voices (" + learned.Character + "), so Only my voice is suggested");
                if (kind == "music" || kind == "fan") Check(learned.AlreadyGone, kind + ": DeepFilterNet already takes it out - nothing to learn (" + learned.AfterDb.ToString("0") + " dBFS left)");
                if (!learned.AlreadyGone && !people)
                    Check(left[2] < left[0] - 6, kind + ": at least 6 dB less of the sound in pauses with your voice learned (" + left[0].ToString("0.0") + " -> " + left[2].ToString("0.0") + " dB)");
                if (!learned.AlreadyGone)
                    Check(sdr[2] > sdr[0] - 1.5, kind + ": the voice stays as intact (SI-SDR " + sdr[0].ToString("0.0") + " -> " + sdr[2].ToString("0.0") + " dB)");
            }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    static void MicSampleWav(string path, float[] x)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int bytes = x.Length * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes);
            foreach (var v in x) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
        }
    }
}
