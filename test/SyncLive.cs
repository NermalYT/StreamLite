// Live: output sync on two real outputs (QCC headphones = output 1, Realtek speakers = output 2), recorded with
// loopback (the clicks turned down 40 dB, so barely audible). Checks: a click for one output plays only there, and a
// 100 ms delay on output 2 moves its clicks 100 ms later relative to output 1. Usage: SyncLive
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

class SyncLive
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    sealed class Rec { public List<float> X = new List<float>(); public List<double> T = new List<double>(); public volatile bool Stop; }

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    /// <summary>Loopback of a device, left channel, every sample with its time (ms, from the packet's QPC stamp).</summary>
    static void Record(string id, Rec r)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        ac.Initialize(0, 0x00020000 | unchecked((int)0x80000000), 2000000, 0, fmt, IntPtr.Zero);
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc; var buf = new float[96000];
        ac.Start();
        while (!r.Stop)
        {
            Thread.Sleep(3);
            uint packet; cap.GetNextPacketSize(out packet);
            while (packet > 0)
            {
                IntPtr data; uint frames, flags; ulong p1, qpc;
                cap.GetBuffer(out data, out frames, out flags, out p1, out qpc);
                int n = (int)frames;
                if ((flags & 2) != 0) Array.Clear(buf, 0, n * 2); else Marshal.Copy(data, buf, 0, n * 2);
                double t0 = qpc / 1e4;   // 100 ns units -> ms
                for (int i = 0; i < n; i++) { r.X.Add(buf[2 * i]); r.T.Add(t0 + i / 48.0); }
                cap.ReleaseBuffer(frames);
                cap.GetNextPacketSize(out packet);
            }
        }
        ac.Stop();
    }

    /// <summary>Times (ms) where clicks start: the level jumps from silence.</summary>
    static List<double> Onsets(Rec r, double from, double to)
    {
        var res = new List<double>(); double last = -1e9;
        float pk = 0; for (int i = 0; i < r.X.Count; i++) if (r.T[i] >= from && r.T[i] < to) pk = Math.Max(pk, Math.Abs(r.X[i]));
        if (pk < 1e-5f) return res;
        for (int i = 0; i < r.X.Count; i++)
            if (r.T[i] >= from && r.T[i] < to && Math.Abs(r.X[i]) > pk * 0.3 && r.T[i] - last > 200) { res.Add(r.T[i]); last = r.T[i]; }
        return res;
    }

    static void Main()
    {
        var outs = AudioDevices.List(EDataFlow.Render);
        var sink = outs.First(d => d.Name.StartsWith("StreamLite - Out"));
        var o1 = outs.First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0);
        var o2 = outs.First(d => d.Name.StartsWith("Speakers (Realtek USB Audio"));
        for (int i = 0; i < GameAudioEngine.ClickShape.Length; i++) GameAudioEngine.ClickShape[i] *= 0.01f;   // -40 dB
        var eng = new GameAudioEngine { Plain = true, StartMargin = AppSettings.Load().Game.LearnedMargin };
        eng.SetOutputs(new[] { new KeyValuePair<string, float>(o2.Id, 0) });
        eng.Start(sink.Id, o1.Id);
        Thread.Sleep(1500);
        var r1 = new Rec(); var r2 = new Rec();
        var th1 = new Thread(() => Record(o1.Id, r1)); var th2 = new Thread(() => Record(o2.Id, r2));
        th1.Start(); th2.Start();
        for (int i = 0; i < 5; i++) { eng.KeepAwake(); Thread.Sleep(100); }   // outputs running before the first click (as the dialog does)
        Func<double> now = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Func<int, int, double[]> burst = (target, count) =>
        {
            double a = now();
            for (int i = 0; i < count; i++) { eng.Click(target); Thread.Sleep(400); }
            Thread.Sleep(600);
            return new[] { a, now() };
        };
        var aOnly = burst(1, 3);                                  // output 2 only
        var both0 = burst(GameAudioEngine.AllOutputs, 4);         // in step
        eng.ExtraOutputs[0].DelayFrames = 4800;                    // output 2: 100 ms later
        Thread.Sleep(300);
        var both100 = burst(GameAudioEngine.AllOutputs, 4);
        r1.Stop = r2.Stop = true; th1.Join(); th2.Join();
        eng.Stop();
        foreach (var rr in new[] { r1, r2 })
            Console.WriteLine("  recorded " + rr.X.Count + " samples, " + (rr.T.Count > 0 ? rr.T[0].ToString("0") + " .. " + rr.T[rr.T.Count - 1].ToString("0") : "-") + " ms, peak " + (rr.X.Count > 0 ? rr.X.Max(v => Math.Abs(v)).ToString("0.00000") : "-"));
        foreach (var rr in new[] { r1, r2 }) Console.WriteLine("  clicks at " + string.Join(", ", Onsets(rr, 0, 1e12).Select(v => (v - aOnly[0]).ToString("0"))));
        Console.WriteLine("  bursts start at 0, " + (both0[0] - aOnly[0]).ToString("0") + ", " + (both100[0] - aOnly[0]).ToString("0"));
        Console.WriteLine("  windows " + aOnly[0].ToString("0") + " .. " + both100[1].ToString("0") + " ms, clicks played " + eng.ClicksPlayed + ", status " + eng.Status);

        Check(Onsets(r1, aOnly[0], aOnly[1]).Count == 0 && Onsets(r2, aOnly[0], aOnly[1]).Count == 3, "a click for output 2 plays on output 2 only (" + Onsets(r2, aOnly[0], aOnly[1]).Count + " there, " + Onsets(r1, aOnly[0], aOnly[1]).Count + " on output 1)");
        Func<double[], double> offset = w =>
        {
            var a = Onsets(r1, w[0], w[1]); var b = Onsets(r2, w[0], w[1]);
            if (a.Count == 0 || b.Count != a.Count) return double.NaN;
            var d = a.Zip(b, (x, y) => y - x).OrderBy(v => v).ToList();
            return d[d.Count / 2];
        };
        double off0 = offset(both0), off100 = offset(both100);
        Console.WriteLine("  output 2 vs output 1: " + off0.ToString("0.0") + " ms without delay, " + off100.ToString("0.0") + " ms with 100 ms");
        Check(!double.IsNaN(off0) && !double.IsNaN(off100) && Math.Abs(off100 - off0 - 100) < 4, "a 100 ms delay moves output 2 by " + (off100 - off0).ToString("0.0") + " ms");
        Console.WriteLine(fails == 0 ? "SYNC LIVE OK" : fails + " FAILED");
    }
}
