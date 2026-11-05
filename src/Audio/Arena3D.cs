using System;
using System.Collections.Generic;
using System.Linq;
using V = StreamLite.Audio.Sphere3D.Vec;

namespace StreamLite.Audio
{
    /// <summary>
    /// The arena of footstep tracking (4.4): a plain cube on three floors, plain coloured boxes only. You stand still in the
    /// middle of the middle floor (your eyes at 0,0,0; x = right, y = up, z = ahead, metres) with walls and crates around you;
    /// a bot walks all over it: round your floor, down the stairs into the lower ring, up the other stairs to the walkway
    /// above, behind everything, in and out of your sight, its footsteps always where it really is. Your head never moves, so
    /// the drawing order of everything is worked out once.
    /// </summary>
    internal sealed class Arena3D
    {
        public enum Kind { Wall, Floor, Crate, Step, Pillar, Ledge }

        public sealed class Box
        {
            public double X0, Y0, Z0, X1, Y1, Z1; public Kind K;
            public double Lo(int axis) { return axis == 0 ? X0 : axis == 1 ? Y0 : Z0; }
            public double Hi(int axis) { return axis == 0 ? X1 : axis == 1 ? Y1 : Z1; }
            public V Center { get { return new V((X0 + X1) / 2, (Y0 + Y1) / 2, (Z0 + Z1) / 2); } }
        }

        public const double Half = 16, FloorY = -1.6, LowerY = -4.6, UpperY = 1.6, BotHeight = 1.8, StepLength = 0.75;
        public readonly List<Box> Boxes = new List<Box>();
        /// <summary>Back to front from your eyes: a box never comes after one it's behind.</summary>
        public int[] Order;
        /// <summary>For each box, the boxes drawn after it because they're in front of it.</summary>
        public int[][] InFrontOf;
        public readonly List<V> Nodes = new List<V>();
        public readonly List<List<int>> Links = new List<List<int>>();

        public Arena3D()
        {
            Build();
            Sort();
        }

        void Add(Kind k, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            Boxes.Add(new Box { K = k, X0 = Math.Min(x0, x1), Y0 = Math.Min(y0, y1), Z0 = Math.Min(z0, z1), X1 = Math.Max(x0, x1), Y1 = Math.Max(y0, y1), Z1 = Math.Max(z0, z1) });
        }

        void Build()
        {
            const double Top = 8, H = Half;
            // The cube.
            Add(Kind.Floor, -H, LowerY - 0.4, -H, H, LowerY, H);
            Add(Kind.Wall, -H - 1, LowerY - 0.4, H, H + 1, Top, H + 1);
            Add(Kind.Wall, -H - 1, LowerY - 0.4, -H - 1, H + 1, Top, -H);
            Add(Kind.Wall, H, LowerY - 0.4, -H, H + 1, Top, H);
            Add(Kind.Wall, -H - 1, LowerY - 0.4, -H, -H, Top, H);
            Add(Kind.Wall, -H - 1, Top, -H - 1, H + 1, Top + 1, H + 1);
            // Your floor: a solid block in the middle.
            Add(Kind.Floor, -6, LowerY, -6, 6, FloorY, 6);
            // The walkway above, round the walls.
            Add(Kind.Floor, -H, UpperY - 0.4, 12, H, UpperY, H);
            Add(Kind.Floor, -H, UpperY - 0.4, -H, H, UpperY, -12);
            Add(Kind.Floor, 12, UpperY - 0.4, -12, H, UpperY, 12);
            Add(Kind.Floor, -H, UpperY - 0.4, -12, -12, UpperY, 12);
            // Its low wall, open where the stairs come up.
            foreach (int s in new[] { 1, -1 })
            {
                Add(Kind.Ledge, -12, UpperY, s * 12, -1.2, UpperY + 1, s * 12.25);
                Add(Kind.Ledge, 1.2, UpperY, s * 12, 12, UpperY + 1, s * 12.25);
                Add(Kind.Ledge, s * 12, UpperY, -12, s * 12.25, UpperY + 1, 12);
            }
            // What holds it up.
            foreach (int sx in new[] { 1, -1 })
                foreach (int sz in new[] { 1, -1 })
                    Add(Kind.Pillar, sx * 12.3, LowerY, sz * 12.3, sx * 13.3, UpperY - 0.4, sz * 13.3);
            foreach (int s in new[] { 1, -1 })
            {
                Add(Kind.Pillar, s * 12.3, LowerY, -0.5, s * 13.3, UpperY - 0.4, 0.5);
                Add(Kind.Pillar, -0.5, LowerY, s * 12.3, 0.5, UpperY - 0.4, s * 13.3);
            }
            // Stairs down to the lower ring, left and right of you: solid steps.
            foreach (int s in new[] { 1, -1 })
                for (int i = 0; i < 9; i++)
                    Add(Kind.Step, s * (6 + 0.4 * i), LowerY, -1.1, s * (6 + 0.4 * (i + 1)), FloorY - (LowerY - FloorY) / -9.0 * 0 - 0.3 * (i + 1), 1.1);
            // Stairs up to the walkway, ahead of you and behind you: plates in the air.
            foreach (int s in new[] { 1, -1 })
                for (int i = 0; i < 12; i++)
                {
                    double top = FloorY + (UpperY - FloorY) * (i + 1) / 12.0;
                    Add(Kind.Step, -1.1, top - 0.15, s * (6 + 0.5 * i), 1.1, top, s * (6 + 0.5 * (i + 1)));
                }
            // Around you on your floor: tall walls and crates, nothing up against you.
            Add(Kind.Wall, 2.5, FloorY, 1, 2.8, FloorY + 2.8, 3.5);
            Add(Kind.Wall, -2.8, FloorY, -3.5, -2.5, FloorY + 2.8, -1);
            Add(Kind.Wall, -3.5, FloorY, 2.5, -1, FloorY + 2.8, 2.8);
            Add(Kind.Wall, 1, FloorY, -2.8, 3.5, FloorY + 2.8, -2.5);
            Crate(3, -1.5, 1.2, 1.2, FloorY); Crate(-4.2, 0.3, 1.2, 2.2, FloorY); Crate(0.5, 3.2, 1.2, 1.2, FloorY); Crate(-1.7, -4.4, 1.2, 2.2, FloorY);
            Crate(3, 3, 1.2, 2.2, FloorY); Crate(-4.2, -4.2, 1.2, 1.2, FloorY); Crate(-4.2, 3, 1.2, 1.2, FloorY); Crate(3, -4.2, 1.2, 2.2, FloorY);
            // In the lower ring.
            foreach (var c in new[] { new[] { 8, 4.0 }, new[] { 8, -5.0 }, new[] { -8, 5.0 }, new[] { -8, -4.0 }, new[] { 4, 8.0 }, new[] { -5, 8.0 }, new[] { 5, -8.0 }, new[] { -4, -8.0 } })
                Crate(c[0] - 0.75, c[1] - 0.75, 1.5, 1.5, LowerY);
            foreach (var c in new[] { new[] { 8.2, 8.2 }, new[] { -9.2, -9.2 }, new[] { -9.2, 8.2 }, new[] { 8.2, -9.2 } }) Crate(c[0], c[1], 1, 3, LowerY);

            // Where the bot can walk: a loop on each floor, joined by the four stairs.
            Func<double, double, double, int> node = (x, y, z) => { Nodes.Add(new V(x, y, z)); Links.Add(new List<int>()); return Nodes.Count - 1; };
            Action<int, int> link = (a, b) => { Links[a].Add(b); Links[b].Add(a); };
            Func<double, double, int[]> loop = (d, y) =>
            {
                var ring = new[] { node(d, y, d), node(0, y, d), node(-d, y, d), node(-d, y, 0), node(-d, y, -d), node(0, y, -d), node(d, y, -d), node(d, y, 0) };
                for (int i = 0; i < 8; i++) link(ring[i], ring[(i + 1) % 8]);
                return ring;
            };
            var low = loop(11, LowerY); var mid = loop(5, FloorY); var up = loop(14, UpperY);
            // Ring order: (d,d) 0, (0,d) 1, (-d,d) 2, (-d,0) 3, (-d,-d) 4, (0,-d) 5, (d,-d) 6, (d,0) 7.
            Action<int, int, double, double, double, double, double, double> stairs = (from, to, x0, y0, z0, x1, y1, z1) =>
            {
                int a = node(x0, y0, z0), b = node(x1, y1, z1);
                link(from, a); link(a, b); link(b, to);
            };
            stairs(mid[7], low[7], 6, FloorY, 0, 9.6, LowerY + 0.0, 0);
            stairs(mid[3], low[3], -6, FloorY, 0, -9.6, LowerY + 0.0, 0);
            stairs(mid[1], up[1], 0, FloorY, 6, 0, UpperY, 12);
            stairs(mid[5], up[5], 0, FloorY, -6, 0, UpperY, -12);
        }

        void Crate(double x, double z, double size, double h, double floor) { Add(Kind.Crate, x, floor, z, x + size, floor + h, z + size); }

        // ------------------------------------------------------------------ drawing order

        /// <summary>A plane between them with you on b's side: a can't be in front of b.</summary>
        static bool Behind(Box a, Box b)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (b.Hi(axis) <= a.Lo(axis) && 0 <= a.Lo(axis)) return true;
                if (b.Lo(axis) >= a.Hi(axis) && 0 >= a.Hi(axis)) return true;
            }
            return false;
        }

        /// <summary>The same for the bot's box (true: it's behind this box from where you stand).</summary>
        internal static bool MustDrawAfter(Box a, Box b) { return Behind(a, b) && !Behind(b, a); }

        void Sort()
        {
            int n = Boxes.Count;
            var after = new List<int>[n]; var waits = new int[n];
            for (int i = 0; i < n; i++) after[i] = new List<int>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i != j && MustDrawAfter(Boxes[i], Boxes[j])) { after[i].Add(j); waits[j]++; }
            var far = Enumerable.Range(0, n).Select(i => Nearest(Boxes[i])).ToArray();
            var done = new bool[n]; var order = new List<int>(n);
            while (order.Count < n)
            {
                // Of those with nothing left in their way, the furthest (if a ring of three should ever happen, the furthest of all).
                int best = -1; double bd = -1;
                for (int i = 0; i < n; i++) if (!done[i] && waits[i] == 0 && far[i] > bd) { bd = far[i]; best = i; }
                if (best < 0) for (int i = 0; i < n; i++) if (!done[i] && far[i] > bd) { bd = far[i]; best = i; }
                done[best] = true; order.Add(best);
                foreach (int j in after[best]) waits[j]--;
            }
            Order = order.ToArray();
            InFrontOf = after.Select(l => l.ToArray()).ToArray();
        }

        static double Nearest(Box b)
        {
            double x = Math.Max(b.X0, Math.Min(0, b.X1)), y = Math.Max(b.Y0, Math.Min(0, b.Y1)), z = Math.Max(b.Z0, Math.Min(0, b.Z1));
            return Math.Sqrt(x * x + y * y + z * z);
        }

        /// <summary>The first box a line from your eyes meets in this direction (-1: none), and how far.</summary>
        public int RayHit(V dir, double upTo, out double at)
        {
            int best = -1; at = upTo;
            for (int i = 0; i < Boxes.Count; i++)
            {
                double t;
                if (Slab(Boxes[i], dir, out t) && t < at) { at = t; best = i; }
            }
            return best;
        }

        static bool Slab(Box b, V d, out double t)
        {
            double t0 = 0, t1 = double.MaxValue; t = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double o = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z, lo = b.Lo(axis), hi = b.Hi(axis);
                if (Math.Abs(o) < 1e-12) { if (lo > 0 || hi < 0) return false; continue; }
                double a = lo / o, c = hi / o;
                if (a > c) { double s = a; a = c; c = s; }
                t0 = Math.Max(t0, a); t1 = Math.Min(t1, c);
                if (t0 > t1) return false;
            }
            t = t0;
            return true;
        }

        /// <summary>Is a point in plain sight from your eyes?</summary>
        public bool Sees(V p)
        {
            double len = p.Length, at;
            return RayHit(p * (1 / len), len - 0.02, out at) < 0;
        }

        /// <summary>Can't see any of a bot standing here: only then your aim shows what your ears heard (the tuning counts only those footsteps).</summary>
        public bool HiddenAt(V feet)
        {
            return !Sees(new V(feet.X, feet.Y + 0.9, feet.Z)) && !Sees(new V(feet.X, feet.Y + BotHeight - 0.1, feet.Z)) && !Sees(new V(feet.X, feet.Y + 0.1, feet.Z));
        }

        // ------------------------------------------------------------------ the bot

        /// <summary>Walks the arena, a footstep at a time, never turning on the spot unless it decides to go back.</summary>
        public sealed class Bot : Sphere3D.IWalker
        {
            readonly Arena3D a; readonly Random rnd;
            int from, to; double along;
            public V Pos { get; private set; }
            /// <summary>Which way it faces (radians around y, 0 = ahead of you).</summary>
            public double Heading { get; private set; }

            public Bot(Arena3D arena, Random rnd)
            {
                a = arena; this.rnd = rnd;
                // Starts somewhere on your floor, in front of you or to the side.
                from = a.Nodes.FindIndex(n => Math.Abs(n.Y - FloorY) < 0.01 && n.Z > 4);
                to = a.Links[from][rnd.Next(a.Links[from].Count)];
                Pos = a.Nodes[from];
            }

            public V Step()
            {
                double left = StepLength;
                while (left > 0)
                {
                    V p0 = a.Nodes[from], p1 = a.Nodes[to];
                    double len = (p1 - p0).Length;
                    if (along + left < len) { along += left; left = 0; }
                    else
                    {
                        left -= len - along; along = 0;
                        int prev = from; from = to;
                        var next = a.Links[from].Where(n => n != prev).ToList();
                        // Mostly on; now and then it turns back. Stairs get taken more often than chance.
                        if (next.Count == 0 || rnd.NextDouble() < 0.05) to = prev;
                        else
                        {
                            var change = next.Where(n => Math.Abs(a.Nodes[n].Y - a.Nodes[from].Y) > 0.5 || a.Nodes[n].X == 0 && a.Nodes[n].Z == 0).ToList();
                            to = change.Count > 0 && rnd.NextDouble() < 0.5 ? change[rnd.Next(change.Count)] : next[rnd.Next(next.Count)];
                        }
                    }
                }
                V q0 = a.Nodes[from], q1 = a.Nodes[to], d = q1 - q0;
                double l = d.Length;
                Pos = l > 0 ? q0 + d * (along / l) : q0;
                if (l > 0) Heading = Math.Atan2(d.X, d.Z);
                return Pos;
            }
        }
    }
}
