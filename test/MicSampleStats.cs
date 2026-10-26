using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// Your recorded voice sample (the raw mic, as the voice test keeps it) - what the mic itself delivers, then through the
// voice chain with your settings and with parts of it changed: noise left in pauses, how far speech is changed in
// clearly spoken moments (per band, against the raw mic), holes inside words, level. Writes each version to
// test\shots\mic\ (stays on this PC). Usage: run.ps1 MicSampleStats [wav]
class MicSampleStats
{
    const int Fs = 48000;

    static float[] ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int n = (b.Length - 44) / 4; var x = new float[n];
        Buffer.BlockCopy(b, 44, x, 0, n * 4);
        return x;
    }

    internal static void WriteWav(string path, float[] x)
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

    static double Db(double p) { return 10 * Math.Log10(p + 1e-20); }

    // Band powers of a 20 ms frame (Hann, 1024-point FFT).
    static readonly double[] Edges = { 80, 200, 400, 800, 1600, 3200, 6400, 12000, 20000 };
    static double[] Bands(float[] x, int at, Fft fft, float[] re, float[] im)
    {
        for (int i = 0; i < 1024; i++) { double w = i < 960 ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 960) : 0; int k = at + i; re[i] = k < x.Length && i < 960 ? (float)(x[k] * w) : 0; im[i] = 0; }
        fft.Transform(re, im, false);
        var bp = new double[Edges.Length - 1];
        for (int k = 1; k < 512; k++)
        {
            double hz = k * 48000.0 / 1024;
            for (int b = 0; b < bp.Length; b++) if (hz >= Edges[b] && hz < Edges[b + 1]) bp[b] += re[k] * re[k] + im[k] * im[k];
        }
        return bp;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string path = args.Length > 0 ? args[0] : Path.Combine(AppSettings.Folder, "voice-sample.wav");
        var raw = ReadWav(path);
        Console.WriteLine("sample: " + (raw.Length / (double)Fs).ToString("0.0") + " s");
        // The raw mic.
        double pk = 0, dc = 0; int clip = 0;
        foreach (var v in raw) { pk = Math.Max(pk, Math.Abs(v)); dc += v; if (Math.Abs(v) > 0.98f) clip++; }
        var lv = new List<double>();
        for (int i = 0; i + 480 <= raw.Length; i += 480) { double p = 0; for (int k = i; k < i + 480; k++) p += raw[k] * raw[k]; lv.Add(Db(p / 480)); }
        var sorted = lv.OrderBy(v => v).ToList();
        double floor = sorted[sorted.Count / 10], loud = sorted[sorted.Count * 95 / 100];
        Console.WriteLine("raw mic: peak " + (20 * Math.Log10(pk)).ToString("0.0") + " dBFS, " + clip + " samples near clipping, DC " + (dc / raw.Length).ToString("0.00000") +
            ", background (10th pct of 10 ms blocks) " + floor.ToString("0.0") + " dB, loud speech (95th) " + loud.ToString("0.0") + " dB, speech-to-background " + (loud - floor).ToString("0.0") + " dB");

        var fft = new Fft(1024); var re = new float[1024]; var im = new float[1024];
        // Background spectrum (quietest 10% of frames) and speech spectrum (loudest 20%).
        var frames = new List<int>(); for (int i = 0; i + 1024 <= raw.Length; i += 480) frames.Add(i);
        var fl = frames.Select(f => new { f, e = Bands(raw, f, fft, re, im) }).ToList();
        var byE = fl.OrderBy(q => q.e.Sum()).ToList();
        var quiet = byE.Take(byE.Count / 10).ToList(); var speech = byE.Skip(byE.Count * 8 / 10).ToList();
        Console.Write("bands (Hz):          "); for (int b = 0; b < Edges.Length - 1; b++) Console.Write((Edges[b] + "-" + Edges[b + 1]).PadLeft(11)); Console.WriteLine();
        Console.Write("raw background dB:   "); for (int b = 0; b < Edges.Length - 1; b++) Console.Write(Db(quiet.Average(q => q.e[b])).ToString("0.0").PadLeft(11)); Console.WriteLine();
        Console.Write("raw speech dB:       "); for (int b = 0; b < Edges.Length - 1; b++) Console.Write(Db(speech.Average(q => q.e[b])).ToString("0.0").PadLeft(11)); Console.WriteLine();

        var S = AppSettings.Load();
        var configs = new List<KeyValuePair<string, Action<AppSettings>>>
        {
            new KeyValuePair<string, Action<AppSettings>>("yours", s => { }),
            new KeyValuePair<string, Action<AppSettings>>("noise removal off", s => { s.NoiseRemoval = false; }),
            new KeyValuePair<string, Action<AppSettings>>("not realtime", s => { s.NsRealtime = false; }),
            new KeyValuePair<string, Action<AppSettings>>("no gate/expander", s => { s.Gate = false; s.GateExpander = false; }),
            new KeyValuePair<string, Action<AppSettings>>("no comp/auto level", s => { s.Compressor = false; s.AutoLevel = false; }),
            new KeyValuePair<string, Action<AppSettings>>("no de-esser", s => { s.DeEsser = false; }),
            new KeyValuePair<string, Action<AppSettings>>("nothing but NS", s => { s.Gate = false; s.GateExpander = false; s.Compressor = false; s.AutoLevel = false; s.DeEsser = false; s.MicEq = new EqProfile { Enabled = false }; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn only", s => { s.NsRealtime = false; s.Gate = false; s.GateExpander = false; s.Compressor = false; s.AutoLevel = false; s.DeEsser = false; s.MicEq = new EqProfile { Enabled = false }; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn no comp-level", s => { s.NsRealtime = false; s.Compressor = false; s.AutoLevel = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn no gate", s => { s.NsRealtime = false; s.Gate = false; s.GateExpander = false; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn strength 30", s => { s.NsRealtime = false; s.Strength = 30; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn strength 80", s => { s.NsRealtime = false; s.Strength = 80; }),
            new KeyValuePair<string, Action<AppSettings>>("dfn strength 100", s => { s.NsRealtime = false; s.Strength = 100; }),
            new KeyValuePair<string, Action<AppSettings>>("rnnoise own", s => { s.NsRealtime = false; s.NsEngine = NoiseEngines.Ai; }),
            new KeyValuePair<string, Action<AppSettings>>("rnnoise realtime", s => { s.NsRealtime = true; s.NsEngine = NoiseEngines.Ai; }),
            new KeyValuePair<string, Action<AppSettings>>("classic own", s => { s.NsRealtime = false; s.NsEngine = NoiseEngines.Classic; }),
        };
        string only = Environment.GetEnvironmentVariable("MIC_CONFIG");
        foreach (var c in configs)
        {
            if (only != null && !c.Key.Contains(only)) continue;
            var s = AppSettings.Load(); c.Value(s);
            var chain = new VoiceChain();
            chain.Suppressor.Synchronous = true;
            chain.Configure(s);
            var blk = new float[144];
            // DeepFilterNet loads in the background: let it, on the start of the sample (not kept).
            for (int k = 0; k < 4000 && chain.Suppressor.Dfn != 0 && s.NoiseRemoval; k++)
            {
                Array.Copy(raw, (k * 144) % (Fs / 2), blk, 0, 144); chain.Process(blk, 144, false);
                var d = chain.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
                System.Threading.Thread.Sleep(2);
            }
            chain = new VoiceChain(); chain.Suppressor.Synchronous = true; chain.Configure(s);
            for (int k = 0; k < 4000 && chain.Suppressor.Dfn != 0 && s.NoiseRemoval; k++)
            {
                Array.Clear(blk, 0, 144); chain.Process(blk, 144, false);
                var d = chain.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break;
                System.Threading.Thread.Sleep(2);
            }
            var y = new float[raw.Length];
            for (int i = 0; i + 144 <= raw.Length; i += 144) { Array.Copy(raw, i, blk, 0, 144); chain.Process(blk, 144, false); Array.Copy(blk, 0, y, i, 144); }
            chain.Suppressor.ReleaseEngine();
            // Delay against the raw mic.
            int best = 0; double bc = double.MinValue;
            for (int lag = 0; lag < 3000; lag += 2) { double cc = 0; for (int i = Fs; i < Math.Min(raw.Length - 3000, 6 * Fs); i += 3) cc += raw[i] * (double)y[i + lag]; if (cc > bc) { bc = cc; best = lag; } }
            var ya = new float[raw.Length]; for (int i = 0; i + best < raw.Length; i++) ya[i] = y[i + best];
            // Overall gain in speech (so EQ/compressor level changes don't count as distortion).
            double gs = 0; int gn = 0;
            var bandDev = new double[Edges.Length - 1]; var bandDevAbs = new double[Edges.Length - 1];
            foreach (var q in speech) { var e = Bands(ya, q.f, fft, re, im); gs += Db(e.Sum()) - Db(q.e.Sum()); gn++; }
            gs /= gn;
            foreach (var q in speech)
            {
                var e = Bands(ya, q.f, fft, re, im);
                for (int b = 0; b < bandDev.Length; b++) { double d = Db(e[b]) - Db(q.e[b]) - gs; bandDev[b] += d; bandDevAbs[b] += Math.Abs(d); }
            }
            double qOut = 0; foreach (var q in quiet) qOut += Bands(ya, q.f, fft, re, im).Sum();
            double qIn = quiet.Sum(q => q.e.Sum());
            // Holes: raw clearly speaking (within 20 dB of loud), output 12 dB below its own level 30 ms around.
            int holes = 0, spoken = 0;
            var ol = new List<double>(); for (int i = 0; i + 480 <= raw.Length; i += 480) { double p = 0; for (int k = i; k < i + 480; k++) p += ya[k] * ya[k]; ol.Add(Db(p / 480)); }
            for (int i = 3; i < Math.Min(lv.Count, ol.Count) - 3; i++)
            {
                if (lv[i] < loud - 20) continue;
                spoken++;
                double around = Math.Max(Math.Max(ol[i - 3], ol[i - 2]), Math.Max(ol[i + 2], ol[i + 3]));
                if (ol[i] < around - 12 && lv[i] > Math.Min(lv[i - 2], lv[i + 2]) - 3) holes++;
            }
            double opk = 0; foreach (var v in ya) opk = Math.Max(opk, Math.Abs(v));
            Console.WriteLine();
            Console.WriteLine(c.Key + ": delay " + (best / 48.0).ToString("0.0") + " ms, speech gain " + gs.ToString("+0.0;-0.0") + " dB, background " + (Db(qOut) - Db(qIn)).ToString("+0.0;-0.0") +
                " dB (vs speech " + (Db(qOut) - Db(qIn) - gs).ToString("+0.0;-0.0") + "), holes in words " + holes + "/" + spoken + ", peak " + (20 * Math.Log10(opk + 1e-9)).ToString("0.0") + " dBFS");
            Console.Write("  speech change dB:  "); for (int b = 0; b < bandDev.Length; b++) Console.Write((bandDev[b] / speech.Count).ToString("+0.0;-0.0").PadLeft(11)); Console.WriteLine();
            Console.Write("  frame-to-frame |dB|:"); for (int b = 0; b < bandDev.Length; b++) Console.Write((bandDevAbs[b] / speech.Count).ToString("0.0").PadLeft(11)); Console.WriteLine();
            WriteWav(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\mic\" + c.Key.Replace(' ', '-').Replace('/', '-') + ".wav"), ya);
        }
        WriteWav(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\mic\raw.wav"), raw);
    }
}
