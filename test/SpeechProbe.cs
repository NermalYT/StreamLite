using System;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;

// Can Windows' own speech recognizer follow a sentence word by word? A TTS voice reads it into a WAV, the recognizer
// listens with a dictation grammar and prints every partial result as it comes. Usage: run.ps1 SpeechProbe
class SpeechProbe
{
    static void Main()
    {
        foreach (var r in SpeechRecognitionEngine.InstalledRecognizers()) Console.WriteLine("recognizer: " + r.Name + " (" + r.Culture + ")");
        string text = "The quick brown fox jumps over the lazy dog while I talk into my microphone.";
        string wav = Path.Combine(Path.GetTempPath(), "speechprobe.wav");
        using (var tts = new SpeechSynthesizer())
        {
            tts.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            tts.Speak(text);
        }
        var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.Name.StartsWith("en"));
        if (info == null) { Console.WriteLine("no English recognizer"); return; }
        foreach (var kind in new[] { "dictation", "phrase" })
        {
            Console.WriteLine("---- " + kind);
            using (var eng = new SpeechRecognitionEngine(info))
            {
                if (kind == "dictation") eng.LoadGrammar(new DictationGrammar());
                else eng.LoadGrammar(new Grammar(new GrammarBuilder(text.TrimEnd('.'))));
                var t0 = DateTime.Now;
                eng.SpeechHypothesized += (s, e) => Console.WriteLine(string.Format("  {0,5:0} ms  hyp: {1}", (DateTime.Now - t0).TotalMilliseconds, e.Result.Text));
                eng.SpeechRecognized += (s, e) => Console.WriteLine(string.Format("  {0,5:0} ms  FINAL ({2:0.00}): {1}", (DateTime.Now - t0).TotalMilliseconds, e.Result.Text, e.Result.Confidence));
                eng.SetInputToWaveFile(wav);
                try { eng.Recognize(TimeSpan.FromSeconds(20)); } catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
            }
        }
    }
}
