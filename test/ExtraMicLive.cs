// Live: the extra-mic recorder (ExtraMic, shared mode) on a real input for N seconds, read through MicMixer at an
// exclusive main mic's pace (144 samples every 3 ms by the system clock), like the app. Reports whether it ran,
// packets kept coming, dropouts after start, and the level. Usage: ExtraMicLive "<input name part>" [seconds]
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

class ExtraMicLive
{
    static void Main(string[] args)
    {
        string part = args.Length > 0 ? args[0] : "StreamLite - In";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 60;
        var dev = AudioDevices.List(EDataFlow.Capture).FirstOrDefault(d => d.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        if (dev == null) { Console.WriteLine("no active input matching " + part); return; }
        Console.WriteLine("input: " + dev.Name);
        var mixer = new MicMixer();
        mixer.Set(new[] { new System.Collections.Generic.KeyValuePair<string, float>(dev.Id, 0) });
        var mic = mixer.Mics[0];
        var buf = new float[144];
        var sw = Stopwatch.StartNew();
        long next = 0, step = Stopwatch.Frequency * 3 / 1000;
        long lastLog = -1; int startDrops = -1; double peak = 0; long nonZero = 0, total = 0;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(1);
            while (sw.ElapsedTicks >= next)
            {
                Array.Clear(buf, 0, 144);
                mixer.Mix(buf, 144);
                foreach (var v in buf) { total++; if (v != 0) nonZero++; peak = Math.Max(peak, Math.Abs(v)); }
                next += step;
            }
            if (startDrops < 0 && sw.Elapsed.TotalSeconds > 3) startDrops = mic.Dropouts;
            if (sw.Elapsed.TotalSeconds < 2.5 && (sw.ElapsedMilliseconds / 50) != lastLog) { lastLog = sw.ElapsedMilliseconds / 50; Console.WriteLine("  " + sw.ElapsedMilliseconds + " ms: fill " + mic.Fill + ", margin " + mic.Margin + ", dropouts " + mic.Dropouts + ", running " + mic.Running); }
        }
        Console.WriteLine("running " + mic.Running + " (" + mic.Status + "), dropouts after start " + (mic.Dropouts - Math.Max(0, startDrops)) + " (" + mic.Dropouts + " total), non-zero " + (100.0 * nonZero / total).ToString("0.0") + "%, peak " + (20 * Math.Log10(peak + 1e-9)).ToString("0") + " dBFS");
        mixer.StopAll();
        Console.WriteLine(mic.Running ? "FAIL still running after stop" : "stopped cleanly");
    }
}
