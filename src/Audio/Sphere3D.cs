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

        /// <summary>How accurately you placed footsteps with a head, 0-100 (from its expected error, so it ranks like Ranked).</summary>
        public static double Accuracy(Variant h) { return Math.Max(0, Math.Min(100, Math.Round(100 * (1 - Expected(h)), 1))); }

        /// <summary>"98.6%", or "80%" for a whole number.</summary>
        public static string Percent(double accuracy) { return Math.Round(accuracy, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%"; }

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

        static readonly Kind[] UpDownSteps = { Kind.Up, Kind.Down };
        static readonly Kind[] SideSteps = { Kind.Side, Kind.Side, Kind.Side, Kind.Side, Kind.Side, Kind.Side };
        static readonly Kind[] FrontBackSteps = { Kind.FrontBack, Kind.FrontBack, Kind.FrontBack, Kind.FrontBack, Kind.FrontBack, Kind.FrontBack };

        public static int TuneSteps { get { return 3 * (SizeSteps.Length + CueSteps.Length + ContrastSteps.Length); } }

        /// <summary>Footsteps in the part everyone does (the progress bar): finding your head. The tests after it are your pick.</summary>
        public static int TotalSteps(int heads) { return Math.Max(SearchSteps, heads); }

        // ---- The tests you pick after finding your head (4.3): each tries settings blind and keeps what you placed best.

        public enum Test { Height, Sides, FrontBack, All, Track, TrackLevel }
        public static readonly Test[] Tests = { Test.Height, Test.Sides, Test.FrontBack, Test.All, Test.Track, Test.TrackLevel };
        static readonly string[] TestNames = { "Floor above / below", "Left / right", "Forward / backward", "All directions", "Footstep tracking: everywhere", "Footstep tracking: 360 at your level" };
        static readonly string[] TestAbouts =
        {
            "Footsteps on the floor above or below you. Tunes floor contrast and cue sharpening.",
            "Footsteps to your left or right. Tunes your head size.",
            "Footsteps in front of you or behind you. Tunes cue sharpening.",
            "All six directions. Tunes head size, cue sharpening and floor contrast.",
            "A bot roams three floors around you, up and down stairs, behind walls and crates. Stay on it by ear.",
            "Follow moving footsteps around you at ear level. You only turn left and right."
        };
        public static string TestName(Test t) { return TestNames[(int)t]; }
        public static string TestAbout(Test t) { return TestAbouts[(int)t]; }
        public static bool Tracking(Test t) { return t == Test.Track || t == Test.TrackLevel; }

        /// <summary>How many tests have tuned each setting so far: each one tries closer around where the last one landed.</summary>
        public sealed class Refine { public int Size, Cue, Contrast; }

        static int[] Around(int cur, int step, int passes, int least, int min, int max)
        {
            int d = Math.Max(least, step >> passes);
            return new[] { cur - d, cur, cur + d }.Select(x => Math.Max(min, Math.Min(max, x))).Distinct().ToArray();
        }

        /// <summary>The settings a test tries, around the head's current ones (a setting it doesn't test stays null).</summary>
        public static Tuning TuningFor(Test test, Variant head, Refine r)
        {
            var t = new Tuning();
            if (test != Test.Height && test != Test.FrontBack) t.Size = Around(head.HeadSize, 12, r.Size, 3, 80, 120).Select(s => head.With(headSize: s)).ToList();
            if (test != Test.Sides) t.Cue = Around(head.Cue, 35, r.Cue, 8, 0, 100).Select(c => head.With(cue: c)).ToList();
            if (test == Test.Height || test == Test.All || test == Test.Track) t.Contrast = Around(head.Contrast, 25, r.Contrast, 6, 0, 100).Select(c => head.With(contrast: c)).ToList();
            return t;
        }

        /// <summary>A test is finished: the settings it tried are tried closer next time.</summary>
        public static void Tuned(Refine r, Tuning t) { if (t.Size != null) r.Size++; if (t.Cue != null) r.Cue++; if (t.Contrast != null) r.Contrast++; }

        /// <summary>The footsteps of a test you answer by clicking, shuffled, for the settings in tu.</summary>
        public static List<Trial> TestRound(Test test, Tuning tu, Random rnd)
        {
            var trials = new List<Trial>();
            Action<List<Variant>, Kind[]> add = (vs, kinds) => { foreach (var v in vs) foreach (var k in kinds) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" }); };
            if (test == Test.Height) { add(tu.Contrast, ContrastSteps); add(tu.Cue, UpDownSteps); }
            else if (test == Test.Sides) add(tu.Size, SideSteps);
            else if (test == Test.FrontBack) add(tu.Cue, FrontBackSteps);
            else { add(tu.Size, SizeSteps); add(tu.Cue, CueSteps); add(tu.Contrast, ContrastSteps); }
            // Only two places: plain shuffle (never the same one twice in a row would give the next answer away).
            return test == Test.All ? Spread(trials, rnd) : trials.OrderBy(_ => rnd.Next()).ToList();
        }

        /// <summary>
        /// All directions: your head size (the time between your ears), cue sharpening and floor contrast, each at three
        /// settings, all shuffled together, the rest at the head's current settings.
        /// </summary>
        public static List<Trial> TuneRound(Variant head, Random rnd, out Tuning tuning)
        {
            tuning = TuningFor(Test.All, head, new Refine());
            return TestRound(Test.All, tuning, rnd);
        }

        /// <summary>
        /// Each setting a test tried, from its own footsteps: the one with the smallest error, unless the head's current
        /// setting is within 0.1 of it (then that stays - a tie isn't evidence). Settings it didn't try stay.
        /// </summary>
        public static Variant DecideTune(Variant head, Tuning t)
        {
            Func<List<Variant>, Func<Variant, int>, int, int> pick = (vs, get, cur) =>
            {
                if (vs == null) return cur;
                var b = vs.OrderBy(v => v.Mean).First(); var c = vs.FirstOrDefault(v => get(v) == cur);
                return c != null && c.Mean - b.Mean < 0.1 ? cur : get(b);
            };
            return head.With(headSize: pick(t.Size, v => v.HeadSize, head.HeadSize), cue: pick(t.Cue, v => v.Cue, head.Cue), contrast: pick(t.Contrast, v => v.Contrast, head.Contrast));
        }

        // ---- Footstep tracking: someone walks across the sphere and you keep the view on them.

        /// <summary>Angle between two directions (degrees).</summary>
        public static double AngleBetween(Vec a, Vec b)
        {
            double n = a.Length * b.Length;
            return n <= 0 ? 0 : Math.Acos(Math.Max(-1, Math.Min(1, a.Dot(b) / n))) * 180 / Math.PI;
        }

        /// <summary>Whatever walks in a tracking test: one footstep at a time.</summary>
        public interface IWalker
        {
            Vec Pos { get; }
            Vec Step();
        }

        /// <summary>
        /// The walker's path, one footstep at a time, moving like someone walking (4.3.1): several seconds one way, then it
        /// curves round or slows down and turns back - never a sudden change of direction or pace. Any way on the sphere (up,
        /// down, sideways, diagonally), or only left and right around the ear-level ring. It starts in front of you.
        /// </summary>
        public sealed class Walker : IWalker
        {
            /// <summary>Degrees per footstep at full pace: about 9 to 19 degrees a second.</summary>
            public const double MinStep = 4, MaxStep = 8, MaxEl = 50;
            /// <summary>The most its heading (degrees) and pace (degrees per footstep) change from one footstep to the next.</summary>
            public const double MaxTurn = 12, MaxPaceChange = 1.5;
            const double SteerBack = 20;   // this far below the top or bottom of its range it curves back
            readonly Random rnd; readonly bool level;
            double way, wantWay, pace, wantPace; int left;
            public double Az { get; private set; }
            public double El { get; private set; }
            public Vec Pos { get { return Forward(Az, El) * Radius; } }

            public Walker(Random rnd, bool level)
            {
                this.rnd = rnd; this.level = level;
                Az = (rnd.NextDouble() * 180 - 90 + 360) % 360;
                way = wantWay = rnd.NextDouble() * 360;
                Choose();
                pace = wantPace;
            }

            double Pace() { return MinStep + rnd.NextDouble() * (MaxStep - MinStep); }

            void Choose()
            {
                left = 8 + rnd.Next(11);   // about 3.5 to 8 seconds
                if (level)
                {
                    // Pace is signed around the ring: mostly on the same way, sometimes slowing down and turning back.
                    double sign = wantPace == 0 ? (rnd.Next(2) == 0 ? 1 : -1) : Math.Sign(wantPace) * (rnd.NextDouble() < 0.35 ? -1 : 1);
                    wantPace = sign * Pace();
                }
                else
                {
                    wantPace = Pace();
                    wantWay = way + (rnd.Next(2) == 0 ? 1 : -1) * (30 + rnd.NextDouble() * 120);
                }
            }

            static double Toward(double from, double to, double most) { return from + Math.Max(-most, Math.Min(most, to - from)); }

            public Vec Step()
            {
                if (--left <= 0) Choose();
                pace = Toward(pace, wantPace, MaxPaceChange);
                if (level) { Az = ((Az + pace) % 360 + 360) % 360; return Pos; }
                // Near the top or bottom of its range it heads back (the very pole can't be followed).
                double want = wantWay * Math.PI / 180;
                if ((El > MaxEl - SteerBack && Math.Sin(want) > 0) || (El < -(MaxEl - SteerBack) && Math.Sin(want) < 0)) wantWay = -wantWay;
                double turn = ((wantWay - way) % 360 + 540) % 360 - 180;
                way += Math.Max(-MaxTurn, Math.Min(MaxTurn, turn));
                double w = way * Math.PI / 180;
                double up = pace * Math.Sin(w);
                // Climbing gets gentler towards the limit, so it rounds off there instead of stopping dead.
                if (up * El > 0) up *= Math.Min(1, (MaxEl - Math.Abs(El)) / SteerBack);
                double e = Math.Max(-MaxEl, Math.Min(MaxEl, El + up));
                double middle = (El + e) / 2 * Math.PI / 180;
                Az = ((Az + pace * Math.Cos(w) / Math.Cos(middle)) % 360 + 360) % 360;
                El = e;
                return Pos;
            }
        }

        /// <summary>
        /// One tracking test: the settings tried in walks of footsteps, blind and without a break, twice each in random
        /// order. Each footstep scores how far the view is from the one just heard (you can only follow what you've
        /// heard), as a fraction of 90 degrees, so DecideTune treats it like any other error.
        /// </summary>
        public sealed class Tracker
        {
            public const int WalkSteps = 24, Repeats = 2, ShowSteps = 4;
            /// <summary>Not scored: the first footsteps of the test (finding them) and of each walk (a new setting).</summary>
            public const int FirstSettle = 6, Settle = 2;
            readonly IWalker walker;
            /// <summary>4.4: which footsteps show what you heard (null: all). In the arena, only those of a bot you can't see.</summary>
            readonly Func<Vec, bool> counts;
            int shown; Vec heard; bool anyHeard; double sum; int counted;
            /// <summary>The walks in order; a setting tried in two lists is the same Variant, tried once.</summary>
            public readonly List<Variant> Order;
            public int Done { get; private set; }
            public int Total { get { return Order.Count * WalkSteps; } }
            public bool Finished { get { return Done >= Total; } }
            /// <summary>"Show me" is on: the view shows where they are, and these footsteps count as a full miss.</summary>
            public bool Showing { get { return shown > 0; } }
            /// <summary>Where the footsteps are now.</summary>
            public Vec Pos { get { return walker.Pos; } }
            /// <summary>How far off the view stayed on average, over everything scored (degrees).</summary>
            public double MeanDeg { get { return counted == 0 ? 0 : sum / counted * 90; } }

            public Tracker(Test test, Tuning tu, Random rnd, IWalker path = null, Func<Vec, bool> countsIf = null)
            {
                walker = path ?? new Walker(rnd, test == Test.TrackLevel);
                counts = countsIf;
                var same = new Dictionary<string, Variant>();
                Action<List<Variant>> merge = vs =>
                {
                    if (vs == null) return;
                    for (int i = 0; i < vs.Count; i++)
                    {
                        string key = vs[i].HeadSize + "|" + vs[i].Cue + "|" + vs[i].Contrast; Variant first;
                        if (same.TryGetValue(key, out first)) vs[i] = first; else same[key] = vs[i];
                    }
                };
                merge(tu.Size); merge(tu.Cue); merge(tu.Contrast);
                Order = Enumerable.Repeat(same.Values, Repeats).SelectMany(v => v).OrderBy(_ => rnd.Next()).ToList();
                for (int pass = 0; pass < 30; pass++)
                    for (int i = 1; i < Order.Count; i++)
                        if (Order[i] == Order[i - 1]) { int j = rnd.Next(Order.Count); var t = Order[i]; Order[i] = Order[j]; Order[j] = t; }
            }

            public void ShowMe() { shown = ShowSteps; }

            /// <summary>The next footstep, with the view aimed at aim now: scores the one before, returns where this one is and the setting it plays with.</summary>
            public Variant Next(Vec aim, out Vec pos)
            {
                if (anyHeard && Done - 1 >= FirstSettle && (Done - 1) % WalkSteps >= Settle && (counts == null || counts(heard)))
                {
                    var was = Order[(Done - 1) / WalkSteps];
                    double e = Showing ? 1 : Math.Min(90, AngleBetween(aim, heard)) / 90;
                    was.ErrSum += e; was.N++; was.Errors.Add(e);
                    sum += e; counted++;
                }
                if (shown > 0) shown--;
                var v = Order[Math.Min(Done / WalkSteps, Order.Count - 1)];
                heard = pos = walker.Step(); anyHeard = true;
                Done++;
                return v;
            }
        }
    }
}
