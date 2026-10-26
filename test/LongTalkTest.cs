// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// "The longer I talked the lower I got - they heard my fan and not my voice": a minute of talking with hardly a pause
// (your recording's talking parts, stays on this PC) over your own fan, through the chain with your settings and with
// others. Every 5 s: how loud your voice comes out and how loud the fan is next to it (the fan's level measured by
// sending the fan alone through the very same gains, frame by frame). Nothing may sink over time.
// Usage: run.ps1 LongTalkTest
class LongTalkTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static double Db(double p) { return 10 * Math.Log10(p + 1e-20); }


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

    static float[] Run(AppSettings s, float[] x, VoiceChain c = null)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        if (c == null) { c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s); }
        Warm(c, x);
        var y = (float[])x.Clone();
        for (int at = 0; at + 480 <= y.Length; at += 480) { var b = new float[480]; Array.Copy(y, at, b, 0, 480); c.Process(b, 480, false); Array.Copy(b, 0, y, at, 480); }
        c.Suppressor.ReleaseEngine();
        return y;
    }

    /// <summary>
    /// How much of the fan comes through: the chain's own gain per frequency and moment (output over input, short-time
    /// spectra), applied to the fan alone.
    /// </summary>
    static double[] FanThrough(float[] input, float[] output, float[] fan, int delay, int window)
    {
        const int N = 1024, H = 512; var fft = new Fft(N); var w = new float[N];
        for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
        var perWindow = new List<double>(); double acc = 0; int frames = 0;
        for (int at = 0; at + N + delay <= output.Length; at += H)
        {
            var xr = new float[N]; var xi = new float[N]; var yr = new float[N]; var yi = new float[N]; var nr = new float[N]; var ni = new float[N];
            for (int i = 0; i < N; i++) { xr[i] = input[at + i] * w[i]; yr[i] = output[at + delay + i] * w[i]; nr[i] = fan[at + i] * w[i]; }
            fft.Transform(xr, xi, false); fft.Transform(yr, yi, false); fft.Transform(nr, ni, false);
            double e = 0;
            for (int k = 1; k < N / 2; k++)
            {
                double px = xr[k] * xr[k] + xi[k] * xi[k], py = yr[k] * yr[k] + yi[k] * yi[k], pn = nr[k] * nr[k] + ni[k] * ni[k];
                double g2 = px > 1e-14 ? Math.Min(1, py / px) : 0;
                e += g2 * pn;
            }
            acc += e / (N * N * 0.375 / 2); frames++;
            if (frames * H >= window) { perWindow.Add(acc / frames); acc = 0; frames = 0; }
        }
        return perWindow.ToArray();
    }

    static void Main(string[] args)
    {
        string live = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\live"));
        string file = Directory.Exists(live) ? Directory.GetFiles(live, "mic-*-mono48.wav").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
        if (file == null) { Console.WriteLine("No recording (run.ps1 CaptureRaw mic 25) - skipped"); Console.WriteLine("ALL PASS"); return; }
        var raw = DnsmosRun.ReadWav(file);
        var cleanS = new AppSettings { NoiseRemoval = true, NsEngine = NoiseEngines.DfnGpu, Strength = 100, Gate = false, Compressor = false, DeEsser = false, AutoLevel = false, Limiter = false, StudioVoice = false, MicEq = new EqProfile { Enabled = false } };
        var voice = Run(cleanS, Run(cleanS, raw));
        // The fan alone: the recording's quietest 50 ms pieces, crossfaded together.
        const int P = 2400;
        var quiet = Enumerable.Range(1, raw.Length / P - 2).Select(i => new { i, e = raw.Skip(i * P).Take(P).Sum(v => (double)v * v) }).OrderBy(p => p.e).Take(raw.Length / P / 4).Select(p => p.i).ToList();
        // A minute of talking: the voice's talking parts (above -45 dBFS in 50 ms pieces) one after another.
        var talk = new List<float>();
        for (int i = 0; i + P <= voice.Length; i += P)
        {
            double e = 0; for (int j = i; j < i + P; j++) e += voice[j] * (double)voice[j];
            if (Db(e / P) > -48) for (int j = i; j < i + P; j++) talk.Add(voice[j]);
        }
        int len = Fs * 60;
        var v60 = new float[len]; for (int i = 0; i < len; i++) v60[i] = talk[i % talk.Count];
        var fan = new float[len]; var rnd = new Random(4);
        for (int pos = 0; pos < len; pos += P - 240)
        {
            int at = quiet[rnd.Next(quiet.Count)] * P;
            for (int i = 0; i < P && pos + i < len; i++) { double w = Math.Min(1, Math.Min(i, P - 1 - i) / 240.0); fan[pos + i] += (float)(raw[at + i] * Math.Sqrt(w)); }
        }
        var mix = new float[len]; for (int i = 0; i < len; i++) mix[i] = v60[i] + fan[i];
        Console.WriteLine("A minute of talking: voice " + Db(v60.Average(x => (double)x * x)).ToString("0.0") + " dBFS, fan " + Db(fan.Average(x => (double)x * x)).ToString("0.0") + " dBFS (" + (talk.Count / (double)Fs).ToString("0") + " s of talking, looped)");

        var configs = new List<KeyValuePair<string, AppSettings>>();
        var yours = AppSettings.Load(); yours.StudioVoice = false;
        configs.Add(new KeyValuePair<string, AppSettings>("your settings now" + (yours.NoiseRemoval ? "" : " (noise removal off)") + (yours.NsRealtime ? ", realtime" : ""), yours));
        var on = AppSettings.Load(); on.StudioVoice = false; on.NoiseRemoval = true; on.NsEngine = NoiseEngines.DfnGpu;
        configs.Add(new KeyValuePair<string, AppSettings>("yours, noise removal on" + (on.NsRealtime ? ", realtime" : ""), on));
        var rt = AppSettings.Load(); rt.StudioVoice = false; rt.NoiseRemoval = true; rt.NsEngine = NoiseEngines.DfnGpu; rt.NsRealtime = false;
        configs.Add(new KeyValuePair<string, AppSettings>("yours, noise removal on, realtime off", rt));
        var noisy = AppSettings.Load(); VoiceStyles.Find("Noisy room").Set(noisy); noisy.StudioVoice = false;
        configs.Add(new KeyValuePair<string, AppSettings>("Noisy room style (engine " + noisy.NsEngine + ")", noisy));
        var def = new AppSettings { NsEngine = NoiseEngines.DfnGpu, StudioVoice = false };
        configs.Add(new KeyValuePair<string, AppSettings>("defaults", def));
        foreach (var kv in configs)
        {
            var s = kv.Value;
            var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Configure(s);
            float delayMs = c.DelayMs;
            var y = Run(s, mix, c);
            int delay = (int)Math.Round(delayMs * Fs / 1000);
            var fanOut = FanThrough(mix, y, fan, delay, Fs * 5);
            var lv = new List<double>(); var lf = new List<double>();
            for (int w = 0; w < 12; w++)
            {
                int a = w * Fs * 5 + delay, b = Math.Min(y.Length, a + Fs * 5); double e = 0; for (int i = a; i < b; i++) e += y[i] * (double)y[i];
                lv.Add(Db(e / Math.Max(1, b - a)));
                lf.Add(w < fanOut.Length ? Db(fanOut[w]) : double.NaN);
            }
            Console.WriteLine("  " + kv.Key);
            Console.WriteLine("     out level every 5 s: " + string.Join(" ", lv.Select(d => d.ToString("0"))) + " dBFS");
            Console.WriteLine("     fan in it:           " + string.Join(" ", lf.Select(d => d.ToString("0"))) + " dBFS   (fan " + (lv.Skip(1).Average() - lf.Skip(1).Where(d => !double.IsNaN(d)).Average()).ToString("0") + " dB under the voice)");
            Check(lv.Skip(2).Min() > lv.Skip(1).Max() - 4, kv.Key + ": the voice doesn't sink while you keep talking (" + (lv.Last() - lv[1]).ToString("+0.0;-0.0") + " dB from 5 s to 60 s)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
