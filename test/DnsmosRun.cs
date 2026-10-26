using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Audio;

// Microsoft's DNSMOS P.835 (the DNS Challenge's quality judge, a network trained on listener ratings): speech quality
// (SIG), background (BAK) and overall (OVRL), 1-5, for each WAV given (or every WAV in a folder). The model lives in
// test\dnsmos (downloaded, not shipped). As dnsmos_local.py: 16 kHz, 9.01 s windows 1 s apart, polynomial-mapped scores.
// Usage: run.ps1 DnsmosRun <wav or folder>...
class DnsmosRun
{
    internal sealed class Scorer : IDisposable
    {
        const int Len = 144160;   // 9.01 s at 16 kHz
        readonly OnnxSession s;
        public Scorer()
        {
            string dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\dnsmos"));
            s = new OnnxSession(Path.Combine(dir, "sig_bak_ovr.onnx"), false, 4);
        }
        public void Dispose() { s.Dispose(); }

        static double Poly(double[] c, double x) { double y = 0; foreach (var k in c) y = y * x + k; return y; }

        /// <summary>48 kHz mono in; SIG, BAK, OVRL out.</summary>
        public double[] Score(float[] x48)
        {
            var a = To16k(x48);
            if (a.Length < Len) { var b = new List<float>(); while (b.Count < Len) b.AddRange(a); a = b.ToArray(); }
            int hops = (int)Math.Floor(a.Length / 16000.0 - 9.01) + 1;
            var sum = new double[3]; int n = 0;
            var inp = new float[Len]; var outp = new float[3];
            for (int h = 0; h < hops; h++)
            {
                int at = h * 16000; if (at + Len > a.Length) continue;
                Array.Copy(a, at, inp, 0, Len);
                s.Run(new[] { inp }, new[] { new long[] { 1, Len } }, new[] { outp });
                sum[0] += Poly(new[] { -0.08397278, 1.22083953, 0.0052439 }, outp[0]);
                sum[1] += Poly(new[] { -0.13166888, 1.60915514, -0.39604546 }, outp[1]);
                sum[2] += Poly(new[] { -0.06766283, 1.11546468, 0.04602535 }, outp[2]);
                n++;
            }
            return new[] { sum[0] / n, sum[1] / n, sum[2] / n };
        }

        static float[] lp;
        static float[] To16k(float[] x)
        {
            if (lp == null)
            {
                const int T = 191; lp = new float[T]; double fc = 7600.0 / 48000, sum = 0;
                for (int i = 0; i < T; i++)
                {
                    double m = i - (T - 1) / 2.0, sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
                    double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (T - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (T - 1));
                    lp[i] = (float)(sinc * w); sum += lp[i];
                }
                for (int i = 0; i < T; i++) lp[i] = (float)(lp[i] / sum);
            }
            int half = lp.Length / 2, n = x.Length / 3; var y = new float[n];
            for (int o = 0; o < n; o++)
            {
                double acc = 0; int c = o * 3;
                for (int t = 0; t < lp.Length; t++) { int k = c + t - half; if (k >= 0 && k < x.Length) acc += x[k] * lp[t]; }
                y[o] = (float)acc;
            }
            return y;
        }
    }

    internal static float[] ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int fmt = BitConverter.ToInt16(b, 20), ch = BitConverter.ToInt16(b, 22), bits = BitConverter.ToInt16(b, 34);
        int at = 12; int dataAt = 44, dataLen = b.Length - 44;
        while (at + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, at, 4); int sz = BitConverter.ToInt32(b, at + 4);
            if (id == "data") { dataAt = at + 8; dataLen = Math.Min(sz, b.Length - dataAt); break; }
            at += 8 + sz + (sz & 1);
        }
        int bps = bits / 8, frames = dataLen / (bps * ch); var x = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int p = dataAt + i * bps * ch;   // first channel
            x[i] = fmt == 3 ? BitConverter.ToSingle(b, p) : bits == 16 ? BitConverter.ToInt16(b, p) / 32768f : bits == 24 ? ((b[p] << 8 | b[p + 1] << 16 | b[p + 2] << 24) >> 8) / 8388608f : BitConverter.ToInt32(b, p) / 2147483648f;
        }
        return x;
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var files = new List<string>();
        foreach (var a in args) { if (Directory.Exists(a)) files.AddRange(Directory.GetFiles(a, "*.wav").OrderBy(f => f)); else files.Add(a); }
        using (var sc = new Scorer())
        {
            Console.WriteLine("file".PadRight(34) + "SIG   BAK   OVRL");
            foreach (var f in files)
            {
                var r = sc.Score(ReadWav(f));
                Console.WriteLine(Path.GetFileNameWithoutExtension(f).PadRight(34) + r[0].ToString("0.00") + "  " + r[1].ToString("0.00") + "  " + r[2].ToString("0.00"));
            }
        }
    }
}
