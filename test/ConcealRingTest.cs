using System;
using System.Linq;
using StreamLite.Audio;
using StreamLite.Interop;

// The mic -> cable path with late packets, in simulated time (0.05 ms steps), old way vs 3.1.1:
//   - an exclusive mic's 3 ms packets on a clock 150 ppm off the cable's, wake-ups up to 0.5 ms late, and late
//     packets like the Yeti's (3 ms late every 7 s, 2 ms every 11 s; once 9 ms, once 15 ms);
//   - the cable writer topping up after each packet and every 0.5 ms, VB-CABLE's play position moving in 0.8-1.4 ms steps.
// Old: a late packet empties the mic buffer - a fade to silence - and recurring ones raise the buffer for good.
// 3.1.1: the cable's own lead rides it out first, and if it can't, the voice is filled in (no silence) and the buffer
// stays small. Checks: no holes, no clicks, never playing past what's written, and less delay.
class ConcealRingTest
{
    const int Fs = 48000, P = 144;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static double Db(double x) { return 10 * Math.Log10(x + 1e-30); }

    sealed class Result { public double AvgMs, HfDb; public int Holes, HolesOutsideLong, CatchUps, Gaps, Concealed, ConcealedLearning, Margin; }

    static double Jitter = Environment.GetEnvironmentVariable("JITTER") != null ? double.Parse(Environment.GetEnvironmentVariable("JITTER"), System.Globalization.CultureInfo.InvariantCulture) : 0.5;
    static double Drift = Environment.GetEnvironmentVariable("DRIFT") != null ? double.Parse(Environment.GetEnvironmentVariable("DRIFT"), System.Globalization.CultureInfo.InvariantCulture) : 1.00015;
    static bool CableJumps = true;
    static bool Hiccups = Environment.GetEnvironmentVariable("HICCUPS") != null;
    static int mustN, mustAtPacket, winLow = int.MaxValue, winCount; static long mustF;
    static int Headroom = Environment.GetEnvironmentVariable("HEADROOM") != null ? int.Parse(Environment.GetEnvironmentVariable("HEADROOM")) : 24;
    static bool DebugHoles = Environment.GetCommandLineArgs().Length > 1;
    static double Source(long i)
    {
        double t = i / (double)Fs, f0 = 120 + 60 * Math.Sin(2 * Math.PI * 0.7 * t), ph = 2 * Math.PI * (120 * t - 60 / (2 * Math.PI * 0.7) * (Math.Cos(2 * Math.PI * 0.7 * t) - 1));
        double v = 0; for (int k = 1; k <= 10; k++) v += Math.Sin(k * ph) / k;
        return 0.25 * v;
    }

    static Result Run(bool newWay, double seconds)
    {
        double t = 0;
        var ring = new SampleRing(Fs, Fs) { Conceal = newWay, ExternalSteer = newWay };
        var steer = new KsRender.LeadSteer();
        ring.Now = () => t;
        ring.SetPacket(P, true, 0);
        var events = new System.Collections.Generic.List<Tuple<string, long>>();
        if (newWay) ring.Trace = (what, at) => events.Add(Tuple.Create(what, at));
        long written0 = -1;
        var rnd = new Random(9);
        double period = P * 1000.0 / Fs / Drift;         // the mic's clock is 150 ppm fast
        long k = 0; double lastDelivery = 0, nextDue = period;
        var pkt = new float[P];
        long micFrames = 0;
        bool jumpHalf = false;
        double playPos = 0, nextStep = 1.0, nextSafety = 0.5, stallUntil = 0, cableJump = 0, nextJump = CableJumps ? 5200 : double.MaxValue;
        long written = -1; const int Lead = 96;
        var cable = new float[(int)(seconds * Fs) + 9600];
        var buf = new float[9600];
        var r = new Result();
        double sumMs = 0; int nMs = 0;
        double end = seconds * 1000;
        Action<int, bool> top = (target, packet) =>
        {
            if (written < 0 && newWay && ring.Ready < 16) return;
            if (written < 0) { written = (long)Math.Floor(playPos) + Lead + (newWay ? 48 : 0); written0 = written; }   // like the writer: start just ahead of the cable
            int ahead = (int)(written - Math.Floor(playPos));
            if (newWay && packet) { steer.Target = Lead + Headroom; winLow = Math.Min(winLow, ahead); if (steer.BeforePacket(ahead)) { ring.Trim = steer.Trim; if (DebugHoles && (++winCount % 80) == 0) Console.WriteLine("    t " + (t / 1000).ToString("0.0") + " s: lowest lead before a packet " + winLow + " (target " + steer.Target + "), trim " + steer.Trim.ToString("0.00000") + ", must so far " + mustN); winLow = int.MaxValue; } }
            int want = target - ahead;
            int ready = ring.Ready;
            int frames = newWay ? KsRender.FramesToWrite(packet ? Math.Max(want, ready) : want, ready, ahead, Lead) : want;
            if (frames < 16) return;
            if (newWay && frames > ready) { steer.NoteFill(); if (DebugHoles && mustN < 12) Console.WriteLine("    must at " + t.ToString("0.00") + " ms: ahead " + ahead + ", ready " + ready + ", frames " + frames + ", ring fill " + ring.Fill + (packet ? " (packet)" : "")); mustN++; mustF += frames - ready; if (packet) mustAtPacket++; }
            ring.Read(buf, frames);
            Array.Copy(buf, 0, cable, written, frames);
            written += frames;
        };
        for (; t < end; t += 0.05)
        {
            // Mic packets: due on the mic's clock, delivered a little late, now and then a lot late (with the next ones).
            while (t >= nextDue)
            {
                double extra = 0;
                if (k % 2333 == 1000) extra = 3; else if (k % 3667 == 2000) extra = 2;   // every ~7 s / ~11 s
                if (k == 13000) extra = 9; if (k == 16500) extra = 15;
                double delivery = Math.Max(lastDelivery, k * period + period + rnd.NextDouble() * Jitter + extra);
                // A system hiccup stalls the cable writer too (its safety timer runs late as well).
                if (extra > 0 && Hiccups && stallUntil < k * period + period + extra) stallUntil = k * period + period + extra - 0.5;
                if (t < delivery) break;
                for (int i = 0; i < P; i++) pkt[i] = (float)Source(micFrames + i);
                micFrames += P;
                ring.Write(pkt, P);
                lastDelivery = delivery; k++; nextDue = k * period + period;
                top(P + Math.Max(Lead + 24, 72), true);                // the mic thread's top-up right after a packet
            }
            if (t >= nextStep)
            {
                double np = Math.Floor(t * 48.0) + cableJump;
                if (jumpHalf) { cableJump += 36; np += 36; jumpHalf = false; }
                if (t >= nextJump) { cableJump += 36; np += 36; jumpHalf = true; nextJump += 9000; }   // the cable running 1.5 ms fast over two updates now and then
                if (written >= 0 && np > written) { if (t > 1000) r.CatchUps++; if (DebugHoles) Console.WriteLine("    catch-up at " + (t / 1000).ToString("0.000") + " s"); written = (long)np + Lead; }    // played past what's written
                playPos = np; nextStep += 0.8 + rnd.NextDouble() * 0.6;
            }
            if (t >= nextSafety && t >= stallUntil)
            {
                top(Lead, false);
                if (t > 2000) { sumMs += (ring.Fill + written - playPos) / 48.0; nMs++; }
                if (t < 10000) r.ConcealedLearning = ring.Concealed;   // the first 10 s: the steering learns the clocks' difference
                if (DebugHoles && newWay && ((int)(t / 0.05)) % 100000 == 0) Console.WriteLine("    t " + (t / 1000).ToString("0") + " s: ring " + ring.Fill + ", lead " + (written - playPos).ToString("0") + ", trim " + steer.Trim.ToString("0.0000"));
                nextSafety = t + 0.5 + rnd.NextDouble() * 0.3;
            }
        }
        r.AvgMs = sumMs / nMs; r.Gaps = ring.Underruns; r.Concealed = ring.Concealed; r.Margin = ring.Least;
        // Holes: 1 ms windows far quieter than the voice (it never pauses); clicks: high frequencies vs the voice's own.
        long played = (long)playPos;
        double hfOut = 0, eOut = 0;
        int run = 0;
        for (long i = 2 * Fs; i < played; i++)
        {
            if (Math.Abs(cable[i]) < 1e-4) { run++; continue; }
            if (run >= 24)
            {
                r.Holes++;
                double sec = i / (double)Fs;                     // the 15 ms one is allowed a short fade (beyond 10 ms)
                if (DebugHoles && r.Holes <= 5) Console.WriteLine("    hole at " + sec.ToString("0.000") + " s, " + run + " samples (" + (newWay ? "new" : "old") + ")");
                if (!(sec > 16500 * P / (double)Fs && sec < 16500 * P / (double)Fs + 0.5)) r.HolesOutsideLong++;
            }
            run = 0;
        }
        for (long i = 2 * Fs; i < played; i++) { double d = cable[i] - 3 * cable[i - 1] + 3 * cable[i - 2] - cable[i - 3]; hfOut += d * d; eOut += cable[i] * cable[i]; }
        double hfSrc = 0, eSrc = 0;
        for (long i = 2 * Fs; i < played; i++) { double d = Source(i) - 3 * Source(i - 1) + 3 * Source(i - 2) - Source(i - 3); hfSrc += d * d; eSrc += Source(i) * Source(i); }
        r.HfDb = (Db(hfOut) - Db(eOut)) - (Db(hfSrc) - Db(eSrc));
        if (DebugHoles && newWay)
        {
            // Where the clicks are: the biggest triple differences.
            var peaks = new System.Collections.Generic.List<Tuple<double, long>>();
            for (long i = 2 * Fs; i < played; i++) { double d = Math.Abs(cable[i] - 3 * cable[i - 1] + 3 * cable[i - 2] - cable[i - 3]); if (d > 0.2) peaks.Add(Tuple.Create(d, i)); }
            foreach (var x in peaks.OrderByDescending(x => x.Item1).Take(6)) Console.WriteLine("    click " + x.Item1.ToString("0.00") + " at " + (x.Item2 / (double)Fs).ToString("0.0000") + " s");
            if (peaks.Count > 0) { long c = peaks.OrderByDescending(x => x.Item1).First().Item2; var sb = new System.Text.StringBuilder("    around it:"); for (long i = c - 20; i < c + 12; i++) sb.Append(" " + cable[i].ToString("0.000")); Console.WriteLine(sb); Console.WriteLine("    written at the end " + written + ", click index " + c); foreach (var e in events) if (Math.Abs(written0 + e.Item2 - c) < 3000) Console.WriteLine("    event " + e.Item1 + " at " + (written0 + e.Item2 - c) + " from the click"); }
        }
        return r;
    }

    static void Main()
    {
        const double secs = 60;
        // The Yeti against VB-CABLE on the test PC turned out about 1000 ppm apart; USB mics can be off by more.
        foreach (double drift in (Environment.GetEnvironmentVariable("DRIFT") != null ? new[] { Drift } : new[] { 1.00015, 0.999, 1.001, 0.998 }))
        {
            Drift = drift;
            var old = Run(false, secs);
            var neu = Run(true, secs);
            string ppm = ((drift - 1) * 1e6).ToString("+0;-0") + " ppm";
            Console.WriteLine("clocks " + ppm + ":");
            foreach (var x in new[] { Tuple.Create("old  ", old), Tuple.Create("3.1.1", neu) })
                Console.WriteLine("  " + x.Item1 + ": delay in buffers " + x.Item2.AvgMs.ToString("0.0") + " ms, silent holes " + x.Item2.Holes + ", gaps " + x.Item2.Gaps +
                    ", filled in " + x.Item2.Concealed + ", played past the end " + x.Item2.CatchUps + ", buffer floor " + (x.Item2.Margin / 48.0).ToString("0.0") + " ms, clicks " + x.Item2.HfDb.ToString("+0.0;-0.0") + " dB");
            Check(neu.CatchUps == 0, ppm + ": the cable never plays past what's written");
            Check(neu.HolesOutsideLong == 0, ppm + ": no silent holes from late packets (only the 15 ms one may fade)");
            Check(neu.HfDb < 3, ppm + ": no clicks where it filled in");
            Check(neu.Margin <= old.Margin && neu.Margin <= 48, ppm + ": late packets don't raise the buffer (" + (neu.Margin / 48.0).ToString("0.0") + " ms)");
            Check(neu.Concealed - neu.ConcealedLearning <= 15 + 2 * 6, ppm + ": once the clocks' difference is learned, filled in only for late packets and cable jumps (" + (neu.Concealed - neu.ConcealedLearning) + " times after 10 s, " + neu.ConcealedLearning + " while learning; 15 late packets and 6 jumps were sent)");
            Check(neu.AvgMs < old.AvgMs, ppm + ": less delay than before (" + neu.AvgMs.ToString("0.0") + " vs " + old.AvgMs.ToString("0.0") + " ms)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}