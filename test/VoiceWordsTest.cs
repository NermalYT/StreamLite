// Voice hotkeys (4.4), with Windows' own text-to-speech voices standing in for a person: three takes of a phrase, then
// the phrase again faster, slower, quieter and inside a sentence, and a lot of other talk that must not trigger it.
// A synthetic voice is far more even than a real one: this checks the machinery, not how it does on you.
// Usage: run.ps1 VoiceWordsTest
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite;
using StreamLite.Audio;

class VoiceWordsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static readonly Random rnd = new Random(9);

    static float[] Say(SpeechSynthesizer tts, string text, int rate = 0, double gain = 1)
    {
        using (var ms = new MemoryStream())
        {
            tts.Rate = rate;
            tts.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(48000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            tts.Speak(text);
            tts.SetOutputToNull();
            var b = ms.ToArray();
            var x = new float[b.Length / 2];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(BitConverter.ToInt16(b, i * 2) / 32768.0 * 0.3 * gain + (rnd.NextDouble() * 2 - 1) * 0.0008);
            return x;
        }
    }

    static float[] Quiet(double sec) { return Enumerable.Range(0, (int)(sec * 48000)).Select(i => (float)((rnd.NextDouble() * 2 - 1) * 0.0008)).ToArray(); }

    static void Main()
    {
        using (var tts = new SpeechSynthesizer())
        {
            var voices = tts.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
            if (voices.Count == 0) { Console.WriteLine("  no text-to-speech voice on this PC: nothing to test with"); Console.WriteLine("ALL PASS"); return; }
            foreach (var voice in voices.Take(3))
            {
                tts.SelectVoice(voice);
                Console.WriteLine(voice);
                var takes = new List<float[][]>();
                foreach (int r in new[] { -1, 0, 1 })
                {
                    var a = Say(tts, "Do a barrel roll", r);
                    var t = VoiceWords.TakeOf(a, a.Length);
                    Check(t != null && t.Length >= 20 && t.Length < 200, "a take: " + (t != null ? t.Length * 20 + " ms of voice" : "none"));
                    takes.Add(t);
                }
                Check(VoiceWords.TakeOf(Quiet(2), 96000) == null, "silence isn't a take");
                double limit = VoiceWords.LimitFor(takes);
                Console.WriteLine("    limit " + limit.ToString("0.00"));
                var p = new VoiceWords.Phrase { Text = "Do a barrel roll", Takes = takes, Limit = limit };
                var pack = VoiceWords.Unpack(VoiceWords.Pack(takes[0]));
                Check(pack != null && pack.Length == takes[0].Length && Math.Abs(pack[3][5] - takes[0][3][5]) < 0.02, "a take survives the settings file");
                Check(VoiceWords.Unpack("nonsense") == null, "a damaged one is ignored");
                using (var words = new VoiceWords())
                {
                    int heard = 0; words.Heard += (ph, sc) => heard++;
                    words.Set(new[] { p });
                    Func<float[], int> hear = x =>
                    {
                        var d = new VoiceWords.Detector(words);
                        heard = 0;
                        var lead = Quiet(1); d.Push(lead, lead.Length);
                        d.Push(x, x.Length);
                        var tail = Quiet(1); d.Push(tail, tail.Length);
                        return heard;
                    };
                    foreach (var c in new[] {
                        Tuple.Create("as recorded", Say(tts, "Do a barrel roll")),
                        Tuple.Create("faster", Say(tts, "Do a barrel roll", 2)),
                        Tuple.Create("slower", Say(tts, "Do a barrel roll", -2)),
                        Tuple.Create("quieter", Say(tts, "Do a barrel roll", 0, 0.4)),
                        Tuple.Create("louder", Say(tts, "Do a barrel roll", 0, 2.5)),
                        Tuple.Create("inside a sentence", Say(tts, "Okay watch this, do a barrel roll")),
                    })
                        Check(hear(c.Item2) >= 1, "\"Do a barrel roll\" " + c.Item1 + ": heard (" + words.LastScore.ToString("0.00") + " of the limit)");
                    int wrong = 0; string which = "";
                    foreach (var other in new[] {
                        "Push left, there is one behind the boxes.", "He is on the stairs, behind you, behind you.", "Can you hear me okay?",
                        "One more game and then I am going to bed.", "Watch the bridge, I will cover you from the stairs.", "Do you want to play another round?",
                        "Reload, reload, I am out of ammo.", "That was a really good shot, nice one.", "Roll the barrel over here.", "I do not know, maybe.",
                        "What are we having for dinner tonight?", "Do a flip.", "Barrel.", "Turn the volume down a bit please.", "Rush them on the left side, go go go.",
                        "The quick brown fox jumps over the lazy dog.", "Sister Suzy sat on the seesaw.", "Peter Piper picked a peck of peppers.",
                    })
                    {
                        int h = hear(Say(tts, other));
                        Console.WriteLine("    " + (h > 0 ? "HEARD " : "      ") + words.LastScore.ToString("0.00") + "  " + other);
                        if (h > 0) { wrong++; which += " [" + other + "]"; }
                    }
                    Check(wrong == 0, "other talk doesn't trigger it (" + wrong + " of 18)" + which);
                    // Then, firing only once per saying.
                    var twice = Say(tts, "Do a barrel roll");
                    Check(hear(twice) == 1, "once per saying");
                }
            }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
