using System;
using System.Diagnostics;
using StreamLite;
using StreamLite.Audio;

// How long does the 3D processor take on the block where it switches between stereo and surround content
// (filters are rebuilt on the audio thread)? Anything near the 5.3 ms block time risks a dropout.
class SwitchTest
{
    static void Main()
    {
        var s = AppSettings.Load();
        var p = new SpatialProcessor();
        p.Configure(s.Game.ToConfig());
        var slots = new float[SpatialProcessor.Slots][];
        for (int i = 0; i < slots.Length; i++) slots[i] = new float[SpatialProcessor.B];
        var l = new float[SpatialProcessor.B]; var r = new float[SpatialProcessor.B];
        var rnd = new Random(1);
        Action fill = () => { foreach (var sl in slots) for (int i = 0; i < sl.Length; i++) sl[i] = (float)(rnd.NextDouble() - 0.5) * 0.1f; };
        var sw = new Stopwatch();
        var seq = new[] { SpatialProcessor.Source.Surround71, SpatialProcessor.Source.Surround71, SpatialProcessor.Source.Stereo, SpatialProcessor.Source.Stereo,
                          SpatialProcessor.Source.Surround71, SpatialProcessor.Source.Surround71, SpatialProcessor.Source.Stereo, SpatialProcessor.Source.Surround71 };
        for (int round = 0; round < 3; round++)
            foreach (var src in seq)
            {
                fill();
                sw.Restart();
                p.Process(slots, src, l, r);
                sw.Stop();
                if (round == 2) Console.WriteLine(string.Format("{0,-11} {1,6:0.00} ms", src, sw.Elapsed.TotalMilliseconds));
            }
        Console.WriteLine("block time " + (SpatialProcessor.B * 1000.0 / SpatialProcessor.Fs).ToString("0.0") + " ms");
    }
}
