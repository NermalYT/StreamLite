using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;
using V = StreamLite.Audio.Building3D.Vec;

// Personalize 3D (the first-person building): looks hit the right ceiling/floor room or wall, directions from your head
// are right as you turn, scoring, and the decisions with simulated listeners (one that hears better with KEMAR picks
// KEMAR, one that mixes up floors gets more floor contrast, one that follows walks better with the alternative gets it).
// The head responses between measured directions: same as measured where measured, the time between the ears moving
// smoothly. A footstep on the left is louder on the left. Plus pictures of the room (test\shots\world).
// Usage: run.ps1 Personal3DWorldTest [shots]
class Personal3DWorldTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // A listener who hears directions with some scatter (degrees) and mixes up floors now and then.
    static Building3D.Answer Listen(Building3D.Source s, double scatter, double floorMix, Random rnd)
    {
        var target = s.Pos; int floor = s.Floor;
        if (rnd.NextDouble() < floorMix)
        {
            floor = s.Floor == 1 ? (rnd.Next(2) == 0 ? 0 : 2) : 1;
            target = Building3D.RandomSource(floor, rnd).Pos;
        }
        V v = target - Building3D.Eye;
        double yaw = Math.Atan2(v.X, v.Z) * 180 / Math.PI, pitch = Math.Atan2(v.Y, Math.Sqrt(v.X * v.X + v.Z * v.Z)) * 180 / Math.PI;
        // Your floor: the wall in that direction (the band at ear height).
        if (floor == 1) pitch = 0;
        yaw += Gauss(rnd) * scatter; pitch = Math.Max(-89, Math.Min(89, pitch + Gauss(rnd) * scatter * (floor == 1 ? 0.15 : 0.6)));
        return Building3D.Aim(yaw, pitch);
    }
    static double Gauss(Random r) { return Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()); }

    static void Main(string[] args)
    {
        Console.WriteLine("Looking around:");
        var up = Building3D.Aim(0, 89.9); var down = Building3D.Aim(0, -89.9); var ahead = Building3D.Aim(0, 0); var right = Building3D.Aim(90, 0); var back = Building3D.Aim(180, 0);
        Check(up.Floor == 2 && up.Room == 6, "straight up: the room above you (upstairs room 7) - got floor " + up.Floor + " room " + (up.Room + 1));
        Check(down.Floor == 0 && down.Room == 6, "straight down: the room below you - got floor " + down.Floor + " room " + (down.Room + 1));
        Check(ahead.Floor == 1 && Math.Abs(ahead.Point.Z - Building3D.HalfZ) < 1e-6, "ahead: the front wall (your floor)");
        Check(right.Floor == 1 && Math.Abs(right.Point.X - Building3D.HalfX) < 1e-6, "turned right: the right wall");
        Check(back.Floor == 1 && Math.Abs(back.Point.Z + Building3D.HalfZ) < 1e-6, "turned around: the back wall");
        var upFront = Building3D.Aim(0, 30); var downLeft = Building3D.Aim(-90, -40);
        Check(upFront.Floor == 2 && upFront.Room >= 0 && upFront.Room < 4, "up and ahead: a front room upstairs (room " + (upFront.Room + 1) + ")");
        Check(downLeft.Floor == 0 && downLeft.Room % 4 <= 1, "down and to the left: a left room downstairs (room " + (downLeft.Room + 1) + ")");
        int rmCheck = 0; for (int r = 0; r < 12; r++) { double x, z; Building3D.RoomCenter(r, out x, out z); if (Building3D.RoomAt(x, z) == r) rmCheck++; }
        Check(rmCheck == 12, "room centres and rooms agree (12 of 12)");

        Console.WriteLine("Where a sound is from your head:");
        double az, el, d;
        Building3D.Direction(new V(0, Building3D.Ear, 3), 0, 0, out az, out el, out d);
        Check(Math.Abs(az) < 0.01 && Math.Abs(el) < 0.01, "straight ahead: azimuth 0, elevation 0");
        Building3D.Direction(new V(-3, Building3D.Ear, 0), 0, 0, out az, out el, out d);
        Check(Math.Abs(az - 90) < 0.01, "to your left: azimuth 90 (the head responses' left)");
        Building3D.Direction(new V(3, Building3D.Ear, 0), 90, 0, out az, out el, out d);
        Check(Math.Abs(az) < 0.01 || Math.Abs(az - 360) < 0.01, "turned right towards a sound on the right: now straight ahead");
        Building3D.Direction(new V(0, Building3D.StepY[2], 1), 0, 0, out az, out el, out d);
        Check(el > 50, "a step upstairs, just ahead: well above (" + el.ToString("0") + "°)");
        Building3D.Direction(new V(0, Building3D.StepY[2], 1), 0, 60, out az, out el, out d);
        Check(Math.Abs(el - (Math.Atan2(1.7, 1) * 180 / Math.PI - 60)) < 0.5, "looking up 60°: that step is almost in front of your face (" + el.ToString("0") + "°)");
        Building3D.Direction(new V(0, Building3D.StepY[0], 2), 0, 0, out az, out el, out d);
        Check(el < -60, "a step downstairs: well below (" + el.ToString("0") + "°)");

        Console.WriteLine("Scoring:");
        var rnd = new Random(1);
        int perfect = 0, n = 0;
        for (int f = 0; f < 3; f++) for (int k = 0; k < 20; k++) { var s = Building3D.RandomSource(f, rnd); var sc = Building3D.Judge(Listen(s, 0, 0, rnd), s); n++; if (sc.FloorRight && (f == 1 ? sc.OffDeg < 1 : sc.RoomRight)) perfect++; }
        Check(perfect == n, "looking exactly at the steps scores right every time (" + perfect + " of " + n + ")");
        var s2 = Building3D.RandomSource(2, rnd); var a2 = Building3D.Aim(0, -60);
        Check(Building3D.Judge(a2, s2).Error >= 1.5, "upstairs answered downstairs: the biggest error");
        var s1 = new Building3D.Source { Floor = 1, Pos = new V(0, 0, 3), Room = -1 };
        var fb = Building3D.Judge(Building3D.Aim(180, 0), s1);
        Check(fb.FrontBack, "a sound ahead answered behind: front/back swap");

        Console.WriteLine("Decisions (simulated listeners, 200 runs each):");
        int kemar = 0, cue70 = 0, contrastUp = 0, keepTie = 0;
        for (int run = 0; run < 200; run++)
        {
            List<Building3D.Variant> vs; var ts = Building3D.Round1(75, rnd, out vs);
            foreach (var t in ts) Building3D.Record(t, Listen(t.S, t.V.Hrtf == HrtfSet.Kemar ? 12 : 30, 0.05, rnd));
            if (Building3D.Decide1(ts, HrtfSet.Ku100, 35, 75).Hrtf == HrtfSet.Kemar) kemar++;
            ts = Building3D.Round1(75, rnd, out vs);
            foreach (var t in ts) Building3D.Record(t, Listen(t.S, t.V.Cue == 70 ? 12 : 30, 0.05, rnd));
            if (Building3D.Decide1(ts, HrtfSet.Ku100, 35, 75).Cue == 70) cue70++;
            ts = Building3D.Round1(75, rnd, out vs);
            foreach (var t in ts) Building3D.Record(t, Listen(t.S, 15, 0.4, rnd));
            if (Building3D.Decide1(ts, HrtfSet.Ku100, 35, 75).Contrast > 75) contrastUp++;
            ts = Building3D.Round1(75, rnd, out vs);
            foreach (var t in ts) Building3D.Record(t, Listen(t.S, 15, 0.05, rnd));
            if (Building3D.Decide1(ts, HrtfSet.Ku100, 35, 75).Hrtf == HrtfSet.Ku100) keepTie++;
        }
        Check(kemar >= 180, "hears better with KEMAR -> KEMAR picked " + kemar + "/200");
        Check(cue70 >= 180, "hears better with more cue sharpening -> 70% picked " + cue70 + "/200");
        Check(contrastUp >= 190, "mixes up floors 40% of the time -> more floor contrast " + contrastUp + "/200");
        Check(keepTie >= 70, "no difference -> the head you have kept at least as often as chance (" + keepTie + "/200)");
        int altPicked = 0, altEqual = 0;
        for (int run = 0; run < 200; run++)
        {
            var best = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75 };
            var alt = Building3D.Alternative(best, true);
            var ws = Building3D.Round2(best, alt, rnd);
            foreach (var w in ws) { for (int i = 0; i < 100; i++) { w.ErrSum += Math.Abs(Gauss(rnd) * (w.V == alt ? 14 : 22)) ; w.N++; } }
            if (Building3D.Decide2(ws, best, alt) == alt) altPicked++;
            ws = Building3D.Round2(best, alt, rnd);
            foreach (var w in ws) { for (int i = 0; i < 100; i++) { w.ErrSum += Math.Abs(Gauss(rnd) * 18); w.N++; } }
            if (Building3D.Decide2(ws, best, alt) == alt) altEqual++;
        }
        Check(altPicked >= 195, "follows walks clearly better with the alternative -> switched " + altPicked + "/200");
        Check(altEqual <= 20, "no difference -> the round-1 winner stays (" + (200 - altEqual) + "/200)");
        Check(Building3D.Round2(new Building3D.Variant { Hrtf = "ku100", Cue = 35, Contrast = 75 }, new Building3D.Variant { Hrtf = "ku100", Cue = 70, Contrast = 55 }, rnd).GroupBy(w => w.Floor).All(g => g.Count() == 2), "round 2: two walks per floor");

        Console.WriteLine("Head responses between the measured directions:");
        foreach (var kind in new[] { HrtfSet.Ku100, HrtfSet.Kemar })
        {
            var set = HrtfSet.Get(kind, null);
            double worstCorr = 1; int measured = 0;
            for (int i = 0; i < set.Az.Length; i += 7)
            {
                float[] l, r, l0, r0; set.BuildPrecise(set.Az[i], set.El[i], 0, out l, out r); set.Build(set.Az[i], set.El[i], 0, out l0, out r0);
                worstCorr = Math.Min(worstCorr, Math.Min(Corr(l, l0), Corr(r, r0))); measured++;
            }
            Check(worstCorr > 0.97, kind + ": at measured directions the same response (worst correlation " + worstCorr.ToString("0.000") + " over " + measured + ")");
            double maxJumpP = 0, maxJumpN = 0, prevP = double.NaN, prevN = double.NaN, sumJump = 0; bool mono = true;
            for (int a = 0; a <= 90; a++)
            {
                float[] l, r; set.BuildPrecise(a, 0, 0, out l, out r);
                double itd = Itd(l, r);
                if (!double.IsNaN(prevP)) { maxJumpP = Math.Max(maxJumpP, Math.Abs(itd - prevP)); sumJump += Math.Abs(itd - prevP); if (itd < prevP - 2.5) mono = false; }
                prevP = itd;
                set.Build(a, 0, 0, out l, out r); double itdN = Itd(l, r);
                if (!double.IsNaN(prevN)) maxJumpN = Math.Max(maxJumpN, Math.Abs(itdN - prevN));
                prevN = itdN;
            }
            Check(maxJumpP < 3.5 && sumJump / 90 < 0.6 && mono, kind + ": the time between the ears grows smoothly from front to left (" + (sumJump / 90).ToString("0.00") + " samples per degree on average, largest step " + maxJumpP.ToString("0.00") + "; nearest-only largest " + maxJumpN.ToString("0.00") + ")");
        }

        Console.WriteLine("Footsteps:");
        var rend = new StepRenderer();
        var v0 = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75 };
        float[] L, R;
        rend.Render(v0, new V(-3, 0, 0.5), 1, 0, 0, 0, 0, out L, out R);
        double el2 = Energy(HighPass(L)), er2 = Energy(HighPass(R));
        Check(10 * Math.Log10(el2 / er2) > 6, "a step on your left is louder in the left ear above 1.5 kHz (" + (10 * Math.Log10(el2 / er2)).ToString("0.0") + " dB; the thud below that hardly differs between ears)");
        Check(L.All(x => !float.IsNaN(x) && Math.Abs(x) < 1) && R.All(x => !float.IsNaN(x) && Math.Abs(x) < 1), "no overload, no NaN");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) rend.Render(v0, new V(2, 3.3, 1), 2, 30, 10, 0.3, i, out L, out R);
        Check(sw.Elapsed.TotalMilliseconds / 20 < 25, "one footstep renders in " + (sw.Elapsed.TotalMilliseconds / 20).ToString("0.0") + " ms (steps are 500 ms apart)");

        if (Array.IndexOf(args, "shots") >= 0) Shots();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    static float[] HighPass(float[] x) { var b = Biquad.HighPass(1500, 48000); var b2 = Biquad.HighPass(1500, 48000); var y = new float[x.Length]; for (int i = 0; i < x.Length; i++) y[i] = b2.Process(b.Process(x[i])); return y; }
    static double Energy(float[] x) { double s = 0; foreach (var v in x) s += v * (double)v; return s + 1e-20; }
    static double Corr(float[] a, float[] b) { double ab = 0, aa = 0, bb = 0; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; } return ab / Math.Sqrt(aa * bb + 1e-30); }

    // Interaural delay (samples, + = right ear later): cross-correlation peak, refined between samples.
    static double Itd(float[] l, float[] r)
    {
        int best = 0; double bc = double.MinValue; var c = new double[81];
        for (int lag = -40; lag <= 40; lag++)
        {
            double s = 0; for (int i = 0; i < l.Length; i++) { int j = i + lag; if (j >= 0 && j < r.Length) s += l[i] * r[j]; }
            c[lag + 40] = s; if (s > bc) { bc = s; best = lag; }
        }
        int k = best + 40; if (k <= 0 || k >= 80) return best;
        double y0 = c[k - 1], y1 = c[k], y2 = c[k + 1], den = y0 - 2 * y1 + y2;
        return best + (Math.Abs(den) > 1e-20 ? 0.5 * (y0 - y2) / den : 0);
    }

    static void Shots()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-uitest");
        Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Application.EnableVisualStyles();
        var core = new AppCore();
        foreach (var f in typeof(AppCore).GetFields(Any)) if (f.FieldType == typeof(Timer)) { var t = (Timer)f.GetValue(core); t.Stop(); t.Enabled = false; }
        var form = new Personal3DWorld(core) { NoMouse = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), WindowState = FormWindowState.Normal, Size = new Size(1280, 800) };
        form.Show(); Application.DoEvents();
        // Pictures of set-up states only: the window's own clock must not move them on (or pause when it loses focus).
        ((Timer)typeof(Personal3DWorld).GetField("timer", Any).GetValue(form)).Stop();
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\world"); Directory.CreateDirectory(dir);
        Action<string> save = name =>
        {
            form.Invalidate(); Application.DoEvents();
            using (var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(dir, name + ".png"));
            }
        };
        Action<string, object> set = (name, value) => typeof(Personal3DWorld).GetField(name, Any).SetValue(form, value);
        Func<string, object> get = name => typeof(Personal3DWorld).GetField(name, Any).GetValue(form);
        save("0-intro");
        var stageType = typeof(Personal3DWorld).GetNestedType("Stage", Any);
        set("stage", Enum.Parse(stageType, "Listen")); set("captured", true);
        set("headline", "Round 1 - 3 of 24"); set("detail", "Look where the footsteps are and click.");
        typeof(Personal3DWorld).GetMethod("ShowButtons", Any).Invoke(form, new object[] { null, null });
        foreach (var view in new[] { new[] { 0f, 0f, 1f }, new[] { 30f, 45f, 2f }, new[] { -20f, -50f, 3f }, new[] { 150f, 5f, 4f } })
        {
            set("yaw", view[0]); set("pitch", view[1]);
            set("hover", Building3D.Aim(view[0], view[1]));
            save(view[2] + "-look-" + view[0] + "-" + view[1]);
        }
        // After a click: the true room (green) and the guess (red).
        var src = Building3D.RandomSource(2, new Random(5), 2);
        set("stage", Enum.Parse(stageType, "Feedback")); set("yaw", 0f); set("pitch", 40f);
        set("reveal", (Building3D.Source?)src); set("lastAnswer", (Building3D.Answer?)Building3D.Aim(-25, 35));
        set("detail", Building3D.Judge(Building3D.Aim(-25, 35), src).Text);
        save("5-feedback");
        set("stage", Enum.Parse(stageType, "Countdown")); set("afterCountdown", Enum.Parse(stageType, "Listen")); set("stageEnd", DateTime.Now.AddSeconds(3.5)); set("reveal", null); set("lastAnswer", null);
        set("yaw", 0f); set("pitch", 0f);
        save("6-countdown");
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        form.Close();
    }
}
