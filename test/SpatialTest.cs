using System;
using System.Collections.Generic;
using System.Diagnostics;
using StreamLite.Audio;

// Sanity + performance checks for the 3D audio processor.
class SpatialTest
{
    const int B = SpatialProcessor.B;

    static float[][] Slots() { var s = new float[8][]; for (int i = 0; i < 8; i++) s[i] = new float[B]; return s; }

    static SpatialConfig Cfg(double room, double cue, double boost, double tamer)
    {
        var c = new SpatialConfig { Room = room, CueSharpen = cue, FootstepBoost = boost, LoudTamer = tamer };
        foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { AutoPreamp = false }; c.DirGainDb[d] = 0; }
        return c;
    }

    // Runs `blocks` blocks with noise (or impulse) in one slot; returns output.
    static void Run(SpatialProcessor p, SpatialProcessor.Source src, int slot, int blocks, double amp, bool impulse, List<float> L, List<float> R, Random rnd)
    {
        var l = new float[B]; var r = new float[B];
        for (int b = 0; b < blocks; b++)
        {
            var s = Slots();
            for (int i = 0; i < B; i++)
                s[slot][i] = impulse ? (b == 2 && i == 0 ? (float)amp : 0f) : (float)((rnd.NextDouble() * 2 - 1) * amp);
            p.Process(s, src, l, r);
            L.AddRange(l); R.AddRange(r);
        }
    }

    static double Db(List<float> x, int from) { double e = 0; for (int i = from; i < x.Count; i++) e += x[i] * x[i]; return 10 * Math.Log10(e / (x.Count - from) + 1e-20); }

    static int Lag(List<float> a, List<float> b)
    {
        int best = 0; double bv = double.MinValue;
        for (int lag = -40; lag <= 40; lag++)
        {
            double s = 0;
            for (int i = 2000; i < a.Count - 100; i++) { int j = i + lag; if (j >= 0 && j < b.Count) s += a[i] * b[j]; }
            if (s > bv) { bv = s; best = lag; }
        }
        return best;   // >0: right ear lags left (sound from the left)
    }

    static void Main()
    {
        var rnd = new Random(1);
        var ku = HrtfSet.Get(HrtfSet.Ku100, null);
        Console.WriteLine("KU100: " + ku.Az.Length + " directions x " + ku.Taps + " taps; room " + (ku.RoomAz == null ? 0 : ku.RoomAz.Length) + " x " + ku.RoomTaps);
        int i90 = ku.Nearest(90, 0); double el = 0, er = 0;
        for (int i = 0; i < ku.Taps; i++) { el += ku.L[i90][i] * ku.L[i90][i]; er += ku.R[i90][i] * ku.R[i90][i]; }
        Console.WriteLine("HRIR at az 90 (left): left ear " + (10 * Math.Log10(el / er)).ToString("+0.0") + " dB vs right");

        Console.WriteLine("\nVirtual speakers (7.1 noise in one channel, room 0):");
        foreach (int slot in new[] { 0, 1, 2, 6, 7, 4, 5 })
        {
            var p = new SpatialProcessor(); p.Configure(Cfg(0, 0, 0, 0));
            var L = new List<float>(); var R = new List<float>();
            Run(p, SpatialProcessor.Source.Surround71, slot, 60, 0.1, false, L, R, rnd);
            Console.WriteLine(string.Format("  {0,-13} L-R level {1,6:+0.0;-0.0} dB, ITD {2,3} samples ({3})", SpatialProcessor.SlotNames[slot], Db(L, 2000) - Db(R, 2000), Lag(L, R), Lag(L, R) > 0 ? "left first" : Lag(L, R) < 0 ? "right first" : "centered"));
        }

        Console.WriteLine("\nImpulse latency through the processor (center channel):");
        {
            var p = new SpatialProcessor(); p.Configure(Cfg(0, 0, 0, 0));
            var L = new List<float>(); var R = new List<float>();
            Run(p, SpatialProcessor.Source.Surround71, 2, 8, 1.0, true, L, R, rnd);
            int pk = 0; for (int i = 0; i < L.Count; i++) if (Math.Abs(L[i]) > Math.Abs(L[pk])) pk = i;
            Console.WriteLine("  input at sample " + (2 * B) + ", output peak at " + pk + " -> " + (pk - 2 * B) + " samples (" + ((pk - 2 * B) / 48.0).ToString("0.0") + " ms) of which HRIR onset/ITD is part");
        }

        Console.WriteLine("\nPer-direction gain: Behind +6 dB, check behind-left vs front-left level:");
        {
            var c = Cfg(0, 0, 0, 0); c.DirGainDb["behind"] = 6;
            var p = new SpatialProcessor(); p.Configure(c);
            var L = new List<float>(); var R = new List<float>();
            Run(p, SpatialProcessor.Source.Surround71, SpatialProcessor.BL, 60, 0.05, false, L, R, rnd);
            var p2 = new SpatialProcessor(); p2.Configure(Cfg(0, 0, 0, 0));
            var L2 = new List<float>(); var R2 = new List<float>();
            Run(p2, SpatialProcessor.Source.Surround71, SpatialProcessor.BL, 60, 0.05, false, L2, R2, rnd);
            Console.WriteLine("  behind-left with +6 dB vs without: " + (Db(L, 2000) - Db(L2, 2000)).ToString("+0.0") + " dB");
        }

        Console.WriteLine("\nStereo game, left zone +6 dB (pan EQ):");
        {
            var c = Cfg(0, 0, 0, 0); c.DirGainDb["left"] = 6;
            foreach (var where in new[] { "hard left", "center", "hard right" })
            {
                var p = new SpatialProcessor(); p.Configure(c);
                var p0 = new SpatialProcessor(); p0.Configure(Cfg(0, 0, 0, 0));
                double gotL = 0, refL = 0;
                var l = new float[B]; var r = new float[B]; var l0 = new float[B]; var r0 = new float[B];
                var rr = new Random(5);
                for (int b = 0; b < 200; b++)
                {
                    var s = Slots(); var s0 = Slots();
                    for (int i = 0; i < B; i++)
                    {
                        float v = (float)((rr.NextDouble() * 2 - 1) * 0.1);
                        float gl = where == "hard right" ? 0 : 1, gr = where == "hard left" ? 0 : 1;
                        s[0][i] = s0[0][i] = v * gl; s[1][i] = s0[1][i] = v * gr;
                    }
                    p.Process(s, SpatialProcessor.Source.Stereo, l, r); p0.Process(s0, SpatialProcessor.Source.Stereo, l0, r0);
                    if (b > 20) for (int i = 0; i < B; i++) { gotL += l[i] * l[i] + r[i] * r[i]; refL += l0[i] * l0[i] + r0[i] * r0[i]; }
                }
                Console.WriteLine("  " + where + ": " + (10 * Math.Log10(gotL / refL)).ToString("+0.0") + " dB");
            }
        }

        Console.WriteLine("\nFootstep booster (boost 60%, tamer 50%), noise in front-left:");
        foreach (var amp in new[] { 0.003, 0.03, 0.5 })
        {
            var p = new SpatialProcessor(); p.Configure(Cfg(0, 0, 0.6, 0.5));
            var p0 = new SpatialProcessor(); p0.Configure(Cfg(0, 0, 0, 0));
            var L = new List<float>(); var R = new List<float>(); var L0 = new List<float>(); var R0 = new List<float>();
            Run(p, SpatialProcessor.Source.Surround71, 0, 150, amp, false, L, R, new Random(9));
            Run(p0, SpatialProcessor.Source.Surround71, 0, 150, amp, false, L0, R0, new Random(9));
            Console.WriteLine("  input " + (20 * Math.Log10(amp / Math.Sqrt(3))).ToString("0") + " dBFS: change " + (Db(L, 8000) - Db(L0, 8000)).ToString("+0.0;-0.0") + " dB (live boost " + p.Dynamics.LiveBoostDb.ToString("0.0") + ", cut " + p.Dynamics.LiveCutDb.ToString("0.0") + ")");
        }

        Console.WriteLine("\nRadar: footstep bursts from behind-right:");
        {
            var p = new SpatialProcessor(); p.Configure(Cfg(0, 0, 0, 0));
            var l = new float[B]; var r = new float[B]; var rr = new Random(2);
            for (int b = 0; b < 200; b++)
            {
                var s = Slots();
                if (b % 40 < 3) for (int i = 0; i < B; i++) s[SpatialProcessor.BR][i] = (float)((rr.NextDouble() * 2 - 1) * 0.2);
                p.Process(s, SpatialProcessor.Source.Surround71, l, r);
            }
            var blips = p.Radar.Snapshot(Environment.TickCount, 60000);
            Console.Write("  " + blips.Count + " blips at azimuth:");
            foreach (var bl in blips) Console.Write(" " + bl.Az.ToString("0"));
            Console.WriteLine("  (behind-right speaker is at " + (360 - 145) + " = -145)");
        }

        Console.WriteLine("\nTest walk-around renders: ");
        {
            var p = new SpatialProcessor(); p.Configure(Cfg(0.25, 0.3, 0.5, 0.4));
            p.StartTest();
            var l = new float[B]; var r = new float[B]; int blocks = 0; double e = 0; float pk = 0; bool nan = false;
            while (p.TestRunning && blocks < 5000) { p.Process(Slots(), SpatialProcessor.Source.Silent, l, r); blocks++; for (int i = 0; i < B; i++) { e += l[i] * l[i]; pk = Math.Max(pk, Math.Abs(l[i])); if (float.IsNaN(l[i])) nan = true; } }
            Console.WriteLine("  " + blocks + " blocks (" + (blocks * B / 48000.0).ToString("0.0") + " s), peak " + pk.ToString("0.00") + ", NaN " + nan);
        }

        Console.WriteLine("\nCPU (7.1 noise in all channels):");
        foreach (var room in new[] { 0.0, 0.3 })
            foreach (var cue in new[] { 0.0, 0.3 })
            {
                var p = new SpatialProcessor(); p.Configure(Cfg(room, cue, 0.5, 0.4));
                var l = new float[B]; var r = new float[B];
                var s = Slots();
                int blocks = 48000 * 10 / B;
                var sw = Stopwatch.StartNew();
                for (int b = 0; b < blocks; b++)
                {
                    for (int c = 0; c < 8; c++) for (int i = 0; i < B; i++) s[c][i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05);
                    p.Process(s, SpatialProcessor.Source.Surround71, l, r);
                }
                sw.Stop();
                Console.WriteLine("  room " + room + ", cue " + cue + ": " + (sw.Elapsed.TotalMilliseconds / 10).ToString("0.0") + " ms per second of audio (" + (sw.Elapsed.TotalMilliseconds / 100).ToString("0.0") + "% of one core, incl. noise generation)");
            }
        GC.Collect();
        Console.WriteLine("Working set " + (Process.GetCurrentProcess().WorkingSet64 >> 20) + " MB, managed " + (GC.GetTotalMemory(true) >> 10) + " KB");
    }
}
