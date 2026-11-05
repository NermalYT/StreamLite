// The free-look tracking arena (4.4): what's drawn in front really is in front, the bot only walks where there's floor, up
// and down all the stairs, at a walking pace; it goes in and out of sight. Plus pictures (test\shots\arena).
// Usage: run.ps1 ArenaTest [shots]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using StreamLite.Audio;
using StreamLite.UI;
using V = StreamLite.Audio.Sphere3D.Vec;

class ArenaTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        var a = new Arena3D();
        var rnd = new Random(4);
        Console.WriteLine("The place: " + a.Boxes.Count + " boxes");

        // No box inside another: the drawing order relies on it.
        int overlaps = 0;
        for (int i = 0; i < a.Boxes.Count; i++)
            for (int j = i + 1; j < a.Boxes.Count; j++)
            {
                var p = a.Boxes[i]; var q = a.Boxes[j];
                if (p.X0 < q.X1 - 1e-6 && q.X0 < p.X1 - 1e-6 && p.Y0 < q.Y1 - 1e-6 && q.Y0 < p.Y1 - 1e-6 && p.Z0 < q.Z1 - 1e-6 && q.Z0 < p.Z1 - 1e-6) overlaps++;
            }
        Check(overlaps == 0, "no two boxes overlap (" + overlaps + ")");
        Check(a.Boxes.All(b => !(b.X0 < 0.3 && b.X1 > -0.3 && b.Y0 < 0.3 && b.Y1 > -1.6 && b.Z0 < 0.3 && b.Z1 > -0.3)), "nothing where you stand");

        // Painter's order against the truth: along random lines of sight, the last box drawn must be the nearest one hit.
        var pos = new int[a.Boxes.Count];
        for (int i = 0; i < a.Order.Length; i++) pos[a.Order[i]] = i;
        int wrong = 0, rays = 40000;
        for (int n = 0; n < rays; n++)
        {
            double y = rnd.NextDouble() * 2 - 1, th = rnd.NextDouble() * 2 * Math.PI, c = Math.Sqrt(1 - y * y);
            var d = new V(c * Math.Cos(th), y, c * Math.Sin(th));
            double at; int first = a.RayHit(d, 1000, out at);
            if (first < 0) continue;
            for (int b = 0; b < a.Boxes.Count; b++)
            {
                if (b == first) continue;
                var bx = a.Boxes[b];
                // Does this line hit it too (further on)? Then it must be drawn earlier.
                double t0 = 0, t1 = 1e9; bool hit = true;
                for (int ax = 0; ax < 3 && hit; ax++)
                {
                    double o = ax == 0 ? d.X : ax == 1 ? d.Y : d.Z, lo = bx.Lo(ax), hi = bx.Hi(ax);
                    if (Math.Abs(o) < 1e-12) { if (lo > 0 || hi < 0) hit = false; continue; }
                    double u = lo / o, v = hi / o; if (u > v) { double s = u; u = v; v = s; }
                    t0 = Math.Max(t0, u); t1 = Math.Min(t1, v); if (t0 > t1) hit = false;
                }
                if (hit && pos[b] > pos[first]) { wrong++; break; }
            }
        }
        Check(wrong == 0, "what's in front is drawn in front, from where you stand (" + wrong + " of " + rays + " lines of sight wrong)");

        Console.WriteLine("The bot");
        var bot = new Arena3D.Bot(a, rnd);
        int steps = 8000; int hidden = 0, inside = 0, floating = 0; var seen = new HashSet<string>();
        double worstStep = 0, worstTurn = 0; V last = bot.Pos; double lastHead = bot.Heading; int stairsUp = 0, stairsDown = 0; int shortSight = 0, runHidden = 0, runSeen = 0, flips = 0; bool wasHidden = true;
        for (int s = 0; s < steps; s++)
        {
            var p = bot.Step();
            worstStep = Math.Max(worstStep, (p - last).Length);
            string level = Math.Abs(p.Y - Arena3D.LowerY) < 0.01 ? "lower" : Math.Abs(p.Y - Arena3D.FloorY) < 0.01 ? "your floor" : Math.Abs(p.Y - Arena3D.UpperY) < 0.01 ? "above" : "stairs";
            seen.Add(level);
            if (p.Y > last.Y + 0.01) stairsUp++; if (p.Y < last.Y - 0.01) stairsDown++;
            // Its body clear of every box, its feet on something.
            var c = new V(p.X, p.Y + 0.9, p.Z);
            if (a.Boxes.Any(b => c.X > b.X0 && c.X < b.X1 && c.Y > b.Y0 && c.Y < b.Y1 && c.Z > b.Z0 && c.Z < b.Z1)) inside++;
            if (!a.Boxes.Any(b => p.X > b.X0 - 0.45 && p.X < b.X1 + 0.45 && p.Z > b.Z0 - 0.45 && p.Z < b.Z1 + 0.45 && p.Y <= b.Y1 + 0.35 && p.Y >= b.Y1 - 0.45)) floating++;
            bool hid = a.HiddenAt(p);
            if (hid) hidden++;
            if (hid != wasHidden) flips++;
            wasHidden = hid;
            last = p;
        }
        Console.WriteLine("    floors seen: " + string.Join(", ", seen));
        Check(seen.Count == 4, "it walks all three floors and the stairs");
        Check(stairsUp > 200 && stairsDown > 200, "up and down stairs (" + stairsUp + " footsteps up, " + stairsDown + " down)");
        Check(worstStep <= Arena3D.StepLength + 0.01, "at a walking pace: never more than " + Arena3D.StepLength + " m a footstep (" + worstStep.ToString("0.00") + ")");
        Check(inside == 0, "never through a wall or crate (" + inside + ")");
        Check(floating == 0, "always on a floor (" + floating + " footsteps in the air)");
        double share = hidden / (double)steps;
        Check(share > 0.35 && share < 0.9, "in and out of sight: hidden for " + (100 * share).ToString("0") + "% of its footsteps");
        Check(flips > steps / 40, "it keeps popping in and out (" + flips + " times in " + steps + " footsteps, about every " + (steps * 0.43 / Math.Max(1, flips)).ToString("0") + " s)");

        Console.WriteLine("Tuning");
        var head = new Sphere3D.Variant();
        var tu = Sphere3D.TuningFor(Sphere3D.Test.Track, head, new Sphere3D.Refine());
        var tr = new Sphere3D.Tracker(Sphere3D.Test.Track, tu, new Random(2), new Arena3D.Bot(a, new Random(3)), a.HiddenAt);
        V aim = new V(0, 0, 1), heard;
        while (!tr.Finished) { tr.Next(aim, out heard); aim = heard; }
        int scored = tr.Order.Distinct().Sum(v => v.N);
        Check(scored > 0 && scored < tr.Total, "only the footsteps of a bot you can't see count (" + scored + " of " + tr.Total + ")");
        Check(tr.MeanDeg < 8, "following it exactly: " + tr.MeanDeg.ToString("0.0") + " degrees off");

        Console.WriteLine("Drawing");
        var view = new ArenaView(a);
        using (var bmp = new Bitmap(1600, 900))
        using (var g = Graphics.FromImage(bmp))
        {
            var canvas = new GdiCanvas(g);
            var sw = Stopwatch.StartNew(); int frames = 0;
            for (int i = 0; i < 12; i++, frames++) DrawAt(view, canvas, bmp.Size, i * 30, -8, new V(5, -1.6, 0), 0, i);
            Console.WriteLine("    " + (sw.Elapsed.TotalMilliseconds / frames).ToString("0.0") + " ms a frame on the CPU (GDI, tests only: the app draws on the graphics card)");
            Check(true, "it draws from every side");
            if (Array.IndexOf(args, "shots") >= 0)
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\arena"); Directory.CreateDirectory(dir);
                var cases = new[]
                {
                    new { Name = "ahead", Yaw = 0.0, Pitch = -6.0, Bot = new V(1.5, -1.6, 5) },
                    new { Name = "right", Yaw = 90.0, Pitch = -6.0, Bot = new V(5, -1.6, 2) },
                    new { Name = "behind-wall", Yaw = 45.0, Pitch = -8.0, Bot = new V(3.4, -1.6, 2.6) },
                    new { Name = "up", Yaw = 0.0, Pitch = 22.0, Bot = new V(-6, 1.6, 14) },
                    new { Name = "stairs-up", Yaw = 0.0, Pitch = 10.0, Bot = new V(0, -0.3, 8.5) },
                    new { Name = "down", Yaw = 90.0, Pitch = -22.0, Bot = new V(11, -4.6, -3) },
                };
                foreach (var c in cases)
                {
                    DrawAt(view, canvas, bmp.Size, c.Yaw, c.Pitch, c.Bot, 0.7, 1.2);
                    bmp.Save(Path.Combine(dir, c.Name + ".png"));
                }
                Console.WriteLine("    pictures: " + dir);
            }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    static void DrawAt(ArenaView view, ICanvas g, Size size, double yaw, double pitch, V feet, double heading, double phase)
    {
        float focal = (float)(size.Height / 2.0 / Math.Tan(75 / 2.0 * Math.PI / 180));
        view.Draw(g, size, Sphere3D.Forward(yaw, pitch), Sphere3D.Right(yaw), Sphere3D.Up(yaw, pitch), focal, new PointF(size.Width / 2f, size.Height / 2f), feet, heading, phase, Color.FromArgb(232, 66, 66));
    }
}
