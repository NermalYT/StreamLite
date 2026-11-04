// 4.3.3: what keeps the audio threads from being frozen, and what shows it if they are, offline. StreamLite runs above normal priority,
// a headphone step that comes too late is counted (with whether a memory collection ran), and a capture that came back only after a
// long wait is counted too. test\GcStallProbe measures the freezes themselves under load.
using System;
using System.Diagnostics;
using StreamLite;
using StreamLite.Audio;

class AudioStallTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        Program.RaisePriority();
        Check(Process.GetCurrentProcess().PriorityClass == ProcessPriorityClass.AboveNormal, "StreamLite runs above normal priority");

        var w = new StepWatch();
        w.Note(3.0, 3, false); w.Note(5.9, 3, true); w.Note(6.0, 3, false);
        Check(w.Late == 0 && w.WorstMs == 0, "steps up to two periods apart aren't late");
        w.Note(6.1, 3, false);
        Check(w.Late == 1 && w.LateCollecting == 0, "a step more than two periods late counts");
        w.Note(46, 3, true); w.Note(9, 3, true);
        Check(w.Late == 3 && w.LateCollecting == 2 && Math.Abs(w.WorstMs - 46) < 0.01, "a memory collection is noted, and the longest kept (" + w.WorstMs + " ms)");

        double now = 1000;
        var ring = new StereoRing(48) { Now = () => now };
        var a = new float[960]; var b = new float[960]; var o = new float[960 * 2];
        for (int i = 0; i < a.Length; i++) a[i] = b[i] = 0.1f;
        ring.Write(a, b, 960); ring.InputArrived();
        ring.Read(o, 480);
        for (int i = 0; i < 4 && ring.Fill > 0; i++) ring.Read(o, 480);   // runs dry
        now += 40;
        ring.Write(a, b, 960); ring.InputArrived();
        Check(ring.LongWaits == 1 && ring.Underruns == 0, "input back after 40 ms: a long wait, not a gap (" + ring.LongWaits + " / " + ring.Underruns + ")");
        ring.Read(o, 480);
        for (int i = 0; i < 4 && ring.Fill > 0; i++) ring.Read(o, 480);
        now += 10;
        ring.Write(a, b, 960); ring.InputArrived();
        Check(ring.LongWaits == 1 && ring.Underruns == 1, "input back after 10 ms: a gap, not a long wait (" + ring.LongWaits + " / " + ring.Underruns + ")");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
