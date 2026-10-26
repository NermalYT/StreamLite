using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// The listening building of "Personalize 3D": three floors. You stand in the middle of the middle floor (one open
    /// room, no walls inside); the floors above and below each have 16 square rooms (4 x 4, 3 m each) whose outlines
    /// you see through your glass ceiling and floor, where they really are. Footsteps play at exact points in metres; you
    /// look where you hear them and click: a room above, a room below, or a wall = on your floor, that way - or say you
    /// can't tell, and it shows you where they were.
    /// x = right, y = up, z = ahead at the start. Everything here is plain geometry and scoring (tested offline).
    /// </summary>
    internal static class Building3D
    {
        public const double RoomSize = 3.0;
        public const int Cols = 4, Rows = 4, Rooms = Cols * Rows;
        public const double HalfX = Cols * RoomSize / 2, HalfZ = Rows * RoomSize / 2;
        /// <summary>Your floor's ceiling height, your ears, and the slab between floors (m).</summary>
        public const double Ceiling = 3.0, Ear = 1.6, Slab = 0.3;
        /// <summary>Where footsteps land on each floor: 0 = below, 1 = your floor, 2 = above.</summary>
        public static readonly double[] StepY = { -Slab - Ceiling, 0, Ceiling + Slab };
        public static readonly string[] FloorNames = { "downstairs", "your floor", "upstairs" };

        public struct Vec
        {
            public double X, Y, Z;
            public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static Vec operator -(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
            public static Vec operator +(Vec a, Vec b) { return new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
            public static Vec operator *(Vec a, double s) { return new Vec(a.X * s, a.Y * s, a.Z * s); }
            public double Dot(Vec b) { return X * b.X + Y * b.Y + Z * b.Z; }
            public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
            public Vec Norm() { double l = Length; return l > 0 ? this * (1 / l) : this; }
        }

        public static readonly Vec Eye = new Vec(0, Ear, 0);

        /// <summary>Looking direction for yaw (degrees, + = turned right) and pitch (+ = up).</summary>
        public static Vec Forward(double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            return new Vec(Math.Sin(y) * Math.Cos(p), Math.Sin(p), Math.Cos(y) * Math.Cos(p));
        }
        public static Vec Right(double yaw) { double y = yaw * Math.PI / 180; return new Vec(Math.Cos(y), 0, -Math.Sin(y)); }
        public static Vec Up(double yaw, double pitch)
        {
            Vec f = Forward(yaw, pitch), r = Right(yaw);
            return new Vec(f.Y * r.Z - f.Z * r.Y, f.Z * r.X - f.X * r.Z, f.X * r.Y - f.Y * r.X);
        }

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

        /// <summary>Angle between two directions from your eyes (degrees).</summary>
        public static double AngleBetween(Vec a, Vec b)
        {
            double c = (a - Eye).Norm().Dot((b - Eye).Norm());
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
        }

        /// <summary>Room 0..15 (row-major from the front-left, as seen at the start) on a floor at (x, z), or -1 outside.</summary>
        public static int RoomAt(double x, double z)
        {
            int c = (int)Math.Floor((x + HalfX) / RoomSize), r = (int)Math.Floor((z + HalfZ) / RoomSize);
            if (c < 0 || c >= Cols || r < 0 || r >= Rows) return -1;
            return (Rows - 1 - r) * Cols + c;
        }

        public static void RoomCenter(int room, out double x, out double z)
        {
            int c = room % Cols, r = Rows - 1 - room / Cols;
            x = -HalfX + (c + 0.5) * RoomSize; z = -HalfZ + (r + 0.5) * RoomSize;
        }

        /// <summary>What a look means: the floor and room (above or below), or a direction on your floor (a wall).</summary>
        public struct Answer { public int Floor, Room; public Vec Point; }

        /// <summary>
        /// What you're looking at. Your floor and ceiling are see-through, with the rooms above and below outlined where
        /// they really are (on the floors their footsteps land on), so a look towards a sound is a look at its room. The
        /// walls of your floor (between your floor and ceiling) mean "on my floor, that way".
        /// </summary>
        public static Answer Aim(double yaw, double pitch)
        {
            Vec d = Forward(yaw, pitch);
            var a = new Answer { Floor = 1, Room = -1 };
            double best = double.MaxValue;
            if (Math.Abs(d.X) > 1e-9) WallHit((HalfX * Math.Sign(d.X)) / d.X, d, ref best, ref a);
            if (Math.Abs(d.Z) > 1e-9) WallHit((HalfZ * Math.Sign(d.Z)) / d.Z, d, ref best, ref a);
            if (best < double.MaxValue) return a;
            // Up or down through the glass: the room on that floor in that direction (at the outer wall if it's past it).
            int floor = d.Y > 0 ? 2 : 0;
            double t = (StepY[floor] - Ear) / d.Y;
            Vec p = Eye + d * t;
            double s = Math.Max(Math.Abs(p.X) / (HalfX - 0.01), Math.Abs(p.Z) / (HalfZ - 0.01));
            if (s > 1) { p = new Vec(p.X / s, p.Y, p.Z / s); }
            a.Floor = floor; a.Point = p; a.Room = RoomAt(p.X, p.Z);
            return a;
        }

        static void WallHit(double t, Vec d, ref double best, ref Answer a)
        {
            if (t <= 0 || t >= best) return;
            Vec p = Eye + d * t;
            if (Math.Abs(p.X) > HalfX + 1e-6 || Math.Abs(p.Z) > HalfZ + 1e-6 || p.Y < -1e-6 || p.Y > Ceiling + 1e-6) return;
            best = t; a.Floor = 1; a.Point = p; a.Room = -1;
        }

        /// <summary>A footstep source: its floor, its exact point (m), and the room it's in (above/below).</summary>
        public struct Source { public int Floor, Room; public Vec Pos; }

        /// <summary>A random spot on a floor: anywhere in a room above/below, or 2.5-4.5 m from you on your floor.</summary>
        public static Source RandomSource(int floor, Random rnd, int room = -1)
        {
            if (floor == 1) return OnYourFloor(rnd.NextDouble() * 360, rnd);
            var s = new Source { Floor = floor, Room = -1 };
            if (room < 0) room = rnd.Next(Rooms);
            double cx, cz; RoomCenter(room, out cx, out cz);
            double j = RoomSize / 2 - 0.5;
            s.Pos = new Vec(cx + (rnd.NextDouble() * 2 - 1) * j, StepY[floor], cz + (rnd.NextDouble() * 2 - 1) * j);
            s.Room = room;
            return s;
        }

        /// <summary>On your floor in a direction (degrees clockwise from ahead), 2.5-4.5 m away.</summary>
        static Source OnYourFloor(double deg, Random rnd)
        {
            double ang = deg * Math.PI / 180, r = 2.5 + rnd.NextDouble() * 2;
            return new Source { Floor = 1, Room = -1, Pos = new Vec(Math.Sin(ang) * r, StepY[1], Math.Cos(ang) * r) };
        }

        /// <summary>What a footstep is there to test: where heads differ most (front/back, up, down) or your head size (sides).</summary>
        public enum Kind { FrontBack, Up, Down, Side }

        public static Source SourceFor(Kind k, Random rnd)
        {
            switch (k)
            {
                case Kind.FrontBack: return OnYourFloor((rnd.Next(2) == 0 ? 0 : 180) + (rnd.NextDouble() * 2 - 1) * 35, rnd);
                case Kind.Side: return OnYourFloor((rnd.Next(2) == 0 ? 90 : 270) + (rnd.NextDouble() * 2 - 1) * 50, rnd);
                case Kind.Up: return RandomSource(2, rnd);
                default: return RandomSource(0, rnd);
            }
        }

        /// <summary>Where a source is, in words ("upstairs, room 7", "on your floor, behind you to the left").</summary>
        public static string Describe(Source s)
        {
            if (s.Floor != 1) return FloorNames[s.Floor] + ", room " + (s.Room + 1);
            double a = Math.Atan2(s.Pos.X, s.Pos.Z) * 180 / Math.PI;   // clockwise from ahead
            string[] dirs = { "ahead", "ahead to the right", "to your right", "behind you to the right", "behind you", "behind you to the left", "to your left", "ahead to the left" };
            int i = (int)Math.Round(((a % 360) + 360) % 360 / 45.0) % 8;
            return "on your floor, " + dirs[i];
        }

        /// <summary>How a click scored against where the footsteps really were.</summary>
        public sealed class Score
        {
            /// <summary>0 = spot on ... 1 = right floor, 90° or more off, or can't tell ... 1.5 = upstairs and downstairs swapped.</summary>
            public double Error;
            public bool FloorRight, RoomRight, FrontBack, CantTell;
            /// <summary>Your floor: how far off the direction was (degrees). Above/below: rooms off (0 = right room).</summary>
            public double OffDeg, RoomsOff;
            public string Text;
        }

        public static Score Judge(Answer a, Source s)
        {
            var sc = new Score { FloorRight = a.Floor == s.Floor };
            if (!sc.FloorRight)
            {
                sc.Error = 1 + 0.5 * (Math.Abs(a.Floor - s.Floor) - 1);
                sc.OffDeg = AngleBetween(a.Point, s.Pos);
                sc.Text = "It was " + Describe(s) + " - you picked " + FloorNames[a.Floor] + ".";
                return sc;
            }
            if (s.Floor == 1)
            {
                double ta = Math.Atan2(s.Pos.X, s.Pos.Z) * 180 / Math.PI, aa = Math.Atan2(a.Point.X, a.Point.Z) * 180 / Math.PI;
                double off = Math.Abs(((ta - aa) % 360 + 540) % 360 - 180);
                // Front and back swapped: far off, but close to the mirror image across your left-right axis.
                double mirror = Math.Abs(((180 - ta - aa) % 360 + 540) % 360 - 180);
                sc.FrontBack = off > 100 && mirror < 40;
                sc.OffDeg = off; sc.Error = Math.Min(1, off / 90); sc.RoomRight = off <= 20;
                sc.Text = sc.FrontBack ? "Front and back swapped (" + off.ToString("0") + "° off)." : off <= 15 ? "Spot on (" + off.ToString("0") + "° off)." : off.ToString("0") + "° off.";
                return sc;
            }
            double ax, az2, sx, sz; RoomCenter(Math.Max(0, a.Room), out ax, out az2); RoomCenter(s.Room, out sx, out sz);
            sc.RoomsOff = Math.Sqrt((ax - sx) * (ax - sx) + (az2 - sz) * (az2 - sz)) / RoomSize;
            sc.RoomRight = a.Room == s.Room;
            sc.OffDeg = AngleBetween(a.Point, s.Pos);
            // By the angle (how far your look was off), not in whole rooms: finer, so fewer footsteps tell settings apart.
            sc.Error = Math.Min(1, sc.OffDeg / 90);
            sc.Text = sc.RoomRight ? "Right room (" + FloorNames[s.Floor] + ", room " + (s.Room + 1) + ")." : "Right floor, " + sc.RoomsOff.ToString("0.#") + " room" + (sc.RoomsOff >= 1.5 ? "s" : "") + " off (it was room " + (s.Room + 1) + ").";
            return sc;
        }

        // ------------------------------------------------------------------ settings being compared

        public sealed class Variant
        {
            public string Hrtf = HrtfSet.Ku100; public int Cue = 35, Contrast = 75, HeadSize = 100;
            public double ErrSum; public int N, FloorWrong, FrontBack, CantTell;
            public double Mean { get { return N == 0 ? double.MaxValue : ErrSum / N; } }
            public readonly List<double> Errors = new List<double>();
            /// <summary>The mean without the single worst footstep (a lapse shouldn't decide a setting tried 4-5 times).</summary>
            public double Trimmed { get { return Errors.Count < 4 ? Mean : (Errors.Sum() - Errors.Max()) / (Errors.Count - 1); } }
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

        public sealed class Trial { public Variant V; public Source S; public Kind K; public Answer A; public Score Sc; public string Stage; }

        public static void Record(Trial t, Answer a)
        {
            t.A = a; t.Sc = Judge(a, t.S);
            Count(t);
        }

        /// <summary>"Can't tell": as bad as a wrong floor for the setting being tried, and you're shown where it was.</summary>
        public static void RecordCantTell(Trial t)
        {
            t.Sc = new Score { Error = 1, CantTell = true, OffDeg = 90, Text = "It was " + Describe(t.S) + " - listen." };
            Count(t);
        }

        static void Count(Trial t)
        {
            t.V.ErrSum += t.Sc.Error; t.V.N++; t.V.Errors.Add(t.Sc.Error);
            if (t.S.Floor != 1 && !t.Sc.FloorRight) t.V.FloorWrong++;
            if (t.Sc.FrontBack) t.V.FrontBack++;
            if (t.Sc.CantTell) t.V.CantTell++;
        }

        // ------------------------------------------------------------------ stage 1: finding your head

        /// <summary>
        /// A round of the head search: every head gets the same footsteps (one per kind, at the same spots, so no head
        /// gets the easy ones), shuffled together, blind.
        /// </summary>
        public static List<Trial> HeadRound(IList<Variant> heads, Kind[] kinds, Random rnd, string stage)
        {
            var spots = kinds.Select(k => SourceFor(k, rnd)).ToArray();
            var trials = new List<Trial>();
            foreach (var h in heads)
                for (int i = 0; i < kinds.Length; i++) trials.Add(new Trial { V = h, S = spots[i], K = kinds[i], Stage = stage });
            return Spread(trials, rnd);
        }

        /// <summary>Shuffled, but the same spot never twice in a row (it would be a free hint).</summary>
        static List<Trial> Spread(List<Trial> trials, Random rnd)
        {
            var list = trials.OrderBy(_ => rnd.Next()).ToList();
            for (int pass = 0; pass < 30; pass++)
                for (int i = 1; i < list.Count; i++)
                    if (Same(list[i].S, list[i - 1].S))
                    {
                        int j = rnd.Next(list.Count); var t = list[i]; list[i] = list[j]; list[j] = t;
                    }
            return list;
        }

        static bool Same(Source a, Source b) { return (a.Pos - b.Pos).Length < 1e-9; }

        /// <summary>The n heads with the smallest error over everything so far (ties: the earlier in the list).</summary>
        public static List<Variant> Keep(IList<Variant> heads, int n)
        {
            return heads.Select((h, i) => new { h, i }).OrderBy(x => Math.Round(x.h.Mean, 3)).ThenBy(x => x.h.CantTell).ThenBy(x => x.i).Take(n).Select(x => x.h).ToList();
        }

        /// <summary>The kinds of footstep for each round of the head search.</summary>
        public static readonly Kind[] Search1 = { Kind.FrontBack, Kind.Up, Kind.Down };
        public static readonly Kind[] Search2 = { Kind.FrontBack, Kind.Up, Kind.Down, Kind.FrontBack, Kind.Side };
        public static readonly Kind[] Search3 = { Kind.FrontBack, Kind.Up, Kind.Down, Kind.Side, Kind.FrontBack, Kind.Up, Kind.Down, Kind.FrontBack, Kind.Up, Kind.Down };
        /// <summary>How many heads go on to the next round.</summary>
        public const int Keep1 = 6, Keep2 = 3;

        // ------------------------------------------------------------------ stage 2: tuning the winner

        public sealed class Tuning { public List<Variant> Size, Cue, Contrast; }

        /// <summary>
        /// Your head size (how far apart your ears are: the time between them), cue sharpening and floor contrast, each
        /// tried at three settings with the footsteps it changes most - sides, front/back and up/down - all shuffled
        /// together, the rest at the head's current settings.
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
            foreach (var v in tuning.Size) foreach (var k in new[] { Kind.Side, Kind.Side, Kind.Side, Kind.FrontBack }) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            foreach (var v in tuning.Cue) foreach (var k in new[] { Kind.FrontBack, Kind.Up, Kind.Down, Kind.FrontBack, Kind.Up }) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            foreach (var v in tuning.Contrast) foreach (var k in new[] { Kind.Up, Kind.Down, Kind.Up, Kind.Down, Kind.Up, Kind.Down }) trials.Add(new Trial { V = v, S = SourceFor(k, rnd), K = k, Stage = "tune" });
            return Spread(trials, rnd);
        }

        /// <summary>
        /// Each setting from its own footsteps: the one with the smallest error (head size and cue sharpening without
        /// their worst footstep), unless it's within 0.03 of the head's
        /// current setting (then that stays - a tie isn't evidence).
        /// </summary>
        public static Variant DecideTune(Variant head, Tuning t)
        {
            Func<List<Variant>, Func<Variant, int>, int, bool, int> pick = (vs, get, cur, trim) =>
            {
                Func<Variant, double> m = v => trim ? v.Trimmed : v.Mean;
                var b = vs.OrderBy(m).First(); var c = vs.FirstOrDefault(v => get(v) == cur);
                return c != null && m(c) - m(b) < 0.03 ? cur : get(b);
            };
            // Floor contrast is about floors mixed up: every miss counts there.
            return head.With(headSize: pick(t.Size, v => v.HeadSize, head.HeadSize, true), cue: pick(t.Cue, v => v.Cue, head.Cue, true), contrast: pick(t.Contrast, v => v.Contrast, head.Contrast, false));
        }

        // ------------------------------------------------------------------ stage 3: following footsteps that walk

        /// <summary>A walk: 8 s of footsteps along a line or arc on one floor.</summary>
        public sealed class Walk
        {
            public int Floor; public Variant V;
            public Vec A, B; public bool Arc; public double ArcFrom, ArcTo, ArcR;
            public const double Seconds = 8;
            public double ErrSum; public int N;
            public double Mean { get { return N == 0 ? double.MaxValue : ErrSum / N; } }

            public Vec At(double t)
            {
                double u = Math.Max(0, Math.Min(1, t / Seconds));
                if (Arc) { double a = (ArcFrom + (ArcTo - ArcFrom) * u) * Math.PI / 180; return new Vec(Math.Sin(a) * ArcR, StepY[Floor], Math.Cos(a) * ArcR); }
                return A + (B - A) * u;
            }
        }

        /// <summary>
        /// The walks: an arc around you on your floor and a straight walk across the rooms above and below - each with
        /// the winner and with the runner-up, alternating.
        /// </summary>
        public static List<Walk> Walks(Variant best, Variant alt, Random rnd)
        {
            var w = new List<Walk>();
            for (int k = 0; k < 2; k++)
                for (int f = 0; f < 3; f++)
                {
                    var v = (k + f) % 2 == 0 ? best : alt;
                    var walk = new Walk { Floor = f, V = v };
                    if (f == 1)
                    {
                        walk.Arc = true; walk.ArcR = 3.2; walk.ArcFrom = rnd.Next(360); walk.ArcTo = walk.ArcFrom + (rnd.Next(2) == 0 ? 160 : -160);
                    }
                    else
                    {
                        bool alongX = rnd.Next(2) == 0;
                        double off = (rnd.NextDouble() * 2 - 1) * 3.5, s = rnd.Next(2) == 0 ? 1 : -1;
                        walk.A = alongX ? new Vec(-s * (HalfX - 0.7), StepY[f], off) : new Vec(off, StepY[f], -s * (HalfZ - 0.7));
                        walk.B = alongX ? new Vec(s * (HalfX - 0.7), StepY[f], -off) : new Vec(-off, StepY[f], s * (HalfZ - 0.7));
                    }
                    w.Add(walk);
                }
            return w.OrderBy(_ => rnd.Next()).ToList();
        }

        /// <summary>The walks' verdict: the runner-up only if you followed them clearly better with it (10%).</summary>
        public static Variant DecideWalks(List<Walk> walks, Variant best, Variant alt)
        {
            double a = Mean(walks.Where(w => w.V == best)), b = Mean(walks.Where(w => w.V == alt));
            return b < a * 0.9 ? alt : best;
        }

        public static double Mean(IEnumerable<Walk> ws) { double s = 0; int n = 0; foreach (var w in ws) { s += w.ErrSum; n += w.N; } return n == 0 ? double.MaxValue : s / n; }
    }
}
