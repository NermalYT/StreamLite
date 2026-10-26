// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite;
using StreamLite.Audio;

// The whole voice chain, judged by DNSMOS (test\dnsmos, see DnsmosRun): your recorded sample plus Windows' voices over
// a fan, keyboard, TV, kids and your own room's background, all as quiet as your mic delivers them. Each version of the
// chain (your settings with one thing changed) gets speech quality, background and overall per clip and on average.
// Outputs are levelled the same before judging (Discord turns you up or down anyway).
// Usage: run.ps1 MicTune [config filter]
class MicTune
{
    const int Fs = 48000;

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

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = a; i < b; i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    // Speech level: the 90th percentile of 50 ms blocks.
    static double Level(float[] x)
    {
        var l = new List<double>(); for (int i = 0; i + 2400 <= x.Length; i += 2400) l.Add(Rms(x, i, i + 2400));
        l.Sort(); return l[l.Count * 9 / 10];
    }
    static void Scale(float[] x, double g) { for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * g); }

    static float[] Speech(string voice, int seconds, Random rnd)
    {
        var lines = new[] { "Enemy behind the wall, push left.", "I need ammo, can somebody cover me?", "Nice shot! Let's go again.", "Wait, wait, there's two on the stairs.",
            "Okay so tomorrow we start at seven.", "Did you hear that? Footsteps above us.", "Rotate to B, they're stacking site.", "Honestly that was the best round all night." };
        var o = new float[seconds * Fs]; int pos = Fs / 2;
        while (pos < o.Length - Fs)
        {
            var s = Tts(lines[rnd.Next(lines.Length)], voice);
            for (int i = 0; i < s.Length && pos + i < o.Length; i++) o[pos + i] = s[i];
            pos += s.Length + Fs * (3 + rnd.Next(6)) / 10;
        }
        return o;
    }

    static float[] Fan(int n, Random rnd, double hum)
    {
        var x = new float[n]; double a = 0, b = 0;
        for (int i = 0; i < n; i++)
        {
            a = 0.98 * a + 0.02 * (rnd.NextDouble() * 2 - 1); b = 0.5 * b + 0.5 * a;
            x[i] = (float)(b * 6 + (rnd.NextDouble() * 2 - 1) * 0.04 + hum * Math.Sin(2 * Math.PI * 120 * i / Fs) * (1 + 0.3 * Math.Sin(2 * Math.PI * 0.4 * i / Fs)));
        }
        return x;
    }

    static float[] Mix(float[] speech, float[] noise, double speechDb, double snrDb)
    {
        var s = (float[])speech.Clone(); Scale(s, Math.Pow(10, speechDb / 20) / Level(s));
        var nz = (float[])noise.Clone(); double nr = Rms(nz, 0, nz.Length); Scale(nz, Math.Pow(10, (speechDb - snrDb) / 20) / nr);
        var o = new float[s.Length]; for (int i = 0; i < o.Length; i++) o[i] = s[i] + nz[i % nz.Length];
        return o;
    }

    static List<KeyValuePair<string, float[]>> Clips()
    {
        var rnd = new Random(11);
        var clips = new List<KeyValuePair<string, float[]>>();
        string you = Path.Combine(AppSettings.Folder, "voice-sample.wav");
        float[] yours = File.Exists(you) ? DnsmosRun.ReadWav(you) : null;
        if (yours != null) clips.Add(new KeyValuePair<string, float[]>("your sample", yours));
        const double Sp = -30;   // a quiet mic's speech level (yours: about -31 dBFS)
        int len = 14 * Fs;
        clips.Add(new KeyValuePair<string, float[]>("fan", Mix(Speech("Microsoft David Desktop", 14, rnd), Fan(len, rnd, 0.02), Sp, 12)));
        var keys = Fan(len, rnd, 0); Scale(keys, 0.15);
        for (int c = Fs / 3; c + 600 < len; c += 3000 + rnd.Next(5000))
            for (int i = 0; i < 600; i++) keys[c + i] += (float)((rnd.NextDouble() * 2 - 1) * 1.2 * Math.Exp(-i / 90.0));
        clips.Add(new KeyValuePair<string, float[]>("keyboard", Mix(Speech("Microsoft Zira Desktop", 14, rnd), keys, Sp, 15)));
        // TV: another voice in a room (low-passed, a little reverb) and a music bed.
        var tv = Speech("Microsoft Zira Desktop", 14, rnd); var tvo = new float[len]; double lp = 0;
        for (int i = 0; i < len; i++)
        {
            lp = 0.7 * lp + 0.3 * tv[i];
            double rev = (i > 2400 ? 0.35 * tvo[i - 2400] : 0) + (i > 3700 ? 0.2 * tvo[i - 3700] : 0);
            double mus = 0.15 * (Math.Sin(2 * Math.PI * 220 * i / Fs) + Math.Sin(2 * Math.PI * 277 * i / Fs) + Math.Sin(2 * Math.PI * 330 * i / Fs)) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.5 * i / Fs));
            tvo[i] = (float)(lp + rev + mus * 0.3);
        }
        clips.Add(new KeyValuePair<string, float[]>("TV", Mix(Speech("Microsoft David Desktop", 14, rnd), tvo, Sp, 10)));
        // Kids: high, fast voices and shrieks.
        var kid = new float[len];
        var kl = new[] { "Mom! He took my toy!", "Look at me, look at me!", "No it's my turn!", "Can we go outside now?", "Wheee!" };
        for (int pos = 0; pos < len - Fs; )
        {
            var s = Tts(kl[rnd.Next(kl.Length)], rnd.Next(2) == 0 ? "Microsoft Zira Desktop" : "Microsoft David Desktop", "x-high", "fast");
            for (int i = 0; i < s.Length && pos + i < len; i++) kid[pos + i] += s[i] * (float)(0.5 + rnd.NextDouble());
            pos += s.Length + rnd.Next(Fs);
        }
        clips.Add(new KeyValuePair<string, float[]>("kids", Mix(Speech("Microsoft Zira Desktop", 14, rnd), kid, Sp, 8)));
        // Your room: the quietest stretches of your sample, looped (crossfaded), under a voice at your sample's level.
        if (yours != null)
        {
            var bl = new List<KeyValuePair<int, double>>();
            for (int i = 0; i + 4800 <= yours.Length; i += 4800) bl.Add(new KeyValuePair<int, double>(i, Rms(yours, i, i + 4800)));
            var q = bl.OrderBy(b => b.Value).Take(Math.Max(3, bl.Count / 5)).Select(b => b.Key).OrderBy(k => k).ToList();
            var room = new float[q.Count * 4800];
            for (int j = 0; j < q.Count; j++)
                for (int i = 0; i < 4800; i++)
                {
                    float w = i < 240 ? i / 240f : i >= 4560 ? (4800 - i) / 240f : 1f;
                    room[j * 4800 + i] += yours[q[j] + i] * w;
                    if (i >= 4560 && j + 1 < q.Count) room[(j + 1) * 4800 + i - 4560] += yours[q[j + 1] + i - 4560] * (1 - w);
                }
            double snr = 20 * Math.Log10(Level(yours) / Rms(room, 0, room.Length));
            clips.Add(new KeyValuePair<string, float[]>("your room", Mix(Speech("Microsoft David Desktop", 14, rnd), room, Sp, snr)));
            Console.WriteLine("your room's background: " + (snr).ToString("0.0") + " dB below your speech");
        }
        return clips;
    }

    static float[] Run(AppSettings s, float[] x)
    {
        var chain = new VoiceChain(); chain.Suppressor.Synchronous = true; chain.Configure(s);
        var blk = new float[144];
        for (int k = 0; k < 4000 && chain.Suppressor.Dfn != 0 && s.NoiseRemoval && chain.Suppressor.UseAi; k++)
        {
            Array.Clear(blk, 0, 144); chain.Process(blk, 144, false);
            var d = chain.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
            System.Threading.Thread.Sleep(2);
        }
        // A second of lead-in (the background alone) so every version starts adapted, like a mic that's been on.
        var y = new float[x.Length];
        for (int i = 0; i + 144 <= Fs; i += 144) { Array.Copy(x, i, blk, 0, 144); chain.Process(blk, 144, false); }
        for (int i = 0; i + 144 <= x.Length; i += 144) { Array.Copy(x, i, blk, 0, 144); chain.Process(blk, 144, false); Array.Copy(blk, 0, y, i, 144); }
        chain.Suppressor.ReleaseEngine();
        return y;
    }

    internal static List<KeyValuePair<string, Action<AppSettings>>> Configs()
    {
        return new List<KeyValuePair<string, Action<AppSettings>>>
        {
            new KeyValuePair<string, Action<AppSettings>>("realtime (before)", s => { s.NsRealtime = true; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn (now)", s => { }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, no gate", s => { s.Gate = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, no expander", s => { s.GateExpander = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, strength 30", s => { s.Strength = 30; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, strength 80", s => { s.Strength = 80; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, no comp/level", s => { s.Compressor = false; s.AutoLevel = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, no de-esser", s => { s.DeEsser = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, flat EQ", s => { s.MicEq = new EqProfile { Enabled = false }; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn, high-pass 80", s => { s.HighPassHz = 80; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn only", s => { s.Gate = false; s.GateExpander = false; s.Compressor = false; s.AutoLevel = false; s.DeEsser = false; s.MicEq = new EqProfile { Enabled = false }; }),
            new KeyValuePair<string, Action<AppSettings>>("noise removal off", s => { s.NoiseRemoval = false; }),
        };
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var clips = Clips();
        string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\mictune");
        foreach (var c in clips) MicSampleStatsWav(Path.Combine(outDir, "in-" + c.Key.Replace(' ', '-') + ".wav"), c.Value);
        string filter = args.Length > 0 ? args[0] : null;
        using (var sc = new DnsmosRun.Scorer())
        {
            Console.WriteLine("".PadRight(22) + string.Join("", clips.Select(c => c.Key.PadLeft(12))) + "     mean (SIG / BAK / OVRL)");
            Console.Write("input".PadRight(22));
            var inp = new double[3];
            foreach (var c in clips) { var r = sc.Score(Levelled(c.Value)); Console.Write(r[2].ToString("0.00").PadLeft(12)); for (int k = 0; k < 3; k++) inp[k] += r[k] / clips.Count; }
            Console.WriteLine("     " + inp[0].ToString("0.00") + " / " + inp[1].ToString("0.00") + " / " + inp[2].ToString("0.00"));
            foreach (var cfg in Configs())
            {
                if (filter != null && !cfg.Key.Contains(filter)) continue;
                Console.Write(cfg.Key.PadRight(22));
                var m = new double[3];
                foreach (var c in clips)
                {
                    var s = AppSettings.Load(); s.NsRealtime = false; cfg.Value(s);
                    var y = Run(s, c.Value);
                    var r = sc.Score(Levelled(y));
                    Console.Write(r[2].ToString("0.00").PadLeft(12));
                    for (int k = 0; k < 3; k++) m[k] += r[k] / clips.Count;
                    MicSampleStatsWav(Path.Combine(outDir, cfg.Key.Replace(' ', '-').Replace('/', '-').Replace(",", "").Replace("(", "").Replace(")", "") + "-" + c.Key.Replace(' ', '-') + ".wav"), y);
                }
                Console.WriteLine("     " + m[0].ToString("0.00") + " / " + m[1].ToString("0.00") + " / " + m[2].ToString("0.00"));
            }
        }
    }

    static float[] Levelled(float[] x) { var y = (float[])x.Clone(); double l = Level(y); if (l > 1e-6) Scale(y, Math.Pow(10, -22 / 20.0) / l); for (int i = 0; i < y.Length; i++) y[i] = Math.Max(-1f, Math.Min(1f, y[i])); return y; }

    static void MicSampleStatsWav(string path, float[] x)
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
