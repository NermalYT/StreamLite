using System;
using System.Collections.Generic;
using System.Drawing;
using StreamLite.Audio;
using V = StreamLite.Audio.Sphere3D.Vec;

namespace StreamLite.UI
{
    /// <summary>
    /// Draws the arena (Arena3D) from your eyes with the bot in it: flat colours, a faint grid on the big surfaces. Your head
    /// doesn't move, so which faces show and in what order is fixed; only the bot's place in that order is worked out each
    /// frame, so it goes behind things and comes out again. Drawn on the graphics card like the rest of Personalize 3D.
    /// </summary>
    internal sealed class ArenaView
    {
        sealed class Face { public V A, B, C, D; public Color Col; public V[][] Grid; }

        readonly Arena3D arena;
        readonly Face[][] faces;
        readonly bool[] botBehind;
        readonly Stack<int> walk = new Stack<int>();
        readonly PointF[][] poly = new PointF[9][];
        readonly double[] cx = new double[10], cy = new double[10], cz = new double[10];
        V f, r, u; float focal; PointF mid; int w, h;
        const double Near = 0.1;

        static readonly Color Back = Color.FromArgb(24, 27, 33);
        static Color Of(Arena3D.Kind k)
        {
            switch (k)
            {
                case Arena3D.Kind.Floor: return Color.FromArgb(84, 90, 102);
                case Arena3D.Kind.Crate: return Color.FromArgb(208, 138, 62);
                case Arena3D.Kind.Step: return Color.FromArgb(104, 126, 160);
                case Arena3D.Kind.Pillar: return Color.FromArgb(70, 76, 90);
                case Arena3D.Kind.Ledge: return Color.FromArgb(120, 128, 142);
                default: return Color.FromArgb(62, 74, 96);
            }
        }

        static Color Shade(Color c, double k) { return Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k)); }

        public ArenaView(Arena3D arena)
        {
            this.arena = arena;
            for (int i = 3; i < poly.Length; i++) poly[i] = new PointF[i];
            botBehind = new bool[arena.Boxes.Count];
            faces = new Face[arena.Boxes.Count][];
            for (int i = 0; i < faces.Length; i++) faces[i] = FacesOf(arena.Boxes[i]);
        }

        /// <summary>The faces of a box you can see from where you stand (at most three, ever).</summary>
        static Face[] FacesOf(Arena3D.Box b)
        {
            var l = new List<Face>();
            var c = Of(b.K);
            bool big = b.K == Arena3D.Kind.Floor || b.K == Arena3D.Kind.Wall;
            if (b.Y1 < 0) l.Add(MakeFace(new V(b.X0, b.Y1, b.Z0), new V(b.X1, b.Y1, b.Z0), new V(b.X1, b.Y1, b.Z1), new V(b.X0, b.Y1, b.Z1), Shade(c, 1.0), big));
            if (b.Y0 > 0) l.Add(MakeFace(new V(b.X0, b.Y0, b.Z0), new V(b.X1, b.Y0, b.Z0), new V(b.X1, b.Y0, b.Z1), new V(b.X0, b.Y0, b.Z1), Shade(c, 0.6), big));
            if (b.X1 < 0) l.Add(MakeFace(new V(b.X1, b.Y0, b.Z0), new V(b.X1, b.Y1, b.Z0), new V(b.X1, b.Y1, b.Z1), new V(b.X1, b.Y0, b.Z1), Shade(c, 0.78), big));
            if (b.X0 > 0) l.Add(MakeFace(new V(b.X0, b.Y0, b.Z0), new V(b.X0, b.Y1, b.Z0), new V(b.X0, b.Y1, b.Z1), new V(b.X0, b.Y0, b.Z1), Shade(c, 0.78), big));
            if (b.Z1 < 0) l.Add(MakeFace(new V(b.X0, b.Y0, b.Z1), new V(b.X1, b.Y0, b.Z1), new V(b.X1, b.Y1, b.Z1), new V(b.X0, b.Y1, b.Z1), Shade(c, 0.9), big));
            if (b.Z0 > 0) l.Add(MakeFace(new V(b.X0, b.Y0, b.Z0), new V(b.X1, b.Y0, b.Z0), new V(b.X1, b.Y1, b.Z0), new V(b.X0, b.Y1, b.Z0), Shade(c, 0.9), big));
            return l.ToArray();
        }

        static Face MakeFace(V a, V b, V c, V d, Color col, bool grid)
        {
            var fc = new Face { A = a, B = b, C = c, D = d, Col = col };
            if (grid)
            {
                // Lines every 2 m: they're what shows how far away a plain surface is.
                var lines = new List<V[]>();
                V e1 = b - a, e2 = d - a;
                int n1 = (int)(e1.Length / 2), n2 = (int)(e2.Length / 2);
                for (int i = 1; i < n1; i++) { V s = a + e1 * (i / (double)n1); lines.Add(new[] { s, s + e2 }); }
                for (int i = 1; i < n2; i++) { V s = a + e2 * (i / (double)n2); lines.Add(new[] { s, s + e1 }); }
                fc.Grid = lines.ToArray();
            }
            return fc;
        }

        void Polygon(ICanvas g, Color c, V a, V b, V cc, V d)
        {
            double[] xs = cx, ys = cy, zs = cz;
            xs[0] = a.Dot(r); ys[0] = a.Dot(u); zs[0] = a.Dot(f);
            xs[1] = b.Dot(r); ys[1] = b.Dot(u); zs[1] = b.Dot(f);
            xs[2] = cc.Dot(r); ys[2] = cc.Dot(u); zs[2] = cc.Dot(f);
            xs[3] = d.Dot(r); ys[3] = d.Dot(u); zs[3] = d.Dot(f);
            int n = 0; var buf = scratch;
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                bool ai = zs[i] >= Near, bi = zs[j] >= Near;
                if (ai) buf[n++] = new PointF((float)(mid.X + xs[i] / zs[i] * focal), (float)(mid.Y - ys[i] / zs[i] * focal));
                if (ai != bi)
                {
                    double t = (Near - zs[i]) / (zs[j] - zs[i]);
                    buf[n++] = new PointF((float)(mid.X + (xs[i] + (xs[j] - xs[i]) * t) / Near * focal), (float)(mid.Y - (ys[i] + (ys[j] - ys[i]) * t) / Near * focal));
                }
            }
            if (n < 3) return;
            // Off the screen altogether: not drawn.
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++) { var p = buf[i]; if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X; if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y; }
            if (maxX < 0 || minX > w || maxY < 0 || minY > h) return;
            var pts = poly[n];
            Array.Copy(buf, pts, n);
            g.Polygon(c, pts);
        }
        readonly PointF[] scratch = new PointF[9];

        void Line(ICanvas g, Color c, V a, V b)
        {
            double xa = a.Dot(r), ya = a.Dot(u), za = a.Dot(f), xb = b.Dot(r), yb = b.Dot(u), zb = b.Dot(f);
            if (za < Near && zb < Near) return;
            if (za < Near) { double t = (Near - za) / (zb - za); xa += (xb - xa) * t; ya += (yb - ya) * t; za = Near; }
            if (zb < Near) { double t = (Near - zb) / (za - zb); xb += (xa - xb) * t; yb += (ya - yb) * t; zb = Near; }
            var p = new PointF((float)(mid.X + xa / za * focal), (float)(mid.Y - ya / za * focal));
            var q = new PointF((float)(mid.X + xb / zb * focal), (float)(mid.Y - yb / zb * focal));
            if ((p.X < 0 && q.X < 0) || (p.X > w && q.X > w) || (p.Y < 0 && q.Y < 0) || (p.Y > h && q.Y > h)) return;
            g.Line(c, 1f, p, q);
        }

        void DrawBox(ICanvas g, int i)
        {
            foreach (var fc in faces[i])
            {
                Polygon(g, fc.Col, fc.A, fc.B, fc.C, fc.D);
                if (fc.Grid != null)
                {
                    var c = Shade(fc.Col, 0.84);
                    foreach (var l in fc.Grid) Line(g, c, l[0], l[1]);
                }
            }
        }

        /// <summary>Everything, with the bot standing at feet, facing heading (radians), in its stride (phase, radians). The camera is your eyes turned by f, r, u.</summary>
        public void Draw(ICanvas g, Size size, V forward, V right, V up, float focalLength, PointF centre, V feet, double heading, double phase, Color botColor)
        {
            f = forward; r = right; u = up; focal = focalLength; mid = centre; w = size.Width; h = size.Height;
            g.Clear(Back);
            var bot = new Arena3D.Box { X0 = feet.X - 0.35, X1 = feet.X + 0.35, Y0 = feet.Y, Y1 = feet.Y + Arena3D.BotHeight, Z0 = feet.Z - 0.35, Z1 = feet.Z + 0.35 };
            // What must be drawn after the bot: whatever's in front of it, and whatever's in front of those.
            Array.Clear(botBehind, 0, botBehind.Length);
            for (int i = 0; i < botBehind.Length; i++)
                if (!botBehind[i] && Arena3D.MustDrawAfter(bot, arena.Boxes[i])) { botBehind[i] = true; walk.Push(i); }
            while (walk.Count > 0)
            {
                int i = walk.Pop();
                foreach (int j in arena.InFrontOf[i]) if (!botBehind[j]) { botBehind[j] = true; walk.Push(j); }
            }
            foreach (int i in arena.Order) if (!botBehind[i]) DrawBox(g, i);
            DrawBot(g, feet, heading, phase, botColor);
            foreach (int i in arena.Order) if (botBehind[i]) DrawBox(g, i);
        }

        void DrawBot(ICanvas g, V feet, double heading, double phase, Color c)
        {
            V fwd = new V(Math.Sin(heading), 0, Math.Cos(heading)), side = new V(Math.Cos(heading), 0, -Math.Sin(heading));
            double swing = 0.22 * Math.Sin(phase);
            // Back to front: whichever part is further from you first.
            bool leftFar = (feet + side * -0.14).Length > (feet + side * 0.14).Length;
            var dark = Shade(c, 0.72);
            Part(g, feet + side * (leftFar ? -0.14 : 0.14) + fwd * (leftFar ? -swing : swing), side, fwd, 0.17, 0.85, 0.2, dark);
            Part(g, feet + side * (leftFar ? 0.14 : -0.14) + fwd * (leftFar ? swing : -swing), side, fwd, 0.17, 0.85, 0.2, dark);
            Part(g, feet + new V(0, 0.85, 0), side, fwd, 0.52, 0.65, 0.28, c);
            Part(g, feet + new V(0, 1.52, 0), side, fwd, 0.26, 0.28, 0.26, Color.FromArgb(c.A, Math.Min(255, c.R + 25), Math.Min(255, c.G + 70), Math.Min(255, c.B + 60)));
        }

        /// <summary>One box of the bot, turned its way: base centre, across, along, sizes.</summary>
        void Part(ICanvas g, V b, V across, V along, double wide, double tall, double deep, Color c)
        {
            V up0 = new V(0, tall, 0), ax = across * (wide / 2), al = along * (deep / 2);
            V p000 = b - ax - al, p100 = b + ax - al, p110 = b + ax + al, p010 = b - ax + al;
            V q000 = p000 + up0, q100 = p100 + up0, q110 = p110 + up0, q010 = p010 + up0;
            // Your eyes are at the origin: a face shows when its outside looks your way.
            Action<V, V, V, V, V, double> face = (a, bb, cc, d, normal, k) => { if (normal.Dot(a) < 0) Polygon(g, Shade(c, k), a, bb, cc, d); };
            face(p000, p100, q100, q000, along * -1, 0.82);
            face(p010, p110, q110, q010, along, 0.82);
            face(p000, p010, q010, q000, across * -1, 0.68);
            face(p100, p110, q110, q100, across, 0.68);
            face(q000, q100, q110, q010, new V(0, 1, 0), 1.0);
        }
    }
}
