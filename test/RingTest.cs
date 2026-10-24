using System;
using StreamLite.Audio;

// The 3D output buffer between the game clock and the headphone clock reads at a slightly varying speed (drift
// correction). This drives it with a writer running 0.1% fast, like two real device clocks, and measures how steady
// high tones come out: the level range over time at each frequency (0 dB = untouched).
class RingTest
{
    const int Fs = 48000;

    static void Main()
    {
        foreach (double f in new[] { 1000.0, 5000.0, 10000.0, 16000.0 })
        {
            var ring = new StereoRing(144);
            var a = new float[256]; var b = new float[256];
            var outp = new float[480 * 2];
            double phase = 0, w = 2 * Math.PI * f / Fs;
            double writeDebt = 0;
            double minDb = 999, maxDb = -999;
            // 20 s: the writer produces 1.001x the samples the reader consumes.
            for (int blk = 0; blk < 2000; blk++)
            {
                writeDebt += 480 * 1.001;
                while (writeDebt >= 256)
                {
                    for (int i = 0; i < 256; i++) { a[i] = b[i] = (float)Math.Sin(phase); phase += w; }
                    ring.Write(a, b, 256); writeDebt -= 256;
                    ring.InputArrived();
                }
                ring.Read(outp, 480);
                if (blk < 200) continue;   // settle
                double e = 0; for (int i = 0; i < 480; i++) e += outp[2 * i] * outp[2 * i];
                double db = 10 * Math.Log10(e / 480 * 2 + 1e-20);
                if (db < minDb) minDb = db; if (db > maxDb) maxDb = db;
            }
            Console.WriteLine(string.Format("{0,6} Hz: level {1,6:0.00} to {2,6:0.00} dB (swing {3:0.00} dB), dropouts {4}", f, minDb, maxDb, maxDb - minDb, ring.Underruns));
        }
    }
}
