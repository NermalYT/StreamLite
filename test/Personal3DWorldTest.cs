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
        double ufx, ufz; Building3D.RoomCenter(Math.Max(0, upFront.Room), out ufx, out ufz);
        Check(upFront.Floor == 2 && upFront.Room >= 0 && ufz > 0, "up and ahead: a room ahead of you upstairs (room " + (upFront.Room + 1) + ")");
        Check(downLeft.Floor == 0 && downLeft.Room % 4 <= 1, "down and to the left: a left room downstairs (room " + (downLeft.Room + 1) + ")");
        int rmCheck = 0; for (int r = 0; r < Building3D.Rooms; r++) { double x, z; Building3D.RoomCenter(r, out x, out z); if (Building3D.RoomAt(x, z) == r) rmCheck++; }
        Check(rmCheck == 16 && Building3D.Rooms == 16, "4 x 4 rooms: centres and rooms agree (" + rmCheck + " of 16)");

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

        Console.WriteLine("Can't tell:");
        {
            var v = new Building3D.Variant();
            var t = new Building3D.Trial { V = v, S = Building3D.RandomSource(2, rnd) };
            Building3D.RecordCantTell(t);
            Check(t.Sc.CantTell && t.Sc.Error == 1 && v.N == 1 && v.CantTell == 1 && v.FloorWrong == 1, "counts as a miss (error 1, a floor not told) for the setting being tried");
            Check(t.Sc.Text.Contains("upstairs, room " + (t.S.Room + 1)), "says where it was: " + t.Sc.Text);
        }

        Console.WriteLine("Finding your head (simulated listeners, 200 runs each, 20 heads):");
        var ids = new List<string> { HrtfSet.Ku100, HrtfSet.Kemar }; for (int h = 3; h <= 20; h++) ids.Add(HrtfSet.HumanPrefix + h);
        int found = 0, foundNear = 0, inFinal = 0;
        for (int run = 0; run < 200; run++)
        {
            // Your ears: one head clearly best (8° scatter), one close (14°), the rest 24-40°.
            string yours = ids[rnd.Next(ids.Count)], close = ids.Where(x => x != yours).ElementAt(rnd.Next(ids.Count - 1));
            var scatter = ids.ToDictionary(x => x, x => x == yours ? 8.0 : x == close ? 14.0 : 24 + 16 * rnd.NextDouble());
            Func<Building3D.Variant, double> mix = v => scatter[v.Hrtf] > 20 ? 0.12 : 0.03;
            var heads = ids.Select(x => new Building3D.Variant { Hrtf = x }).ToList();
            foreach (var t in Building3D.HeadRound(heads, Building3D.Search1, rnd, "1")) Building3D.Record(t, Listen(t.S, scatter[t.V.Hrtf], mix(t.V), rnd));
            var pool = Building3D.Keep(heads, Building3D.Keep1);
            foreach (var t in Building3D.HeadRound(pool, Building3D.Search2, rnd, "2")) Building3D.Record(t, Listen(t.S, scatter[t.V.Hrtf], mix(t.V), rnd));
            pool = Building3D.Keep(pool, Building3D.Keep2);
            if (pool.Any(x => x.Hrtf == yours)) inFinal++;
            foreach (var t in Building3D.HeadRound(pool, Building3D.Search3, rnd, "3")) Building3D.Record(t, Listen(t.S, scatter[t.V.Hrtf], mix(t.V), rnd));
            var win = Building3D.Keep(pool, 1)[0].Hrtf;
            if (win == yours) found++;
            if (win == yours || win == close) foundNear++;
        }
        Check(inFinal >= 180, "your head reaches the last round " + inFinal + "/200");
        Check(found >= 160, "your head wins " + found + "/200");
        Check(foundNear >= 190, "your head or the close one wins " + foundNear + "/200");
        {
            var heads = ids.Select(x => new Building3D.Variant { Hrtf = x }).ToList();
            var ts = Building3D.HeadRound(heads, Building3D.Search1, rnd, "1");
            Check(ts.Count == ids.Count * 3 && ts.GroupBy(t => t.V).All(g => g.Count() == 3), "round 1: every head 3 footsteps");
            var spots = ts.Where(t => t.V == heads[0]).Select(t => t.S.Pos).ToList();
            Check(heads.All(h => ts.Where(t => t.V == h).Select(t => t.S.Pos).OrderBy(p => p.X).SequenceEqual(spots.OrderBy(p => p.X))), "every head gets the same spots (none gets the easy ones)");
            int same = 0; for (int i = 1; i < ts.Count; i++) if ((ts[i].S.Pos - ts[i - 1].S.Pos).Length < 1e-9) same++;
            Check(same <= 2, "the same spot hardly ever twice in a row (" + same + "x)");
        }

        Console.WriteLine("Tuning (simulated listeners, 200 runs each):");
        int size112 = 0, cue70 = 0, contrastUp = 0, keep = 0;
        for (int run = 0; run < 200; run++)
        {
            var head = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 60, HeadSize = 100 };
            Building3D.Tuning tu;
            // Places sides better with a bigger head; front/back and up/down better with more sharpening; mixes up
            // floors less with more contrast.
            foreach (var t in Building3D.TuneRound(head, rnd, out tu))
            {
                double sc = 22; double mixF = 0.05;
                if (tu.Size.Contains(t.V)) sc = t.V.HeadSize == 112 ? 9 : 26;
                if (tu.Cue.Contains(t.V)) sc = t.V.Cue == 70 ? 9 : 26;
                if (tu.Contrast.Contains(t.V)) mixF = t.V.Contrast >= 85 ? 0.03 : 0.4;
                Building3D.Record(t, Listen(t.S, sc, mixF, rnd));
            }
            var r1 = Building3D.DecideTune(head, tu);
            if (r1.HeadSize == 112) size112++; if (r1.Cue == 70) cue70++; if (r1.Contrast == 85) contrastUp++;
            foreach (var t in Building3D.TuneRound(head, rnd, out tu)) Building3D.Record(t, Listen(t.S, 18, 0.05, rnd));
            var r2 = Building3D.DecideTune(head, tu);
            if (r2.HeadSize == 100) keep++;
        }
        Check(size112 >= 150, "places sides better with a bigger head -> head size 112% " + size112 + "/200");
        Check(cue70 >= 160, "hears better with more cue sharpening -> 70% " + cue70 + "/200");
        Check(contrastUp >= 160, "mixes up floors at less contrast -> more floor contrast " + contrastUp + "/200");
        Check(keep >= 70, "no difference -> the head size stays at least as often as chance (" + keep + "/200)");

        Console.WriteLine("Following walks (200 runs each):");
        int altPicked = 0, altEqual = 0;
        for (int run = 0; run < 200; run++)
        {
            var best = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75 };
            var alt = best.With(hrtf: HrtfSet.Kemar);
            var ws = Building3D.Walks(best, alt, rnd);
            foreach (var w in ws) { for (int i = 0; i < 100; i++) { w.ErrSum += Math.Abs(Gauss(rnd) * (w.V == alt ? 14 : 22)) ; w.N++; } }
            if (Building3D.DecideWalks(ws, best, alt) == alt) altPicked++;
            ws = Building3D.Walks(best, alt, rnd);
            foreach (var w in ws) { for (int i = 0; i < 100; i++) { w.ErrSum += Math.Abs(Gauss(rnd) * 18); w.N++; } }
            if (Building3D.DecideWalks(ws, best, alt) == alt) altEqual++;
        }
        Check(altPicked >= 195, "follows walks clearly better with the runner-up -> switched " + altPicked + "/200");
        Check(altEqual <= 20, "no difference -> the winner stays (" + (200 - altEqual) + "/200)");
        Check(Building3D.Walks(new Building3D.Variant(), new Building3D.Variant { Cue = 70 }, rnd).GroupBy(w => w.Floor).All(g => g.Count() == 2), "two walks per floor");

        Console.WriteLine("Head responses between the measured directions:");
        var allHeads = HrtfSet.Heads();
        Check(allHeads.Count == 20, "every head installed: " + allHeads.Count + " of 20 (KU100, KEMAR, 18 people)");
        foreach (var kind in allHeads)
        {
            var load = System.Diagnostics.Stopwatch.StartNew();
            var set = HrtfSet.Get(kind, null);
            double loadMs = load.Elapsed.TotalMilliseconds;
            if (kind == HrtfSet.Ku100 || kind == HrtfSet.Kemar)
                Check(set.Az.Length >= 5762 && set.Taps == 256, kind + ": the full measurement - " + set.Az.Length + " directions (8802 measured, repeats within 1° left out), " + set.Taps + " taps (loaded in " + loadMs.ToString("0") + " ms)");
            else Check(set.Az.Length >= 1500 && set.Taps == 256, kind + ": " + set.Az.Length + " directions, " + set.Taps + " taps (loaded in " + loadMs.ToString("0") + " ms)");
            double worstCorr = 1; int measured = 0;
            int stepI = Math.Max(1, set.Az.Length / 160);
            for (int i = 0; i < set.Az.Length; i += stepI)
            {
                // Only where no other measurement lies within 1° (the heads' two measuring grids repeat some points a
                // fraction of a degree apart, and there it's rightly their blend).
                double ai = set.Az[i] * Math.PI / 180, ei = set.El[i] * Math.PI / 180, nearest = 180;
                for (int j = 0; j < set.Az.Length; j++)
                {
                    if (j == i) continue;
                    double aj = set.Az[j] * Math.PI / 180, ej = set.El[j] * Math.PI / 180;
                    nearest = Math.Min(nearest, Math.Acos(Math.Max(-1, Math.Min(1, Math.Sin(ei) * Math.Sin(ej) + Math.Cos(ei) * Math.Cos(ej) * Math.Cos(ai - aj)))) * 180 / Math.PI);
                }
                if (nearest < 1) continue;
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
            if (kind != HrtfSet.Ku100 && kind != HrtfSet.Kemar) HrtfSet.Forget(HrtfSet.Ku100, HrtfSet.Kemar);
        }
        {
            float[] l, r; HrtfSet.Get(HrtfSet.Ku100, null).BuildPrecise(90, 0, 0, out l, out r); double itd0 = Itd(l, r);
            HrtfSet.Get(HrtfSet.Ku100, null, 1.12).BuildPrecise(90, 0, 0, out l, out r); double itd1 = Itd(l, r);
            HrtfSet.Get(HrtfSet.Ku100, null, 0.88).BuildPrecise(90, 0, 0, out l, out r); double itd2 = Itd(l, r);
            Check(Math.Abs(itd1 / itd0 - 1.12) < 0.03 && Math.Abs(itd2 / itd0 - 0.88) < 0.03, "head size 112% / 88%: the time between the ears at 90° is " + (itd1 / itd0 * 100).ToString("0") + "% / " + (itd2 / itd0 * 100).ToString("0") + "% of the measured " + (Math.Abs(itd0) / 48.0).ToString("0.00") + " ms");
        }

        Console.WriteLine("Footsteps:");
        var rend = new StepRenderer();
        var v0 = new Building3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75 };
        float[] warmL, warmR; rend.Render(v0, new V(2, 3.3, 1), 2, 30, 10, 0.3, 0, out warmL, out warmR);
        float[] L, R;
        rend.Render(v0, new V(-3, 0, 0.5), 1, 0, 0, 0, 0, out L, out R);
        double el2 = Energy(HighPass(L)), er2 = Energy(HighPass(R));
        Check(10 * Math.Log10(el2 / er2) > 6, "a step on your left is louder in the left ear above 1.5 kHz (" + (10 * Math.Log10(el2 / er2)).ToString("0.0") + " dB; the thud below that hardly differs between ears)");
        Check(L.All(x => !float.IsNaN(x) && Math.Abs(x) < 1) && R.All(x => !float.IsNaN(x) && Math.Abs(x) < 1), "no overload, no NaN");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) rend.Render(v0, new V(2, 3.3, 1), 2, 30, 10, 0.3, i, out L, out R);
        Check(sw.Elapsed.TotalMilliseconds / 20 < 60, "one footstep renders in " + (sw.Elapsed.TotalMilliseconds / 20).ToString("0.0") + " ms (each is rendered 100 ms before it plays)");

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
        var lp1 = Biquad.LowPass(1500, 48000); var lp2 = Biquad.LowPass(1500, 48000); var lp3 = Biquad.LowPass(1500, 48000); var lp4 = Biquad.LowPass(1500, 48000);
        l = l.Select(x => lp2.Process(lp1.Process(x))).ToArray(); r = r.Select(x => lp4.Process(lp3.Process(x))).ToArray();
        int best = 0; double bc = double.MinValue; var c = new double[161];
        for (int lag = -80; lag <= 80; lag++)
        {
            double s = 0; for (int i = 0; i < l.Length; i++) { int j = i + lag; if (j >= 0 && j < r.Length) s += l[i] * r[j]; }
            c[lag + 80] = s; if (s > bc) { bc = s; best = lag; }
        }
        int k = best + 80; if (k <= 0 || k >= 160) return best;
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
        core.S.WindowBounds = null;
        // StreamLite's window on the 3D Audio page, as the user sees it before pressing Personalize 3D.
        var form = new MainForm(core) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        form.Size = new Size(1280, 860);
        form.Show(); Application.DoEvents();
        typeof(MainForm).GetMethod("ShowPage", Any).Invoke(form, new object[] { 2 }); Application.DoEvents();
        var page = typeof(MainForm).GetField("gamePage", Any).GetValue(form);
        var radar = (SoundRadarView)page.GetType().GetField("radar", Any).GetValue(page);
        radar.Refresh();
        var snap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(snap, new Rectangle(0, 0, form.Width, form.Height));
        // The screen behind it: a plain stand-in (a test never copies the real screen).
        var full = new Rectangle(form.Left - 640, form.Top - 290, 2560, 1440);
        var desk = new Bitmap(full.Width, full.Height);
        using (var g = Graphics.FromImage(desk)) using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, full.Width, full.Height), Color.FromArgb(40, 60, 90), Color.FromArgb(20, 25, 35), 60f)) g.FillRectangle(b, 0, 0, full.Width, full.Height);
        var c = radar.PointToScreen(Point.Round(radar.Centre));
        var view = new Personal3DView(core, snap, desk, form.Bounds, full, c, radar.Radius, radar.ViewYaw, radar.ViewPitch, form, null) { NoMouse = true };
        view.Bounds = new Rectangle(0, 0, full.Width, full.Height);
        form.Controls.Add(view); view.BringToFront(); Application.DoEvents();
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\world"); Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.png")) File.Delete(old);
        Action<string, object> set = (name, value) => typeof(Personal3DView).GetField(name, Any).SetValue(view, value);
        var size = view.ClientSize;
        Action<string> save = name =>
        {
            using (var bmp = new Bitmap(size.Width, size.Height))
            {
                using (var g = Graphics.FromImage(bmp)) view.Render(new GdiCanvas(g, snap, desk), bmp.Size);
                bmp.Save(Path.Combine(dir, name + ".png"));
            }
        };
        // The way in: the window growing over the screen, zooming into the radar, flying into the building, the card.
        var animType = typeof(Personal3DView).GetNestedType("Anim", Any);
        set("anim", Enum.Parse(animType, "Enter"));
        foreach (var t in new[] { 0.0, 0.2, 0.6, 0.85, 1.0, 1.15, 1.35, 1.6, 1.85, 2.2, 2.75 }) { view.AnimAt = t; save("enter-" + t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); }
        set("anim", Enum.Parse(animType, "None")); view.AnimAt = double.NaN;
        save("0-intro");
        // Playing.
        var stageType = typeof(Personal3DView).GetNestedType("Stage", Any);
        set("stage", Enum.Parse(stageType, "Listen")); set("captured", true); set("totalSteps", 136); set("doneSteps", 23);
        set("headline", "Find your head  ·  round 1 of 3  ·  24 of 60"); set("detail", "Look where the footsteps are and click.     Can't tell?  Right-click or Space.");
        foreach (var v in new[] { new[] { 0f, 0f, 1f }, new[] { 30f, 45f, 2f }, new[] { -20f, -50f, 3f }, new[] { 150f, 5f, 4f } })
        {
            set("yaw", v[0]); set("pitch", v[1]); set("hover", Building3D.Aim(v[0], v[1]));
            save(v[2] + "-look-" + v[0] + "-" + v[1]);
        }
        var src = Building3D.RandomSource(2, new Random(5), 2);
        set("stage", Enum.Parse(stageType, "Feedback")); set("yaw", 0f); set("pitch", 40f);
        set("reveal", (Building3D.Source?)src); set("lastAnswer", (Building3D.Answer?)Building3D.Aim(-25, 35));
        set("detail", Building3D.Judge(Building3D.Aim(-25, 35), src).Text);
        save("5-feedback");
        var behind = new Building3D.Source { Floor = 1, Room = -1, Pos = new V(-1.2, 0, -3.4) };
        set("reveal", (Building3D.Source?)behind); set("lastAnswer", null); set("cantTell", true); set("yaw", 160f); set("pitch", -10f);
        set("detail", "It was " + Building3D.Describe(behind) + " - listen.");
        save("5b-cant-tell");
        set("cantTell", false);
        set("stage", Enum.Parse(stageType, "Countdown")); set("afterCountdown", Enum.Parse(stageType, "Listen")); set("stageEnd", DateTime.Now.AddSeconds(3.5)); set("reveal", null); set("lastAnswer", null);
        set("headline", "Find your head  ·  round 1 of 3"); set("detail", ""); set("yaw", 0f); set("pitch", 0f);
        save("6-countdown");
        // A result card with the heads' bars.
        var vs = HrtfSet.Heads().Select((h, i) => new Building3D.Variant { Hrtf = h, ErrSum = 0.15 + 0.03 * ((i * 7) % 13), N = 1 }).ToList();
        set("stage", Enum.Parse(stageType, "Done")); set("captured", false);
        set("headline", "All set: Human head 12");
        set("detail", "Your 3D setup for Headphones (test):\n   Human head 12 (a real person's measured head), head size 112%, cue sharpening 35%, floor contrast 75%.\n\nFollowing the walks you were on average 14° off with it, 21° with KEMAR.\nOut of 20 heads, the best let you place footsteps with 15 average error, the worst 51.\n\nIt applies to games' surround sound and everything StreamLite places in 3D.");
        typeof(Personal3DView).GetMethod("Chart", Any).Invoke(view, new object[] { vs, vs[5].HeadName });
        save("7-done");
        set("stage", Enum.Parse(stageType, "Listen")); set("captured", true); set("chart", null); set("headline", "Find your head  ·  round 2 of 3  ·  7 of 30"); set("detail", "Look where the footsteps are and click.     Can't tell?  Right-click or Space.");
        // The graphics card's drawing: the same frame through the WPF canvas (rendered to a picture here), and how
        // long building a frame takes on the UI thread (the GPU draws it).
        var dv = new System.Windows.Media.DrawingVisual();
        var wpf = new WpfCanvas(1.0, WpfCanvas.FromBitmap(snap), WpfCanvas.FromBitmap(desk));
        set("yaw", 20f); set("pitch", 30f); set("hover", Building3D.Aim(20, 30));
        var times = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            set("yaw", 20f + i * 0.5f);
            var w = System.Diagnostics.Stopwatch.StartNew();
            using (var dc = dv.RenderOpen()) { wpf.Begin(dc); view.Render(wpf, size); }
            times.Add(w.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double median = times[times.Count / 2], worst = times[times.Count - 2];
        Console.WriteLine("  building a frame for the graphics card: median " + median.ToString("0.00") + " ms, slowest " + worst.ToString("0.00") + " ms");
        Check(median < 1.6, "a frame is built in under 1.6 ms - room for 480 Hz monitors (" + median.ToString("0.00") + " ms)");
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size.Width, size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(dir, "8-gpu-canvas.png"))) enc.Save(fs);
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        form.Controls.Remove(view);
        form.Close();
    }
}
