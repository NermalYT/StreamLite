// Privacy first (4.1.3): no recording of anyone is ever saved - the voice test's recording stays in memory, the voice
// "Remove a specific sound" learns is never written, a voiceprint is saved only while Only my voice is on, and what
// older versions kept by themselves is deleted once. Usage: run.ps1 PrivacyTest
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamLite;
using StreamLite.Audio;

class PrivacyTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_privacy");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        // What an older version left: a voice-test recording, a learned voice and a voiceprint from Tune to my voice.
        foreach (var f in new[] { "voice-sample.wav", "your-voice.bin", "voice-lock-you.bin", "voice-lock-them.bin" }) File.WriteAllBytes(Path.Combine(dir, f), new byte[128]);
        var core = new AppCore(true);
        core.S.VoiceLockOn = false;
        typeof(AppCore).GetMethod("PrivacyCleanup", Any).Invoke(core, null);
        typeof(AppCore).GetMethod("LoadNoisePrint", Any).Invoke(core, null);
        Check(!File.Exists(Path.Combine(dir, "voice-sample.wav")), "a voice-test recording an older version kept is deleted");
        Check(!File.Exists(Path.Combine(dir, "your-voice.bin")), "the voice an older version learned in the background is deleted");
        Check(!File.Exists(Path.Combine(dir, "voice-lock-you.bin")) && !File.Exists(Path.Combine(dir, "voice-lock-them.bin")), "voiceprints saved without Only my voice ever on are deleted");
        Check(core.S.PrivacyCleaned == 1, "that clean-up runs once");

        // A voiceprint from Tune to my voice: in memory while Only my voice is off, saved once it's on.
        var r = new VoiceSetup.Result { Print = Enumerable.Repeat(0.1f, 512).ToArray(), Consistency = 0.9, PrintSeconds = 12, P10 = 0.5f, P25 = 0.6f };
        typeof(AppCore).GetField("pendingPrint", Any).SetValue(core, r);
        core.S.VoiceLockOn = false;
        typeof(AppCore).GetMethod("KeepVoiceprint", Any).Invoke(core, null);
        Check(!File.Exists(Path.Combine(dir, "voice-lock-you.bin")), "Only my voice off: your voiceprint isn't saved");
        core.S.VoiceLockOn = true;
        typeof(AppCore).GetMethod("KeepVoiceprint", Any).Invoke(core, null);
        Check(File.Exists(Path.Combine(dir, "voice-lock-you.bin")), "turning Only my voice on saves it (it's needed every time StreamLite starts)");

        // "Remove a specific sound" off: nothing is listened to; on: your voice is learned in memory only.
        var np = new NoisePrint();
        var buf = new float[480]; var rnd = new Random(3);
        np.Vad = 1;
        for (int b = 0; b < 3000; b++) { for (int i = 0; i < 480; i++) buf[i] = (float)(Math.Sin(i * 0.05 + b) * 0.2 + rnd.NextDouble() * 0.01); np.Process(buf, 480); }
        Check(!np.OwnVoice && GetBank(np) == null, "off: it doesn't collect your voice at all");
        Check(Directory.GetFiles(dir).Select(Path.GetFileName).All(f => f == "voice-lock-you.bin" || f.StartsWith("settings") || f == "health.log"), "nothing else was written next to the settings");

        // The voice test's recording: memory only.
        var vt = new VoiceTest();
        vt.StartRecording();
        for (int p = 0; p < 48000 * 2; p += 144) vt.Feed(new float[144].Select((x, i) => (float)Math.Sin((p + i) * 0.03) * 0.1f).ToArray(), 144);
        vt.StopRecording();
        Check(vt.HasClip && !File.Exists(Path.Combine(dir, "voice-sample.wav")), "the voice test's recording is in memory only");
        vt.DeleteSample();
        Check(!vt.HasClip, "and forgotten when asked");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    static object GetBank(NoisePrint np) { return typeof(NoisePrint).GetField("voiceBank", Any).GetValue(np); }
}
