// Which part of the audio paths allocates (bytes per second of sound). Diagnostic for AllocTest. Usage: run.ps1 AllocParts
using System;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class AllocParts
{
    static double PerSecond(Action<int> run, int calls)
    {
        run(calls);
        GC.Collect(); GC.WaitForPendingFinalizers();
        long a = GC.GetAllocatedBytesForCurrentThread();
        run(calls * 5);
        return (GC.GetAllocatedBytesForCurrentThread() - a) / 5.0;
    }

    static void Main()
    {
        AppDomain.MonitoringIsEnabled = true;
        var rnd = new Random(1);
        var buf = new float[144]; var noise = Enumerable.Range(0, 48000).Select(_ => (float)(rnd.NextDouble() * 0.2 - 0.1)).ToArray();
        int perSec = 48000 / 144;
        Action<string, Action<float[]>> part = (name, f) =>
        {
            int pos = 0;
            double b = PerSecond(c => { for (int k = 0; k < c; k++) { Array.Copy(noise, pos, buf, 0, 144); pos = (pos + 144) % 47800; f(buf); } }, perSec);
            Console.WriteLine("  " + name.PadRight(28) + b.ToString("0").PadLeft(8) + " bytes/s");
        };
        var S = new AppSettings();
        var ch = new VoiceChain(); ch.Configure(S);
        part("NoiseSuppressor (ai)", x => ch.Suppressor.Process(x, 144));
        part("Gate", x => ch.Gate.Process(x, 144));
        part("EqChain", x => ch.Eq.Process(x, 144));
        part("DeEsser", x => ch.Ess.Process(x, 144));
        part("Compressor", x => ch.Comp.Process(x, 144));
        part("AutoLevel", x => ch.Leveler.Process(x, 144));
        part("DeHum", x => ch.Hum.Process(x, 144));
        part("HighCut", x => ch.Hiss.Process(x, 144));
        var mixer = new MicMixer(); var m2 = new ExtraMic("b", 0); mixer.Use(m2); var pkt = new float[480]; int fed = 0, calls = 0;
        part("MicMixer + 2nd mic", x => { calls++; while (fed * 480 <= calls * 144) { m2.Feed(pkt, 480); fed++; } mixer.Mix(x, 144); });
        var sa = new SpectrumAnalyzer(48000);
        part("SpectrumAnalyzer.PushSamples", x => sa.PushSamples(x, 144));
        var hist = new LevelHistory();
        part("LevelHistory.Push + RmsDb", x => hist.Push(LevelHistory.RmsDb(x, 144), -50, -50, -50, true));

        // 3D parts, per 256-sample block.
        var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
        var sp = new SpatialProcessor(); sp.Configure(g.ToConfig());
        var set = HrtfSet.Get(g.Hrtf, null);
        for (int w = 0; w < 1500 && !ContinuousRenderer.IsReady(set); w++) System.Threading.Thread.Sleep(10);
        const int B = SpatialProcessor.B;
        var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
        var l = new float[B]; var r = new float[B];
        int bpos = 0;
        double b3 = PerSecond(c => { for (int k = 0; k < c; k++) { for (int s = 0; s < slots.Length; s++) Array.Copy(noise, (bpos + 997 * s) % (48000 - B), slots[s], 0, B); bpos = (bpos + B) % (48000 - B); sp.Process(slots, SpatialProcessor.Source.Surround71, l, r); } }, 48000 / B);
        Console.WriteLine("  " + "SpatialProcessor.Process".PadRight(28) + b3.ToString("0").PadLeft(8) + " bytes/s");
        foreach (var variant in new[] { "stereo direct", "7.1 speakers", "7.1 no dynamics", "7.1 no cues" })
        {
            var cfg = g.ToConfig();
            if (variant == "7.1 speakers") cfg.Render360 = false;
            if (variant == "7.1 no dynamics") { cfg.FootstepBoost = 0; cfg.LoudTamer = 0; }
            if (variant == "7.1 no cues") cfg.CueSharpen = 0;
            var sp2 = new SpatialProcessor(); sp2.Configure(cfg);
            var src = variant == "stereo direct" ? SpatialProcessor.Source.Stereo : SpatialProcessor.Source.Surround71;
            double bv = PerSecond(c => { for (int k = 0; k < c; k++) { for (int s = 0; s < slots.Length; s++) Array.Copy(noise, (bpos + 997 * s) % (48000 - B), slots[s], 0, B); bpos = (bpos + B) % (48000 - B); sp2.Process(slots, src, l, r); } }, 48000 / B);
            Console.WriteLine("  " + ("  " + variant).PadRight(28) + bv.ToString("0").PadLeft(8) + " bytes/s");
        }
        // DeepFilterNet's inference (its worker thread runs this 100 times a second).
        Environment.SetEnvironmentVariable("STREAMLITE_AI", System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        using (var s = new OnnxSession(DeepFilter.ModelPath, false))
        {
            var st = new float[DeepFilter.States]; var st2 = new float[DeepFilter.States]; var fr = new float[DeepFilter.Frame]; var y = new float[DeepFilter.Frame];
            var ins = new float[][] { fr, st, new[] { 100f } }; var outs = new float[][] { y, st2, new float[1] };
            var shp = new[] { new long[] { DeepFilter.Frame }, new long[] { DeepFilter.States }, new long[0] };
            double bd = PerSecond(c => { for (int k = 0; k < c; k++) s.Run(ins, shp, outs); }, 100);
            Console.WriteLine("  " + "OnnxSession.Run (DFN)".PadRight(28) + bd.ToString("0").PadLeft(8) + " bytes/s");
        }
        var radar = new SoundRadar(); radar.SetLayout(SpatialProcessor.Source.Surround71, g.ToConfig());
        double br = PerSecond(c => { for (int k = 0; k < c; k++) radar.Analyze(slots, SpatialProcessor.Source.Surround71, B); }, 48000 / B);
        Console.WriteLine("  " + "SoundRadar.Analyze".PadRight(28) + br.ToString("0").PadLeft(8) + " bytes/s");
        var ring = new StereoRing(48, 480); var inter = new float[B * 2];
        double bring = PerSecond(c => { for (int k = 0; k < c; k++) { ring.Write(l, r, B); ring.InputArrived(); ring.Read(inter, B); } }, 48000 / B);
        Console.WriteLine("  " + "StereoRing".PadRight(28) + bring.ToString("0").PadLeft(8) + " bytes/s");
    }
}
