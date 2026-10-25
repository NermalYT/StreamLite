// How long a 3D block takes when the game flips between stereo and 7.1 (the processor rebuilds its filters), vs a
// normal block. A block has 5.3 ms of real time. Usage: run.ps1 SwitchCostProbe
using System;
using System.Diagnostics;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class SwitchCostProbe
{
    static void Main()
    {
        var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
        var sp = new SpatialProcessor(); sp.Configure(g.ToConfig());
        var set = HrtfSet.Get(g.Hrtf, null);
        for (int w = 0; w < 1500 && !ContinuousRenderer.IsReady(set); w++) System.Threading.Thread.Sleep(10);
        const int B = SpatialProcessor.B;
        var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
        var l = new float[B]; var r = new float[B]; var rnd = new Random(1);
        Action<SpatialProcessor.Source> block = src => { for (int s = 0; s < slots.Length; s++) for (int i = 0; i < B; i++) slots[s][i] = (float)(rnd.NextDouble() * 0.1 - 0.05); sp.Process(slots, src, l, r); };
        for (int k = 0; k < 200; k++) block(SpatialProcessor.Source.Surround71);
        block(SpatialProcessor.Source.Stereo); block(SpatialProcessor.Source.Surround71);   // first build of each layout
        double normal = 0, flip = 0; int gc0 = GC.CollectionCount(0);
        for (int k = 0; k < 200; k++) { var t = Stopwatch.StartNew(); block(SpatialProcessor.Source.Surround71); normal = Math.Max(normal, t.Elapsed.TotalMilliseconds); }
        for (int k = 0; k < 40; k++)
        {
            var t = Stopwatch.StartNew(); block(k % 2 == 0 ? SpatialProcessor.Source.Stereo : SpatialProcessor.Source.Surround71);
            flip = Math.Max(flip, t.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine("normal block worst " + normal.ToString("0.00") + " ms; block with a stereo/7.1 switch (after the first) worst " + flip.ToString("0.00") + " ms; garbage collections " + (GC.CollectionCount(0) - gc0));
    }
}
