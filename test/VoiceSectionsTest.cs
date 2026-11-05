// The mic setup's sections (4.4): required first, optional later; lines aimed at one sound each; and what each optional
// section tunes - only what it heard, and only in the direction that keeps more of you. Synthetic sounds, offline.
// Usage: run.ps1 VoiceSectionsTest
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class VoiceSectionsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int Fs = 48000;

    static VoiceSetup.Step Step(VoiceSetup.Kind k) { return VoiceSetup.Steps().First(s => s.Kind == k); }

    static void Feed(VoiceSetup.Step st, float[] x, float[] room)
    {
        var b = new float[480];
        if (room != null) for (int i = 0; i + 480 <= room.Length; i += 480) { Array.Copy(room, i, b, 0, 480); st.FeedRaw(b, 480, true); }
        for (int i = 0; i + 480 <= x.Length; i += 480) { Array.Copy(x, i, b, 0, 480); st.FeedRaw(b, 480, false); }
        st.TakeVoice(); st.Done = true;
    }

    static readonly Random rnd = new Random(5);
    static float[] Room(double sec, double level = 0.0014) { return Enumerable.Range(0, (int)(sec * Fs)).Select(i => (float)((rnd.NextDouble() * 2 - 1) * level)).ToArray(); }

    /// <summary>A voice-like held sound: harmonics of f0 falling with frequency.</summary>
    static float Voiced(double t, double f0, double amp)
    {
        double y = 0;
        for (int h = 1; h <= 25 && h * f0 < 8000; h++) y += Math.Sin(2 * Math.PI * h * f0 * t) / h;
        return (float)(amp * y);
    }

    /// <summary>Held hums with gaps and slow fades: mmm, mhm, ooh...</summary>
    static float[] Hums(double sec, double amp)
    {
        var x = new float[(int)(sec * Fs)];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)Fs, ph = t % 1.2, env = ph < 0.9 ? Math.Min(1, ph / 0.05) * Math.Min(1, (0.9 - ph) / 0.2) : 0;
            x[i] = Voiced(t, 130, amp * env) + (float)((rnd.NextDouble() * 2 - 1) * 0.0014);
        }
        return x;
    }

    /// <summary>Vowels with bursts of band noise around hz between them: S sounds at that pitch.</summary>
    static float[] Esses(double sec, double hz, double sAmp)
    {
        var x = new float[(int)(sec * Fs)];
        double bp1 = 0, bp2 = 0, w = 2 * Math.PI * hz / Fs, r = 0.97, c = 2 * r * Math.Cos(w);
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)Fs, ph = t % 0.5;
            double n = rnd.NextDouble() * 2 - 1, y = n * (1 - r) * 3 + c * bp1 - r * r * bp2; bp2 = bp1; bp1 = y;
            x[i] = ph < 0.3 ? Voiced(t, 130, 0.04) : (float)(y * sAmp);
        }
        return x;
    }

    /// <summary>Vowels, with a bass thump at the start of each when pops is on.</summary>
    static float[] Pops(double sec, double thump)
    {
        var x = new float[(int)(sec * Fs)];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)Fs, ph = t % 0.4;
            x[i] = Voiced(t, 130, 0.04 * Math.Min(1, ph / 0.02)) + (float)(thump * Math.Sin(2 * Math.PI * 60 * ph) * Math.Exp(-ph / 0.025));
        }
        return x;
    }

    static void Main()
    {
        Console.WriteLine("The sections");
        var steps = VoiceSetup.Steps();
        int firstOptional = Array.FindIndex(steps, s => s.Optional);
        Check(firstOptional == 3 && steps.Skip(firstOptional).All(s => s.Optional), "three required sections first, then only optional ones");
        Check(steps.Take(3).Select(s => s.Kind).SequenceEqual(new[] { VoiceSetup.Kind.Normal, VoiceSetup.Kind.Loud, VoiceSetup.Kind.Quiet }), "required: normal, loud, quiet");
        foreach (var k in new[] { VoiceSetup.Kind.Hums, VoiceSetup.Kind.Esses, VoiceSetup.Kind.Pops, VoiceSetup.Kind.Soft, VoiceSetup.Kind.Far, VoiceSetup.Kind.Silly })
            Check(steps.Any(s => s.Kind == k && s.Optional && !string.IsNullOrEmpty(s.Tunes)), k + ": optional, and says what it tunes");
        Check(steps.All(s => s.Line.All(c => !char.IsDigit(c)) && (s.Line.IndexOf('-') < 0)), "plain words only: no numbers or hyphens for the recognizer to trip on");
        Func<string, string, int> count = (line, letters) => line.ToLowerInvariant().Count(c => letters.IndexOf(c) >= 0);
        Check(new[] { "mhm", "mmm", "umm", "ooo", "ahh" }.All(w => Step(VoiceSetup.Kind.Hums).Line.ToLowerInvariant().Contains(w)) && !Step(VoiceSetup.Kind.Hums).Follow, "hums: mhm, mmm, umm, ooh, ahh, followed by the voice heard (no recognizer knows them)");
        var es = Step(VoiceSetup.Kind.Esses).Line; Check(count(es, "s") * 100 / es.Length >= 12, "S sounds: an S in every few letters (" + count(es, "s") * 100 / es.Length + "%)");
        var pp = Step(VoiceSetup.Kind.Pops).Line; Check(count(pp, "pbtk") * 100 / pp.Length >= 12, "pops: P, B, T or K in every few letters (" + count(pp, "pbtk") * 100 / pp.Length + "%)");
        var callout = Step(VoiceSetup.Kind.Normal).Line.ToLowerInvariant();
        var missing = new[] { "s", "th", "b", "p", "t", "k", "g", "d", "f", "v", "m", "n", "ng", "l", "r", "w", "h", "dg" }.Where(sound => !callout.Contains(sound)).ToList();
        Check(missing.Count == 0, "the normal callout carries the sounds of English in a few lines" + (missing.Count > 0 ? ", but not: " + string.Join(" ", missing) : ""));

        Console.WriteLine("Hums and thinking sounds");
        {
            var s = AppSettings.Defaults(); s.GateThreshold = -35; s.GateHold = 100; s.GateRelease = 50; s.NsVoiceProtect = 30; s.Strength = 95; s.VolumeDb = 3; s.DeEsser = true;
            var st = Step(VoiceSetup.Kind.Hums); Feed(st, Hums(9, 0.012), Room(2));
            List<float> v; lock (st.RawDb) v = new List<float>(st.Voice48);
            double before = VoiceSetup.LossDb(v, s);
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            double after = VoiceSetup.LossDb(v, s);
            Check(s.GateThreshold < -35, "the gate went under the hums: " + s.GateThreshold + " dB");
            Check(s.GateHold >= 300 && s.GateRelease >= 250, "and closes slowly: hold " + s.GateHold + " ms, release " + s.GateRelease + " ms");
            Check(after <= before + 0.01 && (after <= 3 || s.Strength == 40), "noise removal cuts them less: " + before.ToString("0.0") + " -> " + after.ToString("0.0") + " dB lost");
            Check(s.VolumeDb == 3 && s.DeEsser, "what it says nothing about stays");
        }
        {
            var s = AppSettings.Defaults(); s.GateThreshold = -68; s.NsVoiceProtect = 80;
            var st = Step(VoiceSetup.Kind.Hums); Feed(st, Hums(9, 0.03), Room(2));
            VoiceSetup.TuneOptional(st, s, null, double.NaN);
            Check(s.GateThreshold == -68 && s.NsVoiceProtect >= 80, "already fine: a gate never goes up, voice protection never down");
        }
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var s = AppSettings.Defaults(); string before = js.Serialize(s);
            var st = Step(VoiceSetup.Kind.Hums); Feed(st, new float[Fs / 2], Room(2));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            Check(js.Serialize(s) == before && r.Lines.Count == 1, "nothing heard: nothing changed (" + r.Lines[0] + ")");
        }

        Console.WriteLine("S sounds");
        foreach (var hz in new[] { 7000.0, 5500.0, 4000.0 })
        {
            var s = AppSettings.Defaults();
            var st = Step(VoiceSetup.Kind.Esses); Feed(st, Esses(8, hz, 0.25), Room(2));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(Math.Abs(s.DeEssFreq - hz) <= 400, "an S at " + hz + " Hz: de-esser at " + s.DeEssFreq + " Hz");
        }
        {
            var s = AppSettings.Defaults(); s.DeEsser = true;
            var st = Step(VoiceSetup.Kind.Esses); Feed(st, Esses(8, 4000, 0.02), Room(2));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(!s.DeEsser && r.Lines[0].Contains("lower"), "a low, soft S (a lisp): the de-esser stays out, so nothing dulls it further");
        }
        {
            var s = AppSettings.Defaults(); s.DeEsser = false;
            var st = Step(VoiceSetup.Kind.Esses); Feed(st, Esses(8, 7000, 0.9), Room(2));
            VoiceSetup.TuneOptional(st, s, null, double.NaN);
            Check(s.DeEsser && s.DeEssAmount >= 6, "a sharp S: de-esser on, " + s.DeEssAmount + " dB");
        }

        Console.WriteLine("Pops and hard sounds");
        {
            var s = AppSettings.Defaults(); s.HighPassHz = 80; s.GateAttack = 10;
            var st = Step(VoiceSetup.Kind.Pops); Feed(st, Pops(7, 0.5), Room(2));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(s.HighPassHz >= 100, "pops that thump: low cut up to " + s.HighPassHz + " Hz");
            Check(s.GateAttack <= 2, "the gate opens in time for T and K: " + s.GateAttack + " ms");
        }
        {
            var s = AppSettings.Defaults(); s.HighPassHz = 80;
            var st = Step(VoiceSetup.Kind.Pops); Feed(st, Pops(7, 0), Room(2));
            VoiceSetup.TuneOptional(st, s, null, double.NaN);
            Check(s.HighPassHz == 80, "no thump: the low cut stays where it was");
        }

        Console.WriteLine("Soft sounds");
        {
            var s = AppSettings.Defaults(); s.GateThreshold = -35; s.GateRelease = 50;
            var st = Step(VoiceSetup.Kind.Soft); Feed(st, Esses(7, 3500, 0.03).Select(v => v * 0.4f).ToArray(), Room(2));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(s.GateThreshold < -35 && s.GateRelease >= 200, "the gate lets them through: " + s.GateThreshold + " dB, release " + s.GateRelease + " ms");
        }

        {
            // Noise-like soft sounds are what a noise remover eats: voice protection goes up until they come through.
            var s = AppSettings.Defaults(); s.NsVoiceProtect = 20; s.Strength = 95;
            var x = new float[7 * Fs];
            for (int i = 0; i < x.Length; i++) { double ph = (i / (double)Fs) % 0.6; x[i] = (float)((rnd.NextDouble() * 2 - 1) * (ph < 0.3 ? 0.03 : 0.0005)); }
            var st = Step(VoiceSetup.Kind.Soft); Feed(st, x, Room(2, 0.0005));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(s.NsVoiceProtect > 20, "soft sounds noise removal was eating: voice protection up to " + s.NsVoiceProtect + "%");
        }

        Console.WriteLine("Further away, on its own");
        {
            var s = AppSettings.Defaults(); s.CompThreshold = -22; s.FarVoice = false;
            var st = Step(VoiceSetup.Kind.Far); Feed(st, Hums(8, 0.002), Room(2, 0.0002));
            var r = VoiceSetup.TuneOptional(st, s, null, double.NaN);
            foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(s.FarVoice, "much quieter than your usual voice: Across the room on");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
