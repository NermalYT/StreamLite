// The teleprompter of Tune to my voice (4.1): Windows' recognizer follows each setup line word by word as a TTS voice
// reads it into WordFollower in real time (10 ms packets, as the mic delivers them) - also with a misread word - and
// the teleprompter's pictures (white words still to read, green ones heard, the text gliding up a line). Pictures in
// test\shots\teleprompter. Usage: run.ps1 TeleprompterTest [fast]
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.UI;

class TeleprompterTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>When each word ends in the last Read (ms into the recording).</summary>
    static System.Collections.Generic.List<double> wordEnds = new System.Collections.Generic.List<double>();

    /// <summary>A TTS voice reading text, 48 kHz mono floats (with half a second of quiet after it).</summary>
    static float[] Read(string text, string voice)
    {
        string wav = Path.Combine(Path.GetTempPath(), "teleprompter-test.wav");
        wordEnds.Clear();
        using (var tts = new SpeechSynthesizer())
        {
            try { if (voice != null) tts.SelectVoice(voice); } catch { }
            tts.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(48000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            // A word starts where the one before it ended: each start is the end of the word before.
            tts.Speak(text);
        }
        var b = File.ReadAllBytes(wav);
        int data = 12; while (data + 8 < b.Length && System.Text.Encoding.ASCII.GetString(b, data, 4) != "data") data += 8 + BitConverter.ToInt32(b, data + 4);
        int n = BitConverter.ToInt32(b, data + 4) / 2;
        var x = new float[n + 24000];
        for (int i = 0; i < n; i++) x[i] = BitConverter.ToInt16(b, data + 8 + 2 * i) / 32768f * 0.5f;
        // Where each word ends, roughly: spread evenly over the voiced part (first to last sound).
        int first = 0, last = n - 1; while (first < n && Math.Abs(x[first]) < 0.01f) first++; while (last > first && Math.Abs(x[last]) < 0.01f) last--;
        int words = WordFollower.Words(text).Length;
        for (int i = 0; i < words; i++) wordEnds.Add((first + (last - first) * (i + 1.0) / words) / 48.0);
        return x;
    }

    /// <summary>Feeds a recording to the follower in 10 ms packets at speed (1 = real time); returns words heard and the time it took to hear the last one after the audio ended (ms).</summary>
    static System.Collections.Generic.List<double> lags = new System.Collections.Generic.List<double>();

    static int Follow(WordFollower f, float[] x, double speed, out double lagMs)
    {
        var sw = Stopwatch.StartNew(); var block = new float[480];
        int seen = 0;
        for (int i = 0; i + 480 <= x.Length; i += 480)
        {
            // Each word turning green, against when it was spoken (its end, plus the next word's start = its end).
            int h = f.Heard;
            for (; seen < h; seen++) if (seen < wordEnds.Count) lags.Add(sw.Elapsed.TotalMilliseconds * speed - wordEnds[seen]);
            Array.Copy(x, i, block, 0, 480);
            double e = 0; foreach (var v in block) e += v * v;
            f.Feed(block, 480, 10 * Math.Log10(e / 480 + 1e-12) > -50);
            double due = (i + 480) / 48.0 / speed;
            int wait = (int)(due - sw.Elapsed.TotalMilliseconds); if (wait > 0) Thread.Sleep(wait);
        }
        var end = sw.Elapsed.TotalMilliseconds;
        while (f.Heard < f.Count && sw.Elapsed.TotalMilliseconds - end < 4000) Thread.Sleep(20);
        lagMs = sw.Elapsed.TotalMilliseconds - end;
        return f.Heard;
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool fast = args.Contains("fast");
        // Words as the teleprompter and the recognizer count them.
        Check(WordFollower.Words("Hey, can you hear me okay? I'm here - push left!").SequenceEqual(new[] { "hey", "can", "you", "hear", "me", "okay", "i'm", "here", "push", "left" }), "words: punctuation and the dash don't count, I'm is one word");
        Check(WordFollower.Same("stairs", "stair") && WordFollower.Same("boxes", "boxs") && !WordFollower.Same("left", "right") && !WordFollower.Same("me", "my"), "a word heard a letter off still counts, a different word doesn't");

        var steps = VoiceSetup.Steps();
        Check(steps.Where(s => s.Line.Length > 0).All(s => !s.Line.Contains("-") && !s.Line.Any(char.IsDigit)), "the scripts have no dashes or numbers (each word on screen = one word heard)");
        string[] voices = { "Microsoft David Desktop", "Microsoft Zira Desktop" };
        int k = 0;
        foreach (var st in steps.Where(s => s.Line.Length > 0))
        {
            string voice = voices[k++ % voices.Length];
            using (var f = new WordFollower(st.Line))
            {
                if (f.Estimating) { Check(false, "Windows' speech recognizer didn't start: " + f.Problem); break; }
                double lag; int got = Follow(f, Read(st.Line, voice), fast ? 3 : 1, out lag);
                Check(got == f.Count, st.Kind + " (" + voice.Replace("Microsoft ", "").Replace(" Desktop", "") + "): " + got + " of " + f.Count + " words heard" + (got == f.Count ? ", the last one " + lag.ToString("0") + " ms after the voice stopped" : " - last heard: " + f.LastHeard));
            }
        }
        if (lags.Count > 10)
        {
            lags.Sort();
            double med = lags[lags.Count / 2], p90 = lags[(int)(lags.Count * 0.9)];
            Console.WriteLine("  words turned green about " + med.ToString("0") + " ms after they were spoken (median), " + p90.ToString("0") + " ms for 9 in 10");
            Check(med < 900 && med > -400, "words turn green while you read (median " + med.ToString("0") + " ms after the word, under 0.9 s)");
        }
        // A misread word (and a swallowed one): the rest still counts.
        string script = steps[0].Line, misread = script.Replace("boxes", "crates").Replace(" so it knows", " it knows");
        using (var f = new WordFollower(script))
        {
            double lag; int got = Follow(f, Read(misread, voices[0]), fast ? 3 : 1, out lag);
            Check(got >= f.Count - 1, "read with one word wrong and one left out: " + got + " of " + f.Count + " (the step still finishes)");
        }

        // The teleprompter's pictures: nothing heard, half way (the text has glided up), done.
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\teleprompter"); Directory.CreateDirectory(dir);
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ClientSize = new Size(Theme.Px(852), Theme.Px(230)), ShowInTaskbar = false };
        var p = new Teleprompter { Dock = DockStyle.Fill };
        form.Controls.Add(p); form.Show(); Application.DoEvents();
        p.Script = script;
        int words = p.WordCount;
        p.Run(true);
        foreach (int h in new[] { 0, 9, 21, words })
        {
            p.Heard = h;
            var until = DateTime.Now.AddMilliseconds(600);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(5); }
            using (var b = new Bitmap(p.Width, p.Height))
            {
                p.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(Path.Combine(dir, "heard-" + h + ".png"));
                int green = 0, white = 0;
                for (int y = 0; y < b.Height; y += 2) for (int x = 0; x < b.Width; x += 2) { var c = b.GetPixel(x, y); if (c.G > 90 && c.G > c.R + 40 && c.G > c.B + 25) green++; else if (c.R > 200 && c.G > 200 && c.B > 200) white++; }
                bool ok = h == 0 ? green == 0 && white > 0 : h == words ? green > 0 : green > 0 && white > 0;
                Check(ok, h + " of " + words + " words heard: " + (h == 0 ? "all white" : h == words ? "all green" : "green up to there, white after") + " (" + green + " green, " + white + " white pixels)");
            }
        }
        // Smoothness: a frame takes well under a frame's time.
        var sw = Stopwatch.StartNew(); int frames = 0;
        using (var b = new Bitmap(p.Width, p.Height)) using (var g = Graphics.FromImage(b))
            while (sw.ElapsedMilliseconds < 500) { p.Heard = (frames / 20) % words; var m = typeof(Control).GetMethod("OnPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); m.Invoke(p, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, b.Width, b.Height)) }); frames++; }
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        Check(ms < 4, "a frame draws in " + ms.ToString("0.00") + " ms (room for 240 frames a second)");
        p.Run(false); form.Close();
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
