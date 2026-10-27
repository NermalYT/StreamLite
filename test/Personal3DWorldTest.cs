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
using V = StreamLite.Audio.Sphere3D.Vec;
using Dir = StreamLite.Audio.Sphere3D.Dir;

// Personalize 3D (the sphere, 3.5.8): the six directions and the snapping view, directions from your head, scoring,
// and the decisions with simulated listeners (one who hears best with one head finds it, one who places sides better
// with a bigger head gets it, one who mixes up above and below at less contrast gets more). The head responses between
// measured directions: same as measured where measured, the time between the ears moving smoothly. A footstep on the
// left is louder on the left. Plus pictures of the sphere (test\shots\world).
// Usage: run.ps1 Personal3DWorldTest [shots]
class Personal3DWorldTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // A listener who gets the direction right with probability pRight; wrong, front and back swap (front/back steps)
    // or another direction at random.
    static Dir Listen(Sphere3D.Source s, double pRight, Random rnd)
    {
        if (rnd.NextDouble() < pRight) return s.D;
        if (s.D == Dir.Front) return Dir.Behind;
        if (s.D == Dir.Behind) return Dir.Front;
        var others = Sphere3D.Dirs.Where(d => d != s.D).ToArray();
        return others[rnd.Next(others.Length)];
    }

    static void Main(string[] args)
    {
        Console.WriteLine("The six directions and the view:");
        Check(Sphere3D.Dirs.Length == 6 && Sphere3D.Dirs.All(d => Math.Abs((Sphere3D.Point(d) - Sphere3D.Eye).Length - Sphere3D.Radius) < 1e-9), "six spots, all on the sphere");
        Check(Sphere3D.Dirs.All(a => Sphere3D.Dirs.All(b => a == b || Math.Abs(Sphere3D.Angle(a, b) - 90) < 1e-9 || Math.Abs(Sphere3D.Angle(a, b) - 180) < 1e-9)), "every two are a quarter turn or opposite (90 / 180 degrees)");
        double yw, pt;
        foreach (var d in Sphere3D.Dirs)
        {
            Sphere3D.View(d, Dir.Right, out yw, out pt);
            var f = Sphere3D.Forward(yw, pt);
            Check((f - Sphere3D.Unit(d)).Length < 1e-6, "the view facing " + Sphere3D.Label(d) + " looks straight at it");
        }
        Check(Sphere3D.Turn(Dir.Front, 1) == Dir.Right && Sphere3D.Turn(Dir.Right, 1) == Dir.Behind && Sphere3D.Turn(Dir.Front, -1) == Dir.Left && Sphere3D.Turn(Dir.Left, 1) == Dir.Front, "turning right / left goes round in quarter turns");
        Check(Sphere3D.Tilt(Dir.Left, Dir.Left, 1) == Dir.Up && Sphere3D.Tilt(Dir.Up, Dir.Left, -1) == Dir.Left && Sphere3D.Tilt(Dir.Behind, Dir.Behind, -1) == Dir.Down && Sphere3D.Tilt(Dir.Down, Dir.Behind, 1) == Dir.Behind, "tilting: up or down, and back to where you faced");
        Check(Sphere3D.Turn(Dir.Up, 1) == Dir.Up && Sphere3D.Tilt(Dir.Up, Dir.Front, 1) == Dir.Up && Sphere3D.Tilt(Dir.Down, Dir.Front, -1) == Dir.Down, "past straight up or down, nothing more");

        Console.WriteLine("Where a sound is from your head (fixed: the view only points):");
        double az, el, dd;
        Sphere3D.Direction(Sphere3D.Point(Dir.Front), 0, 0, out az, out el, out dd); Check(Math.Abs(az) < 0.01 && Math.Abs(el) < 0.01 && Math.Abs(dd - Sphere3D.Radius) < 1e-9, "in front: azimuth 0, elevation 0, " + Sphere3D.Radius + " m");
        Sphere3D.Direction(Sphere3D.Point(Dir.Left), 0, 0, out az, out el, out dd); Check(Math.Abs(az - 90) < 0.01, "to your left: azimuth 90 (the head responses' left)");
        Sphere3D.Direction(Sphere3D.Point(Dir.Right), 0, 0, out az, out el, out dd); Check(Math.Abs(az - 270) < 0.01, "to your right: azimuth 270");
        Sphere3D.Direction(Sphere3D.Point(Dir.Behind), 0, 0, out az, out el, out dd); Check(Math.Abs(az - 180) < 0.01, "behind you: azimuth 180");
        Sphere3D.Direction(Sphere3D.Point(Dir.Up), 0, 0, out az, out el, out dd); Check(Math.Abs(el - 90) < 0.01, "above you: elevation 90");
        Sphere3D.Direction(Sphere3D.Point(Dir.Down), 0, 0, out az, out el, out dd); Check(Math.Abs(el + 90) < 0.01, "below you: elevation -90");

        Console.WriteLine("Scoring:");
        var rnd = new Random(1);
        Check(Sphere3D.Dirs.All(d => Sphere3D.Judge(d, Sphere3D.At(d)).Error == 0 && Sphere3D.Judge(d, Sphere3D.At(d)).Right), "the right direction: no error");
        var fb = Sphere3D.Judge(Dir.Behind, Sphere3D.At(Dir.Front));
        Check(fb.FrontBack && fb.Error == 1.5 && fb.Text.Contains("swapped"), "in front answered behind: front/back swap, the biggest error");
        var ud = Sphere3D.Judge(Dir.Down, Sphere3D.At(Dir.Up));
        Check(!ud.HeightRight && ud.Error == 1.5, "above answered below: the biggest error, height wrong");
        var q = Sphere3D.Judge(Dir.Left, Sphere3D.At(Dir.Front));
        Check(q.Error == 1 && q.HeightRight && !q.FrontBack, "a quarter turn off: error 1");
        {
            var v = new Sphere3D.Variant();
            var t = new Sphere3D.Trial { V = v, S = Sphere3D.At(Dir.Up) };
            Sphere3D.RecordCantTell(t);
            Check(t.Sc.CantTell && t.Sc.Error == 1 && v.N == 1 && v.CantTell == 1, "can't tell: a quarter turn's error for the setting being tried");
            Check(t.Sc.Text.Contains("above you"), "and says where it was: " + t.Sc.Text);
        }
        {
            var heads = Enumerable.Range(0, 20).Select(i => new Sphere3D.Variant { Hrtf = HrtfSet.Ku100 }).ToList();
            var ts = Sphere3D.FirstRound(heads, rnd);
            Check(ts.Count == 20 && ts.All(t => t.K == Sphere3D.Kind.FrontBack) && heads.All(h => ts.Count(t => t.V == h) == 1), "first round: every head one front/back footstep");
            int front = ts.Count(t => t.S.D == Dir.Front), behind = ts.Count(t => t.S.D == Dir.Behind);
            Check(front >= 4 && behind >= 4, "in front and behind both come up, in no set order (" + front + " / " + behind + ")");
        }

        Console.WriteLine("Finding your head (simulated listeners, 200 runs, 20 heads, " + Sphere3D.SearchSteps + " footsteps):");
        var ids = new List<string> { HrtfSet.Ku100, HrtfSet.Kemar }; for (int h = 3; h <= 20; h++) ids.Add(HrtfSet.HumanPrefix + h);
        int found = 0, near = 0;
        for (int run = 0; run < 200; run++)
        {
            // Your ears: one head clearly best (right 90% of the time), one close (75%), the rest 35-55%.
            string yours = ids[rnd.Next(ids.Count)], close = ids.Where(x => x != yours).ElementAt(rnd.Next(ids.Count - 1));
            var p = ids.ToDictionary(x => x, x => x == yours ? 0.9 : x == close ? 0.75 : 0.35 + 0.2 * rnd.NextDouble());
            var heads = ids.Select(x => new Sphere3D.Variant { Hrtf = x }).ToList();
            var trials = Sphere3D.FirstRound(heads, rnd);
            foreach (var t in trials) Sphere3D.Record(t, Listen(t.S, p[t.V.Hrtf], rnd));
            for (int k = trials.Count; k < Sphere3D.SearchSteps; k++) { var t = Sphere3D.NextTrial(heads, rnd); Sphere3D.Record(t, Listen(t.S, p[t.V.Hrtf], rnd)); }
            var win = Sphere3D.Ranked(heads)[0].Hrtf;
            if (win == yours) found++; if (win == yours || win == close) near++;
        }
        Check(near >= 120, "your head or the close one wins " + near + "/200 (your head " + found + ")");

        Console.WriteLine("Tuning (simulated listeners, 200 runs each, " + Sphere3D.TuneSteps + " footsteps):");
        int size112 = 0, cue70 = 0, contrastUp = 0, keep = 0;
        for (int run = 0; run < 200; run++)
        {
            var head = new Sphere3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 60, HeadSize = 100 };
            Sphere3D.Tuning tu;
            // Places sides better with a bigger head; front/back and height better with more sharpening; mixes up
            // above and below less with more contrast.
            foreach (var t in Sphere3D.TuneRound(head, rnd, out tu))
            {
                double pr = 0.6;
                if (tu.Size.Contains(t.V)) pr = t.V.HeadSize == 112 ? 0.95 : 0.55;
                if (tu.Cue.Contains(t.V)) pr = t.V.Cue == 70 ? 0.95 : 0.55;
                if (tu.Contrast.Contains(t.V)) pr = t.V.Contrast >= 85 ? 0.95 : 0.55;
                Sphere3D.Record(t, Listen(t.S, pr, rnd));
            }
            var r1 = Sphere3D.DecideTune(head, tu);
            if (r1.HeadSize == 112) size112++; if (r1.Cue == 70) cue70++; if (r1.Contrast == 85) contrastUp++;
            foreach (var t in Sphere3D.TuneRound(head, rnd, out tu)) Sphere3D.Record(t, Listen(t.S, 0.7, rnd));
            if (Sphere3D.DecideTune(head, tu).HeadSize == 100) keep++;
        }
        Check(size112 >= 140, "places sides better with a bigger head -> head size 112% " + size112 + "/200");
        Check(cue70 >= 140, "hears better with more cue sharpening -> 70% " + cue70 + "/200");
        Check(contrastUp >= 140, "mixes up above and below at less contrast -> more floor contrast " + contrastUp + "/200");
        Check(keep >= 80, "no difference -> the head size stays more often than chance (" + keep + "/200)");
        Check(Sphere3D.TotalSteps(20) == Sphere3D.SearchSteps + Sphere3D.TuneSteps && Sphere3D.TotalSteps(20) <= 90, "the whole test: " + Sphere3D.TotalSteps(20) + " footsteps, everything tuned");

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
        var v0 = new Sphere3D.Variant { Hrtf = HrtfSet.Ku100, Cue = 35, Contrast = 75 };
        float[] L, R;
        rend.Render(v0, Sphere3D.At(Dir.Up), 0, out L, out R);
        rend.Render(v0, Sphere3D.At(Dir.Left), 0, out L, out R);
        double el2 = Energy(HighPass(L)), er2 = Energy(HighPass(R));
        Check(10 * Math.Log10(el2 / er2) > 6, "a step on your left is louder in the left ear above 1.5 kHz (" + (10 * Math.Log10(el2 / er2)).ToString("0.0") + " dB)");
        Check(L.All(x => !float.IsNaN(x) && Math.Abs(x) < 1) && R.All(x => !float.IsNaN(x) && Math.Abs(x) < 1), "no overload, no NaN");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 24; i++) rend.Render(v0, Sphere3D.At(Sphere3D.Dirs[i % 6]), i, out L, out R);
        Check(sw.Elapsed.TotalMilliseconds / 24 < 60, "one footstep renders in " + (sw.Elapsed.TotalMilliseconds / 24).ToString("0.0") + " ms (each is rendered 100 ms before it plays)");

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
        Action<string, string> buttons = (x, y) => typeof(Personal3DView).GetMethod("ShowButtons", Any).Invoke(view, new object[] { x, y });
        buttons("Start", "Back to StreamLite");
        save("0-intro");
        buttons(null, null);
        // Playing: the view facing each of the six directions.
        var stageType = typeof(Personal3DView).GetNestedType("Stage", Any);
        set("stage", Enum.Parse(stageType, "Listen")); set("captured", true); set("totalSteps", 84); set("doneSteps", 23);
        set("headline", "Find your head  ·  24 of 48"); set("detail", "Snap the view to the footsteps (mouse or arrow keys) and click.     Can't tell?  Right-click or Space.");
        int shot = 1;
        foreach (var d in new[] { Dir.Front, Dir.Right, Dir.Up, Dir.Behind, Dir.Down, Dir.Left })
        {
            var lv = d == Dir.Up || d == Dir.Down ? Dir.Right : d;
            double yw, pt; Sphere3D.View(d, lv, out yw, out pt);
            set("facing", d); set("level", lv); set("yaw", (float)yw); set("pitch", (float)pt);
            save(shot++ + "-facing-" + Sphere3D.Label(d).ToLower());
        }
        // Half way through a snap.
        set("facing", Dir.Right); set("level", Dir.Right); set("yaw", 45f); set("pitch", 0f); save(shot++ + "-snapping");
        set("stage", Enum.Parse(stageType, "Feedback")); set("facing", Dir.Up); set("yaw", 90f); set("pitch", 90f);
        set("reveal", (Sphere3D.Source?)Sphere3D.At(Dir.Up)); set("lastAnswer", (Dir?)Dir.Front);
        set("detail", Sphere3D.Judge(Dir.Front, Sphere3D.At(Dir.Up)).Text);
        save(shot++ + "-feedback");
        set("reveal", (Sphere3D.Source?)Sphere3D.At(Dir.Behind)); set("lastAnswer", null); set("cantTell", true); set("facing", Dir.Behind); set("level", Dir.Behind); set("yaw", 180f); set("pitch", 0f);
        set("detail", "It was " + Sphere3D.Name(Dir.Behind) + " - listen.");
        save(shot++ + "-cant-tell");
        set("cantTell", false);
        set("stage", Enum.Parse(stageType, "Countdown")); set("afterCountdown", Enum.Parse(stageType, "Listen")); set("stageEnd", DateTime.Now.AddSeconds(3.5)); set("reveal", null); set("lastAnswer", null);
        set("headline", "Find your head"); set("detail", ""); set("facing", Dir.Front); set("level", Dir.Front); set("yaw", 0f); set("pitch", 0f);
        save(shot++ + "-countdown");
        // A result card with the heads' bars.
        var vs = HrtfSet.Heads().Select((h, i) => new Sphere3D.Variant { Hrtf = h, ErrSum = 0.15 + 0.03 * ((i * 7) % 13), N = 1 }).ToList();
        set("stage", Enum.Parse(stageType, "Done")); set("captured", false);
        set("headline", "All set: Human head 12");
        set("detail", "Your 3D setup for Headphones (test):\n   Human head 12 (a real person's measured head)\n   head size 112%  (88%: 5 of 12 right, 100%: 8 of 12 right, 112%: 11 of 12 right)\n   cue sharpening 35%\n   floor contrast 75%\n\nWhile tuning you placed 30 of 36 footsteps right.\n\nIt applies to games' surround sound and everything StreamLite places in 3D.");
        typeof(Personal3DView).GetMethod("Chart", Any).Invoke(view, new object[] { vs, vs[5].HeadName });
        buttons("Back to StreamLite", "Practice");
        save(shot++ + "-done");
        set("stage", Enum.Parse(stageType, "Listen")); set("captured", true); set("chart", null); set("headline", "Tune it  ·  7 of 36"); set("detail", "Snap the view to the footsteps (mouse or arrow keys) and click.     Can't tell?  Right-click or Space.");
        // The graphics card's drawing: the same frame through the WPF canvas (rendered to a picture here), and how
        // long building a frame takes on the UI thread (the GPU draws it).
        var dv = new System.Windows.Media.DrawingVisual();
        var wpf = new WpfCanvas(1.0, WpfCanvas.FromBitmap(snap), WpfCanvas.FromBitmap(desk));
        set("facing", Dir.Right); set("yaw", 20f); set("pitch", 0f);
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
