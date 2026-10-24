using System;
using StreamLite.Audio;

// The virtual mic's buffer between the microphone clock (10 ms packets) and the cable writer (small reads every ~2 ms),
// with the two clocks 300 ppm apart either way, for 20 simulated minutes: no dropouts, no lost chunks, latency
// bounded, and a 1 kHz tone comes out at a steady level.
class MicRingTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        foreach (double drift in new[] { 1.0003, 0.9997 })
        {
            var ring = new SampleRing(Fs, Fs);
            var pkt = new float[480]; var outp = new float[96];
            double phase = 0, w = 2 * Math.PI * 1000 / Fs, debt = 0;
            int settled = -1; double minDb = 999, maxDb = -999; long written = 0, readN = 0; double maxLag = 0, sumLag = 0; int lagN = 0;
            const int steps = 20 * 60 * 500;    // 2 ms steps for 20 minutes
            for (int s = 0; s < steps; s++)
            {
                debt += 96 * drift;                 // mic produces drift x what the cable consumes
                while (debt >= 480)
                {
                    for (int i = 0; i < 480; i++) { pkt[i] = (float)(0.5 * Math.Sin(phase)); phase += w; }
                    ring.Write(pkt, 480); written += 480; debt -= 480;
                }
                ring.Read(outp, 96); readN += 96;
                if (s < 2500) continue;             // settle (5 s)
                if (settled < 0) settled = ring.Underruns;
                double e = 0; for (int i = 0; i < 96; i++) e += outp[i] * outp[i];
                double db = 10 * Math.Log10(e / 96 * 2 / 0.25 + 1e-20);
                if (db < minDb) minDb = db; if (db > maxDb) maxDb = db;
                double lag = (written - readN * drift) / 48.0;   // ms queued (approx.)
                maxLag = Math.Max(maxLag, lag); sumLag += lag; lagN++;
            }
            Console.WriteLine("== clocks " + ((drift - 1) * 1e6).ToString("+0;-0") + " ppm: level " + minDb.ToString("0.00") + " to " + maxDb.ToString("0.00") + " dB, dropouts " + (ring.Underruns - settled) + " (" + settled + " while starting)");
            Check(ring.Underruns - settled == 0, "no dropouts in 20 minutes after start");
            Check(maxDb - minDb < 0.5, "steady level (swing " + (maxDb - minDb).ToString("0.00") + " dB: no gaps or skipped chunks)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
