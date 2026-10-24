using System;
using StreamLite.Audio;

// The low-latency mic path in simulated time (0.1 ms steps): an exclusive mic delivers 3 ms packets (wake-ups up to
// 0.5 ms late), the cable writer tops up every 0.5 ms (up to 0.3 ms late) to 2 ms ahead of a play position that moves
// in ~1 ms steps like VB-CABLE's, the two clocks 300 ppm apart either way. For 10 simulated minutes: no dropouts, a
// steady tone, and the time in the ring stays small - plus the old 10 ms shared path for comparison.
class LowLatencyRingTest
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        foreach (int packet in new[] { 144, 480 })
            foreach (double drift in new[] { 1.0003, 0.9997 })
            {
                var ring = new SampleRing(Fs, Fs);
                ring.SetPacket(packet);
                var rnd = new Random(packet + (int)(drift * 1000));
                var pkt = new float[packet]; var outp = new float[480];
                double phase = 0, w = 2 * Math.PI * 1000 / Fs;
                double micT = 0, writerT = 0, playPos = 0, written = 0;   // ms, samples
                double nextMic = 3.0 * packet / 144, micPeriod = packet * 1000.0 / Fs / drift;
                double nextWriter = 0.5, nextPlayStep = 1.0;
                int settled = -1; double minDb = 999, maxDb = -999, fillSum = 0, winE = 0; int fillN = 0, winN = 0;
                const double minutes = 10, end = minutes * 60000;
                for (double t = 0; t < end; t += 0.1)
                {
                    if (t >= nextMic)
                    {
                        for (int i = 0; i < packet; i++) { pkt[i] = (float)(0.5 * Math.Sin(phase)); phase += w; }
                        ring.Write(pkt, packet);
                        micT += micPeriod;
                        nextMic = micT + micPeriod + rnd.NextDouble() * 0.5;          // the mic's next event, maybe late
                    }
                    if (t >= nextPlayStep) { playPos = Math.Floor(t * 48.0); nextPlayStep += 0.8 + rnd.NextDouble() * 0.6; }
                    if (t >= nextWriter)
                    {
                        double lead = 96;                                             // 2 ms ahead of play
                        int need = (int)(playPos + lead - written);
                        if (need >= 16)
                        {
                            fillSum += ring.Fill; fillN++;
                            ring.Read(outp, Math.Min(need, outp.Length));
                            written += Math.Min(need, outp.Length);
                            if (t > 5000)
                            {
                                if (settled < 0) settled = ring.Underruns;
                                int n = Math.Min(need, outp.Length);
                                for (int i = 0; i < n; i++) { winE += outp[i] * outp[i]; if (++winN == 4800) { double db = 10 * Math.Log10(winE / winN * 2 / 0.25 + 1e-20); if (db < minDb) minDb = db; if (db > maxDb) maxDb = db; winE = 0; winN = 0; } }
                            }
                        }
                        nextWriter = t + 0.5 + rnd.NextDouble() * 0.3;
                    }
                }
                double avgMs = fillSum / fillN / 48.0;
                string name = (packet == 144 ? "exclusive 3 ms packets" : "shared 10 ms packets") + ", clocks " + ((drift - 1) * 1e6).ToString("+0;-0") + " ppm";
                Console.WriteLine("== " + name + ": time in the ring " + avgMs.ToString("0.0") + " ms, level " + minDb.ToString("0.00") + " to " + maxDb.ToString("0.00") + " dB, dropouts " + (ring.Underruns - settled) + " (" + settled + " while starting), margin " + ring.Margin);
                Check(ring.Underruns - settled == 0, name + ": no dropouts in " + minutes + " minutes");
                Check(maxDb - minDb < 0.5, name + ": steady level (no gaps or skipped chunks)");
                if (packet == 144) Check(avgMs < 6, name + ": under 6 ms in the ring on average (was ~19 ms with 10 ms packets)");
            }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
