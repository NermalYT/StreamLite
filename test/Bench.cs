using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;
using StreamLite.UI;

// Measures headphone presets for real, end to end through Equalizer APO:
//   Bench eapo        - plays quiet pink noise to the headphones, writes each preset's EQ the way the app does, captures
//                       the headphones' loopback and compares what came out with what the app intended.
//   Bench rec <s> <name> - records the game device (StreamLite - Out) and the headphones at the same time (WAV) while
//                       something plays (e.g. a YouTube test video), for offline analysis.
//   Bench wav <file>  - analyses a recording: bands, loudness, L/R balance, clipping, then every preset applied to it.
class Bench
{
    const int Fs = 48000;
    static readonly double[] Third = { 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

    static void Main(string[] args)
    {
        try
        {
            if (args[0] == "eapo") Eapo();
            else if (args[0] == "rec") Rec(double.Parse(args[1]), args[2]);
            else if (args[0] == "wav") Wav(args[1]);
            else if (args[0] == "xfer") Xfer(args[1]);
            else if (args[0] == "lr") Lr(args[1]);
            else if (args[0] == "thd") Thd();
            else if (args[0] == "swing") Swing();
            else if (args[0] == "play")
            {
                // Quiet pink noise into the game device for N seconds (keeps the 3D chain busy during soak tests).
                var sink = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith(AppCore.GameOutName));
                var noise = Pink(Fs * 10, 3, -40);
                var t = new Thread(() => PlayLoop(sink.Id, noise)) { IsBackground = true };
                t.Start();
                Thread.Sleep((int)(double.Parse(args[1]) * 1000));
                stopPlay = true; t.Join();
            }
        }
        catch (Exception ex) { Console.WriteLine("ERROR " + ex); Environment.Exit(1); }
    }

    // ---------------------------------------------------------------- presets

    static string Headphones() { return AudioDevices.List(EDataFlow.Render).First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0).Id; }

    /// <summary>Every headphone curve a user can pick: game profiles (with the MOMENTUM 5 correction, as applied) and the graphic presets.</summary>
    static List<Tuple<string, EqProfile>> Presets(bool withCorrection)
    {
        var list = new List<Tuple<string, EqProfile>>();
        foreach (var p in GameProfiles.Builtins())
        {
            var e = p.Eq.Clone();
            if (withCorrection) { e.Extra.AddRange(GameProfiles.Momentum5Correction()); e.ExtraName = GameProfiles.CorrectionName; }
            e.AutoPreamp = true;
            list.Add(Tuple.Create("profile: " + p.Name, e));
        }
        foreach (var kv in EqEditor.HeadphonePresets())
        {
            var e = new EqProfile { Mode = "graphic", Bands = (double[])kv.Value.Clone(), AutoPreamp = true, Enabled = true };
            if (withCorrection) e.Extra.AddRange(GameProfiles.Momentum5Correction());
            list.Add(Tuple.Create("preset: " + kv.Key, e));
        }
        return list;
    }

    // ---------------------------------------------------------------- analysis helpers

    static float[] Pink(int n, int seed, double rmsDb)
    {
        var r = new Random(seed); var o = new float[n];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, e = 0;
        for (int i = 0; i < n; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double p = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362; b6 = w * 0.115926;
            o[i] = (float)p; e += p * p;
        }
        float g = (float)(Math.Pow(10, rmsDb / 20) / Math.Sqrt(e / n));
        for (int i = 0; i < n; i++) o[i] *= g;
        return o;
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    /// <summary>Power per 1/3 octave (dB), Welch average with a Hann window.</summary>
    static double[] Bands(float[] x) { return Bands(x, Fs); }

    static double[] Bands(float[] x, int fs)
    {
        int N = fs > 48000 ? 32768 : 16384;
        var pw = new double[N / 2 + 1];
        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
        int frames = 0;
        var re = new double[N]; var im = new double[N];
        for (int s = 0; s + N <= x.Length; s += N / 2)
        {
            for (int i = 0; i < N; i++) { re[i] = x[s + i] * win[i]; im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k <= N / 2; k++) pw[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        var res = new double[Third.Length];
        for (int b = 0; b < Third.Length; b++)
        {
            double lo = Third[b] / Math.Pow(2, 1 / 6.0), hi = Third[b] * Math.Pow(2, 1 / 6.0), sum = 0;
            for (int k = (int)Math.Ceiling(lo * N / fs); k <= Math.Min(N / 2, (int)(hi * N / fs)); k++) sum += pw[k];
            // Parseval: one-sided bin power -> mean-square signal power in the band (comparable across rates).
            res[b] = 10 * Math.Log10(Math.Max(2 * sum / (Math.Max(1, frames) * (double)N * N * 0.375), 1e-20));
        }
        return res;
    }

    /// <summary>Loudness per ITU-R BS.1770 (K-weighted mean square, ungated) in LUFS-ish dB.</summary>
    static double Loudness(float[] x) { return Loudness(x, Fs); }

    static double Loudness(float[] x, int fs)
    {
        // K-weighting (BS.1770 pre-filter + RLB high pass) designed for this sample rate, as in libebur128.
        double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196, K = Math.Tan(Math.PI * f0 / fs);
        double Vh = Math.Pow(10, G / 20), Vb = Math.Pow(Vh, 0.4996667741545416), a0 = 1 + K / Q + K * K;
        double[] b1 = { (Vh + Vb * K / Q + K * K) / a0, 2 * (K * K - Vh) / a0, (Vh - Vb * K / Q + K * K) / a0 }, a1 = { 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0 };
        f0 = 38.13547087602444; Q = 0.5003270373238773; K = Math.Tan(Math.PI * f0 / fs); a0 = 1 + K / Q + K * K;
        double[] b2 = { 1.0, -2.0, 1.0 }, a2 = { 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0 };
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0, u1 = 0, u2 = 0, v1 = 0, v2 = 0, sum = 0;
        foreach (float s in x)
        {
            double y = b1[0] * s + b1[1] * x1 + b1[2] * x2 - a1[0] * y1 - a1[1] * y2; x2 = x1; x1 = s; y2 = y1; y1 = y;
            double v = b2[0] * y + b2[1] * u1 + b2[2] * u2 - a2[0] * v1 - a2[1] * v2; u2 = u1; u1 = y; v2 = v1; v1 = v;
            sum += v * v;
        }
        return -0.691 + 10 * Math.Log10(Math.Max(sum / x.Length, 1e-20));
    }

    /// <summary>The app's own model of a curve: preamp + its biquads.</summary>
    static float[] ApplyModel(EqProfile e, float[] x)
    {
        var y = (float[])x.Clone();
        if (!e.Enabled) return y;
        float g = (float)Math.Pow(10, e.EffectivePreamp(Fs) / 20);
        var fl = e.AllFilters().Select(f => Biquad.Create(f, Fs)).ToList();
        for (int i = 0; i < y.Length; i++) { float v = y[i] * g; foreach (var q in fl) v = q.Process(v); y[i] = v; }
        return y;
    }

    // ---------------------------------------------------------------- WASAPI

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static volatile bool stopPlay;

    /// <summary>
    /// Plays sine tones into the game device (as a game would) and measures THD+N at the headphones' loopback:
    /// the whole chain - 3D engine, limiter, resampling, Equalizer APO - at quiet, loud and full-scale levels.
    /// </summary>
    static void Thd()
    {
        var sink = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith(AppCore.GameOutName));
        string phones = Headphones();
        Console.WriteLine(string.Format("{0,-8} {1,8} {2,12} {3,12} {4,10}", "tone", "level", "out level", "THD+N", "THD+N %"));
        foreach (double f in new[] { 100.0, 1000.0, 4000.0 })
            foreach (double lvl in new[] { -30.0, -12.0, -1.0, 0.0 })
            {
                var tone = new float[Fs * 2];
                double a = Math.Pow(10, lvl / 20);
                for (int i = 0; i < tone.Length; i++) tone[i] = (float)(a * Math.Sin(2 * Math.PI * f * i / Fs));
                stopPlay = false;
                var t = new Thread(() => PlayLoop(sink.Id, tone)) { IsBackground = true };
                t.Start();
                Thread.Sleep(2500);   // let the dynamics settle: measure the steady state
                int ch, rate;
                var cap = Loopback(phones, 1.0, out ch, out rate);
                stopPlay = true; t.Join();
                Thread.Sleep(300);
                // THD+N: notch the fundamental (bins +-3 around it) in a Hann-windowed FFT; everything else 20 Hz-20 kHz is distortion + noise.
                const int N = 32768;
                var x = cap[0];
                if (x.Length < N + 1000) { Console.WriteLine("  short capture"); continue; }
                var re = new double[N]; var im = new double[N];
                for (int i = 0; i < N; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N); re[i] = x[1000 + i] * w; }
                Fft(re, im);
                int k0 = (int)Math.Round(f * N / rate);
                // The 3D output buffer trims clock drift by up to +-0.3% playback speed: follow the real peak.
                int span = Math.Max(4, (int)(k0 * 0.006));
                for (int k = Math.Max(1, k0 - span), best = k0; k <= k0 + span; k++)
                    if (re[k] * re[k] + im[k] * im[k] > re[best] * re[best] + im[best] * im[best]) { best = k; k0 = k; }
                double fund = 0, rest = 0;
                for (int k = (int)(20.0 * N / rate); k <= (int)(20000.0 * N / rate); k++)
                {
                    double p = re[k] * re[k] + im[k] * im[k];
                    if (Math.Abs(k - k0) <= 6) fund += p; else rest += p;
                }
                double outDb = 10 * Math.Log10(x.Skip(1000).Take(N).Average(v => (double)v * v) * 2 + 1e-20);
                double thd = 10 * Math.Log10(rest / Math.Max(fund, 1e-30));
                Console.WriteLine(string.Format("{0,-8} {1,6:0} dB {2,9:0.0} dBFS {3,9:0.0} dB {4,9:0.000}", f + " Hz", lvl, outDb, thd, 100 * Math.Pow(10, thd / 20)));
            }
    }

    /// <summary>Steady high tones through the live 3D chain: how much their level wobbles over 8 s (drift correction).</summary>
    static void Swing()
    {
        var sink = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith(AppCore.GameOutName));
        string phones = Headphones();
        foreach (double f in new[] { 1000.0, 10000.0, 16000.0 })
        {
            var tone = new float[Fs * 2];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.25 * Math.Sin(2 * Math.PI * f * i / Fs));
            stopPlay = false;
            var t = new Thread(() => PlayLoop(sink.Id, tone)) { IsBackground = true };
            t.Start();
            Thread.Sleep(2000);
            int ch, rate;
            var cap = Loopback(phones, 8.0, out ch, out rate);
            stopPlay = true; t.Join();
            Thread.Sleep(300);
            int win = rate / 50; double lo = 999, hi = -999;
            for (int s = rate / 2; s + win <= cap[0].Length - rate / 2; s += win)
            {
                double e = 0; for (int i = s; i < s + win; i++) e += cap[0][i] * cap[0][i];
                double db = 10 * Math.Log10(e / win + 1e-20);
                lo = Math.Min(lo, db); hi = Math.Max(hi, db);
            }
            Console.WriteLine(string.Format("{0,6} Hz: level range {1:0.00} .. {2:0.00} dBFS, swing {3:0.00} dB", f, lo, hi, hi - lo));
        }
    }

    static void PlayLoop(string id, float[] mono)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(2, 4, Fs, true, 32);
        int hr = ac.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 2000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) throw new Exception("play init 0x" + hr.ToString("X8"));
        uint buf; ac.GetBufferSize(out buf);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        ac.Start();
        int pos = 0; var tmp = new float[buf * 2];
        while (!stopPlay)
        {
            Thread.Sleep(10);
            uint pad; ac.GetCurrentPadding(out pad);
            int n = (int)(buf - pad);
            if (n <= 0) continue;
            IntPtr data; rc.GetBuffer((uint)n, out data);
            for (int i = 0; i < n; i++) { float v = mono[pos]; tmp[2 * i] = v; tmp[2 * i + 1] = v; if (++pos >= mono.Length) pos = 0; }
            Marshal.Copy(tmp, 0, data, n * 2);
            rc.ReleaseBuffer((uint)n, 0);
        }
        ac.Stop();
    }

    /// <summary>Loopback-captures a render device: returns one array per channel.</summary>
    static float[][] Loopback(string id, double seconds, out int channels) { int r; return Loopback(id, seconds, out channels, out r); }

    static float[][] Loopback(string id, double seconds, out int channels, out int rate)
    {
        var ac = Open(id);
        IntPtr fmt; ac.GetMixFormat(out fmt);
        var wf = (WaveFormatEx)Marshal.PtrToStructure(fmt, typeof(WaveFormatEx));
        channels = wf.nChannels; rate = (int)wf.nSamplesPerSec;
        int hr = ac.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero);   // AUDCLNT_STREAMFLAGS_LOOPBACK
        if (hr != 0) throw new Exception("loopback init 0x" + hr.ToString("X8"));
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc;
        int ch = channels;
        var outp = new List<float>[ch]; for (int c = 0; c < ch; c++) outp[c] = new List<float>();
        var buf = new float[Fs * ch];
        ac.Start();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            Thread.Sleep(5);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cap.GetBuffer(out data, out frames, out fl, out a, out b);
                int count = (int)frames * ch;
                if ((fl & 2) != 0 || count > buf.Length) for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) outp[c].Add(0);
                else
                {
                    Marshal.Copy(data, buf, 0, count);
                    for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) outp[c].Add(buf[i * ch + c]);
                }
                cap.ReleaseBuffer(frames);
                cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        Marshal.FreeCoTaskMem(fmt);
        return outp.Select(l => l.ToArray()).ToArray();
    }

    // ---------------------------------------------------------------- eapo: real measurement of every preset

    static void Eapo()
    {
        string phones = Headphones();
        string cfg = Path.Combine(EqualizerApo.ConfigPath, "StreamLite.txt");
        string original = File.ReadAllText(cfg);
        var pink = Pink(Fs * 10, 9, -30);
        var refBands = Bands(pink);
        var player = new Thread(() => PlayLoop(phones, pink)) { IsBackground = true };
        player.Start();
        Console.WriteLine("Pink noise at -30 dBFS on the headphones; each preset written to Equalizer APO and measured from the loopback.");
        Console.WriteLine();
        Console.WriteLine(string.Format("{0,-40} {1,8} {2,8} {3,8} {4,9} {5,9}", "preset", "maxErr", "rmsErr", "preamp", "loudness", "headroom"));
        try
        {
            int ch, rate = Fs;
            Func<EqProfile, double[]> measure = e =>
            {
                var d = new Dictionary<string, EqProfile> { { phones, e } };
                File.WriteAllText(cfg, EqualizerApo.BuildConfig(d, k => "bench"));
                Thread.Sleep(1600);
                var cap = Loopback(phones, 4, out ch, out rate);
                var bands = Bands(cap[0], rate);
                return bands.Concat(new[] { Loudness(cap[0], rate), cap[0].Max(v => Math.Abs(v)) }).ToArray();
            };
            var flat = measure(new EqProfile { Enabled = false });
            var flat2 = measure(new EqProfile { Enabled = false });
            Console.WriteLine("repeatability (flat vs flat): max " + Enumerable.Range(1, Third.Length - 2).Max(b => Math.Abs(flat2[b] - flat[b])).ToString("0.00") + " dB");
            var errSum = new double[Third.Length]; int presets = 0;
            var report = new List<Dictionary<string, object>>();
            Console.WriteLine("(headphones run at " + rate / 1000 + " kHz)");
            double flatLoud = flat[Third.Length];
            foreach (var t in Presets(true))
            {
                presets++;
                var m = measure(t.Item2);
                var fl = t.Item2.AllFilters(); double pre0 = t.Item2.EffectivePreamp(Fs);
                double maxErr = 0, sq = 0; int n = 0;
                var mList = new List<double>(); var iList = new List<double>();
                for (int b = 1; b < Third.Length - 1; b++)   // 31.5 Hz .. 12.5 kHz
                {
                    // Intended: the app's curve at the device rate, power-averaged over the band for pink noise.
                    double lo = Third[b] / Math.Pow(2, 1 / 6.0), hi = Third[b] * Math.Pow(2, 1 / 6.0), acc = 0; int cnt = 0;
                    for (double f = lo; f <= hi; f *= 1.01) { acc += Math.Pow(10, Biquad.ResponseDb(fl, f, rate) / 10); cnt++; }
                    double measured = m[b] - flat[b], intended = pre0 + 10 * Math.Log10(acc / cnt);
                    double err = measured - intended;
                    maxErr = Math.Max(maxErr, Math.Abs(err)); sq += err * err; n++;
                    errSum[b] += err;
                    mList.Add(Math.Round(measured, 2)); iList.Add(Math.Round(intended, 2));
                }
                double loud = m[Third.Length] - flatLoud;
                // Worst case: how far the curve (incl. preamp) peaks above 0 dB anywhere.
                var filters = t.Item2.AllFilters(); double pre = t.Item2.EffectivePreamp(Fs), peak = double.MinValue;
                for (int i = 0; i < 300; i++) peak = Math.Max(peak, pre + Biquad.ResponseDb(filters, 20 * Math.Pow(1000, i / 299.0), rate));
                Console.WriteLine(string.Format("{0,-40} {1,7:0.00}dB {2,6:0.00}dB {3,7:0.0}dB {4,8:+0.0;-0.0}dB {5,8:0.0}dB", t.Item1, maxErr, Math.Sqrt(sq / n), pre, loud, -peak));
                report.Add(new Dictionary<string, object> { { "name", t.Item1 }, { "measured", mList }, { "intended", iList }, { "maxErr", Math.Round(maxErr, 2) },
                    { "rmsErr", Math.Round(Math.Sqrt(sq / n), 2) }, { "preamp", Math.Round(pre, 1) }, { "loudness", Math.Round(loud, 1) } });
            }
            var freqs = Enumerable.Range(1, Third.Length - 2).Select(b => Third[b]).ToList();
            File.WriteAllText("presets.json", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "freqs", freqs }, { "rate", rate }, { "presets", report } }));
            Console.WriteLine();
            Console.WriteLine("mean error per band (measured - intended):");
            Console.WriteLine("  " + string.Join(" ", Enumerable.Range(1, Third.Length - 2).Select(b => Third[b] + ":" + (errSum[b] / presets).ToString("+0.0;-0.0"))));
        }
        finally
        {
            stopPlay = true;
            File.WriteAllText(cfg, original);
            Thread.Sleep(300);
        }
    }

    // ---------------------------------------------------------------- rec: capture what's playing

    static void Rec(double seconds, string name)
    {
        string phones = Headphones();
        var sink = AudioDevices.List(EDataFlow.Render).FirstOrDefault(d => d.Name.StartsWith(AppCore.GameOutName));
        float[][] a = null, b = null; int cha = 0, chb = 0, ra = Fs, rb = Fs;
        var t1 = sink != null ? new Thread(() => a = Loopback(sink.Id, seconds, out cha, out ra)) : null;
        var t2 = new Thread(() => b = Loopback(phones, seconds, out chb, out rb));
        if (t1 != null) t1.Start();
        t2.Start();
        if (t1 != null) t1.Join();
        t2.Join();
        if (a != null) { WriteWav(name + "-source.wav", a, ra); Console.WriteLine("source (" + sink.Name + "): " + Describe(a)); }
        WriteWav(name + "-phones.wav", b, rb); Console.WriteLine("headphones: " + Describe(b));
    }

    static string Describe(float[][] ch)
    {
        return string.Join(" | ", ch.Select((c, i) => "ch" + i + " rms " + (10 * Math.Log10(c.Sum(v => (double)v * v) / Math.Max(1, c.Length) + 1e-20)).ToString("0.0") + " peak " + (20 * Math.Log10(c.Max(v => Math.Abs(v)) + 1e-10)).ToString("0.0")).Where((s, i) => ch[i].Any(v => v != 0)));
    }

    static void WriteWav(string path, float[][] ch) { WriteWav(path, ch, Fs); }

    static void WriteWav(string path, float[][] ch, int rate)
    {
        int n = ch[0].Length, c = ch.Length;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + n * c * 4); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)3); w.Write((short)c); w.Write(rate); w.Write(rate * c * 4); w.Write((short)(c * 4)); w.Write((short)32);
            w.Write("data".ToCharArray()); w.Write(n * c * 4);
            for (int i = 0; i < n; i++) for (int k = 0; k < c; k++) w.Write(ch[k][i]);
        }
    }

    static float[][] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int c = BitConverter.ToInt16(bytes, 22), n = BitConverter.ToInt32(bytes, 40) / (4 * c);
        var o = new float[c][]; for (int k = 0; k < c; k++) o[k] = new float[n];
        for (int i = 0; i < n; i++) for (int k = 0; k < c; k++) o[k][i] = BitConverter.ToSingle(bytes, 44 + (i * c + k) * 4);
        return o;
    }

    // ---------------------------------------------------------------- xfer: what the live chain did (source -> headphones)

    static void Xfer(string name)
    {
        var src = ReadWav(name + "-source.wav"); var outp = ReadWav(name + "-phones.wav");
        int rate = ReadRate(name + "-phones.wav");
        Console.WriteLine("source " + ReadRate(name + "-source.wav") / 1000 + " kHz " + src.Length + " ch, headphones " + rate / 1000 + " kHz " + outp.Length + " ch");
        for (int c = 0; c < 2; c++)
        {
            var bi = Bands(src[c], ReadRate(name + "-source.wav")); var bo = Bands(outp[c], rate);
            Console.WriteLine("  " + (c == 0 ? "L" : "R") + " gain per band: " + string.Join(" ", new[] { 2, 4, 6, 8, 10, 13, 16, 19, 21, 22, 24, 25, 26, 27 }.Select(b => Third[b] + ":" + (bo[b] - bi[b]).ToString("+0.0;-0.0"))));
        }
        Console.WriteLine("  loudness: source " + Loudness(src[0], ReadRate(name + "-source.wav")).ToString("0.0") + " -> headphones " + Loudness(outp[0], rate).ToString("0.0") + " LUFS (L)");
        Func<float[], float[], double> corr = (a, b) => { double s2 = 0, ea = 0, eb = 0; int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) { s2 += a[i] * b[i]; ea += a[i] * a[i]; eb += b[i] * b[i]; } return s2 / Math.Sqrt(ea * eb + 1e-30); };
        Console.WriteLine("  L/R correlation: source " + corr(src[0], src[1]).ToString("0.00") + " -> headphones " + corr(outp[0], outp[1]).ToString("0.00"));
        double eL = outp[0].Sum(v => (double)v * v), eR = outp[1].Sum(v => (double)v * v);
        Console.WriteLine("  headphone balance L-R: " + (10 * Math.Log10(eL / (eR + 1e-30))).ToString("+0.0;-0.0") + " dB, peak " + (20 * Math.Log10(Math.Max(outp[0].Max(v => Math.Abs(v)), outp[1].Max(v => Math.Abs(v))) + 1e-10)).ToString("0.0") + " dBFS");
    }

    /// <summary>Channel check over time: whenever the source plays on one side only, how much leaks to the other headphone side.</summary>
    static void Lr(string name)
    {
        var src = ReadWav(name + "-source.wav"); var outp = ReadWav(name + "-phones.wav");
        int rs = ReadRate(name + "-source.wav"), ro = ReadRate(name + "-phones.wav");
        Func<float[], int, int, double> db = (x, a0, n) => { double q = 0; int e = Math.Min(x.Length, a0 + n); for (int i = a0; i < e; i++) q += x[i] * x[i]; return 10 * Math.Log10(q / Math.Max(1, e - a0) + 1e-20); };
        int win = 0, leftOnly = 0, rightOnly = 0, swapped = 0; double worstLeak = -200;
        for (double t = 0; t + 0.5 < src[0].Length / (double)rs; t += 0.5)
        {
            double sl = db(src[0], (int)(t * rs), rs / 2), sr = db(src[1], (int)(t * rs), rs / 2);
            // The output is a little later (3D latency) - look 50 ms on.
            double ol = db(outp[0], (int)((t + 0.05) * ro), ro / 2), or = db(outp[1], (int)((t + 0.05) * ro), ro / 2);
            if (Math.Max(sl, sr) < -80) continue;
            win++;
            string side = sl - sr > 20 ? "L" : sr - sl > 20 ? "R" : "both";
            if (side == "L") { leftOnly++; worstLeak = Math.Max(worstLeak, or - ol); if (or > ol) swapped++; }
            if (side == "R") { rightOnly++; worstLeak = Math.Max(worstLeak, ol - or); if (ol > or) swapped++; }
            Console.WriteLine(string.Format("  {0,5:0.0}s source L {1,6:0.0} R {2,6:0.0} ({3,-4}) -> headphones L {4,6:0.0} R {5,6:0.0}", t, sl, sr, side, ol, or));
        }
        Console.WriteLine("windows " + win + ", left-only " + leftOnly + ", right-only " + rightOnly + ", swapped " + swapped + ", worst leak to the silent side " + worstLeak.ToString("0.0") + " dB");
    }

    static int ReadRate(string path) { return BitConverter.ToInt32(File.ReadAllBytes(path), 24); }

    // ---------------------------------------------------------------- wav: analyse a recording and every preset on it

    static void Wav(string path)
    {
        var ch = ReadWav(path);
        // Trim leading/trailing silence.
        int s = 0, e = ch[0].Length;
        while (s < e && Math.Abs(ch[0][s]) < 1e-5 && Math.Abs(ch[1][s]) < 1e-5) s++;
        while (e > s && Math.Abs(ch[0][e - 1]) < 1e-5 && Math.Abs(ch[1][e - 1]) < 1e-5) e--;
        var L = ch[0].Skip(s).Take(e - s).ToArray(); var R = ch[1].Skip(s).Take(e - s).ToArray();
        Console.WriteLine(Path.GetFileName(path) + ": " + ((e - s) / (double)Fs).ToString("0.0") + " s of sound, " + ch.Length + " channels");
        if (L.Length < Fs) { Console.WriteLine("  (too short)"); return; }
        double lL = Loudness(L), lR = Loudness(R);
        double corr = 0, eL = 0, eR = 0; for (int i = 0; i < L.Length; i++) { corr += L[i] * R[i]; eL += L[i] * L[i]; eR += R[i] * R[i]; }
        Console.WriteLine("  loudness L " + lL.ToString("0.0") + " / R " + lR.ToString("0.0") + " LUFS, L/R correlation " + (corr / Math.Sqrt(eL * eR + 1e-20)).ToString("0.00") +
            ", peak " + (20 * Math.Log10(Math.Max(L.Max(v => Math.Abs(v)), R.Max(v => Math.Abs(v))) + 1e-10)).ToString("0.0") + " dBFS");
        var mono = new float[L.Length]; for (int i = 0; i < L.Length; i++) mono[i] = 0.5f * (L[i] + R[i]);
        var bands = Bands(mono);
        Console.WriteLine("  spectrum (dB re 1 kHz): " + string.Join(" ", new[] { 2, 5, 8, 10, 13, 16, 19, 22, 25, 27 }.Select(b => Third[b] + ":" + (bands[b] - bands[16]).ToString("+0;-0"))));
        Console.WriteLine();
        Console.WriteLine(string.Format("  {0,-40} {1,9} {2,9} {3,9} {4,9} {5,8}", "on this audio", "loudness", "bass<150", "mid 1-4k", "air>8k", "peak"));
        double baseLoud = Loudness(mono);
        Func<float[], double, double, double> region = (x, lo, hi) => { var bb = Bands(x); double sum = 0; for (int b = 0; b < Third.Length; b++) if (Third[b] >= lo && Third[b] <= hi) sum += Math.Pow(10, bb[b] / 10); return 10 * Math.Log10(sum + 1e-20); };
        double rb = region(mono, 20, 150), rm = region(mono, 1000, 4000), ra = region(mono, 8000, 20000);
        foreach (var t in Presets(true))
        {
            var y = ApplyModel(t.Item2, mono);
            Console.WriteLine(string.Format("  {0,-40} {1,8:+0.0;-0.0} {2,8:+0.0;-0.0} {3,8:+0.0;-0.0} {4,8:+0.0;-0.0} {5,7:0.0}", t.Item1,
                Loudness(y) - baseLoud, region(y, 20, 150) - rb, region(y, 1000, 4000) - rm, region(y, 8000, 20000) - ra, 20 * Math.Log10(y.Max(v => Math.Abs(v)) + 1e-10)));
        }
    }
}
