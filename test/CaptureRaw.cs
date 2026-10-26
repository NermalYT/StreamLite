// include: DnsmosRun.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

// Records a capture device the way an app would (shared mode, its own format) and reports per channel: level,
// background, how alike the channels are (correlation, delay between them), clicks, and DNSMOS for each channel and the
// mix. "mic" = your StreamLite microphone (StreamLite must be closed: it holds it exclusively), "out" = StreamLite - In
// (what Discord gets). Keeps the recording in test\shots\live\ (stays on this PC).
// Usage: run.ps1 CaptureRaw mic|out|<name prefix> [seconds]
class CaptureRaw
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string which = args.Length > 0 ? args[0] : "mic";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 12;
        var all = AudioDevices.List(EDataFlow.Capture);
        AudioDeviceInfo cap;
        if (which == "mic") { string id = AppSettings.Load().MicId; cap = all.First(d => d.Id == id); }
        else if (which == "out") cap = all.First(d => d.Name.StartsWith("StreamLite - In") || d.Name.StartsWith("StreamLite Mic"));
        else cap = all.First(d => d.Name.StartsWith(which));
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(cap.Id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4), bits = Marshal.ReadInt16(fmt, 14);
        int hr = ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) { Console.WriteLine("Couldn't open " + cap.Name + " (0x" + hr.ToString("X8") + ") - is StreamLite still holding it?"); Environment.Exit(1); }
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cc = (IAudioCaptureClient)svc;
        var rec = new List<float>(rate * ch * secs); var tmp = new float[rate * ch];
        int silentPk = 0, discont = 0;
        ac.Start();
        Console.WriteLine("recording " + cap.Name + " (" + ch + " ch, " + rate + " Hz, " + bits + " bit) for " + secs + " s - talk now");
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint p; cc.GetNextPacketSize(out p);
            while (p > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cc.GetBuffer(out data, out frames, out fl, out a, out b);
                if ((fl & 1) != 0) discont++;
                if ((fl & 2) != 0) { silentPk++; for (int i = 0; i < frames * ch; i++) rec.Add(0); }
                else { Marshal.Copy(data, tmp, 0, (int)frames * ch); for (int i = 0; i < frames * ch; i++) rec.Add(tmp[i]); }
                cc.ReleaseBuffer(frames); cc.GetNextPacketSize(out p);
            }
        }
        ac.Stop();
        Marshal.ReleaseComObject(cc); Marshal.ReleaseComObject(ac); Marshal.ReleaseComObject(dev);
        var x = rec.ToArray(); int n = x.Length / ch;
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\live"); Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("HHmmss");
        WriteWav(Path.Combine(dir, which + "-" + stamp + ".wav"), x, ch, rate);
        Console.WriteLine(n + " frames, silent packets " + silentPk + ", discontinuities " + discont);

        // Per channel at 48 kHz.
        var chans = new float[ch][];
        for (int c = 0; c < ch; c++) { var m = new float[n]; for (int i = 0; i < n; i++) m[i] = x[i * ch + c]; chans[c] = rate == 48000 ? m : Resample(m, rate); }
        var mix = new float[chans[0].Length]; for (int c = 0; c < ch; c++) for (int i = 0; i < mix.Length; i++) mix[i] += chans[c][i] / ch;
        for (int c = 0; c < ch; c++) Stats("channel " + (c + 1), chans[c]);
        if (ch > 1) Stats("mix", mix);
        if (ch == 2)
        {
            // How alike: correlation at each delay up to +-2 ms (in loud parts).
            var a = chans[0]; var b = chans[1]; int best = 0; double bestC = -2;
            for (int d = -96; d <= 96; d++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int i = 200; i < a.Length - 200; i += 2) { double u = a[i], v = b[i + d]; xy += u * v; xx += u * u; yy += v * v; }
                double cr = xy / Math.Sqrt(xx * yy + 1e-30); if (cr > bestC) { bestC = cr; best = d; }
            }
            double c0 = 0, x0 = 0, y0 = 0; for (int i = 0; i < a.Length; i++) { c0 += a[i] * b[i]; x0 += a[i] * a[i]; y0 += b[i] * b[i]; }
            Console.WriteLine("channels: correlation " + (c0 / Math.Sqrt(x0 * y0 + 1e-30)).ToString("0.000") + " at the same time, best " + bestC.ToString("0.000") + " at " + best + " samples (" + (best / 48.0).ToString("0.00") + " ms)");
        }
        using (var sc = new DnsmosRun.Scorer())
        {
            for (int c = 0; c < ch; c++) { var r = sc.Score(chans[c]); Console.WriteLine("DNSMOS channel " + (c + 1) + ": SIG " + r[0].ToString("0.00") + "  BAK " + r[1].ToString("0.00") + "  OVRL " + r[2].ToString("0.00")); }
            if (ch > 1) { var r = sc.Score(mix); Console.WriteLine("DNSMOS mix: SIG " + r[0].ToString("0.00") + "  BAK " + r[1].ToString("0.00") + "  OVRL " + r[2].ToString("0.00")); }
        }
        WriteWav(Path.Combine(dir, which + "-" + stamp + "-mono48.wav"), mix, 1, 48000);
    }

    static double Db(double p) { return 10 * Math.Log10(p + 1e-20); }
    static readonly double[] Edges = { 80, 200, 400, 800, 1600, 3200, 6400, 12000, 20000 };

    internal static void Stats(string name, float[] x)
    {
        double pk = 0; int clip = 0, clicks = 0;
        for (int i = 0; i < x.Length; i++) { pk = Math.Max(pk, Math.Abs(x[i])); if (Math.Abs(x[i]) > 0.98f) clip++; }
        // Clicks: a sample far off the curve its neighbours predict, relative to the local level.
        for (int i = 2; i < x.Length - 2; i++)
        {
            double pred = (-x[i - 2] + 4 * x[i - 1] + 4 * x[i + 1] - x[i + 2]) / 6.0, e = Math.Abs(x[i] - pred);
            double loc = (Math.Abs(x[i - 2]) + Math.Abs(x[i - 1]) + Math.Abs(x[i + 1]) + Math.Abs(x[i + 2])) / 4 + 1e-4;
            if (e > 0.02 && e > 6 * loc) clicks++;
        }
        var lv = new List<double>(); for (int i = 0; i + 480 <= x.Length; i += 480) { double p = 0; for (int k = i; k < i + 480; k++) p += x[k] * x[k]; lv.Add(Db(p / 480)); }
        var s = lv.OrderBy(v => v).ToList();
        double floor = s[s.Count / 10], loud = s[s.Count * 95 / 100];
        Console.WriteLine(name + ": peak " + (20 * Math.Log10(pk + 1e-12)).ToString("0.0") + " dBFS, " + clip + " near clipping, " + clicks + " clicks, background " + floor.ToString("0.0") + " dB, loud speech " + loud.ToString("0.0") + " dB (" + (loud - floor).ToString("0.0") + " dB apart)");
        // Band levels: background (quietest 10% of 20 ms frames) and speech (loudest 20%), and the gap per band.
        var fft = new Fft(1024); var re = new float[1024]; var im = new float[1024];
        var fr = new List<double[]>();
        for (int at = 0; at + 1024 <= x.Length; at += 480)
        {
            for (int i = 0; i < 1024; i++) { double w = i < 960 ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 960) : 0; re[i] = (float)(x[at + i] * w); im[i] = 0; }
            fft.Transform(re, im, false);
            var bp = new double[Edges.Length - 1];
            for (int k = 1; k < 512; k++) { double hz = k * 46.875; for (int bb = 0; bb < bp.Length; bb++) if (hz >= Edges[bb] && hz < Edges[bb + 1]) bp[bb] += re[k] * re[k] + im[k] * im[k]; }
            fr.Add(bp);
        }
        var byE = fr.OrderBy(q => q.Sum()).ToList();
        var quiet = byE.Take(byE.Count / 10).ToList(); var sp = byE.Skip(byE.Count * 8 / 10).ToList();
        Console.Write("  band:      "); for (int bb = 0; bb < Edges.Length - 1; bb++) Console.Write((Edges[bb] + "-" + Edges[bb + 1]).PadLeft(11)); Console.WriteLine();
        Console.Write("  speech:    "); for (int bb = 0; bb < Edges.Length - 1; bb++) Console.Write(Db(sp.Average(q => q[bb])).ToString("0.0").PadLeft(11)); Console.WriteLine();
        Console.Write("  above bg:  "); for (int bb = 0; bb < Edges.Length - 1; bb++) Console.Write((Db(sp.Average(q => q[bb])) - Db(quiet.Average(q => q[bb]))).ToString("0.0").PadLeft(11)); Console.WriteLine();
    }

    static float[] Resample(float[] x, int rate)
    {
        double r = rate / 48000.0; int n = (int)(x.Length / r); var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i * r; int i0 = (int)t; double acc = 0, ws = 0;
            for (int k = i0 - 16; k <= i0 + 16; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                double d = t - k, s = Math.Abs(d) < 1e-9 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d), w = 0.5 + 0.5 * Math.Cos(Math.PI * d / 17);
                acc += x[k] * s * w; ws += s * w;
            }
            y[i] = (float)(acc / ws);
        }
        return y;
    }

    static void WriteWav(string path, float[] x, int ch, int rate)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int bytes = x.Length * 4;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)3); w.Write((short)ch); w.Write(rate); w.Write(rate * 4 * ch); w.Write((short)(4 * ch)); w.Write((short)32);
            w.Write("data".ToCharArray()); w.Write(bytes);
            foreach (var v in x) w.Write(v);
        }
    }
}
