// Live: StreamLite - Out played on two outputs at once (GameAudioEngine with an extra output), in plain or 3D mode.
// Quiet noise (-46 dBFS, the extra output another -20 dB) goes into StreamLite - Out for 4 s while both outputs are
// recorded (loopback); checks that the sound reaches both, without holes, and the extra output's level follows its
// volume. Usage: MultiOutLive [3d] [runs]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

class MultiOutLive
{
    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static void Main(string[] args)
    {
        bool threeD = args.Contains("3d");
        int runs = args.Select(a => { int v; return int.TryParse(a, out v) ? v : 0; }).FirstOrDefault(v => v > 0);
        if (runs == 0) runs = 2;
        var outs = AudioDevices.List(EDataFlow.Render);
        var sink = outs.First(d => d.Name.StartsWith("StreamLite - Out"));
        var phones = outs.First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0);
        var second = outs.First(d => d.Name.StartsWith("Speakers (Realtek USB Audio"));
        Console.WriteLine("output 1: " + phones.Name + ", output 2: " + second.Name + (threeD ? " (3D)" : " (plain)"));
        var eng = new GameAudioEngine { Plain = !threeD };
        var cfg = AppSettings.Load().Game.ToConfig(); eng.Processor.Configure(cfg);
        eng.StartMargin = AppSettings.Load().Game.LearnedMargin;
        eng.SetOutputs(new[] { new KeyValuePair<string, float>(second.Id, -20) });
        bool appOnly = args.Contains("app");   // test the running StreamLite instead (its output 2 must be this device)
        if (!appOnly) eng.Start(sink.Id, phones.Id);
        Thread.Sleep(1500);
        int fails = 0;
        for (int run = 0; run < runs; run++)
        {
            List<float> r1 = null, r2 = null; int rate1 = 0, rate2 = 0;
            var t1 = new Thread(() => r1 = Record(phones.Id, 5.5, out rate1));
            var t2 = new Thread(() => r2 = Record(second.Id, 5.5, out rate2));
            t1.Start(); t2.Start(); Thread.Sleep(400);
            Play(sink.Id, 4.0);
            t1.Join(); t2.Join();
            double lv1, lv2; int h1, h2; double d1, d2;
            Analyze(r1, rate1, out lv1, out d1, out h1);
            Analyze(r2, rate2, out lv2, out d2, out h2);
            var o = appOnly ? null : eng.ExtraOutputs[0];
            Console.WriteLine("run " + (run + 1) + ": output 1 " + lv1.ToString("0") + " dBFS for " + d1.ToString("0.00") + " s, holes " + h1 +
                " | output 2 " + lv2.ToString("0") + " dBFS for " + d2.ToString("0.00") + " s, holes " + h2 + (o == null ? "" : " | output 2 " + (o.Playing ? "playing" : o.Status) + ", dropouts " + o.Dropouts));
            bool ok = d1 > 3.5 && d2 > 3.5 && h2 == 0 && lv2 < lv1 - 10;
            Console.WriteLine(ok ? "  PASS both outputs play it, output 2 at its own volume, no holes" : "  FAIL");
            if (!ok) fails++;
            Thread.Sleep(800);
        }
        eng.Stop();
        Console.WriteLine(fails == 0 ? "MULTI OUT OK" : fails + " FAILED");
    }

    /// <summary>Median level of the sound (dBFS), how long it lasted, and holes (1 ms runs 30 dB under it) inside it.</summary>
    static void Analyze(List<float> rec, int rate, out double level, out double secs, out int holes)
    {
        int ms = rate / 1000 * 2; int nMs = rec.Count / ms;   // stereo interleaved
        var env = new double[nMs];
        for (int m = 0; m < nMs; m++) { double e = 0; for (int i = 0; i < ms; i++) { float v = rec[m * ms + i]; e += v * v; } env[m] = 10 * Math.Log10(e / ms + 1e-20); }
        var loud = env.Where(v => v > -120).OrderBy(v => v).ToArray();
        level = loud.Length > 0 ? loud[loud.Length * 3 / 4] : -200;
        double lvl = level;
        int first = Array.FindIndex(env, v => v > lvl - 12), last = Array.FindLastIndex(env, v => v > lvl - 12);
        secs = first >= 0 ? (last - first) / 1000.0 : 0;
        holes = 0; bool inHole = false;
        for (int m = first; m <= last && first >= 0; m++)
        {
            bool h = env[m] < lvl - 30;
            if (h && !inHole) holes++;
            inHole = h;
        }
    }

    static void Play(string id, double secs)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
        if (ac.Initialize(0, unchecked((int)0x80000000), 2000000, 0, fmt, IntPtr.Zero) != 0) throw new Exception("play init");
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc; var rnd = new Random(3); var tmp = new float[size * 8];
        ac.Start();
        long total = (long)(secs * 48000), done = 0;
        while (done < total)
        {
            Thread.Sleep(5);
            uint pad; ac.GetCurrentPadding(out pad);
            int n = (int)Math.Min(size - pad, total - done); if (n <= 0) continue;
            IntPtr data; rc.GetBuffer((uint)n, out data);
            Array.Clear(tmp, 0, n * 8);
            for (int i = 0; i < n; i++) { tmp[i * 8] = (float)((rnd.NextDouble() * 2 - 1) * 0.005); tmp[i * 8 + 1] = (float)((rnd.NextDouble() * 2 - 1) * 0.005); }   // front left/right
            Marshal.Copy(tmp, 0, data, n * 8); rc.ReleaseBuffer((uint)n, 0); done += n;
        }
        Thread.Sleep(300);
        ac.Stop();
    }

    static List<float> Record(string id, double secs, out int rate)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32); rate = 48000;
        ac.Initialize(0, 0x00020000 | unchecked((int)0x80000000), 2000000, 0, fmt, IntPtr.Zero);
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc; var outp = new List<float>(); var buf = new float[48000 * 2];
        ac.Start();
        var end = DateTime.UtcNow.AddSeconds(secs);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(5);
            uint packet; cap.GetNextPacketSize(out packet);
            while (packet > 0)
            {
                IntPtr data; uint frames, flags; ulong p1, p2;
                cap.GetBuffer(out data, out frames, out flags, out p1, out p2);
                int n = (int)frames * 2;
                if ((flags & 2) != 0) Array.Clear(buf, 0, n); else Marshal.Copy(data, buf, 0, n);
                for (int i = 0; i < n; i++) outp.Add(buf[i]);
                cap.ReleaseBuffer(frames);
                cap.GetNextPacketSize(out packet);
            }
        }
        ac.Stop();
        return outp;
    }
}


