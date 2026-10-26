using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>
    /// The listening building of "Personalize 3D": three floors. You stand in the middle of the middle floor (one open
    /// room, no walls inside); the floors above and below each have 12 square rooms (4 x 3, 3 m each) whose outlines
    /// you see through your glass ceiling and floor, where they really are. Footsteps play at exact points in metres; you
    /// look where you hear them and click: a room above, a room below, or a wall = on your floor, that way.
    /// x = right, y = up, z = ahead at the start. Everything here is plain geometry and scoring (tested offline).
    /// </summary>
    internal static class Building3D
    {
        public const double RoomSize = 3.0;
        public const int Cols = 4, Rows = 3;
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

        /// <summary>Room 0..11 (row-major from the front-left, as seen at the start) on a floor at (x, z), or -1 outside.</summary>
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

        /// <summary>A random spot on a floor: anywhere in a room above/below, or 2.5-4 m from you on your floor.</summary>
        public static Source RandomSource(int floor, Random rnd, int room = -1)
        {
            var s = new Source { Floor = floor, Room = -1 };
            if (floor == 1)
            {
                double ang = rnd.NextDouble() * 2 * Math.PI, r = 2.5 + rnd.NextDouble() * 1.5;
                double x = Math.Sin(ang) * r, z = Math.Cos(ang) * r;
                x = Math.Max(-HalfX + 0.6, Math.Min(HalfX - 0.6, x)); z = Math.Max(-HalfZ + 0.6, Math.Min(HalfZ - 0.6, z));
                s.Pos = new Vec(x, StepY[1], z);
                return s;
            }
            if (room < 0) room = rnd.Next(Cols * Rows);
            double cx, cz; RoomCenter(room, out cx, out cz);
            double j = RoomSize / 2 - 0.5;
            s.Pos = new Vec(cx + (rnd.NextDouble() * 2 - 1) * j, StepY[floor], cz + (rnd.NextDouble() * 2 - 1) * j);
            s.Room = room;
            return s;
        }

        /// <summary>How a click scored against where the footsteps really were.</summary>
        public sealed class Score
        {
            /// <summary>0 = spot on ... 1 = right floor, 90° or more off ... 1.5 = upstairs and downstairs swapped.</summary>
            public double Error;
            public bool FloorRight, RoomRight, FrontBack;
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
                sc.Text = "It was " + FloorNames[s.Floor] + (s.Floor != 1 ? ", room " + (s.Room + 1) : "") + " - you picked " + FloorNames[a.Floor] + ".";
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
            public string Hrtf; public int Cue, Contrast;
            public double ErrSum; public int N, FloorWrong, FrontBack;
            public double Mean { get { return N == 0 ? double.MaxValue : ErrSum / N; } }
            public override string ToString() { return (Hrtf == HrtfSet.Kemar ? "KEMAR" : "Neumann KU100") + ", cue sharpening " + Cue + "%, floor contrast " + Contrast + "%"; }
        }

        public sealed class Trial { public Variant V; public Source S; public Answer A; public Score Sc; }

        /// <summary>
        /// Round 1: the two head models, each with cue sharpening at 35% and 70% (your floor contrast), every one of
        /// them twice on every floor - 24 footsteps, shuffled, blind.
        /// </summary>
        public static List<Trial> Round1(int contrast, Random rnd, out List<Variant> variants)
        {
            variants = new List<Variant>();
            foreach (var h in new[] { HrtfSet.Ku100, HrtfSet.Kemar })
                foreach (int cue in new[] { 35, 70 }) variants.Add(new Variant { Hrtf = h, Cue = cue, Contrast = contrast });
            var trials = new List<Trial>();
            var rooms = Enumerable.Range(0, Cols * Rows).OrderBy(_ => rnd.Next()).ToList(); int ri = 0;
            foreach (var v in variants)
                for (int f = 0; f < 3; f++)
                    for (int k = 0; k < 2; k++)
                        trials.Add(new Trial { V = v, S = RandomSource(f, rnd, f == 1 ? -1 : rooms[ri++ % rooms.Count]) });
            return trials.OrderBy(_ => rnd.Next()).ToList();
        }

        public static void Record(Trial t, Answer a)
        {
            t.A = a; t.Sc = Judge(a, t.S);
            t.V.ErrSum += t.Sc.Error; t.V.N++;
            if (!t.Sc.FloorRight) t.V.FloorWrong++;
            if (t.Sc.FrontBack) t.V.FrontBack++;
        }

        /// <summary>
        /// Round 1's verdict: the head model with the smaller error over all its footsteps, then the cue sharpening
        /// with the smaller error (both heads together), and the floor contrast up when floors were mixed up often
        /// (down a little when they never were). Ties keep what you have.
        /// </summary>
        public static Variant Decide1(List<Trial> trials, string curHrtf, int curCue, int curContrast)
        {
            Func<IEnumerable<Trial>, double> mean = ts => ts.Any() ? ts.Average(t => t.Sc.Error) : double.MaxValue;
            var done = trials.Where(t => t.Sc != null).ToList();
            double ku = mean(done.Where(t => t.V.Hrtf == HrtfSet.Ku100)), ke = mean(done.Where(t => t.V.Hrtf == HrtfSet.Kemar));
            string head = Math.Abs(ku - ke) < 0.02 ? curHrtf : ku < ke ? HrtfSet.Ku100 : HrtfSet.Kemar;
            double c35 = mean(done.Where(t => t.V.Cue == 35)), c70 = mean(done.Where(t => t.V.Cue == 70));
            int cue = Math.Abs(c35 - c70) < 0.02 ? (curCue >= 53 ? 70 : 35) : c35 < c70 ? 35 : 70;
            double wrong = done.Count == 0 ? 0 : done.Count(t => !t.Sc.FloorRight) / (double)done.Count;
            int contrast = wrong >= 0.3 ? Math.Min(100, curContrast + 25) : wrong >= 0.15 ? Math.Min(100, curContrast + 10) : wrong == 0 && curContrast > 60 ? curContrast - 10 : curContrast;
            return new Variant { Hrtf = head, Cue = cue, Contrast = contrast };
        }

        // ------------------------------------------------------------------ round 2: following footsteps that walk

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
        /// Round 2: six walks - an arc around you on your floor and a straight walk across the rooms above and below -
        /// each with the round-1 winner and with the alternative, alternating.
        /// </summary>
        public static List<Walk> Round2(Variant best, Variant alt, Random rnd)
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
                        double off = (rnd.NextDouble() * 2 - 1) * 2.5, s = rnd.Next(2) == 0 ? 1 : -1;
                        walk.A = alongX ? new Vec(-s * (HalfX - 0.7), StepY[f], off * HalfZ / HalfX) : new Vec(off, StepY[f], -s * (HalfZ - 0.7));
                        walk.B = alongX ? new Vec(s * (HalfX - 0.7), StepY[f], -off * HalfZ / HalfX) : new Vec(-off, StepY[f], s * (HalfZ - 0.7));
                    }
                    w.Add(walk);
                }
            return w.OrderBy(_ => rnd.Next()).ToList();
        }

        /// <summary>The alternative tried in round 2: the other cue sharpening, and floor contrast stepped the other way.</summary>
        public static Variant Alternative(Variant best, bool floorsMixedUp)
        {
            return new Variant { Hrtf = best.Hrtf, Cue = best.Cue >= 53 ? 35 : 70, Contrast = floorsMixedUp ? Math.Min(100, best.Contrast + 20) : Math.Max(40, best.Contrast - 20) };
        }

        /// <summary>Round 2's verdict: the alternative only if you followed the walks clearly better with it (10%).</summary>
        public static Variant Decide2(List<Walk> walks, Variant best, Variant alt)
        {
            double a = Mean(walks.Where(w => w.V == best)), b = Mean(walks.Where(w => w.V == alt));
            return b < a * 0.9 ? alt : best;
        }

        static double Mean(IEnumerable<Walk> ws) { double s = 0; int n = 0; foreach (var w in ws) { s += w.ErrSum; n += w.N; } return n == 0 ? double.MaxValue : s / n; }
    }
}
