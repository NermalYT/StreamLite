// Voice comparison: a processed version 12 dB louder (or 9 dB quieter) than the recording is brought to the
// recording's loudness, the Original plays untouched, the compare engine changes only the test chain, and the
// recording is kept on disk and comes back. Own settings folder (STREAMLITE_DATA). Usage: run.ps1 VoiceCompareTest
using System;
using System.IO;
using StreamLite;
using StreamLite.Audio;

class VoiceCompareTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int Fs = 48000;

    static float[] Voice(int len, int seed)
    {
        var x = new float[len]; var r = new Random(seed);
        for (int i = 0; i < len; i++)
        {
            double t = (double)i / Fs, env = Math.Max(0, Math.Sin(2 * Math.PI * 1.3 * t));   // words and pauses
            double v = 0; for (int k = 1; k < 8; k++) v += Math.Sin(2 * Math.PI * k * 150 * t + k) / k;
            x[i] = (float)(0.05 * env * v + 0.0003 * (r.NextDouble() * 2 - 1));
        }
        return x;
    }

    static double Run(VoiceTest vt, float[] clip, double gainDb, bool original, int seconds)
    {
        vt.Original = original;
        double g = Math.Pow(10, gainDb / 20), last = 0;
        var block = new float[480];
        for (int pos = 0, b = 0; b < seconds * 100; b++)
        {
            for (int i = 0; i < 480; i++) { block[i] = (float)(clip[pos] * (original ? 1 : g)); pos = (pos + 1) % clip.Length; }
            vt.Match(block, 480);
            if (b >= (seconds - 2) * 100) last += VoiceTest.SpeechPower(block, 480);
        }
        return last;
    }

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_voicecompare");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);

        var clip = Voice(6 * Fs, 1);
        var vt = new VoiceTest();
        vt.StartRecording();
        for (int p = 0; p + 144 <= clip.Length; p += 144) { var pk = new float[144]; Array.Copy(clip, p, pk, 0, 144); vt.Feed(pk, 144); }
        vt.StopRecording();
        Check(vt.HasClip && File.Exists(Path.Combine(dir, "voice-sample.wav")), "the recording is kept on this PC (voice-sample.wav)");

        foreach (var db in new[] { 12.0, -9.0 })
        {
            var t = new VoiceTest();   // loads the saved recording
            Run(t, clip, db, false, 8);
            Check(Math.Abs(t.MatchDb + db) < 1.0, "a version " + db.ToString("+0;-0") + " dB off is played " + t.MatchDb.ToString("+0.0;-0.0") + " dB to match the recording");
        }
        var o = new VoiceTest();
        Run(o, clip, 0, true, 3);
        Check(Math.Abs(o.MatchDb) < 0.01, "the Original plays untouched");
        var off = new VoiceTest { MatchLoudness = false };
        Run(off, clip, 12, false, 6);
        Check(Math.Abs(off.MatchDb) < 0.01, "Same loudness off: the version plays at its own level");

        var S = new AppSettings { NsEngine = NoiseEngines.Ai, NoiseRemoval = true };
        var c = new VoiceTest();
        c.Configure(S);
        c.Compare(NoiseEngines.Classic);
        Check(!c.Chain.Suppressor.UseAi && S.NsEngine == NoiseEngines.Ai, "comparing Classic changes only the test chain, not your settings");
        c.Compare("off");
        Check(!c.Chain.Suppressor.Enabled, "comparing 'no noise removal' turns it off in the test chain");
        c.Compare(null);
        Check(c.Chain.Suppressor.Enabled && c.Chain.Suppressor.UseAi, "back to your settings");
        c.DeleteSample();
        Check(!File.Exists(Path.Combine(dir, "voice-sample.wav")) && !c.HasClip, "the recording can be deleted");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
