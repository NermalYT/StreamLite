using System;
using System.IO;
using System.Linq;
using System.Text;
using StreamLite.Audio;

// Personalize 3D's footsteps from CS2 (read from the game on this PC): decoding a compiled sound (vsnd_c 4, 16-bit PCM)
// built here, resampling 44.1 -> 48 kHz, the walk never playing one recording twice in a row - and, when CS2 is
// installed, loading its footsteps for real.
// Usage: run.ps1 GameFootstepsTest
class GameFootstepsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }

    /// <summary>A minimal vsnd_c: 16-byte header, one block entry (DATA) at 16, the DATA block, then the samples.</summary>
    static byte[] Sound(short[] pcm, int rate, int channels)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        int dataAt = 28, dataLen = 48, size = dataAt + dataLen;
        w.Write(size); w.Write((ushort)12); w.Write((ushort)4); w.Write(8); w.Write(1);
        w.Write(Encoding.ASCII.GetBytes("DATA")); w.Write(dataAt - 20); w.Write(dataLen);
        w.Write((ushort)rate); w.Write((byte)0); w.Write((byte)channels); w.Write(0); w.Write(pcm.Length / channels);
        w.Write(new byte[dataLen - 12]);
        foreach (var s in pcm) w.Write(s);
        return m.ToArray();
    }

    static void Main()
    {
        // A 1 kHz tone, mono and stereo, at 44.1 kHz.
        var tone = Enumerable.Range(0, 4410).Select(i => (short)(16000 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0))).ToArray();
        var mono = GameFootsteps.DecodeSound(Sound(tone, 44100, 1));
        Check(mono != null && Math.Abs(mono.Length - 4800) <= 1, "mono decodes and resamples to 48 kHz (" + (mono == null ? 0 : mono.Length) + " samples)");
        if (mono != null)
        {
            double peak = mono.Skip(200).Take(4400).Max(v => Math.Abs(v));
            int crossings = 0; for (int i = 201; i < 4600; i++) if (mono[i - 1] < 0 && mono[i] >= 0) crossings++;
            Check(Math.Abs(peak - 16000 / 32768.0) < 0.01, "level kept (" + peak.ToString("0.000") + ")");
            Check(Math.Abs(crossings - 4400 / 48.0) <= 1, "pitch kept (" + crossings + " cycles in 4400 samples)");
        }
        var stereo = GameFootsteps.DecodeSound(Sound(tone.SelectMany(s => new[] { s, (short)0 }).ToArray(), 44100, 2));
        Check(stereo != null && Math.Abs(stereo.Length - 4800) <= 1, "stereo mixed to mono");
        var adpcm = Sound(tone, 44100, 1); adpcm[28 + 2] = 1;
        Check(GameFootsteps.DecodeSound(adpcm) == null, "anything but 16-bit PCM is skipped");

        // Walking: every recording once a round, never the same one twice in a row.
        int same = 0; int[] seen = new int[17];
        for (int n = 0; n < 17 * 50; n++) { int a = StepRenderer.Walk(n, 17); seen[a]++; if (n > 0 && a == StepRenderer.Walk(n - 1, 17)) same++; }
        Check(same == 0, "no recording twice in a row (" + same + ")");
        Check(seen.All(c => c == 50), "each recording as often as the others");

        // CS2 on this PC.
        string dir = GameFootsteps.FindCs2();
        if (dir == null) Console.WriteLine("  --   CS2 isn't installed here (Personalize 3D uses StreamLite's own footsteps)");
        else
        {
            var t = DateTime.Now;
            var steps = GameFootsteps.LoadCs2(17, 0.5f);
            Check(steps != null && steps.Length == 17, "CS2's footsteps load: " + (steps == null ? 0 : steps.Length) + " in " + (DateTime.Now - t).TotalMilliseconds.ToString("0") + " ms");
            if (steps != null)
                Check(steps.All(s => s.Length > 4800 && Math.Abs(s.Max(v => Math.Abs(v)) - 0.5f) < 0.001), "each a real step, peaking at 0.5 (" + steps.Min(s => s.Length) / 48 + "-" + steps.Max(s => s.Length) / 48 + " ms)");
        }
        Console.WriteLine(fails == 0 ? "PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
