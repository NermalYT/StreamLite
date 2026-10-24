using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Captures the raw mic and the processed mic (CABLE Output) at the same time and compares their
// tone (octave bands, while you're talking), background noise and peak level.
class ToneProbe
{
    static readonly double[] Centers = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    class Result { public List<double[]> Blocks = new List<double[]>(); public List<double> Rms = new List<double>(); public double Peak; }

    static void Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 10;
        var mics = AudioDevices.List(EDataFlow.Capture);
        var raw = mics.First(d => d.Name.Contains("Yeti"));
        var cable = mics.First(d => d.Name.Contains("CABLE Output"));
        Result rr = null, rc = null;
        var t1 = new Thread(() => rr = Capture(raw.Id, secs));
        var t2 = new Thread(() => rc = Capture(cable.Id, secs));
        t1.Start(); t2.Start(); t1.Join(); t2.Join();

        int n = Math.Min(rr.Rms.Count, rc.Rms.Count);
        var sorted = rr.Rms.Take(n).OrderBy(x => x).ToList();
        double speechGate = sorted[(int)(n * 0.7)], quietGate = sorted[(int)(n * 0.2)];
        Console.WriteLine("Raw peak " + Db(rr.Peak).ToString("0.0") + " dBFS, processed peak " + Db(rc.Peak).ToString("0.0") + " dBFS");
        Console.WriteLine("Background (quietest 20%): raw " + Avg(rr, n, i => rr.Rms[i] <= quietGate) + " dB, processed " + Avg(rc, n, i => rr.Rms[i] <= quietGate) + " dB");
        Console.WriteLine("Voice (loudest 30%):       raw " + Avg(rr, n, i => rr.Rms[i] >= speechGate) + " dB, processed " + Avg(rc, n, i => rr.Rms[i] >= speechGate) + " dB");
        Console.WriteLine("Tone while talking (dB relative to 1 kHz):");
        Console.WriteLine("  band      " + string.Join("", Centers.Select(c => (c >= 1000 ? (c / 1000) + "k" : c.ToString()).PadLeft(7))));
        Console.WriteLine("  raw       " + Tone(rr, n, i => rr.Rms[i] >= speechGate));
        Console.WriteLine("  processed " + Tone(rc, n, i => rr.Rms[i] >= speechGate));
    }

    static string Avg(Result r, int n, Func<int, bool> pick)
    {
        var v = Enumerable.Range(0, n).Where(pick).Select(i => r.Rms[i]).ToList();
        return v.Count == 0 ? "n/a" : Db(Math.Sqrt(v.Average(x => x * x))).ToString("0.0");
    }

    static string Tone(Result r, int n, Func<int, bool> pick)
    {
        var idx = Enumerable.Range(0, n).Where(pick).ToList();
        if (idx.Count == 0) return "n/a";
        var bands = new double[Centers.Length];
        foreach (int i in idx) for (int b = 0; b < bands.Length; b++) bands[b] += r.Blocks[i][b];
        double refDb = 10 * Math.Log10(bands[4] / idx.Count + 1e-20);
        return string.Join("", bands.Select(b => (10 * Math.Log10(b / idx.Count + 1e-20) - refDb).ToString("0.0").PadLeft(7)));
    }

    static double Db(double v) { return 20 * Math.Log10(Math.Max(v, 1e-9)); }

    static Result Capture(string id, int secs)
    {
        var res = new Result();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr mix; ac.GetMixFormat(out mix);
        var wf = (WaveFormatEx)Marshal.PtrToStructure(mix, typeof(WaveFormatEx));
        ac.Initialize(0, 0, 1000000, 0, mix, IntPtr.Zero);
        object svc; Guid cid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref cid, out svc);
        var cap = (IAudioCaptureClient)svc;
        int ch = wf.nChannels; double fs = wf.nSamplesPerSec;
        var bp = new double[Centers.Length][]; var st = new double[Centers.Length][];
        for (int k = 0; k < Centers.Length; k++)
        {
            double w0 = 2 * Math.PI * Math.Min(Centers[k], fs * 0.45) / fs, alpha = Math.Sin(w0) / (2 * 1.41), a0 = 1 + alpha;
            bp[k] = new[] { alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w0) / a0, (1 - alpha) / a0 };
            st[k] = new double[2];
        }
        int blockLen = (int)(fs * 0.05); // 50 ms blocks
        var acc = new double[Centers.Length]; double sq = 0; int cnt = 0;
        var data = new float[48000 * 8];
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr p; uint frames, fl; ulong a, b;
                cap.GetBuffer(out p, out frames, out fl, out a, out b);
                int total = (int)frames * ch;
                if (total <= data.Length) Marshal.Copy(p, data, 0, total);
                cap.ReleaseBuffer(frames);
                for (int i = 0; i < frames; i++)
                {
                    double x = data[i * ch];
                    res.Peak = Math.Max(res.Peak, Math.Abs(x));
                    sq += x * x;
                    for (int k = 0; k < Centers.Length; k++)
                    {
                        var c = bp[k]; var s = st[k];
                        double y = c[0] * x + s[0];
                        s[0] = c[1] * x - c[3] * y + s[1];
                        s[1] = c[2] * x - c[4] * y;
                        acc[k] += y * y;
                    }
                    if (++cnt == blockLen)
                    {
                        res.Rms.Add(Math.Sqrt(sq / cnt));
                        res.Blocks.Add(acc.Select(v => v / cnt).ToArray());
                        acc = new double[Centers.Length]; sq = 0; cnt = 0;
                    }
                }
                cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        return res;
    }
}
