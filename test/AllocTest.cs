// Memory allocated per second of sound on each audio path (after warm-up). Every allocation brings the next garbage
// collection closer, and a collection pauses every thread - the mic's and the 3D audio's too - so the steady-state
// audio paths should allocate nothing. Usage: run.ps1 AllocTest
using System;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class AllocTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static long Allocated() { return GC.GetAllocatedBytesForCurrentThread(); }

    /// <summary>Bytes allocated per second of audio by run(seconds), after one second of warm-up.</summary>
    static double PerSecond(Action<double> run)
    {
        run(1);
        GC.Collect(); GC.WaitForPendingFinalizers();
        long a = Allocated();
        run(5);
        return (Allocated() - a) / 5.0;
    }

    static void Main()
    {
        AppDomain.MonitoringIsEnabled = true;
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var rnd = new Random(1);
        var noise = Enumerable.Range(0, 48000).Select(_ => (float)(rnd.NextDouble() * 0.2 - 0.1)).ToArray();

        // The voice chain, as the mic thread runs it (3 ms packets), for each noise remover, realtime and not.
        foreach (var engine in new[] { NoiseEngines.Classic, NoiseEngines.Ai })
            foreach (bool rt in new[] { false, true })
            {
                var S = new AppSettings { NsEngine = engine, NsRealtime = rt };
                var chain = new VoiceChain(); chain.Configure(S);
                var mixer = new MicMixer(); var m2 = new ExtraMic("b", 0); mixer.Use(m2);
                var buf = new float[144]; var pkt = new float[480]; int pos = 0, fed = 0;
                double bps = PerSecond(sec =>
                {
                    for (int k = 0; k < sec * 48000 / 144; k++)
                    {
                        while (fed * 480 <= (k + 1) * 144) { Array.Copy(noise, (fed * 480) % 47000, pkt, 0, 480); m2.Feed(pkt, 480); fed++; }
                        Array.Copy(noise, pos, buf, 0, 144); pos = (pos + 144) % 47800;
                        mixer.Mix(buf, 144); chain.Process(buf, 144, false);
                    }
                });
                Console.WriteLine("  mic chain " + (engine + (rt ? " realtime" : "")).PadRight(17) + " + 2nd mic: " + bps.ToString("0").PadLeft(7) + " bytes/s");
                Check(bps == 0, "mic chain (" + engine + (rt ? ", realtime" : "") + "): no allocations while running");
            }

        // 3D audio: 7.1 through true 360, as the render thread runs it (5.3 ms blocks), plus an added output's EQ and ring.
        {
            var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
            var sp = new SpatialProcessor(); sp.Configure(g.ToConfig());
            var set = HrtfSet.Get(g.Hrtf, null);
            for (int w = 0; w < 1500 && !ContinuousRenderer.IsReady(set); w++) System.Threading.Thread.Sleep(10);
            const int B = SpatialProcessor.B;
            var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B]; var inter = new float[B * 2];
            var eq = new EqChain(); var e = new EqProfile(); e.Bands[3] = 3; eq.Update(e, 48000);
            var ring = new StereoRing(48, 480);
            int pos = 0;
            double bps = PerSecond(sec =>
            {
                for (int k = 0; k < sec * 48000 / B; k++)
                {
                    for (int s = 0; s < slots.Length; s++) Array.Copy(noise, (pos + 997 * s) % (48000 - B), slots[s], 0, B);
                    pos = (pos + B) % (48000 - B);
                    sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);
                    eq.Process(l, B); ring.Write(l, r, B); ring.InputArrived(); ring.Read(inter, B);
                }
            });
            Console.WriteLine("  3D audio (7.1, true 360) + an added output: " + bps.ToString("0").PadLeft(7) + " bytes/s");
            Check(bps == 0, "3D audio: no allocations while running");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
