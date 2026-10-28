// Recordings for live tests of Tune to my voice (4.1.1): each setup step's lines read by a Windows voice into a 48 kHz
// mono WAV, for StreamLite's test mic (STREAMLITE_TEST_MIC). The silly step gets a squeaky made-up line.
// Usage: run.ps1 TestMicClips <folder>
using System;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using StreamLite.Audio;

class TestMicClips
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "StreamLite-testmic");
        Directory.CreateDirectory(dir);
        var steps = VoiceSetup.Steps();
        string[] voices = { "Microsoft David Desktop", "Microsoft Zira Desktop" };
        for (int i = 0; i < steps.Length; i++)
        {
            using (var s = new SpeechSynthesizer())
            {
                s.SelectVoice(voices[i % 2]);
                string file = Path.Combine(dir, "step" + (i + 1) + ".wav");
                s.SetOutputToWaveFile(file, new SpeechAudioFormatInfo(48000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                string text = steps[i].Line.Length > 0 ? steps[i].Line : "Ooh hello there, I am a tiny cartoon mouse and I love cheese, cheese, cheese, so much cheese!";
                if (steps[i].Line.Length == 0)
                {
                    var pb = new PromptBuilder();
                    pb.StartStyle(new PromptStyle { Rate = PromptRate.Fast });
                    pb.AppendSsmlMarkup("<prosody pitch=\"x-high\">" + System.Security.SecurityElement.Escape(text) + "</prosody>");
                    pb.EndStyle();
                    s.Speak(pb);
                }
                else s.Speak(text);
                s.SetOutputToNull();
                Console.WriteLine(file + "  " + (new FileInfo(file).Length / 96000.0).ToString("0.0") + " s  (" + steps[i].Title + ")");
            }
        }
    }
}
