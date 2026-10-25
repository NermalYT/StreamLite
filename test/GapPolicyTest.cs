// Short gaps in simulated time (the mic's low-latency path: 3 ms packets in, 1 ms reads out): a one-off stall (a game
// loading) is bridged with a fade and covered for a while but doesn't add delay for good; gaps that keep coming set a
// floor that 10 calm minutes lower again; a margin learned in an earlier run starts as the floor. And the Health
// page's rule: a warning only for gaps that keep coming without a reason.
// Usage: run.ps1 GapPolicyTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class GapPolicyTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>Runs the ring for some simulated ms; stalls: (start ms, length ms) during which no packet arrives (they come late, all at once).</summary>
    sealed class Sim
    {
        public double T;
        public readonly StereoRing Ring;
        long written;
        public readonly List<float> Out = new List<float>();
        double nextPacket, nextRead;
        public Sim(int startMargin = 0) { Ring = new StereoRing(32, startMargin); Ring.Now = () => T; Ring.SetBaseMargin(32); if (startMargin > 0) Ring = Fresh(startMargin); }
        StereoRing Fresh(int start) { var r = new StereoRing(32, start); r.Now = () => T; return r; }

        public void Run(double ms, double stallAt = -1, double stallLen = 0, bool record = false)
        {
            double end = T + ms;
            var a = new float[144]; var inter = new float[96];
            while (T < end)
            {
                T += 0.1;
                bool stalled = stallAt >= 0 && T >= stallAt && T < stallAt + stallLen;
                while (!stalled && T >= nextPacket)
                {
                    for (int i = 0; i < 144; i++) a[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (written + i) / 48000.0));
                    Ring.Write(a, a, 144); written += 144; Ring.InputArrived();
                    nextPacket += 3.0;
                }
                if (T >= nextRead)
                {
                    Ring.Read(inter, 48);
                    if (record) for (int i = 0; i < 48; i++) Out.Add(inter[2 * i]);
                    nextRead += 1.0;
                }
            }
        }
    }

    static void Main()
    {
        // ---- A one-off 12 ms stall.
        var s = new Sim();
        s.Run(5000);
        int calm = s.Ring.Margin, before = s.Ring.Underruns;
        s.Run(400, s.T + 200, 12, true);
        Check(s.Ring.Underruns == before + 1 && s.Ring.LastGapMs > 8 && s.Ring.LastGapMs < 14, "a 12 ms stall is one gap (" + s.Ring.LastGapMs.ToString("0.0") + " ms)");
        Check(s.Ring.Margin >= Math.Min(s.Ring.LastGapMs * 48, s.Ring.Least + 192) && s.Ring.Floor == 0, "covered at once, up to 4 ms more (" + (s.Ring.Margin / 48.0).ToString("0.0") + " ms in reserve), but no floor: a one-off doesn't add delay for good");
        // Fades: the biggest jump between samples stays like the tone's own (a hard cut to silence jumps up to 0.5).
        double own = 2 * Math.PI * 440 / 48000 * 0.5, jump = 0;
        for (int i = 1; i < s.Out.Count; i++) jump = Math.Max(jump, Math.Abs(s.Out[i] - s.Out[i - 1]));
        Check(jump < own * 6, "the gap fades out and back in - no click (largest step " + jump.ToString("0.000") + ", a hard cut would be up to 0.5)");
        s.Run(5 * 60000);
        Check(s.Ring.Margin <= calm + 12 && s.Ring.Underruns == before + 1, "5 calm minutes later the reserve is back to " + (s.Ring.Margin / 48.0).ToString("0.0") + " ms (was " + (calm / 48.0).ToString("0.0") + " before the stall)");

        // ---- Gaps that keep coming (2 ms, every 2 minutes).
        var r = new Sim();
        r.Run(5000);
        for (int k = 0; k < 4; k++) { r.Run(120000, r.T + 1000, 4); }
        int floor = r.Ring.Floor;
        Check(floor >= 2 * 48 && floor < 6 * 48, "recurring gaps set a floor that bridges them (" + (floor / 48.0).ToString("0.0") + " ms)");
        int gapsThen = r.Ring.Underruns;
        r.Run(120000, r.T + 1000, 4);
        Check(r.Ring.Underruns == gapsThen, "with the floor, the same hiccup no longer makes a gap");
        r.Run(20 * 60000);
        Check(r.Ring.Floor == floor, "20 calm minutes: the floor that worked stays");
        r.Run(40 * 60000);
        Check(r.Ring.Least < floor, "30+ calm minutes: a lower floor is tried, slowly (" + (floor / 48.0).ToString("0.0") + " -> " + (r.Ring.Least / 48.0).ToString("0.0") + " ms)");
        int g0 = r.Ring.Underruns;
        r.Run(60000, r.T + 1000, 4);
        Check(r.Ring.Underruns <= g0 + 1 && r.Ring.Floor == floor, "if that's too little: one gap, and straight back to the floor that worked (no re-learning with more gaps)");
        r.Run(50 * 60000);
        Check(r.Ring.Floor == floor, "and no more trying for an hour");

        // ---- A margin learned in an earlier run starts as the floor.
        var l = new StereoRing(48, 240);
        Check(l.Margin == 240 && l.Floor == 240, "a learned margin (5 ms) is this session's floor from the start");
        var mic = new SampleRing(48000, 0); mic.SetPacket(144, true, 130);
        Check(mic.Margin == 130 && mic.Least == 130, "the mic's learned floor (2.7 ms) applies when its stream starts");

        // ---- A one-off long pause gets at most 4 ms of cover (more would only add delay).
        var p = new Sim();
        p.Run(5000);
        int least = p.Ring.Least;
        p.Run(400, p.T + 200, 25);
        Check(p.Ring.Underruns > 0 && p.Ring.Margin <= least + 192 + 24, "a 25 ms pause: covered with at most 4 ms more (" + (p.Ring.Margin / 48.0).ToString("0.0") + " ms in reserve, not 25)");

        // ---- Health page: a warning only for gaps that keep coming without a reason.
        var w = new GapWatch(); var t0 = new DateTime(2026, 10, 25, 12, 0, 0);
        bool warned = false;
        for (int k = 0; k < 20; k++) warned |= w.Add(1, t0.AddSeconds(k * 5), "Rainbow Six Siege was loading");
        Check(!warned && !w.Troubled(t0.AddMinutes(2)) && w.Explained == 20, "20 gaps while a game loads: counted, no warning");
        for (int k = 0; k < 4; k++) warned |= w.Add(1, t0.AddMinutes(30 + k), null);
        Check(!warned && !w.Troubled(t0.AddMinutes(34)), "4 unexplained gaps in 10 minutes: no warning yet");
        bool fifth = w.Add(1, t0.AddMinutes(35), null);
        Check(fifth && w.Troubled(t0.AddMinutes(35)), "the 5th within 10 minutes: one warning, and the row turns yellow");
        bool again = w.Add(1, t0.AddMinutes(36), null);
        Check(!again, "no new warning for 30 minutes after one");
        Check(!w.Troubled(t0.AddMinutes(50)), "10 quiet minutes later the row is back to normal");
        var iso = new GapWatch(); bool any = false;
        for (int k = 0; k < 12; k++) any |= iso.Add(1, t0.AddMinutes(k * 11), null);
        Check(!any, "one gap every 11 minutes for 2 hours: never a warning");
        Console.WriteLine("       " + w.Describe());
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
