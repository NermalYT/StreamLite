// Personal 3D listening test, offline: every version plays at the same loudness, "no 3D" carries left/right only
// (front = behind, above = below), the head models carry front/back and height cues, every setting is tried at
// every place once in random order, answers are scored with their mix-ups, the best wins (3D on a tie), and the
// result is kept per pair of headphones. The 7.1 game version goes through the real game path: it places left, front
// and behind like the head model does, carries no height, and is never picked as a setting. Usage: run.ps1 Personal3DTest
using System;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class Personal3DTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static double Rms(float[] x, int ch) { double s = 0; int n = 0; for (int i = ch; i < x.Length; i += 2) { s += x[i] * x[i]; n++; } return Math.Sqrt(s / n); }
    static double Db(double v) { return 20 * Math.Log10(v + 1e-12); }

    /// <summary>Largest difference between two renders' 2-12 kHz spectra (dB): direction cues live there.</summary>
    static double SpectralDiff(float[] a, float[] b)
    {
        int n = 4096; var fa = new double[n / 2]; var fb = new double[n / 2];
        foreach (var pair in new[] { Tuple.Create(a, fa), Tuple.Create(b, fb) })
        {
            var fft = new Fft(n); var re = new float[n]; var im = new float[n];
            for (int s = 0; s + 2 * n <= pair.Item1.Length; s += n)
            {
                for (int i = 0; i < n; i++) { re[i] = pair.Item1[s + 2 * i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)); im[i] = 0; }
                fft.Transform(re, im, false);
                for (int k = 0; k < n / 2; k++) pair.Item2[k] += re[k] * re[k] + im[k] * im[k];
            }
        }
        double worst = 0;
        for (int c = 2000; c <= 12000; c = (int)(c * 1.26))
        {
            int k0 = (int)(c / 1.12 * n / 48000), k1 = (int)(c * 1.12 * n / 48000);
            double ea = 0, eb = 0; for (int k = k0; k <= k1; k++) { ea += fa[k]; eb += fb[k]; }
            worst = Math.Max(worst, Math.Abs(10 * Math.Log10((ea + 1e-20) / (eb + 1e-20))));
        }
        return worst;
    }

    static void Main()
    {
        var baseCfg = new AppSettings().Game; baseCfg.Repair(); GamePresets.Apply(baseCfg, baseCfg.Preset);
        var cfg = baseCfg.ToConfig();
        var vs = Personal3D.Round1();
        var none = vs[0]; var ku = vs[1];
        var levels = vs.Select(v => Db(Rms(Personal3D.Render(v, 0, 0, cfg), 0) + Rms(Personal3D.Render(v, 0, 0, cfg), 1))).ToList();
        Check(levels.Max() - levels.Min() < 1.5, "every version plays at the same loudness (" + string.Join(" / ", levels.Select(l => l.ToString("0.0"))) + " dB)");

        var nFront = Personal3D.Render(none, 0, 0, cfg); var nBack = Personal3D.Render(none, 180, 0, cfg);
        var nUp = Personal3D.Render(none, 0, 60, cfg); var nDown = Personal3D.Render(none, 0, -45, cfg);
        Check(SpectralDiff(nFront, nBack) < 0.5 && SpectralDiff(nUp, nDown) < 0.5, "no 3D: front sounds like behind, above like below (left/right only)");
        var nLeft = Personal3D.Render(none, 90, 0, cfg);
        Check(Db(Rms(nLeft, 0)) - Db(Rms(nLeft, 1)) > 20, "no 3D: left is in the left ear");

        var kFront = Personal3D.Render(ku, 0, 0, cfg); var kBack = Personal3D.Render(ku, 180, 0, cfg);
        var kUp = Personal3D.Render(ku, 0, 60, cfg); var kDown = Personal3D.Render(ku, 0, -45, cfg);
        double fb = SpectralDiff(kFront, kBack), ud = SpectralDiff(kUp, kDown);
        Check(fb > 3 && ud > 3, "a head model: front and behind (" + fb.ToString("0") + " dB), above and below (" + ud.ToString("0") + " dB) sound different");

        var game = vs[3];
        var gFront = Personal3D.Render(game, 0, 0, cfg); var gBack = Personal3D.Render(game, 180, 0, cfg);
        var gUp = Personal3D.Render(game, 0, 60, cfg); var gDown = Personal3D.Render(game, 0, -45, cfg);
        var gLeft = Personal3D.Render(game, 90, 0, cfg);
        double gfb = SpectralDiff(gFront, gBack);
        Check(game.Game71 && gfb > 3, "a 7.1 game mix through the game path: front and behind sound different (" + gfb.ToString("0") + " dB)");
        Check(SpectralDiff(gUp, gDown) < 0.5, "a 7.1 game mix: above sounds like below (a channel mix has no height)");
        // Left vs right ear level: the same as the head model playing the sound straight from the left (within 2 dB).
        var pLeft = Personal3D.Render(new Personal3D.Variant { Hrtf = cfg.HrtfKind, Cue = (int)Math.Round(cfg.CueSharpen * 100) }, 90, 0, cfg);
        double gild = Db(Rms(gLeft, 0)) - Db(Rms(gLeft, 1)), pild = Db(Rms(pLeft, 0)) - Db(Rms(pLeft, 1));
        Check(gild > 3 && Math.Abs(gild - pild) < 2, "a 7.1 game mix: left is in the left ear, as loud there as a sound placed straight left (" + gild.ToString("0.0") + " vs " + pild.ToString("0.0") + " dB)");
        var mine = new Personal3D.Variant { Hrtf = cfg.HrtfKind, Cue = (int)Math.Round(cfg.CueSharpen * 100) };
        var pFront = Personal3D.Render(mine, 0, 0, cfg); var pBack = Personal3D.Render(mine, 180, 0, cfg);
        double df = SpectralDiff(gFront, pFront), db = SpectralDiff(gBack, pBack), dfx = SpectralDiff(gFront, pBack), dbx = SpectralDiff(gBack, pFront);
        Check(df < dfx && db < dbx, "a 7.1 game mix: in front sounds like a sound placed in front, not behind (" + df.ToString("0.0") + " vs " + dfx.ToString("0.0") + " dB), and the other way round (" + db.ToString("0.0") + " vs " + dbx.ToString("0.0") + " dB)");

        var rnd = new Random(3);
        var trials = Personal3D.Trials(vs, rnd);
        Check(trials.Count == 24 && vs.All(v => Enumerable.Range(0, 6).All(p => trials.Count(t => t.V == v && t.Place == p) == 1)) && trials.Take(6).Select(t => t.V).Distinct().Count() > 1,
            "every version at every place once, shuffled (24 trials)");
        foreach (var t in trials)
        {
            // A listener who mixes up front/behind and above/below without 3D, and gets KU100 right except once.
            int a = t.Place;
            if (t.V == none && (t.Place == 0 || t.Place == 4)) a = t.Place + 1;
            if (t.V == ku && t.Place == 1) a = 0;
            Personal3D.Answer(t, a);
        }
        Check(none.Right == 4 && none.FrontBack == 1 && none.UpDown == 1 && ku.Right == 5 && ku.FrontBack == 1, "answers are scored, with front/behind and above/below mix-ups");
        var best = Personal3D.Best(vs, HrtfSet.Kemar, 35);
        Check(best == vs[2], "the best setting wins (KEMAR 6/6 over KU100 5/6)");
        var tie = Personal3D.Round1(); tie[0].Right = 3; tie[0].Total = 6; tie[1].Right = 3; tie[1].Total = 6;
        Check(Personal3D.Best(tie, HrtfSet.Ku100, 35) == tie[1], "on a tie, 3D wins over no 3D");
        tie[3].Right = 6; tie[3].Total = 6;
        Check(Personal3D.Best(tie, HrtfSet.Ku100, 35) == tie[1], "the 7.1 game version is shown, never picked as a setting");
        var r2 = Personal3D.Round2(vs[2]);
        Check(r2.All(v => v.Hrtf == HrtfSet.Kemar) && r2.Select(v => v.Cue).OrderBy(c => c).SequenceEqual(new[] { 0, 70 }), "round 2: the winning head with cue sharpening off and at 70%");

        string dir = Path.Combine(Path.GetTempPath(), "sl_personal3d");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var core = new AppCore(true);
        core.S.Game.Hrtf = HrtfSet.Ku100; core.S.Game.CueSharpen = 30;
        core.SavePersonal3D("{phones-a}", HrtfSet.Kemar, 70);
        Check(core.S.Game.Hrtf == HrtfSet.Kemar && core.S.Game.CueSharpen == 70 && core.Personal3D("{phones-a}") == "kemar|70", "saved for these headphones and used right away");
        Check(core.Personal3D("{phones-b}") == null, "other headphones keep their own setting");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
