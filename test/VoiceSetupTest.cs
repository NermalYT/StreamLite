// include: VoiceLockTest.cs
// "Tune StreamLite to your voice" (3.5.2) offline: the five steps fed with Windows' voices (low David, mid Mark, high
// Zira) - a quiet room first, then normal, far (20 dB down, with the room's echo), loud (clipping), quiet (15 dB down) and
// a silly voice (the same voice +35%) - and what the setup tunes: low cut and de-esser from the pitch, auto level,
// Across the room, clipping, voiceprints (far and silly count as you).
// Usage: run.ps1 VoiceSetupTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class VoiceSetupTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static float[] Scaled(float[] x, double db) { double g = Math.Pow(10, db / 20); return x.Select(v => (float)(v * g)).ToArray(); }

    static void Feed(VoiceSetup.Step st, float[] x48, bool silence)
    {
        var b = new float[480];
        for (int i = 0; i + 480 <= x48.Length; i += 480) { Array.Copy(x48, i, b, 0, 480); st.FeedRaw(b, 480, silence); }
    }

    static float[] Down3(float[] x) { var y = new float[x.Length / 3]; for (int i = 0; i < y.Length; i++) y[i] = (x[3 * i] + x[3 * i + 1] + x[3 * i + 2]) / 3f; return y; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string dir = Path.Combine(Path.GetTempPath(), "StreamLite-voices");
        Func<string, int, float[]> line = (v, i) => VoiceLockTest.Wav16Public(Path.Combine(dir, v + "-" + i.ToString("00") + ".wav"));
        var rnd = new Random(3);
        var expect = new Dictionary<string, int> { { "David", 80 }, { "Mark", 100 }, { "Zira", 120 } };
        foreach (var voice in new[] { "David", "Mark", "Zira" })
        {
            var steps = VoiceSetup.Steps();
            Func<VoiceSetup.Kind, VoiceSetup.Step> st = k => steps.First(x => x.Kind == k);
            Func<int[], double, bool, float[]> say = (idx, db, room) =>
            {
                var l16 = idx.SelectMany(i => line(voice, i).Concat(new float[3200])).ToArray();
                var x48 = Scaled(VoiceLockTest.Up3Public(l16), db);
                return room ? VoiceLockTest.InRoomPublic(x48, 0, rnd) : x48;
            };
            // A quiet room (-62 dBFS of soft noise), then each step fed as the raw mic at 48 kHz: the setup takes the levels
            // and the voice itself from that (3.5.6 - nothing from StreamLite's processing).
            var normal = st(VoiceSetup.Kind.Normal);
            Feed(normal, Enumerable.Range(0, 96000).Select(i => (float)((rnd.NextDouble() * 2 - 1) * 0.0014)).ToArray(), true);
            var n48 = say(new[] { 0, 1, 2 }, 0, false); Feed(normal, n48, false); normal.TakeVoice(); normal.Done = true;
            var f48 = say(new[] { 3, 4 }, -20, true); var far = st(VoiceSetup.Kind.Far); Feed(far, f48, false); far.TakeVoice(); far.Done = true;
            var l48 = say(new[] { 5 }, 10, false); for (int i = 0; i < l48.Length; i++) l48[i] = Math.Max(-1f, Math.Min(1f, l48[i]));
            var loud = st(VoiceSetup.Kind.Loud); Feed(loud, l48, false); loud.TakeVoice(); loud.Done = true;
            var q48 = say(new[] { 6, 7 }, -15, false); var quiet = st(VoiceSetup.Kind.Quiet); Feed(quiet, q48, false); quiet.TakeVoice(); quiet.Done = true;
            var silly = st(VoiceSetup.Kind.Silly);
            var s16 = new[] { 8, 9 }.SelectMany(i => VoiceLockTest.Wav16Public(Path.Combine(dir, voice + "High-" + i.ToString("00") + ".wav"))).ToArray();
            var s48 = VoiceLockTest.Up3Public(s16); Feed(silly, s48, false); silly.TakeVoice(); silly.Done = true;

            var s = AppSettings.Defaults();
            using (var lk = new VoiceLock())
            {
                var r = VoiceSetup.Apply(steps, s, lk);
                Console.WriteLine(voice + ":");
                foreach (var l in r.Lines) Console.WriteLine("    " + l);
                Check(normal.Voice16.Length > 16000 * 4 && quiet.Voice16.Length > 16000 * 2, voice + ": the voice comes from the raw mic (" + (normal.Voice16.Length / 16000.0).ToString("0.0") + " s normal, " + (quiet.Voice16.Length / 16000.0).ToString("0.0") + " s quiet)");
                Check(s.HighPassHz == expect[voice], voice + ": low cut " + s.HighPassHz + " Hz for a " + r.PitchHz.ToString("0") + " Hz voice");
                Check(s.DeEssFreq == (r.PitchHz < 160 ? 6000 : 7000), voice + ": de-esser at " + s.DeEssFreq + " Hz");
                Check(s.FarVoice, voice + ": 20 dB quieter from further away -> Across the room on");
                Check(r.Clipping, voice + ": the loud take clipped -> said so");
                Check(s.AutoLevelMaxBoost >= 20 && s.AutoLevelMaxBoost <= 40, voice + ": auto level up to " + s.AutoLevelMaxBoost + " dB");
                Check(s.DeHumHz == 0 && !s.Gate && s.Strength <= 70, voice + ": a quiet room without hum -> no de-hum, no gate, noise removal at " + s.Strength + "%");
                Check(r.Print != null && r.AlsoYou.Count == 2, voice + ": voiceprint from normal, loud and quiet, plus far and silly (" + r.AlsoYou.Count + ")");
            }
        }
        // A noisy room with mains hum (a fan 20 dB under the voice, 60 Hz hum): de-hum on, noise removal harder, the gate on
        // and well under the quiet voice; a quiet room (above): no gate, gentler noise removal.
        {
            var steps = VoiceSetup.Steps();
            var normal = steps[0];
            var room = Enumerable.Range(0, 96000).Select(i => (float)((rnd.NextDouble() * 2 - 1) * 0.03 + 0.02 * Math.Sin(2 * Math.PI * 60 * i / 48000.0) + 0.008 * Math.Sin(2 * Math.PI * 180 * i / 48000.0))).ToArray();
            Feed(normal, room, true);
            var n48 = VoiceLockTest.Up3Public(new[] { 0, 1, 2 }.SelectMany(i => line("Mark", i)).ToArray());
            var noisy = n48.Select((v, i) => v + room[i % room.Length]).ToArray();
            Feed(normal, noisy, false); normal.TakeVoice(); normal.Done = true;
            var q48 = Scaled(VoiceLockTest.Up3Public(new[] { 6, 7 }.SelectMany(i => line("Mark", i)).ToArray()), -12);
            var quiet = steps[3]; Feed(quiet, q48.Select((v, i) => v + room[i % room.Length]).ToArray(), false); quiet.TakeVoice(); quiet.Done = true;
            steps[1].Skipped = true; steps[4].Skipped = true;
            var s = AppSettings.Defaults();
            var r = VoiceSetup.Apply(steps, s, null);
            Console.WriteLine("Noisy room with hum:"); foreach (var l in r.Lines) Console.WriteLine("    " + l);
            Check(s.DeHumHz == 60, "60 Hz hum heard in the room -> de-hum at " + s.DeHumHz + " Hz");
            Check(s.Strength >= 85, "a noisy room -> noise removal at " + s.Strength + "%");
            Check(s.Gate && s.GateThreshold <= VoiceSetup.CleanDb(quiet.Voice16) - 14, "noisy room -> gate on, at " + s.GateThreshold + " dB, 15 dB under the quiet voice (" + VoiceSetup.CleanDb(quiet.Voice16).ToString("0") + " dB)");
            Check(s.Compressor && s.CompThreshold < VoiceSetup.CleanDb(normal.Voice16), "compressor from just under the normal voice (" + s.CompThreshold + " dB)");
        }
        // Without the optional steps: nothing about far voices changes, still a voiceprint.
        {
            var steps = VoiceSetup.Steps();
            var normal = steps[0];
            Feed(normal, Enumerable.Range(0, 96000).Select(i => (float)((rnd.NextDouble() * 2 - 1) * 0.0014)).ToArray(), true);
            var n48 = VoiceLockTest.Up3Public(new[] { 0, 1, 2, 3 }.SelectMany(i => line("Mark", i)).ToArray()); Feed(normal, n48, false); normal.TakeVoice(); normal.Done = true;
            steps[1].Skipped = true; steps[4].Skipped = true;
            var s = AppSettings.Defaults(); s.FarVoice = false;
            using (var lk = new VoiceLock())
            {
                var r = VoiceSetup.Apply(steps, s, lk);
                Check(!s.FarVoice && r.Print != null && r.AlsoYou.Count == 0, "far and silly skipped: Across the room untouched, a voiceprint all the same");
            }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
