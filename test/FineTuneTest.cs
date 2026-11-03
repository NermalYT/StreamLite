// Personalize 3D's tests you pick (4.3), with no sound played: which settings each tries and how closely, the walker's
// paths, how tracking scores and decides (simulated listeners), and the footsteps' real audio as the view turns.
// Usage: run.ps1 FineTuneTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite.Audio;

class FineTuneTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static double Rms(float[] x) { double e = 0; foreach (var v in x) e += v * v; return Math.Sqrt(e / Math.Max(1, x.Length)); }
    static double Db(double a) { return 20 * Math.Log10(a + 1e-9); }

    static void Main()
    {
        var head = new Sphere3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75, HeadSize = 100 };
        var rnd = new Random(11);

        // ---- which settings each test tries
        Func<List<Sphere3D.Variant>, Func<Sphere3D.Variant, int>, string> vals = (l, g) => l == null ? "-" : string.Join(",", l.Select(g));
        Func<Sphere3D.Tuning, string> all = t => vals(t.Size, v => v.HeadSize) + " " + vals(t.Cue, v => v.Cue) + " " + vals(t.Contrast, v => v.Contrast);
        var r0 = new Sphere3D.Refine();
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.Height, head, r0)) == "- 0,35,70 50,75,100", "above / below: floor contrast and cue sharpening");
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.Sides, head, r0)) == "88,100,112 - -", "left / right: head size");
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.FrontBack, head, r0)) == "- 0,35,70 -", "forward / backward: cue sharpening");
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.All, head, r0)) == "88,100,112 0,35,70 50,75,100", "all directions: all three");
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.Track, head, r0)) == "88,100,112 0,35,70 50,75,100", "tracking everywhere: all three");
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0)) == "88,100,112 0,35,70 -", "tracking at ear level: head size and cue (no floor to hear)");
        var r1 = new Sphere3D.Refine();
        Sphere3D.Tuned(r1, Sphere3D.TuningFor(Sphere3D.Test.Sides, head, r1));
        Check(r1.Size == 1 && r1.Cue == 0 && r1.Contrast == 0, "only what a test tried counts as tuned");
        var moved = head.With(headSize: 112);
        Check(all(Sphere3D.TuningFor(Sphere3D.Test.All, moved, r1)) == "106,112,118 0,35,70 50,75,100", "the next test tries closer around where the last one landed");
        Check(vals(Sphere3D.TuningFor(Sphere3D.Test.Sides, head.With(headSize: 120), new Sphere3D.Refine()).Size, v => v.HeadSize) == "108,120", "a setting stays in its range");
        var kept = Sphere3D.DecideTune(head, new Sphere3D.Tuning());
        Check(kept.HeadSize == 100 && kept.Cue == 35 && kept.Contrast == 75, "settings a test didn't try stay");

        // ---- the clicked tests' places: no free hints in a two-way test
        foreach (var kv in new Dictionary<Sphere3D.Test, Sphere3D.Dir[]> { { Sphere3D.Test.Height, new[] { Sphere3D.Dir.Up, Sphere3D.Dir.Down } }, { Sphere3D.Test.Sides, new[] { Sphere3D.Dir.Left, Sphere3D.Dir.Right } }, { Sphere3D.Test.FrontBack, new[] { Sphere3D.Dir.Front, Sphere3D.Dir.Behind } } })
        {
            int repeats = 0, total = 0, lefts = 0;
            for (int run = 0; run < 200; run++)
            {
                var tr = Sphere3D.TestRound(kv.Key, Sphere3D.TuningFor(kv.Key, head, r0), rnd);
                if (run == 0) Check(tr.Count == 18 && tr.All(t => kv.Value.Contains(t.S.D)), Sphere3D.TestName(kv.Key) + ": 18 footsteps, only those places");
                for (int i = 1; i < tr.Count; i++) { total++; if (tr[i].S.D == tr[i - 1].S.D) repeats++; }
                lefts += tr.Count(t => t.S.D == kv.Value[0]);
            }
            double share = repeats / (double)total, even = lefts / (200.0 * 18);
            Check(share > 0.35 && share < 0.65, Sphere3D.TestName(kv.Key) + ": the next place can't be guessed from the last (the same one " + (100 * share).ToString("0") + "% of the time)");
            Check(even > 0.4 && even < 0.6, Sphere3D.TestName(kv.Key) + ": both places come up about as often (" + (100 * even).ToString("0") + "%)");
        }

        // ---- the walker
        var w = new Sphere3D.Walker(new Random(3), false);
        double maxEl = 0, up = 0, down = 0, left = 0, right = 0, diag = 0, biggest = 0;
        var prev = w.Pos; double prevAz = w.Az, prevEl = w.El;
        for (int i = 0; i < 20000; i++)
        {
            var p = w.Step();
            biggest = Math.Max(biggest, Sphere3D.AngleBetween(prev, p)); prev = p;
            double dAz = ((w.Az - prevAz + 540) % 360) - 180, dEl = w.El - prevEl; prevAz = w.Az; prevEl = w.El;
            maxEl = Math.Max(maxEl, Math.Abs(w.El));
            if (Math.Abs(dEl) > 2 * Math.Abs(dAz)) { if (dEl > 0) up++; else down++; }
            else if (Math.Abs(dAz) > 2 * Math.Abs(dEl)) { if (dAz > 0) right++; else left++; }
            else diag++;
        }
        Check(up > 1000 && down > 1000 && left > 1000 && right > 1000 && diag > 1000, "everywhere: it walks up (" + up + "), down (" + down + "), left (" + left + "), right (" + right + ") and diagonally (" + diag + ")");
        Check(maxEl <= Sphere3D.Walker.MaxEl + 0.01, "it stays where the view can follow (up to " + maxEl.ToString("0") + " degrees high)");
        Check(biggest <= Sphere3D.Walker.MaxStep + 1.5, "no jumps: at most " + biggest.ToString("0.0") + " degrees a footstep");
        var ring = new Sphere3D.Walker(new Random(4), true);
        double elMax = 0, cw = 0, ccw = 0, travelled = 0; prevAz = ring.Az;
        for (int i = 0; i < 5000; i++)
        {
            var p = ring.Step(); elMax = Math.Max(elMax, Math.Abs(p.Y));
            double d = ((ring.Az - prevAz + 540) % 360) - 180; prevAz = ring.Az; if (d > 0) cw++; else ccw++; travelled += Math.Abs(d);
        }
        Check(elMax < 1e-9, "ear level: every footstep on the ring");
        Check(cw > 1000 && ccw > 1000, "ear level: both ways round (" + cw + " / " + ccw + ")");
        Check(travelled / 5000 >= Sphere3D.Walker.MinStep && travelled / 5000 <= Sphere3D.Walker.MaxStep, "a walking pace: " + (travelled / 5000 / 0.43).ToString("0") + " degrees a second on average");

        // ---- a tracking test's walks
        var tuAll = Sphere3D.TuningFor(Sphere3D.Test.Track, head, r0);
        var t0 = new Sphere3D.Tracker(Sphere3D.Test.Track, tuAll, new Random(1));
        Check(t0.Order.Distinct().Count() == 7 && t0.Order.Count == 14 && tuAll.Size.Concat(tuAll.Cue).Concat(tuAll.Contrast).Distinct().Count() == 7, "your current settings are in all three lists but tried as one: 7 settings, twice each");
        Check(Enumerable.Range(1, t0.Order.Count - 1).All(i => t0.Order[i] != t0.Order[i - 1]), "the same setting never twice in a row");
        Check(t0.Total * 0.43 / 60 < 3, "about " + (t0.Total * 0.43 / 60).ToString("0.0") + " minutes");
        var tl = new Sphere3D.Tracker(Sphere3D.Test.TrackLevel, Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0), new Random(1));
        Check(tl.Order.Count == 10 && tl.Total * 0.43 / 60 < 2, "at ear level: 5 settings, about " + (tl.Total * 0.43 / 60).ToString("0.0") + " minutes");

        // Following exactly, one footstep behind: nothing off. Not following: about 90 degrees.
        var exact = new Sphere3D.Tracker(Sphere3D.Test.TrackLevel, Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0), new Random(2));
        Sphere3D.Vec heard = new Sphere3D.Vec(0, 0, 1), pos;
        while (!exact.Finished) { exact.Next(heard, out pos); heard = pos; }
        Check(exact.MeanDeg < 0.01, "aiming where the last footstep was: " + exact.MeanDeg.ToString("0.00") + " degrees off");
        int scored = exact.Order.Distinct().Sum(v => v.N), most = 2 * (Sphere3D.Tracker.WalkSteps - Sphere3D.Tracker.Settle);
        Check(exact.Order.Distinct().All(v => v.N > 0 && v.N <= most) && scored < exact.Total, "every setting was scored, the moments after a change aren't (" + scored + " of " + exact.Total + " footsteps)");
        var fixedAim = new Sphere3D.Tracker(Sphere3D.Test.TrackLevel, Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0), new Random(2));
        while (!fixedAim.Finished) fixedAim.Next(new Sphere3D.Vec(0, 0, 1), out pos);
        Check(fixedAim.MeanDeg > 45 && fixedAim.MeanDeg <= 90, "staring ahead while they circle: " + fixedAim.MeanDeg.ToString("0") + " degrees off (misses count up to 90)");
        var showed = new Sphere3D.Tracker(Sphere3D.Test.TrackLevel, Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0), new Random(2));
        heard = new Sphere3D.Vec(0, 0, 1);
        for (int i = 0; i < 40; i++) { showed.Next(heard, out pos); heard = pos; }
        double beforeShow = showed.MeanDeg;
        showed.ShowMe();
        Check(showed.Showing, "Show me is on");
        for (int i = 0; i < Sphere3D.Tracker.ShowSteps; i++) { showed.Next(heard, out pos); heard = pos; }
        Check(!showed.Showing && showed.MeanDeg > beforeShow + 3, "... for " + Sphere3D.Tracker.ShowSteps + " footsteps, which count as misses (" + beforeShow.ToString("0.0") + " to " + showed.MeanDeg.ToString("0.0") + " degrees)");

        // ---- simulated listeners: a real difference is found, no difference keeps what you have
        Func<double, double> gauss = sd => sd * Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        Func<Func<Sphere3D.Variant, double>, Sphere3D.Variant> listen = errFor =>
        {
            var tu = Sphere3D.TuningFor(Sphere3D.Test.TrackLevel, head, r0);
            var tr = new Sphere3D.Tracker(Sphere3D.Test.TrackLevel, tu, rnd);
            Sphere3D.Vec last = new Sphere3D.Vec(0, 0, 1), p; Sphere3D.Variant now = null; double drift = 0;
            while (!tr.Finished)
            {
                // The listener's aim is off by a slowly wandering angle whose size depends on the setting heard.
                double target = now != null ? errFor(now) : 20;
                drift = drift * 0.8 + gauss(target * 0.6);
                double a = (target + drift) * Math.PI / 180, cx = Math.Cos(a), sx = Math.Sin(a);
                var aim = new Sphere3D.Vec(last.X * cx + last.Z * sx, 0, -last.X * sx + last.Z * cx);
                now = tr.Next(aim, out p); last = p;
            }
            return Sphere3D.DecideTune(head, tu);
        };
        int bigFound = 0, cueFound = 0, keepAll = 0;
        for (int i = 0; i < 200; i++)
        {
            if (listen(v => v.HeadSize == 112 ? 10 : 35).HeadSize == 112) bigFound++;
            if (listen(v => v.Cue == 70 ? 10 : 35).Cue == 70) cueFound++;
            var same = listen(v => 25);
            if (same.HeadSize == 100 && same.Cue == 35) keepAll++;
        }
        Check(bigFound >= 170, "follows far better with a bigger head: head size 112% found " + bigFound + "/200");
        Check(cueFound >= 170, "follows far better with more sharpening: cue 70% found " + cueFound + "/200");
        Check(keepAll >= 100, "no difference: your settings stay " + keepAll + "/200");

        // ---- the sound itself: world-locked, aiming at it brings it to the middle (nothing played aloud)
        var rend = new StepRenderer();
        Func<double, double, double, double, double> side = (azSrc, elSrc, yaw, pitch) =>
        {
            float[] l, r; double dl = 0, dr = 0;
            for (int s = 0; s < 4; s++) { rend.RenderAt(head, Sphere3D.Forward(azSrc, elSrc) * Sphere3D.Radius, yaw, pitch, s, out l, out r); dl += Rms(l) * Rms(l); dr += Rms(r) * Rms(r); }
            return Db(Math.Sqrt(dr)) - Db(Math.Sqrt(dl));
        };
        double ahead = side(60, 0, 60, 0), onRight = side(60, 0, 0, 0), onLeft = side(60, 0, 120, 0);
        Check(onRight > 3, "footsteps 60 degrees to the right, view ahead: louder on the right (" + onRight.ToString("+0.0;-0.0") + " dB)");
        Check(onLeft < -3, "the view turned past them: louder on the left (" + onLeft.ToString("+0.0;-0.0") + " dB)");
        Check(Math.Abs(ahead) < 2, "the view on them: in the middle (" + ahead.ToString("+0.0;-0.0") + " dB)");
        double aimedUp = side(30, 40, 30, 40);
        Check(Math.Abs(aimedUp) < 2, "footsteps up and to the side, the view on them: in the middle (" + aimedUp.ToString("+0.0;-0.0") + " dB)");
        float[] a1, b1, a2, b2;
        rend.RenderAt(head, Sphere3D.Forward(0, 0) * Sphere3D.Radius, 0, 0, 1, out a1, out b1);
        rend.Render(head, Sphere3D.At(Sphere3D.Dir.Front), 1, out a2, out b2);
        double diff = 0; for (int i = 0; i < Math.Min(a1.Length, a2.Length); i++) diff = Math.Max(diff, Math.Abs(a1[i] - a2[i]));
        Check(a1.Length == a2.Length && diff < 1e-5, "straight ahead sounds exactly as in the clicked tests");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int s = 0; s < 20; s++) { float[] l, r; rend.RenderAt(head, Sphere3D.Forward(s * 17, 20) * Sphere3D.Radius, 0, 0, s, out l, out r); }
        double ms = sw.Elapsed.TotalMilliseconds / 20;
        Check(ms < 40, "a footstep renders in " + ms.ToString("0.0") + " ms (it's rendered 50 ms before it plays)");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
