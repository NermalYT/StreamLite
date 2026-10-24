// Multi-channel mics: Auto must use only the inputs that carry the voice (interfaces, out-of-phase laptop mics),
// keep both channels of a stereo mic, switch without clicks, and honour the fixed / mix choices.
// Usage: run.ps1 InputChannelsTest
using System;
using StreamLite.Audio;

class InputChannelsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    // A voice-like signal: noise shaped into syllables (4 per second), about -25 dBFS while "talking".
    static float Voice(Random r, long t)
    {
        double env = Math.Max(0, Math.Sin(2 * Math.PI * 4 * t / 48000.0));
        return (float)((r.NextDouble() * 2 - 1) * 0.1 * env);
    }

    /// <summary>Runs seconds of audio; fill(frame, channel) gives each sample. Returns the output RMS (dBFS) of the last second and the largest jump between samples.</summary>
    static double Run(InputChannels ic, int ch, double seconds, Func<long, int, float> fill, out float maxJump, long start = 0)
    {
        int B = 480;
        var inter = new float[B * ch]; var mono = new float[B];
        long frames = (long)(seconds * 48000);
        double e = 0; long n = 0; maxJump = 0; float prev = 0;
        for (long t = 0; t < frames; t += B)
        {
            for (int i = 0; i < B; i++) for (int c = 0; c < ch; c++) inter[i * ch + c] = fill(start + t + i, c);
            ic.Process(inter, B, ch, mono);
            for (int i = 0; i < B; i++)
            {
                if (t + i >= frames - 48000) { e += mono[i] * mono[i]; n++; }
                maxJump = Math.Max(maxJump, Math.Abs(mono[i] - prev)); prev = mono[i];
            }
        }
        return 10 * Math.Log10(e / Math.Max(1, n) + 1e-20);
    }

    static double Level(Func<long, float> f)
    {
        double e = 0; for (long t = 0; t < 48000; t++) { float x = f(t); e += x * x; }
        return 10 * Math.Log10(e / 48000 + 1e-20);
    }

    static void Main()
    {
        var r = new Random(1);
        var voice = new float[48000 * 6];
        for (int i = 0; i < voice.Length; i++) voice[i] = Voice(r, i);
        var noise = new float[voice.Length];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((r.NextDouble() * 2 - 1) * 0.00005);   // an empty input's hiss (-90 dB)
        Func<long, float> V = t => voice[t % voice.Length];
        Func<long, float> N = t => noise[t % noise.Length];
        double vDb = Level(V);
        float jump;

        // 1) Audio interface: XLR mic on input 1, nothing on input 2.
        var ic = new InputChannels();
        double outDb = Run(ic, 2, 4, (t, c) => c == 0 ? V(t) : N(t), out jump);
        Check(ic.InUse == "1", "interface, mic on input 1: auto uses input " + ic.InUse);
        Check(Math.Abs(outDb - vDb) < 1, "...at full level (" + outDb.ToString("0.0") + " dB, voice " + vDb.ToString("0.0") + " dB; Windows' mix would be " + (vDb - 6).ToString("0.0") + ")");
        Check(jump < 0.25f, "...no click when it switches (largest step " + jump.ToString("0.000") + ")");
        Console.WriteLine("     " + ic.Describe());

        // 2) Mic on input 2.
        ic = new InputChannels();
        Run(ic, 2, 4, (t, c) => c == 1 ? V(t) : N(t), out jump);
        Check(ic.InUse == "2", "interface, mic on input 2: auto uses input " + ic.InUse);

        // 3) Stereo USB mic / headset: both channels carry the voice.
        ic = new InputChannels();
        outDb = Run(ic, 2, 4, (t, c) => V(t) * (c == 0 ? 1f : 0.9f), out jump);
        Check(ic.InUse == "1+2", "stereo mic: auto keeps both channels (" + ic.InUse + ")");
        Check(Math.Abs(outDb - (vDb + 20 * Math.Log10(0.95))) < 1, "...level unchanged (" + outDb.ToString("0.0") + " dB)");

        // 4) Laptop mic with an out-of-phase second channel: mixing would cancel the voice.
        ic = new InputChannels();
        outDb = Run(ic, 2, 4, (t, c) => c == 0 ? V(t) : -0.95f * V(t), out jump);
        Check(ic.InUse == "1", "out-of-phase channel: auto drops it (" + ic.InUse + ")");
        Check(outDb > vDb - 3, "...the voice doesn't cancel (" + outDb.ToString("0.0") + " dB; mixed it would be " + (vDb + 20 * Math.Log10(0.025)).ToString("0.0") + ")");

        // 5) 4-input interface, mic on input 3, a quiet line on input 1.
        ic = new InputChannels();
        Run(ic, 4, 4, (t, c) => c == 2 ? V(t) : c == 0 ? V(t + 7777) * 0.01f : N(t + c * 1000), out jump);
        Check(ic.InUse == "3", "4-input interface, mic on input 3: auto uses " + ic.InUse);

        // 6) Nobody talking yet: keeps mixing (no wrong guess from hiss).
        ic = new InputChannels();
        Run(ic, 2, 3, (t, c) => N(t + c * 333), out jump);
        Check(ic.InUse == "1+2", "silence: no decision yet (" + ic.InUse + ")");

        // 7) Fixed choices.
        ic = new InputChannels { Mode = "2" };
        outDb = Run(ic, 2, 2, (t, c) => c == 1 ? V(t) : 0f, out jump);
        Check(ic.InUse == "2" && Math.Abs(outDb - vDb) < 1, "Input 2 chosen by hand: just input 2");
        ic = new InputChannels { Mode = InputChannels.Mix };
        outDb = Run(ic, 2, 2, (t, c) => c == 0 ? V(t) : 0f, out jump);
        Check(ic.InUse == "1+2" && Math.Abs(outDb - (vDb - 6)) < 1, "Mix: average of both (" + outDb.ToString("0.0") + " dB)");

        // 8) The mic moves to the other input (cable swapped): auto follows within a few seconds.
        ic = new InputChannels();
        Run(ic, 2, 3, (t, c) => c == 0 ? V(t) : N(t), out jump);
        Run(ic, 2, 4, (t, c) => c == 1 ? V(t) : N(t), out jump, 3 * 48000);
        Check(ic.InUse == "2", "cable moved to input 2: auto follows within 4 s (" + ic.InUse + ")");
        // ...and long pauses while talking don't make it change its mind.
        ic = new InputChannels();
        Run(ic, 2, 3, (t, c) => c == 0 ? V(t) : N(t), out jump);
        Run(ic, 2, 6, (t, c) => c == 0 ? (t % 144000 < 48000 ? V(t) : N(t + 5)) : N(t), out jump, 3 * 48000);
        Check(ic.InUse == "1", "talking with 2 s pauses: stays on input 1 (" + ic.InUse + ")");

        // 9) Mono mic: straight through.
        ic = new InputChannels();
        var m = new float[] { 0.1f, -0.2f, 0.3f }; var o = new float[3];
        ic.Process(m, 3, 1, o);
        Check(o[0] == 0.1f && o[1] == -0.2f && o[2] == 0.3f, "mono mic passes straight through");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
