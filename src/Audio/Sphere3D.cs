using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// The listening sphere of "Personalize 3D" (3.5.8). You are in the middle of a sphere; footsteps play on its edge
    /// from one of the six directions around your head - in front, behind, left, right, above, below - in random order.
    /// The view only ever faces one of those six (the mouse snaps it to the next one) and you click the one you heard.
    /// The footsteps are fixed around your head: turning the view only points, it never moves them.
    /// x = right, y = up, z = ahead. Plain geometry, scoring and the test's decisions (tested offline).
    /// </summary>
    internal static class Sphere3D
    {
        /// <summary>How far the footsteps are (m): the sphere's radius.</summary>
        public const double Radius = 3.0;

        /// <summary>The six directions around your head. Front..Left go round clockwise seen from above.</summary>
        public enum Dir { Front, Right, Behind, Left, Up, Down }
        public static readonly Dir[] Dirs = { Dir.Front, Dir.Right, Dir.Behind, Dir.Left, Dir.Up, Dir.Down };
        static readonly string[] Names = { "in front of you", "to your right", "behind you", "to your left", "above you", "below you" };
        static readonly string[] Labels = { "FRONT", "RIGHT", "BEHIND", "LEFT", "ABOVE", "BELOW" };

        public struct Vec
        {
            public double X, Y, Z;
            public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static Vec operator -(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
            public static Vec operator +(Vec a, Vec b) { return new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
            public static Vec operator *(Vec a, double s) { return new Vec(a.X * s, a.Y * s, a.Z * s); }
            public double Dot(Vec b) { return X * b.X + Y * b.Y + Z * b.Z; }
            public Vec Cross(Vec b) { return new Vec(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X); }
            public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        }

        /// <summary>Your head: the sphere's middle.</summary>
        public static readonly Vec Eye = new Vec(0, 0, 0);

        // ------------------------------------------------------------------ directions

        public static string Name(Dir d) { return Names[(int)d]; }
        public static string Label(Dir d) { return Labels[(int)d]; }
        public static bool Level(Dir d) { return d != Dir.Up && d != Dir.Down; }
        /// <summary>2 = above, 1 = ear level, 0 = below.</summary>
        public static int Height(Dir d) { return d == Dir.Up ? 2 : d == Dir.Down ? 0 : 1; }

        /// <summary>A direction as a unit vector (x right, y up, z ahead).</summary>
        public static Vec Unit(Dir d)
        {
            switch (d)
            {
                case Dir.Front: return new Vec(0, 0, 1);
                case Dir.Right: return new Vec(1, 0, 0);
                case Dir.Behind: return new Vec(0, 0, -1);
                case Dir.Left: return new Vec(-1, 0, 0);
                case Dir.Up: return new Vec(0, 1, 0);
                default: return new Vec(0, -1, 0);
            }
        }

        /// <summary>Where on the sphere a direction's footsteps play.</summary>
        public static Vec Point(Dir d) { return Eye + Unit(d) * Radius; }

        /// <summary>The view facing a direction: yaw (+ = turned right) and pitch (+ = up). Up and down keep the yaw
        /// of the way you faced before (level), so the picture stays the right way round.</summary>
        public static void View(Dir d, Dir level, out double yaw, out double pitch)
        {
            yaw = 90 * (int)(Level(d) ? d : level); pitch = d == Dir.Up ? 90 : d == Dir.Down ? -90 : 0;
        }

        /// <summary>The next view turning right (+1) or left (-1); looking up or down, turning does nothing.</summary>
        public static Dir Turn(Dir d, int step) { return Level(d) ? (Dir)((((int)d + step) % 4 + 4) % 4) : d; }

        /// <summary>The next view tilting up (+1) or down (-1): from ear level up or down, from up or down back to level.</summary>
        public static Dir Tilt(Dir d, Dir level, int step)
        {
            if (Level(d)) return step > 0 ? Dir.Up : Dir.Down;
            if ((d == Dir.Up && step < 0) || (d == Dir.Down && step > 0)) return level;
            return d;
        }

        /// <summary>Angle between two directions (0, 90 or 180 degrees).</summary>
        public static double Angle(Dir a, Dir b) { return Math.Acos(Math.Max(-1, Math.Min(1, Unit(a).Dot(Unit(b))))) * 180 / Math.PI; }

        // ------------------------------------------------------------------ camera helpers (the view)

        /// <summary>Looking direction for yaw (degrees, + = turned right) and pitch (+ = up).</summary>
        public static Vec Forward(double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            return new Vec(Math.Sin(y) * Math.Cos(p), Math.Sin(p), Math.Cos(y) * Math.Cos(p));
        }
        public static Vec Right(double yaw) { double y = yaw * Math.PI / 180; return new Vec(Math.Cos(y), 0, -Math.Sin(y)); }
        public static Vec Up(double yaw, double pitch) { return Forward(yaw, pitch).Cross(Right(yaw)); }

        /// <summary>
        /// Where a point is, seen from your head turned to (yaw, pitch): azimuth anticlockwise (90 = left, as the head
        /// responses count) and elevation, degrees, and the distance.
        /// </summary>
        public static void Direction(Vec p, double yaw, double pitch, out double az, out double el, out double dist)
        {
            Vec v = p - Eye, f = Forward(yaw, pitch), r = Right(yaw), u = Up(yaw, pitch);
            double x = v.Dot(r), y = v.Dot(u), z = v.Dot(f);
            az = Math.Atan2(-x, z) * 180 / Math.PI; if (az < 0) az += 360;
            el = Math.Atan2(y, Math.Sqrt(x * x + z * z)) * 180 / Math.PI;
            dist = v.Length;
        }

        // ------------------------------------------------------------------ footsteps and answers

        /// <summary>A footstep source: its direction and its point on the sphere.</summary>
        public struct Source
        {
            public Dir D; public Vec Pos;
            public int Height { get { return Sphere3D.Height(D); } }
        }

        public static Source At(Dir d) { return new Source { D = d, Pos = Point(d) }; }

        /// <summary>What a footstep is there to test: where heads differ most (front/back, up, down) or your head size (sides).</summary>
        public enum Kind { FrontBack, Up, Down, Side }

        public static Source SourceFor(Kind k, Random rnd)
        {
            switch (k)
            {
                case Kind.FrontBack: return At(rnd.Next(2) == 0 ? Dir.Front : Dir.Behind);
                case Kind.Side: return At(rnd.Next(2) == 0 ? Dir.Left : Dir.Right);
                case Kind.Up: return At(Dir.Up);
                default: return At(Dir.Down);
            }
        }

        /// <summary>How an answer scored against where the footsteps really were.</summary>
        public sealed class Score
        {
            /// <summary>0 = right, 1 = a quarter turn off (or can't tell), 1.5 = the opposite side.</summary>
            public double Error;
            public bool Right, HeightRight, FrontBack, CantTell;
            public double OffDeg;
            public string Text;
        }

        public static Score Judge(Dir answer, Source s)
        {
            double off = Angle(answer, s.D);
            var sc = new Score { OffDeg = off, Right = answer == s.D, HeightRight = Height(answer) == s.Height };
            sc.FrontBack = (s.D == Dir.Front && answer == Dir.Behind) || (s.D == Dir.Behind && answer == Dir.Front);
            sc.Error = sc.Right ? 0 : off > 135 ? 1.5 : 1;
            sc.Text = sc.Right ? "Right - " + Name(s.D) + "."
                : sc.FrontBack ? "Front and back swapped - it was " + Name(s.D) + "."
                : "It was " + Name(s.D) + " - you picked " + Name(answer) + ".";
            return sc;
        }

        // ------------------------------------------------------------------ settings being compared

        public sealed class Variant
        {
            public string Hrtf = HrtfSet.Ku100; public int Cue = 35, Contrast = 75, HeadSize = 100;
            public double ErrSum; public int N, HeightWrong, FrontBack, CantTell;
            public double Mean { get { return N == 0 ? double.MaxValue : ErrSum / N; } }
            public readonly List<double> Errors = new List<double>();
            public Variant With(int? cue = null, int? contrast = null, int? headSize = null, string hrtf = null)
            {
                return new Variant { Hrtf = hrtf ?? Hrtf, Cue = cue ?? Cue, Contrast = contrast ?? Contrast, HeadSize = headSize ?? HeadSize };
            }
            public string HeadName { get { return HrtfSet.DisplayName(Hrtf); } }
            public override string ToString()
            {
                return HeadName + (HeadSize != 100 ? " (head size " + HeadSize + "%)" : "") + ", cue sharpening " + Cue + "%, floor contrast " + Contrast + "%";
            }
        }

        public sealed class Trial { public Variant V; public Source S; public Kind K; public Dir? A; public Score Sc; public string Stage; }

        public static void Record(Trial t, Dir answer)
        {
            t.A = answer; t.Sc = Judge(answer, t.S);
            Count(t);
        }

        /// <summary>"Can't tell": a quarter turn off for the setting being tried, and you're shown where it was.</summary>
        public static void RecordCantTell(Trial t)
        {
            t.Sc = new Score { Error = 1, CantTell = true, OffDeg = 90, Text = "It was " + Name(t.S.D) + " - listen." };
            Count(t);
        }

        static void Count(Trial t)
        {
            t.V.ErrSum += t.Sc.Error; t.V.N++; t.V.Errors.Add(t.Sc.Error);
            if (!t.Sc.CantTell && !t.Sc.HeightRight) t.V.HeightWrong++;
            if (t.Sc.FrontBack) t.V.FrontBack++;
            if (t.Sc.CantTell) t.V.CantTell++;
        }

        // ------------------------------------------------------------------ part 1: finding your head

        /// <summary>Footsteps for finding your head: every head once, then each where it tells most.</summary>
        public const int SearchSteps = 48;

        /// <summary>Every head one footstep, all at the same spot kind (none gets the easy ones), shuffled, blind.</summary>
        public static List<Trial> FirstRound(IList<Variant> heads, Random rnd)
        {
            var trials = heads.Select(h => new Trial { V = h, K = Kind.FrontBack, S = SourceFor(Kind.FrontBack, rnd), Stage = "search" }).ToList();
            return Spread(trials, rnd);
        }

        /// <summary>Shuffled, but the same direction hardly ever twice in a row (it would be a free hint).</summary>
        static List<Trial> Spread(List<Trial> trials, Random rnd)
        {
            var list = trials.OrderBy(_ => rnd.Next()).ToList();
            for (int pass = 0; pass < 30; pass++)
                for (int i = 1; i < list.Count; i++)
                    if (list[i].S.D == list[i - 1].S.D)
                    {
                        int j = rnd.Next(list.Count); var t = list[i]; list[i] = list[j]; list[j] = t;
                    }
            return list;
        }

        const double PriorError = 0.5, ErrorSpread = 0.45;

        /// <summary>A head's expected error from its footsteps so far, pulled towards the typical error while it has few.</summary>
        public static double Expected(Variant h) { return (PriorError + h.ErrSum) / (h.N + 1); }

        /// <summary>The heads in the order the footsteps rank them (smallest expected error first).</summary>
        public static List<Variant> Ranked(IList<Variant> heads) { return heads.OrderBy(Expected).ThenBy(h => h.CantTell).ToList(); }

        /// <summary>
        /// Which head the next footstep tries: the likely best one or its strongest rival, each half the time (top-two
        /// Thompson sampling - a plausible error is drawn for every head from what its footsteps showed; the smallest
        /// is the leader, a second draw that makes another head the smallest gives the rival). Footsteps go where the
        /// winner is still in doubt, none to heads that are clearly out.
        /// </summary>
        public static Variant NextHead(IList<Variant> heads, Random rnd)
        {
            Func<Variant> draw = () =>
            {
                Variant best = null; double bv = double.MaxValue;
                foreach (var h in heads)
                {
                    double v = Expected(h) + Gauss(rnd) * ErrorSpread / Math.Sqrt(h.N + 1);
                    if (v < bv) { bv = v; best = h; }
                }
                return best;
            };
            var leader = draw();
            if (rnd.NextDouble() < 0.5) return leader;
            for (int i = 0; i < 100; i++) { var c = draw(); if (c != leader) return c; }
            return leader;
        }

        /// <summary>The next search footstep: for the head NextHead picks, front/back, up and down in turn (where heads differ).</summary>
        public static Trial NextTrial(IList<Variant> heads, Random rnd)
        {
            var h = NextHead(heads, rnd);
            var kinds = new[] { Kind.FrontBack, Kind.Up, Kind.Down };
            var k = kinds[h.N % kinds.Length];
            return new Trial { V = h, K = k, S = SourceFor(k, rnd), Stage = "search" };
        }

        static double Gauss(Random r) { return Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()); }

        // ------------------------------------------------------------------ part 2: tuning the winner

        public sealed class Tuning { public List<Variant> Size, Cue, Contrast; }

        // Each setting is tried with the footsteps it changes most: head size with the sides, cue sharpening with
        // front/back and height, floor contrast with above and below.
        static readonly Kind[] SizeSteps = { Kind.Side, Kind.Side, Kind.Side, Kind.FrontBack };
        static readonly Kind[] CueSteps = { Kind.FrontBack, Kind.FrontBack, Kind.Up, Kind.Down };
        static readonly Kind[] ContrastSteps = { Kind.Up, Kind.Down, Kind.Up, Kind.Down };

        public static int TuneSteps { get { return 3 * (SizeSteps.Length + CueSteps.Length + ContrastSteps.Length); } }

        /// <summary>Footsteps in the whole test (the progress bar).</summary>
        public static int TotalSteps(int heads) { return Math.Max(SearchSteps, heads) + TuneSteps; }

        /// <summary>
        /// Everything tuned, every time: your head size (the time between your ears), cue sharpening and floor contrast,
        /// each at three settings, all shuffled together, the rest at the head's current settings.
        /// </summary>
        public static List<Trial> TuneRound(Variant head, Random rnd, out Tuning tuning)
        {
            tuning = new Tuning
            {
                Size = new[] { 88, 100, 112 }.Select(s => head.With(headSize: s)).ToList(),
                Cue = new[] { 0, 35, 70 }.Select(c => head.With(cue: c)).ToList(),
                Contrast = new[] { head.Contrast - 25, head.Contrast, head.Contrast + 25 }.Select(c => Math.Max(0, Math.Min(100, c))).Distinct().Select(c => head.With(contrast: c)).ToList()
            };
            var trials = new List<Trial>();
            foreach (var v in tuning.Size) foreach (var k in SizeSteps) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            foreach (var v in tuning.Cue) foreach (var k in CueSteps) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            foreach (var v in tuning.Contrast) foreach (var k in ContrastSteps) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            return Spread(trials, rnd);
        }

        /// <summary>
        /// Each setting from its own footsteps: the one with the smallest error, unless the head's current setting is
        /// within 0.1 of it (then that stays - a tie isn't evidence).
        /// </summary>
        public static Variant DecideTune(Variant head, Tuning t)
        {
            Func<List<Variant>, Func<Variant, int>, int, int> pick = (vs, get, cur) =>
            {
                var b = vs.OrderBy(v => v.Mean).First(); var c = vs.FirstOrDefault(v => get(v) == cur);
                return c != null && c.Mean - b.Mean < 0.1 ? cur : get(b);
            };
            return head.With(headSize: pick(t.Size, v => v.HeadSize, head.HeadSize), cue: pick(t.Cue, v => v.Cue, head.Cue), contrast: pick(t.Contrast, v => v.Contrast, head.Contrast));
        }
    }
}
