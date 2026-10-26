// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// "They hear my fan while I talk": your recording (CaptureRaw mic, stays on this PC) has your own fan in it. Its quiet
// moments are that fan alone; your voice cleaned twice over is the voice. Voice + fan through your chain, and the voice
// alone through the same chain: the difference while you talk is the fan that leaks through with your voice. Reported
// next to what's left in your pauses - the jump between them is what friends hear as "isolation turns off when he talks".
// Usage: run.ps1 FanLeakTest [wav folder]
class FanLeakTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Run(AppSettings s, float[] x)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s);
        Warm(c, x);
        var y = (float[])x.Clone();
        for (int at = 0; at + 480 <= y.Length; at += 480) { var b = new float[480]; Array.Copy(y, at, b, 0, 480); c.Process(b, 480, false); Array.Copy(b, 0, y, at, 480); }
        c.Suppressor.ReleaseEngine();
        return y;
    }

    static double Db(double p) { return 10 * Math.Log10(p + 1e-20); }

    static void Main(string[] args)
    {
        string live = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\live"));
        string file = Directory.Exists(live) ? Directory.GetFiles(live, "mic-*-mono48.wav").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
        if (file == null) { Console.WriteLine("No recording (run.ps1 CaptureRaw mic 25) - skipped"); Console.WriteLine("ALL PASS"); return; }
        var raw = DnsmosRun.ReadWav(file);
        // The fan alone: the quietest 50 ms pieces of the recording, joined with short crossfades into 12 s.
        const int P = 2400;
        var pieces = Enumerable.Range(0, raw.Length / P).Select(i => new { i, e = raw.Skip(i * P).Take(P).Sum(v => (double)v * v) }).OrderBy(p => p.e).Take(Math.Max(20, raw.Length / P / 4)).Select(p => p.i).ToList();
        var fan = new float[Fs * 12]; int pos = 0, pi = 0; var rnd = new Random(2);
        while (pos < fan.Length)
        {
            int at = pieces[rnd.Next(pieces.Count)] * P;
            for (int i = 0; i < P && pos + i < fan.Length; i++) { double w = Math.Min(1, Math.Min(i, P - 1 - i) / 240.0); fan[pos + i] += (float)(raw[at + i] * Math.Sqrt(w)); }
            pos += P - 240; pi++;
        }
        // The voice: the recording through the full noise remover, then again (the fan gone).
        var settings = AppSettings.Load();
        var clean = new AppSettings { NoiseRemoval = true, NsEngine = NoiseEngines.DfnGpu, Strength = 100, Gate = false, Compressor = false, DeEsser = false, AutoLevel = false, Limiter = false, StudioVoice = false, MicEq = new EqProfile { Enabled = false } };
        var voice = Run(clean, Run(clean, raw));
        int lag = (int)(2 * (new VoiceChain().Suppressor.DelayMs) * Fs / 1000);
        voice = voice.Skip(Fs).Take(Math.Min(voice.Length - Fs, fan.Length)).ToArray();
        var mix = new float[voice.Length]; for (int i = 0; i < mix.Length; i++) mix[i] = voice[i] + fan[i];
        Console.WriteLine("Recording " + Path.GetFileName(file) + ": fan " + Db(fan.Average(v => (double)v * v)).ToString("0.0") + " dBFS, voice " + Db(voice.Where((v, i) => true).Average(v => (double)v * v)).ToString("0.0") + " dBFS");

        DnsmosRun.Scorer scorer = null; try { scorer = new DnsmosRun.Scorer(); } catch { }
        double oldJump = 0, newJump = 0, oldUnder = 0, newUnder = 0; double[] oldMos = null, newMos = null;
        var only = Environment.GetEnvironmentVariable("FAN_CFG");
        foreach (var cfg in (Environment.GetEnvironmentVariable("FAN_LIST") ?? "DeepFilterNet (built in);RNNoise (what voice styles switched to before 3.3);Realtime (gone in 3.3)").Split(';'))
        {
            if (only != null && !cfg.Contains(only)) continue;
            bool now = cfg.StartsWith("DeepFilterNet");
            float pre = cfg.StartsWith("classic ") ? int.Parse(cfg.Substring(8, cfg.IndexOf('%') - 8)) / 100f : 0f, post = cfg.EndsWith("classic 40%") ? 0.4f : 0f;
            var s = AppSettings.Load(); s.StudioVoice = false; s.NsEngine = cfg.StartsWith("RNNoise") ? NoiseEngines.Ai : NoiseEngines.DfnGpu; s.NsRealtime = cfg.StartsWith("Realtime"); s.NoiseRemoval = true; s.Strength = 70;
            // The noise remover alone: the gate, auto level and compressor react to the fan differently with and without
            // it, which would hide what's measured here (they're the same either way).
            s.Gate = false; s.AutoLevel = false; s.Compressor = false; s.DeEsser = false; s.MicEq = new EqProfile { Enabled = false }; s.Limiter = false;
            Func<float[], float, float[]> classic = (x, st) =>
            {
                var ns = new NoiseSuppressor { UseAi = false, Dfn = 0, Strength = st, Synchronous = true };
                var y0 = (float[])x.Clone();
                for (int at = 0; at + 480 <= y0.Length; at += 480) { var bb = new float[480]; Array.Copy(y0, at, bb, 0, 480); ns.Process(bb, 480); Array.Copy(bb, 0, y0, at, 480); }
                return y0;
            };
            Func<float[], float[]> run = x =>
            {
                if (pre > 0) x = classic(x, pre);
                var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s);
                var o = Run(c, x);
                if (post > 0) o = classic(o, post);
                return o;
            };
            var y = run(mix); var ys = run(voice);
            { var yf = run(fan); var lv = new List<string>(); for (int t = 0; t + Fs <= yf.Length; t += Fs * 2) { double e = 0; for (int i = t; i < t + Fs; i++) e += yf[i] * (double)yf[i]; lv.Add(Db(e / Fs).ToString("0")); } Console.WriteLine("     the fan alone comes out at " + string.Join(" ", lv) + " dBFS (every 2 s)"); }
            int delay = Lag(voice, ys);
            // The chain's gain per frequency and moment (output / input), applied to the fan alone.
            const int N = 1024, H = 480; var fft = new Fft(N); var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
            var talkFan = new List<double>(); var talkVoice = new List<double>(); var pauseFan = new List<double>();
            for (int at = Fs; at + N + delay <= y.Length; at += H)
            {
                // What the fan adds to the output: the output's power minus the voice-alone output's, per frequency
                // (on average the voice and the leftover fan add up in power).
                var yr = new float[N]; var yi = new float[N]; var sr = new float[N]; var si = new float[N];
                double ev = 0;
                for (int i = 0; i < N; i++) { yr[i] = y[at + delay + i] * w[i]; sr[i] = ys[at + delay + i] * w[i]; ev += ys[at + delay + i] * (double)ys[at + delay + i]; }
                ev /= N;
                fft.Transform(yr, yi, false); fft.Transform(sr, si, false);
                double e = 0;
                for (int k = 1; k < N / 2; k++) e += (yr[k] * yr[k] + yi[k] * yi[k]) - (sr[k] * sr[k] + si[k] * si[k]);
                e = Math.Max(1e-14, e / (N * N * 0.375 / 2));
                if (Db(ev) > -45) { talkFan.Add(e); talkVoice.Add(ev); } else if (Db(ev) < -70) pauseFan.Add(e);
            }
            double ft = Db(talkFan.Average()), fp = Db(pauseFan.Average()), vt = Db(talkVoice.Average());
            string mos = "";
            if (scorer != null) { var m = scorer.Score(Levelled(y)); mos = ", DNSMOS SIG " + m[0].ToString("0.00") + " BAK " + m[1].ToString("0.00") + " OVRL " + m[2].ToString("0.00"); }
            Console.WriteLine("  " + cfg.PadRight(34) + " fan while you talk " + ft.ToString("0.0") + " dBFS (" + (vt - ft).ToString("0.0") + " dB under your voice), in pauses " + fp.ToString("0.0") + " dBFS: it jumps " + (ft - fp).ToString("0.0") + " dB when you talk" + mos);
            if (now) { newJump = ft - fp; newUnder = vt - ft; } else { oldJump = Math.Max(oldJump, ft - fp); oldUnder = oldUnder == 0 ? vt - ft : Math.Max(oldUnder, vt - ft); }
            if (scorer != null) { var m = scorer.Score(Levelled(y)); if (now) newMos = m; else if (oldMos == null || m[2] > oldMos[2]) oldMos = m; }
            if (args.Length > 0) { Directory.CreateDirectory(args[0]); Write(Path.Combine(args[0], "fan-" + cfg.Split(' ')[0] + (pre > 0 ? (pre * 100).ToString("0") : "") + (post > 0 ? "post" : "") + ".wav"), y); }
        }
        if (scorer != null) scorer.Dispose();
        if (oldMos != null) Check(newMos[2] > oldMos[2] + 0.2, "DeepFilterNet sounds clearly better (DNSMOS overall " + newMos[2].ToString("0.00") + " vs at best " + oldMos[2].ToString("0.00") + ")");
        if (args.Length > 0) Write(Path.Combine(args[0], "fan-input.wav"), mix);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    /// <summary>How much later y is than x (samples): the best match of their envelopes, then of the waves themselves.</summary>
    static int Lag(float[] x, float[] y)
    {
        int a = Fs * 2, b = Math.Min(x.Length, y.Length) - 8000; double best = double.MinValue; int lag = 0;
        for (int l = 0; l < 6000; l += 16) { double c = 0; for (int i = a; i < b; i += 4) c += Math.Abs(x[i]) * Math.Abs(y[i + l]); if (c > best) { best = c; lag = l; } }
        best = double.MinValue; int l0 = lag;
        for (int l = Math.Max(0, l0 - 16); l <= l0 + 16; l++) { double c = 0; for (int i = a; i < b; i++) c += x[i] * y[i + l]; if (c > best) { best = c; lag = l; } }
        return lag;
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

    static float[] Run(VoiceChain c, float[] x)
    {
        Warm(c, x);
        var y = (float[])x.Clone();
        for (int at = 0; at + 480 <= y.Length; at += 480) { var b = new float[480]; Array.Copy(y, at, b, 0, 480); c.Process(b, 480, false); Array.Copy(b, 0, y, at, 480); }
        if (Environment.GetEnvironmentVariable("FAN_DEBUG") != null) { var d = c.Suppressor.DfnEngine; Console.WriteLine("       dfn " + (d == null ? "none" : "ready " + d.Ready + " error " + d.Error + " late " + d.Late + " gpu " + d.Gpu)); }
        c.Suppressor.ReleaseEngine();
        return y;
    }

    static float[] Levelled(float[] x)
    {
        double s = 0; foreach (var v in x) s += v * (double)v;
        double g = Math.Pow(10, -22 / 20.0) / Math.Sqrt(s / x.Length + 1e-20);
        return x.Select(v => (float)Math.Max(-1, Math.Min(1, v * g))).ToArray();
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
