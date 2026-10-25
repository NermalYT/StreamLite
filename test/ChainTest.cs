using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using StreamLite;
using StreamLite.Audio;

// End-to-end checks of the voice chain (the same steps the live mic runs) on synthesized speech + room noise:
// bypass transparency, level/noise results per settings variant, clipping, NaNs, CPU, settings changed while
// running (the voice test's "adjust while it plays"), and the voice-test recorder.
class ChainTest
{
    const int Fs = 48000, Block = 480;
    static int failures;

    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

    static float[] Tts(string text)
    {
        using (var synth = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            var b = ms.ToArray();
            var f = new float[b.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, 2 * i) / 32768f;
            return f;
        }
    }

    static float[] Pink(int len, Random rnd, double dbfs)
    {
        var o = new float[len]; double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, e = 0;
        for (int i = 0; i < len; i++)
        {
            double w = rnd.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double p = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362; b6 = w * 0.115926;
            double t = (double)i / Fs;
            o[i] = (float)(p + 0.4 * Math.Sin(2 * Math.PI * 60 * t) + 0.2 * Math.Sin(2 * Math.PI * 120 * t)); e += o[i] * o[i];
        }
        float g = (float)(Math.Pow(10, dbfs / 20) / Math.Sqrt(e / len));
        for (int i = 0; i < len; i++) o[i] *= g;
        return o;
    }

    static double Rms(float[] a, int s, int e) { double q = 0; for (int i = s; i < e; i++) q += a[i] * a[i]; return Math.Sqrt(q / Math.Max(1, e - s)); }
    static double Db(double v) { return 20 * Math.Log10(Math.Max(v, 1e-9)); }

    static float[] Run(VoiceChain c, float[] x, bool bypass, out double msPerSec)
    {
        var y = (float[])x.Clone();
        var blk = new float[Block];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i + Block <= y.Length; i += Block)
        {
            Array.Copy(y, i, blk, 0, Block);
            c.Process(blk, Block, bypass);
            Array.Copy(blk, 0, y, i, Block);
        }
        msPerSec = sw.Elapsed.TotalMilliseconds / (x.Length / (double)Fs);
        return y;
    }

    /// <summary>Lag (samples) that best aligns y to x, by envelope correlation.</summary>
    static int Lag(float[] x, float[] y, int max)
    {
        int best = 0; double bv = double.MinValue;
        for (int lag = 0; lag <= max; lag += 1)
        {
            double s = 0;
            for (int i = Fs; i < x.Length - max - 1; i += 7) s += x[i] * y[i + lag];
            if (s > bv) { bv = s; best = lag; }
        }
        return best;
    }

    static void Main(string[] args)
    {
        var rnd = new Random(3);
        var voice = Tts("Hey, can you hear me alright? I just changed my microphone settings. " +
                        "Let me know if I sound too quiet, too loud, or if there is any background noise. " +
                        "Sixty-six sizzling sausages sit in the saucepan, sharply hissing.");
        int lead = Fs * 2, tail = Fs * 2, len = lead + voice.Length + tail;
        len -= len % Block;
        // Speech at about -28 dBFS RMS while talking (a typical Yeti level at arm's length).
        double e = 0; int c0 = 0; foreach (var v in voice) if (Math.Abs(v) > 0.01) { e += v * v; c0++; }
        float vg = (float)(Math.Pow(10, -28 / 20.0) / Math.Sqrt(e / c0));
        var speech = new float[len];
        for (int i = 0; i < voice.Length && lead + i < len; i++) speech[lead + i] = voice[i] * vg;
        var noise = Pink(len, rnd, -58);
        var mix = new float[len];
        for (int i = 0; i < len; i++) mix[i] = speech[i] + noise[i];
        var talking = new bool[len / Block];
        for (int b = 0; b < talking.Length; b++) talking[b] = Rms(speech, b * Block, b * Block + Block) > 0.003;

        Console.WriteLine("Input: speech -28 dBFS (while talking), room noise -58 dBFS (pink + 60/120 Hz hum), " + (len / (double)Fs).ToString("0.0") + " s");

        // 1) Bypass ("Original" in the voice test) must be transparent apart from a fixed delay.
        {
            // Realtime noise removal (the default) passes the input through as is. Otherwise RNNoise keeps its ~15 Hz DC
            // filter on in bypass (inaudible, but it shifts the phase of the lowest notes), so the reference gets the same filter.
            bool realtime = new AppSettings().NsRealtime;
            var x = new float[len];
            double h0 = 0, h1 = 0;
            for (int i = 0; i < len; i++) { double xv = mix[i], yv = xv + h0; h0 = h1 + (-2 * xv + 1.99599 * yv); h1 = xv - 0.99600 * yv; x[i] = realtime ? mix[i] : (float)yv; }
            var c = new VoiceChain(); c.Configure(new AppSettings());
            double ms; var y = Run(c, mix, true, out ms);
            int lag = Lag(x, y, 4096);
            double err = 0, sig = 0; for (int i = Fs; i < len - lag; i++) { double d = y[i + lag] - x[i]; err += d * d; sig += x[i] * x[i]; }
            double errDb = 10 * Math.Log10(err / sig);
            Console.WriteLine("Bypass: delay " + lag + " samples (" + (lag * 1000.0 / Fs).ToString("0.0") + " ms), difference " + errDb.ToString("0.0") + " dB below the signal");
            Check(errDb < -60, "bypass is transparent");
        }

        // 2) Settings variants.
        var variants = new List<Tuple<string, AppSettings>>();
        variants.Add(Tuple.Create("defaults", new AppSettings()));
        var mine = AppSettings.Load(); mine.MicEq.Repair();
        variants.Add(Tuple.Create("your settings", mine));
        var classic = new AppSettings { NsEngine = "classic" }; variants.Add(Tuple.Create("classic engine", classic));
        var everything = new AppSettings { Gate = true, GateExpander = false, DeEsser = true, DeHumHz = 60, HighCutHz = 14000, Strength = 100 };
        variants.Add(Tuple.Create("everything on, max", everything));
        var nothing = new AppSettings { NoiseRemoval = false, Gate = false, Compressor = false, AutoLevel = false, Limiter = false, HighPassHz = 0 };
        nothing.MicEq.Enabled = false;
        variants.Add(Tuple.Create("all effects off", nothing));
        foreach (var st in VoiceStyles.All) { var t = new AppSettings(); st.Set(t); variants.Add(Tuple.Create("style: " + st.Name, t)); }
        foreach (var v in variants)
        {
            Console.WriteLine();
            Console.WriteLine("[" + v.Item1 + "]");
            var c = new VoiceChain(); c.Configure(v.Item2);
            double ms; var y = Run(c, mix, false, out ms);
            int lag = Lag(speech, y, 4096);
            double sIn = 0, sOut = 0, nIn = 0, nOut = 0; int sc = 0, nc = 0;
            // Skip the first second (adaptation) when measuring.
            for (int b = Fs / Block; b < talking.Length - 1; b++)
            {
                int s = b * Block;
                if (s + lag + Block > len) break;
                double ri = Rms(mix, s, s + Block), ro = Rms(y, s + lag, s + lag + Block);
                if (talking[b]) { sIn += ri * ri; sOut += ro * ro; sc++; }
                else if (b < lead / Block - 2 || b > (lead + voice.Length) / Block + 25) { nIn += ri * ri; nOut += ro * ro; nc++; }
            }
            double peak = y.Max(a => Math.Abs(a));
            bool nan = y.Any(a => float.IsNaN(a) || float.IsInfinity(a));
            double speechIn = Db(Math.Sqrt(sIn / sc)), speechOut = Db(Math.Sqrt(sOut / sc)), noiseIn = Db(Math.Sqrt(nIn / nc)), noiseOut = Db(Math.Sqrt(nOut / nc));
            Console.WriteLine("  delay " + (lag * 1000.0 / Fs).ToString("0.0") + " ms | speech " + speechIn.ToString("0.0") + " -> " + speechOut.ToString("0.0") + " dB | noise between words " +
                noiseIn.ToString("0.0") + " -> " + noiseOut.ToString("0.0") + " dB | SNR " + (speechIn - noiseIn).ToString("0") + " -> " + (speechOut - noiseOut).ToString("0") + " dB | peak " + Db(peak).ToString("0.0") + " dB | " +
                ms.ToString("0.0") + " ms CPU per s");
            Check(!nan, "no NaN/Inf");
            Check(peak <= 1.0, "no clipping");
            Check(ms < 60, "CPU under 6% of one core");
            if (v.Item1 != "all effects off") Check(speechOut - noiseOut >= speechIn - noiseIn, "background noise not made worse relative to the voice");
            if (v.Item2.Limiter) Check(peak <= 0.9, "limiter holds -1 dBFS");
        }

        // 3) Settings changed while it plays: another thread re-configures constantly.
        {
            Console.WriteLine();
            Console.WriteLine("[settings changed while playing]");
            var s = new AppSettings();
            var c = new VoiceChain(); c.Configure(s);
            bool stop = false; int changes = 0; Exception err = null;
            var t = new Thread(() =>
            {
                var r = new Random(5);
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        s.Strength = r.Next(0, 101); s.NsEngine = r.Next(2) == 0 ? "ai" : "classic"; s.Gate = r.Next(2) == 0; s.GateExpander = r.Next(2) == 0;
                        s.GateThreshold = r.Next(-70, -20); s.Compressor = r.Next(2) == 0; s.DeEsser = r.Next(2) == 0; s.HighPassHz = r.Next(0, 160);
                        s.DeHumHz = new[] { 0, 50, 60 }[r.Next(3)]; s.HighCutHz = r.Next(2) == 0 ? 0 : r.Next(5000, 20000); s.VolumeDb = r.Next(-10, 11);
                        s.AutoLevel = r.Next(2) == 0; s.MicEq.Enabled = r.Next(2) == 0;
                        for (int k = 0; k < 10; k++) s.MicEq.Bands[k] = r.Next(-12, 13);
                        c.Configure(s); changes++;
                        Thread.Sleep(2);
                    }
                }
                catch (Exception ex) { err = ex; }
            });
            t.Start();
            double ms; float[] y = null;
            try { for (int k = 0; k < 3; k++) y = Run(c, mix, k % 2 == 1, out ms); }
            catch (Exception ex) { err = ex; }
            Volatile.Write(ref stop, true); t.Join();
            Console.WriteLine("  " + changes + " setting changes during 3 passes");
            Check(err == null, "no exceptions" + (err != null ? " (" + err.GetType().Name + ": " + err.Message + ")" : ""));
            Check(y != null && !y.Any(a => float.IsNaN(a) || float.IsInfinity(a)), "no NaN/Inf");
            Check(y != null && y.Max(a => Math.Abs(a)) <= 1.0, "no clipping");
        }

        // 4) Voice styles: recognised, distinct, and "My settings" gives back your own tuning.
        {
            Console.WriteLine();
            Console.WriteLine("[voice styles]");
            var prints = new HashSet<string>();
            bool allMatch = true;
            foreach (var st in VoiceStyles.All)
            {
                var t = new AppSettings(); st.Set(t);
                if (VoiceStyles.Match(t) != st.Name) allMatch = false;
                prints.Add(VoiceStyles.Fingerprint(t));
            }
            Check(allMatch, "every style is recognised after applying it");
            Check(prints.Count == VoiceStyles.All.Count, "all styles are different");
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            var me = js.Deserialize<AppSettings>(js.Serialize(AppSettings.Load()));
            me.MicEq.Repair();
            me.VolumeDb = -3;
            string myPrint = VoiceStyles.Fingerprint(me);
            bool custom = VoiceStyles.Match(me) == null;
            Console.WriteLine("  your current tuning is " + (custom ? "custom" : "the \"" + VoiceStyles.Match(me) + "\" style"));
            me.MyVoice = null;
            VoiceStyles.Apply(me, "Broadcast");
            Check(VoiceStyles.Match(me) == "Broadcast" && me.VolumeDb == -3, "style applied, volume untouched");
            VoiceStyles.Apply(me, "Noisy room");
            // Survive a save/load like the real settings file.
            me = js.Deserialize<AppSettings>(js.Serialize(me));
            Check(VoiceStyles.Match(me) == "Noisy room", "style survives saving");
            if (custom)
            {
                Check(VoiceStyles.RestoreMine(me) && VoiceStyles.Fingerprint(me) == myPrint, "\"My settings\" restores your exact tuning after trying two styles and a save");
            }
        }

        // 5) Voice-test recorder.
        {
            Console.WriteLine();
            Console.WriteLine("[voice test recorder]");
            var vt = new VoiceTest();
            vt.StartRecording();
            Check(vt.Recording, "recording starts");
            var blk = new float[Block];
            for (int i = 0; i + Block <= Fs * 4; i += Block) { Array.Copy(mix, i, blk, 0, Block); vt.Feed(blk, Block); }
            vt.StopRecording();
            Check(!vt.Recording && vt.HasClip && Math.Abs(vt.ClipSeconds - 4.0) < 0.02, "4 s clip kept (" + vt.ClipSeconds.ToString("0.00") + " s)");
            vt.StartRecording();
            for (int i = 0; i < VoiceTest.MaxSeconds * Fs / Block + 50; i++) vt.Feed(blk, Block);
            Check(!vt.Recording && Math.Abs(vt.ClipSeconds - VoiceTest.MaxSeconds) < 0.02, "stops by itself at " + VoiceTest.MaxSeconds + " s (" + vt.ClipSeconds.ToString("0.0") + " s)");
            vt.StartRecording(); vt.StopRecording();
            Check(!vt.HasClip, "an empty recording is not playable");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
