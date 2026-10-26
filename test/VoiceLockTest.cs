// "Only my voice" (3.5.0), measured offline with Windows' voices: David and Mark (two men) and Zira (a woman), 12
// lines each (spoken by test\voices.ps1 into %TEMP%\StreamLite-voices). Part 1: how alike the speaker model finds the
// same voice and two different ones. Part 2: a call - you learn your voice from 6 lines, then talk; someone else in the
// room (quieter, with the room's echo) talks in between and over you; the mic's turned-down share of each.
// Usage: run.ps1 VoiceLockTest [voices folder]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Audio;

class VoiceLockTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    internal static float[] Wav16Public(string path) { return Wav16(path); }
    internal static float[] Up3Public(float[] x) { return Up3(x); }
    internal static float[] InRoomPublic(float[] x, double db, Random r) { return InRoom(x, db, r); }
    static float[] Wav16(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, data = -1, len = 0, rate = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4); int sz = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") rate = BitConverter.ToInt32(b, pos + 12);
            if (id == "data") { data = pos + 8; len = Math.Min(sz, b.Length - data); break; }
            pos += 8 + sz + (sz & 1);
        }
        if (rate != 16000) throw new Exception(path + ": " + rate + " Hz");
        var x = new float[len / 2];
        for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(b, data + 2 * i) / 32768f;
        // Trim the synthesizer's silence at both ends.
        int a = 0, e = x.Length; while (a < e && Math.Abs(x[a]) < 0.003f) a++; while (e > a && Math.Abs(x[e - 1]) < 0.003f) e--;
        return x.Skip(a).Take(e - a).ToArray();
    }

    /// <summary>16 kHz -> 48 kHz (windowed-sinc interpolation).</summary>
    static float[] Up3(float[] x)
    {
        var y = new float[x.Length * 3];
        for (int i = 0; i < y.Length; i++)
        {
            double t = i / 3.0, s = 0; int c = (int)t;
            for (int k = c - 16; k <= c + 16; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                double d = t - k, sinc = Math.Abs(d) < 1e-9 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                s += x[k] * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * d / 17));
            }
            y[i] = (float)s;
        }
        return y;
    }

    /// <summary>Someone else in the room: quieter, duller, with the room's echo (48 kHz).</summary>
    static float[] InRoom(float[] x, double gainDb, Random rnd)
    {
        var y = new float[x.Length + 48000 / 2]; double g = Math.Pow(10, gainDb / 20), lp = 0;
        var taps = Enumerable.Range(0, 40).Select(k => new { D = 200 + rnd.Next(20000), A = 0.35 * Math.Exp(-k / 12.0) * (rnd.NextDouble() < 0.5 ? -1 : 1) }).ToArray();
        for (int i = 0; i < x.Length; i++)
        {
            lp += 0.5 * (x[i] - lp);
            y[i] += (float)(lp * g);
            foreach (var t in taps) if (i + t.D < y.Length) y[i + t.D] += (float)(lp * g * t.A);
        }
        return y;
    }

    /// <summary>The calls tried: you, them (VL_PAIRS="MarkHigh:Zira,..." for others).</summary>
    static string[][] Pairs()
    {
        string e = Environment.GetEnvironmentVariable("VL_PAIRS");
        if (!string.IsNullOrEmpty(e)) return e.Split(',').Select(x => x.Split(':')).ToArray();
        return new[] { new[] { "David", "Zira" }, new[] { "Zira", "David" }, new[] { "David", "Mark" }, new[] { "Mark", "David" },
            new[] { "MarkHigh", "Zira" }, new[] { "Zira", "MarkHigh" }, new[] { "ZiraHigh", "DavidHigh" }, new[] { "DavidHigh", "ZiraHigh" } };
    }

    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "StreamLite-voices");
        var voices = new[] { "David", "Mark", "Zira" };
        if (!File.Exists(Path.Combine(dir, "MarkHigh-11.wav")))
        {
            // Windows' OneCore voices speak the lines (test\voices.ps1; WinRT, nothing installed).
            string script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\voices.ps1"));
            var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Out \"" + dir + "\"") { UseShellExecute = false, CreateNoWindow = true });
            pr.WaitForExit();
        }
        // Each voice also higher (voices.ps1: a high-pitched man +35%, a higher woman +25%): DavidHigh ~112 Hz, MarkHigh
        // ~182 Hz (as high as Zira, 184 Hz), ZiraHigh ~212 Hz.
        var lines = voices.Concat(voices.Select(v => v + "High")).ToDictionary(v => v, v => Enumerable.Range(0, 12).Select(i => Wav16(Path.Combine(dir, v + "-" + i.ToString("00") + ".wav"))).ToList());
        using (var vl = new VoiceLock { NoWorker = true })
        {
            // ---- Part 1: voiceprints from lines 0-5, compared with lines 6-11 on their own.
            Console.WriteLine("How alike (cosine; 1 = same):        " + string.Join("  ", voices.Select(v => ("vs " + v).PadLeft(9))));
            var prints = new Dictionary<string, float[]>();
            foreach (var v in voices)
            {
                var enrol = lines[v].Take(6).SelectMany(l => l).ToArray(); double cons;
                prints[v] = vl.PrintOf(enrol, enrol.Length, out cons);
                if (prints[v] == null) { Console.WriteLine("no voiceprint: " + vl.Error); return; }
                Console.WriteLine("  " + v + "'s voiceprint: " + (enrol.Length / 16000.0).ToString("0") + " s, pieces agree " + cons.ToString("0.00"));
            }
            double worstSame = 1, bestOther = -1;
            foreach (var v in voices)
            {
                var row = voices.Select(w => lines[v].Skip(6).Select(l => (double)VoiceLock.Cos(vl.Embed(l, 0, l.Length), prints[w])).ToList()).ToList();
                Console.WriteLine(("  " + v + "'s other lines").PadLeft(36) + "  " + string.Join("  ", row.Select(r => (r.Average().ToString("0.00") + " (" + r.Min().ToString("0.00") + "-" + r.Max().ToString("0.00") + ")").PadLeft(9))));
                for (int w = 0; w < voices.Length; w++) { if (voices[w] == v) worstSame = Math.Min(worstSame, row[w].Min()); else bestOther = Math.Max(bestOther, row[w].Max()); }
            }
            Check(worstSame > bestOther + 0.1, "whole lines: every line is clearly closer to its own voice (lowest own " + worstSame.ToString("0.00") + ", highest other " + bestOther.ToString("0.00") + ")");
        }

        // ---- Part 2: calls.
        foreach (bool two in new[] { false, true })
        foreach (var pair in Pairs())
        {
            string you = pair[0], them = pair[1];
            var rnd = new Random(you.Length * 7 + them.Length);
            using (var vl = new VoiceLock { NoWorker = true, Enabled = true })
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                string eo = Environment.GetEnvironmentVariable("VL_OPEN"), ec = Environment.GetEnvironmentVariable("VL_CLOSE"), es = Environment.GetEnvironmentVariable("VL_SURE");
                if (eo != null) vl.OpenAt = float.Parse(eo, ic); if (ec != null) vl.CloseBelow = float.Parse(ec, ic); if (es != null) vl.SureAt = float.Parse(es, ic);
                var enrol = lines[you].Take(6).SelectMany(l => l).ToArray(); double cons;
                vl.SetPrint(vl.PrintOf(enrol, enrol.Length, out cons));
                // Made for your voice (3.5.2), as the app does when you learn it (VL_CAL=0: the fixed thresholds).
                float p10, p25; string cal = "";
                if (Environment.GetEnvironmentVariable("VL_CAL") != "0" && vl.Calibrate(enrol, enrol.Length, out p10, out p25))
                {
                    vl.Tune(p10, p25);
                    cal = " [your pieces p10 " + p10.ToString("0.00") + ": you from " + vl.OpenAt.ToString("0.00") + ", someone else under " + vl.CloseBelow.ToString("0.00") + "]";
                }
                if (eo != null) vl.OpenAt = float.Parse(eo, ic); if (ec != null) vl.CloseBelow = float.Parse(ec, ic); if (es != null) vl.SureAt = float.Parse(es, ic);
                if (two)
                {
                    // Their voice learned too, as your mic hears them (in the room).
                    var r2 = new Random(5); var theirs = new List<float>();
                    foreach (var l in lines[them].Take(6)) { var b48 = InRoom(Up3(l), -6, r2); for (int i = 0; i + 2 < b48.Length; i += 3) theirs.Add((b48[i] + b48[i + 1] + b48[i + 2]) / 3f); }
                    double c2; var arr = theirs.ToArray(); vl.SetBlockPrint(vl.PrintOf(arr, arr.Length, out c2));
                }
                // The call: 0 = you, 1 = them, 2 = both at once; pauses of 0.3-2.5 s.
                var parts = new List<int[]>();   // kind, start, end (48 kHz)
                var mic = new List<float>();
                int li = 6, lj = 6;
                int[] plan = { 0, 1, 0, 0, 1, 1, 0, 2, 1, 0, 1, 2, 0, 1, 0 };
                foreach (int kind in plan)
                {
                    int pause = 48000 * (3 + rnd.Next(22)) / 10;
                    for (int i = 0; i < pause; i++) mic.Add(0);
                    var a = kind != 1 ? Up3(lines[you][li++ % 12 < 6 ? 6 + li % 6 : li % 12]) : null;
                    var b = kind != 0 ? InRoom(Up3(lines[them][lj++ % 12 < 6 ? 6 + lj % 6 : lj % 12]), -6, rnd) : null;
                    int len = Math.Max(a != null ? a.Length : 0, b != null ? b.Length : 0), s0 = mic.Count;
                    for (int i = 0; i < len; i++) mic.Add((a != null && i < a.Length ? a[i] : 0) + (b != null && i < b.Length ? b[i] * (kind == 2 ? 1 : 1) : 0));
                    parts.Add(new[] { kind, s0, s0 + (a != null ? a.Length : len) });
                }
                var x = mic.ToArray(); var y = (float[])x.Clone(); var blk = new float[480];
                bool debug = Environment.GetEnvironmentVariable("VL_DEBUG") == you + ":" + them;
                for (int i = 0; i + 480 <= y.Length; i += 480)
                {
                    Array.Copy(y, i, blk, 0, 480); vl.Process(blk, 480); Array.Copy(blk, 0, y, i, 480);
                    if ((i / 480) % 10 == 9) { long lk = vl.Looks; vl.DecideNow(); if (debug && vl.Looks > lk) Console.WriteLine("    " + (i / 48000.0).ToString("0.0") + " s: " + vl.LastSimilarity.ToString("0.00") + (vl.Blocking ? " other" : " you") + "  picked " + vl.DebugPicked + " newest " + vl.DebugNewest.ToString("0.00") + " db " + vl.DebugDb.ToString("0")); }
                }
                if (debug)
                    for (int pi = 0; pi < parts.Count; pi++)
                    {
                        var p = parts[pi]; int k = 0, n = 0; var lost = new List<string>();
                        for (int s0 = p[1]; s0 + 480 <= p[2]; s0 += 480)
                        {
                            double ex = 0, ey = 0; for (int i = s0; i < s0 + 480; i++) { ex += x[i] * (double)x[i]; ey += y[i] * (double)y[i]; }
                            if (ex < 1e-6) continue; n++;
                            if (ey > ex * 0.25) k++; else lost.Add((s0 / 48000.0).ToString("0.00"));
                        }
                        Console.WriteLine("    part " + pi + " kind " + p[0] + " " + (p[1] / 48000.0).ToString("0.0") + "-" + (p[2] / 48000.0).ToString("0.0") + " s: kept " + k + "/" + n + (lost.Count > 0 && p[0] != 1 ? "  lost at " + lost.First() + "-" + lost.Last() : ""));
                    }
                // Per 10 ms of voice: kept (> -6 dB) or turned down.
                var kept = new double[3]; var total = new double[3]; double onsetLost = 0, onsetTotal = 0;
                foreach (var p in parts)
                    for (int s = p[1]; s + 480 <= p[2]; s += 480)
                    {
                        double ex = 0, ey = 0; for (int i = s; i < s + 480; i++) { ex += x[i] * (double)x[i]; ey += y[i] * (double)y[i]; }
                        if (ex < 1e-6) continue;
                        bool keep = ey > ex * 0.25;
                        total[p[0]]++; if (keep) kept[p[0]]++;
                        if (p[0] == 1 && s - p[1] < 48000 * 3 / 4) { onsetTotal++; if (keep) onsetLost++; }
                    }
                Func<int, double> pct = k => total[k] == 0 ? 0 : 100 * kept[k] / total[k];
                Console.WriteLine((two ? "[their voice learned too] " : "") + you + " (learned, " + cons.ToString("0.00") + ") with " + them + " in the room" + cal + ": " + you + " kept " + pct(0).ToString("0") + "%, " + them + " kept " + pct(1).ToString("0") + "% (" +
                    (onsetTotal > 0 ? (100 * onsetLost / onsetTotal).ToString("0") : "-") + "% of their first 0.75 s), both at once kept " + pct(2).ToString("0") + "%; " + vl.Looks + " looks");
                // Two men with only your voice learned: too alike for one voiceprint (Mark gets through 50-74%) - the
                // app says to learn their voice too then. Everything else is held to what it measured (3.5.0).
                bool alike = !two && (you == "Zira") == (them == "Zira");
                // Your voice: 100% in every call but one - the high-pitched man loses one short word said right after
                // the other person (0.27 s: too little voice to tell), 94.6%.
                Check(pct(0) >= 94, you + ": your voice stays on (" + pct(0).ToString("0") + "%)");
                if (!alike) Check(pct(1) <= (two ? 30 : 25), them + ": turned down most of the time (" + pct(1).ToString("0") + "% gets through, mostly the start of a sentence)");
                Check(pct(2) >= (two ? 55 : 60), "both talking: you stay on most of the time (" + pct(2).ToString("0") + "%)");
            }
        }
        // ---- Part 4: your silly voice (3.5.2's voice setup). Mark talks normally and in a put-on high voice (MarkHigh,
        // +35%: as high as Zira); Zira talks in the room. Without the silly voice learned it's cut; learned too, it stays
        // on - and Zira, at the same pitch, is still turned down.
        foreach (bool silly in new[] { false, true })
        {
            var rnd = new Random(21);
            using (var vl = new VoiceLock { NoWorker = true, Enabled = true })
            {
                var enrol = lines["Mark"].Take(6).SelectMany(l => l).ToArray(); double cons;
                vl.SetPrint(vl.PrintOf(enrol, enrol.Length, out cons));
                if (silly) { var se = lines["MarkHigh"].Take(4).SelectMany(l => l).ToArray(); double c3; vl.SetAlsoYou(new[] { vl.PrintOf(se, se.Length, out c3) }); }
                var parts = new List<int[]>(); var mic = new List<float>();
                int[] plan = { 0, 3, 1, 0, 3, 3, 1, 0, 1, 3, 0, 1 }; int li = 6;
                foreach (int kind in plan)
                {
                    int pause = 48000 * (6 + rnd.Next(15)) / 10; for (int i = 0; i < pause; i++) mic.Add(0);
                    string who = kind == 0 ? "Mark" : kind == 3 ? "MarkHigh" : "Zira";
                    var a = Up3(lines[who][6 + (li++ % 6)]);
                    if (kind == 1) a = InRoom(a, -6, rnd);
                    int s0 = mic.Count; mic.AddRange(a); parts.Add(new[] { kind, s0, s0 + a.Length });
                }
                var x = mic.ToArray(); var y = (float[])x.Clone(); var blk = new float[480];
                for (int i = 0; i + 480 <= y.Length; i += 480) { Array.Copy(y, i, blk, 0, 480); vl.Process(blk, 480); Array.Copy(blk, 0, y, i, 480); if ((i / 480) % 10 == 9) vl.DecideNow(); }
                var kept = new double[4]; var tot = new double[4];
                foreach (var pt in parts)
                    for (int s0 = pt[1]; s0 + 480 <= pt[2]; s0 += 480)
                    {
                        double ex = 0, ey = 0; for (int i = s0; i < s0 + 480; i++) { ex += x[i] * (double)x[i]; ey += y[i] * (double)y[i]; }
                        if (ex < 1e-6) continue; tot[pt[0]]++; if (ey > ex * 0.25) kept[pt[0]]++;
                    }
                Func<int, double> pc = k => tot[k] == 0 ? 0 : 100 * kept[k] / tot[k];
                Console.WriteLine("Mark's silly high voice " + (silly ? "learned too" : "not learned") + ": normal voice kept " + pc(0).ToString("0") + "%, silly voice kept " + pc(3).ToString("0") + "%, Zira kept " + pc(1).ToString("0") + "%");
                if (silly)
                {
                    Check(pc(0) >= 97 && pc(3) >= 90, "your silly voice learned too: it stays on (" + pc(3).ToString("0") + "%), your normal voice too (" + pc(0).ToString("0") + "%)");
                    Check(pc(1) <= 30, "someone else at the same pitch is still turned down (" + pc(1).ToString("0") + "% gets through)");
                }
            }
        }

        // ---- Part 3: the whole mic chain, as in the app - DeepFilterNet on, a fan running, your voice learned
        // through it; then the call with someone else in the room (David learned, Zira in the room).
        {
            var s = new StreamLite.AppSettings();
            typeof(StreamLite.AppSettings).GetMethod("Normalize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Invoke(s, null);
            s.NsEngine = StreamLite.NoiseEngines.DfnGpu; s.NoiseRemoval = true; s.VoiceLockOn = true;
            var c = new VoiceChain(); c.Suppressor.Synchronous = true; c.Lock.NoWorker = true; c.Configure(s);
            var blk = new float[480];
            for (int k = 0; k < 4000; k++) { Array.Clear(blk, 0, 480); c.Process(blk, 480, false); var d = c.Suppressor.DfnEngine; if (d != null && (d.Ready || d.Error != null)) break; System.Threading.Thread.Sleep(2); }
            var fanRnd = new Random(9); double fa = 0, fb = 0;
            Func<float> fan = () => { fa = 0.98 * fa + 0.02 * (fanRnd.NextDouble() * 2 - 1); fb = 0.5 * fb + 0.5 * fa; return (float)(fb * 0.15); };
            Action<float[], Action<int>> run = (x, each) =>
            {
                for (int i = 0; i + 480 <= x.Length; i += 480)
                {
                    for (int j = 0; j < 480; j++) blk[j] = x[i + j] + fan();
                    c.Process(blk, 480, false); each(i / 480);
                }
            };
            var learnAudio = new List<float>();
            foreach (var l in lines["David"].Take(6)) { learnAudio.AddRange(Up3(l)); learnAudio.AddRange(new float[24000]); }
            c.Lock.StartLearning();
            run(learnAudio.ToArray(), b => { });
            double cons, secs; var pr = c.Lock.FinishLearning(out cons, out secs);
            c.Lock.SetPrint(pr);
            Console.WriteLine("Through the whole chain (DeepFilterNet, a fan): David learned from " + secs.ToString("0") + " s of voice, pieces agree " + cons.ToString("0.00"));
            var rnd = new Random(3); var mic = new List<float>(); var kind = new List<int>();
            int[] plan = { 0, 1, 0, 1, 1, 0, 2, 0, 1, 0 }; int li = 6, lj = 6;
            foreach (int k in plan)
            {
                int pause = 48000 * (3 + rnd.Next(20)) / 10; mic.AddRange(new float[pause]); kind.AddRange(Enumerable.Repeat(-1, pause));
                var a = k != 1 ? Up3(lines["David"][li++]) : null; var b = k != 0 ? InRoom(Up3(lines["Zira"][lj++]), -6, rnd) : null;
                int len = Math.Max(a != null ? a.Length : 0, b != null ? b.Length : 0);
                for (int i = 0; i < len; i++) { mic.Add((a != null && i < a.Length ? a[i] : 0) + (b != null && i < b.Length ? b[i] : 0)); kind.Add(k == 2 ? (a != null && i < a.Length ? 2 : 1) : (k == 0 && (a == null || i >= a.Length) ? -1 : k)); }
                if (li > 11) li = 6; if (lj > 11) lj = 6;
            }
            var kept = new double[3]; var tot = new double[3];
            var times = new List<double>();
            run(mic.ToArray(), b =>
            {
                if (b % 10 == 9) { var w = System.Diagnostics.Stopwatch.StartNew(); long before = c.Lock.Looks; c.Lock.DecideNow(); if (c.Lock.Looks > before) times.Add(w.Elapsed.TotalMilliseconds); }
                int k = kind[b * 480 + 240];
                if (k >= 0) { tot[k]++; if (c.Lock.CurrentGain > 0.5f) kept[k]++; }
            });
            Func<int, double> pc = k => tot[k] == 0 ? 0 : 100 * kept[k] / tot[k];
            times.Sort();
            Console.WriteLine("  David kept " + pc(0).ToString("0") + "%, Zira kept " + pc(1).ToString("0") + "%, both at once kept " + pc(2).ToString("0") + "%; a look takes " + times[times.Count / 2].ToString("0.0") + " ms (slowest " + times[times.Count - 1].ToString("0") + " ms), 10 a second");
            Check(pc(0) >= 95, "through the chain: your voice stays on (" + pc(0).ToString("0") + "%)");
            Check(pc(1) <= 30, "through the chain: the other voice is turned down most of the time (" + pc(1).ToString("0") + "% gets through)");
            Check(times[times.Count / 2] < 40, "a look costs little CPU (" + times[times.Count / 2].ToString("0.0") + " ms, 10 a second on its own thread)");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
