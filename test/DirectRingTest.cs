using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;

// A test sound through 3D audio's exclusive stream (Direct to headphones), in real time: a reader takes 3 ms steps like
// the headphones, HeadphonesDirect.Play writes a 4 s tone. Every sample must come out once, in order: no gap (the buffer
// running dry), no skip (it jumping ahead because too much was queued).
class DirectRingTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr sec, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    // The headphones' clock: precise 3 ms steps (a Sleep can take 16 ms on Windows 11 for a process without a window).
    static void WaitUntil(IntPtr timer, System.Diagnostics.Stopwatch sw, double ms)
    {
        double left = ms - sw.Elapsed.TotalMilliseconds;
        if (left <= 0) return;
        long due = -(long)(left * 10000);
        SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(timer, 50);
    }

    static void Main()
    {
        int fails = 0;
        foreach (int margin in new[] { HeadphonesDirect.TestMargin, 96 })
        {
            var ring = new StereoRing(margin) { ReadSignal = new AutoResetEvent(false) };
            ring.SetBaseMargin(margin, margin < 480 ? 480 : 0);
            int frames = 48000 * 4;
            var src = new float[frames * 2];
            for (int i = 0; i < frames; i++) src[2 * i] = src[2 * i + 1] = (float)Math.Sin(i * 2 * Math.PI * 440 / 48000) * 0.5f;
            var got = new float[(frames + 48000) * 2]; int gotN = 0;
            bool done = false; int log = 0;
            var reader = new Thread(() =>
            {
                var buf = new float[144 * 2];
                var sw = System.Diagnostics.Stopwatch.StartNew(); double next = 3;
                IntPtr tm = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
                while (!done)
                {
                    WaitUntil(tm, sw, next);
                    next += 3;
                    int before = ring.Fill; ring.Read(buf, 144);
                    if (gotN > 48000 && gotN < 48000 * 39 / 10 && buf[0] == 0 && buf[286] == 0 && log < 12) { log++; Console.WriteLine("  dry at " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, fill before " + before + ", late by " + (sw.Elapsed.TotalMilliseconds - next + 3).ToString("0.0")); }
                    if (gotN + 144 <= got.Length / 2) { Array.Copy(buf, 0, got, gotN * 2, 288); gotN += 144; }
                }
            }) { Priority = ThreadPriority.Highest };
            reader.Start();
            var t0 = DateTime.Now;
            if (margin == 96)
            {
                // Hearing yourself: 10 ms packets as the mic sends them.
                var l = new float[480];
                var sw = System.Diagnostics.Stopwatch.StartNew(); double next = 10;
                IntPtr tm = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
                for (int p = 0; p * 480 < frames; p++)
                {
                    WaitUntil(tm, sw, next);
                    next += 10;
                    for (int i = 0; i < 480; i++) l[i] = src[2 * (p * 480 + i)];
                    ring.Write(l, l, 480); ring.InputArrived();
                }
                Thread.Sleep(100);
            }
            else HeadphonesDirect.Play(ring, src, null);
            double took = (DateTime.Now - t0).TotalSeconds;
            done = true; reader.Join();
            // Find the tone in what was read and check it continues sample for sample.
            int start = -1;
            for (int i = 0; i < gotN; i++) if (Math.Abs(got[2 * i]) > 1e-4) { start = i; break; }
            // Where in the tone the output starts (the buffer starts with its reserve: the first ms are skipped).
            int off = 0; double bestE = double.MaxValue;
            for (int k = 0; k < 4800 && start >= 0; k++)
            {
                double e = 0; for (int i = 2000; i < 2480; i++) e += Math.Abs(got[2 * (start + i)] - src[2 * (k + i)]);
                if (e < bestE) { bestE = e; off = k; }
            }
            int bad = 0, firstBad = -1;
            int len = Math.Min(frames - 2000 - off, gotN - start);
            for (int i = 2000; i < len; i++)
            {
                // The drift correction stretches time by up to 0.3%, so the phase wanders slowly: check continuity instead
                // (a 440 Hz tone at 0.5 moves at most 0.029 per sample; a gap or a skip jumps) and that no 10 ms window fades.
                double step = Math.Abs(got[2 * (start + i)] - got[2 * (start + i - 1)]);
                bool jump = step > 0.04;
                bool fade = false;
                if (i % 480 == 0 && i + 480 < len) { double p = 0; for (int k = i; k < i + 480; k++) p += got[2 * (start + k)] * got[2 * (start + k)]; fade = Math.Abs(Math.Sqrt(p / 480) - 0.3536) > 0.02; }
                if (jump || fade) { bad++; if (firstBad < 0) firstBad = i; }
            }
            bool ok = start >= 0 && bad == 0 && ring.Underruns == 0 && len > frames - 8000;
            Console.WriteLine((margin == 96 ? "hearing yourself (10 ms packets)" : "test sound (Play)") + ": " + (ok ? "PASS" : "FAIL") +
                " - starts after " + (start / 48.0).ToString("0.0") + " ms (" + (off / 48.0).ToString("0.0") + " ms into the tone), " + bad + " breaks" + (firstBad >= 0 ? " (first at " + firstBad + ")" : "") +
                ", gaps " + ring.Underruns + ", took " + took.ToString("0.00") + " s for 4 s, margin " + (ring.Margin / 48.0).ToString("0.0") + " ms");
            if (!ok) fails++;
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
