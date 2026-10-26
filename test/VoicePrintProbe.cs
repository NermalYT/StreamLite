// include: VoiceLockTest.cs
// The similarity to your voiceprint (from lines 0-5) of pieces of held-out lines (6-11): you alone, someone else alone
// (in the room: -6 dB, echo), and both at once - percentiles per piece length, to place the decision thresholds.
// Usage: run.ps1 VoicePrintProbe
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamLite.Audio;

class VoicePrintProbe
{
    static string P(List<double> v) { v.Sort(); Func<double, double> q = f => v[(int)Math.Min(v.Count - 1, f * v.Count)]; return string.Format("p5 {0:0.00} p25 {1:0.00} p50 {2:0.00} p75 {3:0.00} p95 {4:0.00}", q(0.05), q(0.25), q(0.5), q(0.75), q(0.95)); }

    static float[] Down3(float[] x) { var y = new float[x.Length / 3]; for (int i = 0; i < y.Length; i++) y[i] = (x[3 * i] + x[3 * i + 1] + x[3 * i + 2]) / 3f; return y; }

    static void Main()
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        string dir = Path.Combine(Path.GetTempPath(), "StreamLite-voices");
        var voices = new[] { "David", "Mark", "Zira" };
        var lines = voices.ToDictionary(v => v, v => Enumerable.Range(0, 12).Select(i => VoiceLockTest.Wav16Public(Path.Combine(dir, v + "-" + i.ToString("00") + ".wav"))).ToList());
        var rnd = new Random(1);
        using (var vl = new VoiceLock())
        {
            var prints = voices.ToDictionary(v => v, v => { var e = lines[v].Take(6).SelectMany(l => l).ToArray(); double c; return vl.PrintOf(e, e.Length, out c); });
            foreach (double secs in new[] { 0.35, 0.5, 0.75, 1.0 })
            {
                int w = (int)(secs * 16000);
                var me = new List<double>(); var them = new List<double>(); var both = new List<double>(); var themVsBlock = new List<double>(); var bothMargin = new List<double>();
                foreach (var you in voices)
                    foreach (var other in voices.Where(t => t != you))
                        for (int k = 6; k < 12; k++)
                        {
                            var a = lines[you][k]; var bRoom = Down3(VoiceLockTest.InRoomPublic(VoiceLockTest.Up3Public(lines[other][k]), -6, rnd));
                            int len = Math.Min(a.Length, bRoom.Length);
                            var mix = new float[len]; for (int i = 0; i < len; i++) mix[i] = a[i] + bRoom[i];
                            for (int s = 0; s + w <= len; s += w)
                            {
                                var ea = vl.Embed(a, s, w); var eb = vl.Embed(bRoom, s, w); var em = vl.Embed(mix, s, w);
                                me.Add(VoiceLock.Cos(ea, prints[you])); them.Add(VoiceLock.Cos(eb, prints[you])); both.Add(VoiceLock.Cos(em, prints[you]));
                                themVsBlock.Add(VoiceLock.Cos(eb, prints[other]) - VoiceLock.Cos(eb, prints[you]));
                                bothMargin.Add(VoiceLock.Cos(em, prints[you]) - VoiceLock.Cos(em, prints[other]));
                            }
                        }
                Console.WriteLine("pieces of " + secs + " s (" + me.Count + " each):");
                Console.WriteLine("  you alone        " + P(me));
                Console.WriteLine("  them alone       " + P(them));
                Console.WriteLine("  both at once     " + P(both));
                Console.WriteLine("  them: their print minus yours   " + P(themVsBlock));
                Console.WriteLine("  both: your print minus theirs   " + P(bothMargin));
            }
        }
    }
}
