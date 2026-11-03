using System;
using StreamLite.Audio;
using StreamLite.Interop;

// The mic -> cable path as it runs on a shared-mode USB mic (10 ms packets), in simulated time: the ring, the cable
// writer's lead steering and VB-CABLE's play position. 4.2.2: a stall that loses sound (the mic thread away for a moment)
// used to leave the voice filler waiting for that sound forever: silence for good, the buffer growing to 600 ms and the
// drift steering pinned at its limit (which was then saved).
// Usage: run.ps1 MicCableSim                                        the checks
//        run.ps1 MicCableSim startPpm clockPpm seconds [stallAtSeconds stallMs]   one run, printed every 10 s
class MicCableSim
{
    const int Fs = 48000, P = 480;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    sealed class Result { public double LastRealPct, RingMs, DriftPpm; }

    static Result Run(double startPpm, double clockPpm, double secs, double stallAt, double stallMs, bool print)
    {
        double t = 0;
        var ring = new SampleRing(Fs, Fs) { Conceal = true, ExternalSteer = true };
        ring.Now = () => t;
        ring.SetPacket(P, true, 0);
        var steer = new KsRender.LeadSteer { DriftPpm = startPpm };
        ring.Trim = (float)(startPpm / 1e6);
        var rnd = new Random(5);
        double period = P * 1000.0 / Fs / (1 + clockPpm / 1e6);
        long k = 0, written = -1, fills = 0, catchUps = 0, real = 0, other = 0, lastReal = 0, lastOther = 0;
        double playPos = 0, nextStep = 1, nextSafety = 0.5;
        int lead = 72, pending = 0, lastReport = 0;   // 1.5 ms
        var pkt = new float[P]; var buf = new float[9600];
        for (int i = 0; i < P; i++) pkt[i] = 0.1f;
        Action<int, bool> top = (target, packet) =>
        {
            if (written < 0 && ring.Ready < 16) return;
            if (written < 0) written = (long)playPos + lead + 48;
            int ahead = (int)(written - (long)playPos);
            int critical = Math.Max(72, lead);
            if (packet) { steer.Target = critical + 24; steer.Warmup = t < 5000; if (steer.BeforePacket(ahead)) ring.Trim = steer.Trim; }
            int want = Math.Max(lead, target) - ahead, ready = ring.Ready;
            int frames = KsRender.FramesToWrite(packet ? Math.Min(4800, Math.Max(want, ready)) : want, ready, ahead, critical);
            if (frames < 16) return;
            if (frames > ready) { steer.NoteFill(); fills++; }
            ring.Read(buf, frames);
            bool last = t >= (secs - 10) * 1000;
            for (int i = 0; i < frames; i++)
            {
                bool isReal = buf[i] > 0.05f;
                if (isReal) real++; else other++;
                if (last) { if (isReal) lastReal++; else lastOther++; }
            }
            written += frames;
        };
        for (; t < secs * 1000; t += 0.05)
        {
            if (t >= (k + 1) * period + rnd.NextDouble() * 0.3)
            {
                // The mic thread away for stallMs: only what's still in Windows' capture buffer (the last 30 ms) comes after it.
                if (t >= stallAt && t < stallAt + stallMs) { k++; if (t >= stallAt + stallMs - 30) pending++; continue; }
                for (; pending > 0; pending--) { ring.Write(pkt, P); top(P + lead + 24, true); }
                ring.Write(pkt, P); k++; top(P + lead + 24, true);
            }
            if (t >= nextStep)
            {
                double np = Math.Floor(t * 48.0);
                if (written >= 0 && np > written) { catchUps++; lead = Math.Min(480, lead + 24); written = (long)np + lead; }
                playPos = np; nextStep += 0.8 + rnd.NextDouble() * 0.6;
            }
            if (t >= nextSafety) { top(lead, false); nextSafety = t + 0.5 + rnd.NextDouble() * 0.3; }
            if (print && (int)(t / 10000) > lastReport)
            {
                lastReport = (int)(t / 10000);
                Console.WriteLine("t " + lastReport * 10 + " s: ring " + (ring.Fill / 48.0).ToString("0.0") + " ms, lead " + (written - playPos).ToString("0") +
                    ", drift " + steer.DriftPpm.ToString("0") + " ppm, fills " + fills + ", catch-ups " + catchUps + ", real sound so far " + (100.0 * real / Math.Max(1, real + other)).ToString("0.0") + "%");
            }
        }
        return new Result { LastRealPct = 100.0 * lastReal / Math.Max(1, lastReal + lastOther), RingMs = ring.Fill / 48.0, DriftPpm = steer.DriftPpm };
    }

    static void Main(string[] a)
    {
        if (a.Length >= 3)
        {
            Run(double.Parse(a[0]), double.Parse(a[1]), double.Parse(a[2]), a.Length > 3 ? double.Parse(a[3]) * 1000 : double.MaxValue, a.Length > 4 ? double.Parse(a[4]) : 0, true);
            return;
        }
        foreach (var ms in new[] { 60.0, 300.0, 1000.0 })
        {
            var r = Run(0, -14, 90, 15000, ms, false);
            Check(r.LastRealPct > 99.9, ms + " ms stall that loses sound: the voice comes back (" + r.LastRealPct.ToString("0.0") + "% real sound at the end)");
            Check(r.RingMs < 5, ms + " ms stall: the buffer doesn't grow (" + r.RingMs.ToString("0.0") + " ms)");
            Check(Math.Abs(r.DriftPpm) < 200, ms + " ms stall: the drift steering settles on the real clocks (" + r.DriftPpm.ToString("0") + " ppm, they're 14 apart)");
        }
        var s = Run(-3000, -14, 60, double.MaxValue, 0, false);
        Check(s.LastRealPct > 99.9 && Math.Abs(s.DriftPpm) < 200, "starting from a pinned -3000 ppm: it recovers (" + s.DriftPpm.ToString("0") + " ppm)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
