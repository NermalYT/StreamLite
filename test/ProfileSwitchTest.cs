// Auto EQ when apps overlap, offline: which profile wins and why (game, music while playing, chat), your own priority
// order, no flip-flopping on brief overlaps (a simulated 10-minute session), every profile equally loud through the
// real EQ (pink noise, K-weighted), and no click, gap or level jump in the 3D path when a switch reconfigures it.
// Usage: run.ps1 ProfileSwitchTest
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Audio;

class ProfileSwitchTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static Func<string, bool> Set(params string[] a) { return x => a.Contains(x, StringComparer.OrdinalIgnoreCase); }

    static ProfileSettings Fresh() { var ps = new ProfileSettings(); GameProfiles.Merge(ps, null); return ps; }

    /// <summary>Pink noise (Paul Kellet's filter), mono.</summary>
    static float[] Pink(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            x[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.05); b6 = w * 0.115926;
        }
        return x;
    }

    /// <summary>K-weighted loudness (dB) of a signal - what a loudness meter (LUFS) would show, without the gating.</summary>
    static double Lufs(float[] x)
    {
        var k = new EqChain(); k.Update(new EqProfile { Mode = "parametric", AutoPreamp = false, Parametric = { new EqFilter { Type = "HSC", Freq = 1682, Gain = 4, Q = 0.7072 }, new EqFilter { Type = "HP", Freq = 38.1, Q = 0.5 } } }, 48000);
        var y = (float[])x.Clone(); k.Process(y, y.Length);
        double s = 0; for (int i = 4800; i < y.Length; i++) s += y[i] * (double)y[i];
        return 10 * Math.Log10(s / (y.Length - 4800));
    }

    static void Main()
    {
        // ---- Who wins, and why.
        var ps = Fresh();
        var d = GameProfiles.Decide(ps, Set("cs2", "Spotify", "Discord"), Set("Spotify"));
        Console.WriteLine("       " + d.Profile.Name + ": " + d.Explain());
        Check(d.Profile.Name == "Counter-Strike 2" && d.Reason == "cs2 is running" && d.AlsoInUse.SequenceEqual(new[] { "Spotify (Music)", "Discord (Chat)" }),
            "game + music playing + chat open: the game wins, and it says what else is in use");
        d = GameProfiles.Decide(ps, Set("Spotify", "Discord"), Set());
        Check(d.Profile.Name == GameProfiles.Chat && d.Reason == "Discord is open" && d.AlsoInUse.Count == 0, "Spotify open but quiet doesn't count: chat wins");
        d = GameProfiles.Decide(ps, Set("explorer"), Set());
        Check(d.Profile == null, "nothing of a profile in use: none (the fallback applies)");

        // Music moved to the top of the list (the dialog saves the whole order).
        ps.Priority = new[] { GameProfiles.Music }.Concat(GameProfiles.PriorityOrder(ps).Select(p => p.Name).Where(n => n != GameProfiles.Music)).ToList();
        var order = GameProfiles.PriorityOrder(ps);
        d = GameProfiles.Decide(ps, Set("cs2", "Spotify", "Discord"), Set("Spotify"));
        Check(order[0].Name == GameProfiles.Music && order.Last().Name == GameProfiles.Chat && d.Profile.Name == GameProfiles.Music && d.AlsoInUse[0] == "cs2 (Counter-Strike 2)",
            "your priority: Music above games - Spotify playing wins over CS2");
        d = GameProfiles.Decide(ps, Set("cs2", "Spotify"), Set());
        Check(d.Profile.Name == "Counter-Strike 2", "...and when Spotify stops playing, the game is back");
        ps.Profiles.Add(new GameProfile { Name = "My shooter", Exes = new List<string> { "shooter" }, Eq = new EqProfile() });
        var o1 = GameProfiles.PriorityOrder(ps).Select(p => p.Name).ToList();
        Check(o1[0] == GameProfiles.Music && o1.IndexOf("My shooter") == o1.FindLastIndex(n => n != GameProfiles.Chat), "a game profile added later joins the games - below Music, as you ordered");

        d = GameProfiles.Decide(Fresh(), Set("RainbowSix", "cs2"), Set());
        var ps2 = Fresh(); ps2.Priority = new List<string> { "Counter-Strike 2" };
        var d2 = GameProfiles.Decide(ps2, Set("RainbowSix", "cs2"), Set());
        Check(d.Profile.Name == "Rainbow Six Siege" && d2.Profile.Name == "Counter-Strike 2" && d2.AlsoInUse[0] == "RainbowSix (Rainbow Six Siege)", "two games at once: the higher one in your order wins");

        var ps3 = Fresh(); ps3.Priority = new List<string> { "Counter-Strike 2", GameProfiles.Music, GameProfiles.Chat };
        ps3.Profiles.Add(new GameProfile { Name = "My shooter", Exes = new List<string> { "shooter" }, Eq = new EqProfile() });
        var o3 = GameProfiles.PriorityOrder(ps3).Select(p => p.Name).ToList();
        Check(o3.IndexOf("My shooter") < o3.IndexOf(GameProfiles.Music) && o3.IndexOf(GameProfiles.Music) < o3.IndexOf(GameProfiles.Chat) && o3[0] == "Counter-Strike 2",
            "a new game profile lands above music and chat in a saved order");

        // ---- No flip-flopping: a change sticks only after it wins twice in a row.
        var sw = new ProfileSwitcher(); sw.Offer(GameProfiles.Movies, true);
        int flips = 0;
        foreach (var n in new[] { "Counter-Strike 2", GameProfiles.Movies, GameProfiles.Movies, "Counter-Strike 2", GameProfiles.Movies }) if (sw.Offer(n, false)) flips++;
        Check(flips == 0 && sw.Current == GameProfiles.Movies, "an app seen for one check only (a launcher, an updater) doesn't switch the EQ");
        bool first = sw.Offer("Counter-Strike 2", false), second = sw.Offer("Counter-Strike 2", false);
        Check(!first && second && sw.Current == "Counter-Strike 2", "a game that keeps running switches on the second check (3-6 s)");

        // A 10-minute session, a check every 3 s: Discord open throughout; CS2 for the first 5 minutes; Spotify plays
        // songs (3:00 each, 4 s between them) and is paused from 7:00 to 8:00. Music counts while playing + 15 s hold.
        var ps4 = Fresh(); var sw4 = new ProfileSwitcher(); var log = new List<string>();
        int lastHeard = -100000;
        for (int t = 0; t <= 600; t += 3)
        {
            bool gameOn = t < 300, paused = t >= 420 && t < 480, gap = t % 184 >= 180;
            bool sounding = !paused && !gap;
            if (sounding) lastHeard = t;
            var run = new List<string> { "Discord", "Spotify" }; if (gameOn) run.Add("cs2");
            var dd = GameProfiles.Decide(ps4, Set(run.ToArray()), x => t - lastHeard <= 15);
            string name = dd.Profile != null ? dd.Profile.Name : GameProfiles.Movies;
            if (sw4.Offer(name, t == 0)) log.Add((t / 60) + ":" + (t % 60).ToString("00") + " " + name);
        }
        Console.WriteLine("       switches: " + string.Join(", ", log));
        Check(log.Count == 4 && log[0].EndsWith("Counter-Strike 2") && log[1].EndsWith(GameProfiles.Music) && log[2].EndsWith(GameProfiles.Chat) && log[3].EndsWith(GameProfiles.Music),
            "10 minutes with overlapping apps: 3 switches, each for a real change (game quit, music paused, music back) - none between songs");

        // ---- Same loudness for every profile, through the real EQ with a headphone correction under it.
        var ps5 = Fresh();
        var corr = new DeviceCorrection { Name = GameProfiles.Momentum5Name, Filters = GameProfiles.Momentum5Correction(), On = true };
        ps5.MatchLoudness = false;
        var raw = GameProfiles.MatchLoudness(ps5, corr);
        var used = ps5.Profiles.Where(p => p.Exes.Count > 0 || p.Name == ps5.DefaultProfile).ToList();
        Func<GameProfile, double> heard = p =>
        {
            var e = p.Eq.Clone(); e.Extra.Clear(); foreach (var f in corr.Filters) e.Extra.Add(f.Copy()); e.AutoPreamp = true;
            var chain = new EqChain(); chain.Update(e, 48000);
            var x = Pink(48000 * 4, 7); chain.Process(x, x.Length);
            return Lufs(x);
        };
        var before = used.ToDictionary(p => p, heard);
        ps5.MatchLoudness = true;
        GameProfiles.MatchLoudness(ps5, corr);
        var after = used.ToDictionary(p => p, heard);
        double spreadBefore = before.Values.Max() - before.Values.Min(), spreadAfter = after.Values.Max() - after.Values.Min();
        var loud = before.OrderByDescending(k => k.Value).First(); var quiet = before.OrderBy(k => k.Value).First();
        Console.WriteLine("       before: " + loud.Key.Name + " " + (loud.Value - quiet.Value).ToString("0.0") + " dB louder than " + quiet.Key.Name);
        Check(spreadBefore > 1 && spreadAfter < 0.5, "switching profiles: loudness differences " + spreadBefore.ToString("0.0") + " dB -> " + spreadAfter.ToString("0.00") + " dB (pink noise, K-weighted, with the MOMENTUM 5 correction)");
        Check(ps5.Profiles.All(p => p.Eq.MatchDb <= 0) && used.Max(p => -p.Eq.MatchDb) < 3, "never a boost (nothing can clip); the most any profile is turned down: " + used.Max(p => -p.Eq.MatchDb).ToString("0.0") + " dB");
        ps5.MatchLoudness = false; GameProfiles.MatchLoudness(ps5, corr);
        Check(ps5.Profiles.All(p => p.Eq.MatchDb == 0), "switched off: every profile at its own level again");
        var ps6 = Fresh(); var mine = new GameProfile { Name = "Bass monster", Exes = new List<string> { "bass" }, Eq = new EqProfile() };
        mine.Eq.Bands[0] = 12; mine.Eq.Bands[1] = 12; ps6.Profiles.Add(mine);
        GameProfiles.MatchLoudness(ps6, corr);
        Check(ps6.Profiles.Where(p => p != mine).All(p => p.Eq.MatchDb > -3), "one profile with a huge boost doesn't make every other one quieter");

        // ---- 3D path: a profile switch (game settings -> natural, as for a film) mid-sound: no click, gap or jump.
        var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
        var game = g.ToConfig(); var natural = g.ToConfig();
        natural.FootstepBoost = 0; natural.CueSharpen = 0; natural.LoudTamer = 0;
        foreach (var dir in SpatialProcessor.Dirs) { natural.DirEq[dir].Enabled = false; natural.DirGainDb[dir] = 0; }
        var sp = new SpatialProcessor(); sp.Configure(game);
        var set = HrtfSet.Get(game.HrtfKind, game.CustomPath);
        for (int w = 0; w < 1000 && !ContinuousRenderer.IsReady(set); w++) Thread.Sleep(10);
        const int B = SpatialProcessor.B;
        int blocks = 48000 * 4 / B, switchAt = blocks / 2;
        var src = Enumerable.Range(0, SpatialProcessor.Slots).Select(s => Pink(blocks * B, 100 + s)).ToArray();
        var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
        var l = new float[B]; var r = new float[B]; var outL = new float[blocks * B];
        for (int b = 0; b < blocks; b++)
        {
            if (b == switchAt) sp.Configure(natural);
            for (int s = 0; s < slots.Length; s++) { if (s == SpatialProcessor.LFE) Array.Clear(slots[s], 0, B); else Array.Copy(src[s], b * B, slots[s], 0, B); }
            sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);
            Array.Copy(l, 0, outL, b * B, B);
        }
        // 5 ms windows: level steps and sample-to-sample jumps around the switch vs. the steady parts.
        int win = 240, at = switchAt * B;
        Func<int, double> lvl = i => { double s = 0; for (int k = i; k < i + win; k++) s += outL[k] * (double)outL[k]; return 10 * Math.Log10(s / win + 1e-12); };
        Func<int, int, double> maxStep = (a, z) => { double m = 0; for (int i = a; i + 2 * win <= z; i += win) m = Math.Max(m, Math.Abs(lvl(i + win) - lvl(i))); return m; };
        Func<int, int, double> maxDiff = (a, z) => { double m = 0; for (int i = a + 1; i < z; i++) m = Math.Max(m, Math.Abs(outL[i] - outL[i - 1])); return m; };
        Func<int, int, double> minLvl = (a, z) => { double m = 1e9; for (int i = a; i + win <= z; i += win) m = Math.Min(m, lvl(i)); return m; };
        double steadyStep = Math.Max(maxStep(48000, at - 9600), maxStep(at + 24000, outL.Length)), switchStep = maxStep(at - 4800, at + 9600);
        double steadyDiff = Math.Max(maxDiff(48000, at - 9600), maxDiff(at + 24000, outL.Length)), switchDiff = maxDiff(at - 4800, at + 9600);
        double steadyMin = Math.Min(minLvl(48000, at - 9600), minLvl(at + 24000, outL.Length)), switchMin = minLvl(at - 4800, at + 9600);
        Console.WriteLine("       5 ms level steps: steady " + steadyStep.ToString("0.0") + " dB, at the switch " + switchStep.ToString("0.0") + " dB; sample jumps " + steadyDiff.ToString("0.000") + " / " + switchDiff.ToString("0.000") + "; quietest window " + steadyMin.ToString("0.0") + " / " + switchMin.ToString("0.0") + " dB");
        Check(switchDiff <= steadyDiff * 1.5, "3D path, profile switch: no click (largest sample jump " + switchDiff.ToString("0.000") + " vs " + steadyDiff.ToString("0.000") + " in steady sound)");
        Check(switchMin >= steadyMin - 3, "3D path, profile switch: no gap (quietest 5 ms " + switchMin.ToString("0.0") + " dB vs " + steadyMin.ToString("0.0") + " dB)");
        Check(switchStep <= steadyStep + 1.5, "3D path, profile switch: no sudden level jump (" + switchStep.ToString("0.0") + " dB per 5 ms vs " + steadyStep.ToString("0.0") + " dB)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
