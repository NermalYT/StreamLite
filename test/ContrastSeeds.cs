// How often the thorough tuning picks more floor contrast for a listener who mixes up floors at less contrast, over
// several seeds (the Personal3DWorldTest check runs one). Usage: ContrastSeeds
using System;
using System.Linq;
using StreamLite.Audio;
using V = StreamLite.Audio.Building3D.Vec;

class ContrastSeeds
{
    static double Gauss(Random r) { return Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()); }
    static Building3D.Answer Listen(Building3D.Source s, double scatter, double floorMix, Random rnd)
    {
        var target = s.Pos; int floor = s.Floor;
        if (rnd.NextDouble() < floorMix) { floor = s.Floor == 1 ? (rnd.Next(2) == 0 ? 0 : 2) : 1; target = Building3D.RandomSource(floor, rnd).Pos; }
        V v = target - Building3D.Eye;
        double yaw = Math.Atan2(v.X, v.Z) * 180 / Math.PI, pitch = Math.Atan2(v.Y, Math.Sqrt(v.X * v.X + v.Z * v.Z)) * 180 / Math.PI;
        yaw += Gauss(rnd) * scatter; pitch = Math.Max(-89, Math.Min(89, pitch + Gauss(rnd) * scatter * (floor == 1 ? 0.15 : 0.6)));
        return Building3D.Aim(yaw, pitch);
    }
    static void Main()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var rnd = new Random(seed); int up = 0;
            for (int run = 0; run < 200; run++)
            {
                var head = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 60, HeadSize = 100 };
                Building3D.Tuning tu;
                foreach (var t in Building3D.TuneRound(head, rnd, out tu))
                {
                    double sc = 22, mixF = 0.05;
                    if (tu.Contrast.Contains(t.V)) mixF = t.V.Contrast >= 85 ? 0.03 : 0.4;
                    Building3D.Record(t, Listen(t.S, sc, mixF, rnd));
                }
                if (Building3D.DecideTune(head, tu).Contrast == 85) up++;
            }
            Console.WriteLine("seed " + seed + ": more contrast " + up + "/200");
        }
    }
}
