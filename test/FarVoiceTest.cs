// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite;
using StreamLite.Audio;

// "Across the room" (3.5.0), measured: a voice (Windows' David) at the mic and from across a room - 3 m away: 20 dB
// quieter, the room's echo as loud as the voice itself, the top end dulled - with a fan running at the mic, through the
// whole voice chain (DeepFilterNet, studio voice, auto level...). How loud the voice comes out, how much is left in
// pauses, how intact it is (SI-SDR against the dry voice) and DNSMOS (signal / background / overall).
// Usage: run.ps1 FarVoiceTest [wav]   (wav: also writes the outputs to test\shots\farvoice)
class FarVoiceTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

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

    static float[] Talk(string voice, string[] lines, int seconds, Random rnd, List<int[]> spans)
    {
        var o = new float[seconds * Fs]; int pos = Fs, li = 0;
        while (pos < o.Length - Fs)
        {
            var s = Tts(lines[li++ % lines.Length], voice);
            int len = Math.Min(s.Length, o.Length - pos);
            Array.Copy(s, 0, o, pos, len);
            spans.Add(new[] { pos, pos + len });
            pos += s.Length + Fs * (8 + rnd.Next(6)) / 10;
        }
        return o;
    }

    /// <summary>The room: a decaying diffuse tail (RT60 seconds) after the direct sound, its energy drr dB under it.</summary>
    static float[] Room(float[] dry, double gain, double rt60, double drrDb, double lowpass, Random rnd)
    {
        int len = (int)(rt60 * Fs);
        var ir = new double[len];
        double decay = 3 * Math.Log(10) / rt60, e = 0;
        for (int i = (int)(0.004 * Fs); i < len; i++) { ir[i] = (rnd.NextDouble() * 2 - 1) * Math.Exp(-decay * i / Fs); e += ir[i] * ir[i]; }
        double tail = Math.Pow(10, -drrDb / 20) / Math.Sqrt(e);
        for (int i = 0; i < len; i++) ir[i] *= tail;
        ir[0] = 1;
        // Sparse convolution: the tail every 4th tap (plenty for a diffuse tail), the direct sound exactly.
        var y = new double[dry.Length];
        for (int i = 0; i < dry.Length; i++)
        {
            double x = dry[i]; if (x == 0) continue;
            y[i] += x;
            int end = Math.Min(len, dry.Length - i);
            for (int k = (int)(0.004 * Fs); k < end; k += 4) y[i + k] += x * ir[k] * 2;
        }
        var o = new float[dry.Length]; double lp = 0;
        for (int i = 0; i < o.Length; i++) { lp += lowpass * (y[i] - lp); o[i] = (float)(lp * gain); }
        return o;
    }

    static float[] Fan(int n, Random rnd)
    {
        var x = new float[n]; double a = 0, b = 0;
        for (int i = 0; i < n; i++) { a = 0.98 * a + 0.02 * (rnd.NextDouble() * 2 - 1); b = 0.5 * b + 0.5 * a; x[i] = (float)(b * 6 + 0.3 * (rnd.NextDouble() * 2 - 1) * 0.05 + 0.02 * Math.Sin(2 * Math.PI * 120 * i / Fs)); }
        return x;
    }

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = Math.Max(0, a); i < Math.Min(x.Length, b); i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    static double Db(double r) { return 20 * Math.Log10(r + 1e-12); }

    static double SpeechDb(float[] y, int d, List<int[]> spans)
    {
        var l = new List<double>();
        foreach (var s in spans) for (int i = s[0]; i + 2400 <= s[1]; i += 2400) l.Add(Rms(y, i + d, i + d + 2400));
        l.Sort(); return Db(l[l.Count * 8 / 10]);
    }

    static double PauseDb(float[] y, int d, List<int[]> spans)
    {
        double e = 0; long n = 0;
        for (int k = 0; k + 1 < spans.Count; k++)
            for (int i = spans[k][1] + Fs * 4 / 10; i < spans[k + 1][0] - Fs / 10; i++) { e += y[i + d] * (double)y[i + d]; n++; }
        return Db(Math.Sqrt(e / Math.Max(1, n)));
    }

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

    static float[] Levelled(float[] x) { var y = (float[])x.Clone(); var l = new List<double>(); for (int i = 0; i + 2400 <= y.Length; i += 2400) l.Add(Rms(y, i, i + 2400)); l.Sort(); double lv = l[l.Count * 9 / 10]; if (lv > 1e-6) for (int i = 0; i < y.Length; i++) y[i] = (float)Math.Max(-1, Math.Min(1, y[i] * Math.Pow(10, -22 / 20.0) / lv)); return y; }

    static string Trace;
    static float[] Chain(float[] x, int mode)
    {
        var s = new AppSettings();
        typeof(AppSettings).GetMethod("Normalize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Invoke(s, null);
        s.NsEngine = NoiseEngines.DfnGpu; s.NoiseRemoval = true;
        s.FarVoice = mode > 0; s.FarPresenceDb = mode == 2 ? 4 : 0;
        var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s);
        string pr = Environment.GetEnvironmentVariable("FV_PRESENCE"); if (pr != null && mode == 2) { s.FarPresenceDb = int.Parse(pr); c.Configure(s); }
        var blk = new float[480];
        for (int k = 0; k < 4000; k++)
        {
            Array.Clear(blk, 0, 480); c.Process(blk, 480, false);
            var d = c.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
            System.Threading.Thread.Sleep(2);
        }
        var y = new float[x.Length];
        var gains = new List<double>(); var comp = new List<double>();
        for (int i = 0; i + 480 <= x.Length; i += 480) { Array.Copy(x, i, blk, 0, 480); c.Process(blk, 480, false); Array.Copy(blk, 0, y, i, 480); gains.Add(c.Leveler.CurrentGainDb); }
        gains.Sort(); Trace = "level gain median " + gains[gains.Count / 2].ToString("0.0") + " dB, 90% " + gains[gains.Count * 9 / 10].ToString("0.0") + " dB";
        return y;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        bool wav = args.Contains("wav");
        var rnd = new Random(4);
        var spans = new List<int[]>();
        var clean = Talk("Microsoft David Desktop", new[] { "Hey, can you hear me from over here?", "I'm lying on my bed, the mic is on the desk.", "Push left, there's one behind the boxes.", "Okay, I'm going to grab a drink, one second.", "Nice shot, that was actually insane." }, 30, rnd, spans);
        double peak = clean.Max(v => Math.Abs(v)); for (int i = 0; i < clean.Length; i++) clean[i] = (float)(clean[i] / peak * 0.5);   // close: peaks at -6 dBFS
        var fan = Fan(clean.Length, new Random(9));
        double fanRms = Rms(fan, 0, fan.Length);
        // The fan at the mic: 25 dB under the close voice (talking at the desk it's barely there).
        double voiceClose = SpeechDb(clean, 0, spans);
        for (int i = 0; i < fan.Length; i++) fan[i] = (float)(fan[i] / fanRms * Math.Pow(10, (voiceClose - 25) / 20));
        var near = Room(clean, 1.0, 0.45, 14, 0.9, new Random(2));          // at the desk: little room
        var far = Room(clean, 0.1, 0.55, 0, 0.45, new Random(3));           // across the room: -20 dB, echo as loud as the voice, duller
        var cases = new[] { new { Name = "at the desk", X = near }, new { Name = "across the room", X = far } };
        Console.WriteLine("case                       mode            voice out   pauses   SI-SDR   DNSMOS sig/bak/ovrl");
        var res = new Dictionary<string, double[]>();
        using (var sc = new DnsmosRun.Scorer())
            foreach (var cs in cases)
                foreach (int farMode in new[] { 0, 1, 2 })
                {
                    var x = new float[cs.X.Length]; for (int i = 0; i < x.Length; i++) x[i] = cs.X[i] + fan[i];
                    var y = Chain(x, farMode);
                    int d = Delay(clean, y, spans);
                    double v = SpeechDb(y, d, spans), p = PauseDb(y, d, spans), sdr = SiSdr(clean, y, d, spans);
                    var m = sc.Score(Levelled(y));
                    string mode = farMode == 0 ? "normal" : farMode == 1 ? "far, no treble" : "Across the room";
                    res[cs.Name + "|" + mode] = new[] { v, p, sdr, m[0], m[1], m[2] };
                    Console.WriteLine(cs.Name.PadRight(27) + mode.PadRight(15) + (v.ToString("0.0") + " dB").PadLeft(10) + (p.ToString("0.0") + " dB").PadLeft(10) + (sdr.ToString("0.0") + " dB").PadLeft(9) + "   " + m[0].ToString("0.00") + " / " + m[1].ToString("0.00") + " / " + m[2].ToString("0.00"));
                    Console.WriteLine("      " + Trace);
                    if (wav) Write(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\farvoice\" + cs.Name.Replace(' ', '-') + "-" + mode.Replace(' ', '-').Replace(",", "") + ".wav"), y);
                }
        var nf = res["across the room|normal"]; var ff = res["across the room|Across the room"]; var nn = res["at the desk|normal"]; var fn = res["at the desk|Across the room"]; var fe = res["across the room|far, no treble"];
        Check(ff[0] > nf[0] + 4 && ff[0] > nn[0] - 4, "across the room: the voice comes out about as loud as at the desk (" + nf[0].ToString("0.0") + " -> " + ff[0].ToString("0.0") + " dB; at the desk " + nn[0].ToString("0.0") + ")");
        Check(ff[1] < -60, "pauses stay silent (" + nf[1].ToString("0.0") + " -> " + ff[1].ToString("0.0") + " dBFS)");
        Check(ff[5] >= fe[5], "giving the treble back sounds at least as good (DNSMOS " + fe[5].ToString("0.00") + " -> " + ff[5].ToString("0.00") + ")");
        Check(ff[5] >= nf[5] - 0.05, "overall quality across the room at least as good (DNSMOS " + nf[5].ToString("0.00") + " -> " + ff[5].ToString("0.00") + ")");
        Check(fn[5] >= nn[5] - 0.15, "at the desk with Across the room on: still fine (DNSMOS " + nn[5].ToString("0.00") + " -> " + fn[5].ToString("0.00") + ")");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    static void Write(string path, float[] x)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + x.Length * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
        }
    }
}
