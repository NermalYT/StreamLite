// Which head-search design finds your head best for the footsteps it costs: simulated listeners (as in
// Personal3DWorldTest) under two models - your best head is any head (random), or heads that sound alike work alike
// (scatter grows with how differently a head sounds from yours, measured from the heads' responses). Also tried and
// dropped (3.5.0): trying only the heads that sound most different and then their closest neighbours (the measured
// people are about equally different from each other - 31% found), and stopping early once 90% sure (never was).
// Usage: HeadSearchDesigns
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;
using V = StreamLite.Audio.Building3D.Vec;

class HeadSearchDesigns
{
    static double Gauss(Random r) { return Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()); }
    static Building3D.Answer Listen(Building3D.Source s, double scatter, double floorMix, Random rnd)
    {
        var target = s.Pos; int floor = s.Floor;
        if (rnd.NextDouble() < floorMix) { floor = s.Floor == 1 ? (rnd.Next(2) == 0 ? 0 : 2) : 1; target = Building3D.RandomSource(floor, rnd).Pos; }
        V v = target - Building3D.Eye;
        double yaw = Math.Atan2(v.X, v.Z) * 180 / Math.PI, pitch = Math.Atan2(v.Y, Math.Sqrt(v.X * v.X + v.Z * v.Z)) * 180 / Math.PI;
        if (floor == 1) pitch = 0;
        yaw += Gauss(rnd) * scatter; pitch = Math.Max(-89, Math.Min(89, pitch + Gauss(rnd) * scatter * (floor == 1 ? 0.15 : 0.6)));
        return Building3D.Aim(yaw, pitch);
    }

    /// <summary>A head's directional responses (1/6-octave, 1.5-16 kHz, 12 directions, both ears, minus its own average).</summary>
    static double[] Print(string kind)
    {
        var set = HrtfSet.Get(kind, null, 1.0);
        var fft = new Fft(512); var re = new float[512]; var im = new float[512];
        double[][] dirs = { new[] { 0.0, 0 }, new[] { 180.0, 0 }, new[] { 40.0, 0 }, new[] { 140.0, 0 }, new[] { 320.0, 0 }, new[] { 220.0, 0 }, new[] { 0.0, 40 }, new[] { 180.0, 40 }, new[] { 0.0, -30 }, new[] { 180.0, -30 }, new[] { 90.0, 0 }, new[] { 270.0, 0 } };
        var levels = new List<double[]>();
        foreach (var d in dirs)
        {
            float[] l, r; set.BuildPrecise(d[0], d[1], 0, out l, out r);
            foreach (var ear in new[] { l, r })
            {
                Array.Clear(re, 0, 512); Array.Clear(im, 0, 512); Array.Copy(ear, re, Math.Min(ear.Length, 512));
                fft.Transform(re, im, false);
                var bands = new double[21];
                for (int b = 0; b < 21; b++)
                {
                    double fc = 1500 * Math.Pow(2, b / 6.0), e = 0; int n = 0;
                    for (int k = (int)Math.Ceiling(fc / Math.Pow(2, 1 / 12.0) * 512 / 48000); k <= (int)(fc * Math.Pow(2, 1 / 12.0) * 512 / 48000) && k < 256; k++) { e += re[k] * (double)re[k] + im[k] * (double)im[k]; n++; }
                    if (n == 0) { int k = (int)Math.Round(fc * 512 / 48000); e = re[k] * (double)re[k] + im[k] * (double)im[k]; n = 1; }
                    bands[b] = 10 * Math.Log10(e / n + 1e-12);
                }
                levels.Add(bands);
            }
        }
        var common = new double[21];
        foreach (var v in levels) for (int b = 0; b < 21; b++) common[b] += v[b] / levels.Count;
        return levels.SelectMany(v => v.Select((x, b) => x - common[b])).ToArray();
    }

    class Design { public string Name; public bool Adaptive; public Func<List<Building3D.Variant>, Random, Func<Building3D.Variant, double>, Func<Building3D.Variant, double>, int> Run; }

    static int Play(List<Building3D.Variant> pool, Building3D.Kind[] kinds, Random rnd, Func<Building3D.Variant, double> sc, Func<Building3D.Variant, double> mix)
    {
        var ts = Building3D.HeadRound(pool, kinds, rnd, "x");
        foreach (var t in ts) Building3D.Record(t, Listen(t.S, sc(t.V), mix(t.V), rnd));
        return ts.Count;
    }

    /// <summary>The app's quick search: every head once (front/back), then Building3D.NextTrial up to the budget.</summary>
    static int Adaptive(List<Building3D.Variant> heads, int budget, Random rnd, Func<Building3D.Variant, double> sc, Func<Building3D.Variant, double> mx)
    {
        int n = Play(heads, new[] { Building3D.Kind.FrontBack }, rnd, sc, mx);
        for (; n < budget; n++) { var t = Building3D.NextTrial(heads, rnd, "x"); Building3D.Record(t, Listen(t.S, sc(t.V), mx(t.V), rnd)); }
        return n;
    }

    static void Main()
    {
        var ids = new List<string> { HrtfSet.Ku100, HrtfSet.Kemar }; for (int h = 3; h <= 20; h++) ids.Add(HrtfSet.HumanPrefix + h);
        var prints = ids.ToDictionary(a => a, Print);
        Func<string, string, double> distance = (a, b) => Math.Sqrt(prints[a].Zip(prints[b], (x, y) => (x - y) * (x - y)).Average());
        var dist = ids.ToDictionary(a => a, a => ids.ToDictionary(b => b, b => distance(a, b)));
        var K = Building3D.Kind.FrontBack; var U = Building3D.Kind.Up; var D = Building3D.Kind.Down;
        var designs = new List<Design>
        {
            new Design { Name = "thorough: all x3, 6 x5, 3 x10", Run = (h, r, sc, mx) => { int n = Play(h, Building3D.Search1, r, sc, mx); var p = Building3D.Keep(h, Building3D.Keep1); n += Play(p, Building3D.Search2, r, sc, mx); p = Building3D.Keep(p, Building3D.Keep2); n += Play(p, Building3D.Search3, r, sc, mx); return n; } },
            new Design { Name = "all x1, 6 x2, 2 x4", Run = (h, r, sc, mx) => { int n = Play(h, new[]{K}, r, sc, mx); var p = Building3D.Keep(h, 6); n += Play(p, new[]{U,D}, r, sc, mx); p = Building3D.Keep(p, 2); n += Play(p, new[]{K,U,D,K}, r, sc, mx); return n; } },
            new Design { Name = "all x2, 4 x3, 2 x4", Run = (h, r, sc, mx) => { int n = Play(h, new[]{K,U}, r, sc, mx); var p = Building3D.Keep(h, 4); n += Play(p, new[]{K,D,U}, r, sc, mx); p = Building3D.Keep(p, 2); n += Play(p, new[]{K,U,D,K}, r, sc, mx); return n; } },
        };
        foreach (int budget in new[] { 40, Building3D.QuickSteps, 60 }) { int b = budget; designs.Add(new Design { Name = "quick (adaptive), " + b, Adaptive = true, Run = (h, r, sc, mx) => Adaptive(h, b, r, sc, mx) }); }
        foreach (var model in new[] { "random", "alike" })
        {
            Console.WriteLine("Listener model: " + (model == "random" ? "best head is any head (one 8 deg, one close 14 deg, rest 24-40 deg)" : "heads that sound like yours work like it (8 deg + 5 deg per dB of difference)"));
            foreach (var d in designs)
            {
                var rnd = new Random(7); int found = 0, near = 0, steps = 0; const int Runs = 400;
                for (int run = 0; run < Runs; run++)
                {
                    string yours = ids[rnd.Next(ids.Count)];
                    var others = ids.Where(x => x != yours).ToList();
                    string close = model == "random" ? others[rnd.Next(others.Count)] : others.OrderBy(x => dist[yours][x]).First();
                    var scatter = ids.ToDictionary(x => x, x => x == yours ? 8.0 : model == "random" ? (x == close ? 14.0 : 24 + 16 * rnd.NextDouble()) : 8 + 5 * dist[yours][x]);
                    Func<Building3D.Variant, double> sc = v => scatter[v.Hrtf], mx = v => scatter[v.Hrtf] > 20 ? 0.12 : 0.03;
                    var heads = ids.Select(x => new Building3D.Variant { Hrtf = x }).ToList();
                    steps += d.Run(heads, rnd, sc, mx);
                    var tried = heads.Where(x => x.N > 0).ToList();
                    var win = (d.Adaptive ? Building3D.Ranked(tried)[0] : Building3D.Keep(tried, 1)[0]).Hrtf;
                    if (win == yours) found++;
                    if (win == yours || scatter[win] <= 14.5) near++;
                }
                Console.WriteLine(string.Format("  {0,-32} {1,4} footsteps  your head {2,5:0.0}%  yours or within 14 deg {3,5:0.0}%", d.Name, steps / Runs, found * 100.0 / Runs, near * 100.0 / Runs));
            }
        }
    }
}
