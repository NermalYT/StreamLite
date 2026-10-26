using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using StreamLite.Audio;

// DeepFilterNet's buffer, paced like the live mic (one 10 ms frame every 10 ms), with the waits shortened: on a fast
// device it goes to 0 frames (the mic waits for each frame - 10 ms less delay) without late frames; a frame not back
// in time sends it to 1 at once. Needs the ai folder (CI skips it).
class DfnBufferTest
{
    const int F = 480;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        DeepFilter.StartupFrames = 100; DeepFilter.CalmFrames = 300;
        foreach (bool gpu in new[] { true, false })
        {
            DeepFilter.ZeroWaitMs = 3;
            var d = new DeepFilter(gpu);
            var rnd = new Random(1);
            var inp = new float[F]; var outp = new float[F];
            var sw = Stopwatch.StartNew(); long next = 0;
            int zeroAt = -1, lateAtZero = -1, n = 0; double worstWait = 0, sumWait = 0; int waits = 0;
            while (n < 3000)
            {
                while (sw.ElapsedTicks < next) Thread.SpinWait(50);
                next += Stopwatch.Frequency / 100;
                // Fan noise, with a "voice" (a gliding tone) half of the time.
                for (int i = 0; i < F; i++) inp[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05 + ((n / 50) % 2 == 0 ? 0.3 * Math.Sin(2 * Math.PI * (200 + n % 50 * 4) * (n * F + i) / 48000.0) : 0));
                double t0 = sw.Elapsed.TotalMilliseconds;
                d.Process(inp, outp);
                double took = sw.Elapsed.TotalMilliseconds - t0;
                if (d.Buffer == 0) { if (zeroAt < 0) { zeroAt = n; lateAtZero = d.Late; } else { worstWait = Math.Max(worstWait, took); sumWait += took; waits++; } }
                n++;
                if (zeroAt >= 0 && n - zeroAt > 600) break;
            }
            string name = gpu ? "graphics card" : "CPU";
            Console.WriteLine(name + ": " + d.AvgMs.ToString("0.00") + " ms per frame (slowest " + d.MaxMs.ToString("0.0") + "), ready after " + d.LoadMs + " ms");
            if (d.Error != null) { Console.WriteLine("  (" + d.Error + ")"); if (gpu) continue; }
            if (d.AvgMs < 1.5)
            {
                Check(zeroAt >= 0, name + ": fast enough - went to 0 frames after " + (zeroAt * 10) + " ms of frames");
                if (zeroAt >= 0)
                {
                    Check(d.Buffer == 0 && d.Late == lateAtZero, name + ": 6 s at 0 frames without a late frame (buffer " + d.Buffer + ", late " + (d.Late - lateAtZero) + ")");
                    Check(worstWait < 4.5, name + ": the mic waits " + (sumWait / Math.Max(1, waits)).ToString("0.00") + " ms on average, at most " + worstWait.ToString("0.0") + " ms");
                    // Not back in time: 1 frame again, at once.
                    DeepFilter.ZeroWaitMs = 0;
                    for (int i = 0; i < 3; i++) { while (sw.ElapsedTicks < next) Thread.SpinWait(50); next += Stopwatch.Frequency / 100; d.Process(inp, outp); }
                    Check(d.Buffer == 1, name + ": a frame not back in time - 1 frame again (buffer " + d.Buffer + ")");
                }
            }
            else if (d.MaxMs > DeepFilter.FastMs * 2) Check(d.Buffer > 0 || zeroAt < 0, name + ": too slow for 0 frames - it stays at " + d.Buffer);
            else Console.WriteLine("  (" + name + " between fast and slow here: " + (zeroAt >= 0 ? "went to 0 frames" : "stayed at " + d.Buffer) + ", both fine)");
            d.Dispose();
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
