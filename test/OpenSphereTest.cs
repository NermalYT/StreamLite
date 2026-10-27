// Open sphere (3.5.7): no walls, no muffling.
//  1. Demuffle: footsteps a game muffled behind a wall or floor get their treble back; open footsteps, loud sounds
//     and silence are left alone; every speaker gets the same lift (directions don't move); off = untouched.
//  2. The floor-below cue keeps its treble (it used to roll it off like steps through a floor).
//  3. Open sphere in the settings: no room reflections, demuffle on (films: off).
// Usage: run.ps1 OpenSphereTest
using System;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class OpenSphereTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    const int B = SpatialProcessor.B, Fs = SpatialProcessor.Fs;

    // Plays a footstep (in front-left and, half as loud, left) through Demuffle; the largest lift, and the output.
    static float Run(float[] step, float gain, float strength, out float[][] outs)
    {
        var d = new Demuffle(SpatialProcessor.Slots) { Strength = strength };
        int blocks = (step.Length + Fs / 5) / B;
        outs = new[] { new float[blocks * B], new float[blocks * B] };
        float most = 0;
        for (int k = 0; k < blocks; k++)
        {
            var x = Enumerable.Range(0, SpatialProcessor.Slots).Select(_ => new float[B]).ToArray();
            for (int i = 0; i < B; i++) { int j = k * B + i; float v = j < step.Length ? step[j] * gain : 0; x[SpatialProcessor.FL][i] = v; x[SpatialProcessor.SL][i] = v * 0.5f; }
            d.Process(x, B);
            most = Math.Max(most, d.LiveLiftDb);
            Array.Copy(x[SpatialProcessor.FL], 0, outs[0], k * B, B); Array.Copy(x[SpatialProcessor.SL], 0, outs[1], k * B, B);
        }
        return most;
    }

    static float[] Muffle(float[] s)
    {
        var a = Biquad.LowPass(900, Fs); var b = Biquad.LowPass(900, Fs);
        return s.Select(v => b.Process(a.Process(v))).ToArray();
    }

    static double DetailDb(float[] s)
    {
        // Steep (48 dB/octave), so the muffled step's strong lows don't leak in.
        var h = Enumerable.Range(0, 4).Select(_ => Biquad.HighPass(2500, Fs)).ToArray(); var l = Biquad.LowPass(8000, Fs); double e = 0;
        foreach (var v in s) { float y = v; foreach (var f in h) y = f.Process(y); y = l.Process(y); e += y * y; }
        return 10 * Math.Log10(e + 1e-15);
    }

    static double Db(float[] s) { return 10 * Math.Log10(s.Sum(v => (double)v * v) + 1e-15); }

    static void Main()
    {
        var bank = FootstepSynth.Bank(10, 23);
        float worstOpen = 0, leastMuffled = 99, loudLift = 0; double leastGain = 99, worstPan = 0; bool same = true;
        foreach (var step in bank)
        {
            float peak = step.Max(v => Math.Abs(v));
            float[][] o;
            worstOpen = Math.Max(worstOpen, Run(step, 0.05f / peak, 1, out o));
            var m = Muffle(step); float mp = m.Max(v => Math.Abs(v));
            leastMuffled = Math.Min(leastMuffled, Run(m, 0.05f / mp, 1, out o));
            leastGain = Math.Min(leastGain, DetailDb(o[0]) - DetailDb(m.Select(v => v * 0.05f / mp).ToArray()));
            worstPan = Math.Max(worstPan, Math.Abs(Db(o[0]) - Db(o[1]) - 20 * Math.Log10(2)));
            loudLift = Math.Max(loudLift, Run(m, 0.9f / mp, 1, out o));
            float[][] off; Run(m, 0.05f / mp, 0, out off);
            for (int i = 0; i < m.Length && same; i++) if (off[0][i] != m[i] * (0.05f / mp)) same = false;
        }
        Check(worstOpen <= 3, "an open footstep is left as it is (lift at most " + worstOpen.ToString("0.0") + " dB)");
        Check(leastMuffled >= 8, "a footstep muffled behind a wall gets its treble back (lift at least " + leastMuffled.ToString("0.0") + " dB)");
        Check(leastGain >= 6, "its 2.5-8 kHz detail comes back by at least " + leastGain.ToString("0.0") + " dB");
        Check(worstPan < 0.1, "every speaker gets the same lift: where it is doesn't move (" + worstPan.ToString("0.00") + " dB)");
        Check(loudLift == 0, "loud sounds (gunfire) aren't brightened (" + loudLift.ToString("0.0") + " dB)");
        Check(same, "off: the sound is untouched, sample for sample");
        float[][] sil; float silLift = Run(new float[Fs], 1, 1, out sil);
        Check(silLift == 0 && sil[0].All(v => v == 0), "silence stays silent");

        // Steps behind a wall over a steady tone: the lift comes and goes without clicks.
        {
            var d = new Demuffle(SpatialProcessor.Slots) { Strength = 1 };
            var m = Muffle(bank[0]); float mp = m.Max(v => Math.Abs(v));
            double worstJump = 0, prev1 = 0, prev2 = 0; int n = 0;
            for (int k = 0; k < Fs * 3 / B; k++)
            {
                var x = Enumerable.Range(0, SpatialProcessor.Slots).Select(_ => new float[B]).ToArray();
                for (int i = 0; i < B; i++)
                {
                    int j = (k * B + i) % (Fs / 2);
                    x[0][i] = (float)(0.01 * Math.Sin(2 * Math.PI * 200 * (k * B + i) / Fs)) + (j < m.Length ? m[j] * 0.05f / mp : 0);
                }
                var before = (float[])x[0].Clone();
                d.Process(x, B);
                for (int i = 0; i < B; i++)
                {
                    double e = x[0][i] - before[i];   // what the shelf added
                    if (n++ > 2) worstJump = Math.Max(worstJump, Math.Abs(e - 2 * prev1 + prev2));
                    prev2 = prev1; prev1 = e;
                }
            }
            Check(worstJump < 0.02, "no clicks as the lift changes (largest second difference " + worstJump.ToString("0.0000") + ")");
        }

        // The floor-below cue: nothing muffled any more.
        var below = FloorCues.Profile(-45, 1).AllFilters();
        var pre = FloorCues.Profile(-45, 1).Preamp;
        double hi = Enumerable.Range(0, 5).Select(i => Biquad.ResponseDb(below, 2500 + i * 500, Fs)).Average() + pre;
        double air = Biquad.ResponseDb(below, 12000, Fs) + pre;
        Check(hi > -3 && air > -3, "below: the treble stays (2.5-4.5 kHz " + hi.ToString("+0.0;-0.0") + " dB, 12 kHz " + air.ToString("+0.0;-0.0") + " dB)");
        double notch = Biquad.ResponseDb(below, 5800, Fs) + pre, up = Biquad.ResponseDb(FloorCues.Profile(45, 1).AllFilters(), 8000, Fs) + FloorCues.Profile(45, 1).Preamp;
        Check(notch < -6 && up > 6, "below and above still sound different (5.8 kHz notch " + notch.ToString("0.0") + " dB below, 8 kHz " + up.ToString("+0.0") + " dB above)");

        // The setting.
        var g = new GameAudioSettings(); g.Repair();
        var c = g.ToConfig();
        Check(g.OpenSphere && c.Room == 0 && c.Demuffle > 0.99, "open sphere is on by default: no room (walls), demuffle on");
        g.OpenSphere = false; g.Room = 20; c = g.ToConfig();
        Check(Math.Abs(c.Room - 0.2) < 1e-9 && c.Demuffle == 0, "off: your room setting and no demuffle, as before");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
