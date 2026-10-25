using System;
using StreamLite.Audio;

// A packet the mic's driver lost, filled in (PacketConcealer): on a voice-like sound (110-240 Hz pitch gliding, with
// harmonics) and on a held vowel, a 3 ms and a 6 ms hole. Checks: the filled-in sound is close to what was lost (far
// closer than silence), and the seams don't click (no more high-frequency energy than the sound itself has).
class PlcTest
{
    const int Fs = 48000, P = 144;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    static double Db(double x) { return 10 * Math.Log10(x + 1e-30); }

    static float[] Voice(bool glide)
    {
        var x = new float[Fs]; double ph = 0; var rnd = new Random(2);
        for (int i = 0; i < x.Length; i++)
        {
            double f0 = glide ? 110 + 130 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 1.3 * i / Fs)) : 140;
            ph += 2 * Math.PI * f0 / Fs;
            double v = 0;
            for (int k = 1; k <= 12; k++) v += Math.Sin(k * ph) / k * (k == 3 || k == 4 ? 2 : 1);
            x[i] = (float)(0.1 * v + 0.002 * (rnd.NextDouble() * 2 - 1));
        }
        return x;
    }

    static void Main()
    {
        foreach (bool glide in new[] { false, true })
            foreach (int lostPackets in new[] { 1, 2 })
            {
                var x = Voice(glide);
                int lost = lostPackets * P, at = 30 * P;   // the hole starts 90 ms in
                var plc = new PacketConcealer();
                var y = new float[x.Length]; int o = 0;
                var pkt = new float[P]; var fill = new float[lost];
                for (int s = 0; s + P <= x.Length; s += P)
                {
                    if (s >= at && s < at + lost) continue;   // never arrived
                    Array.Copy(x, s, pkt, 0, P);
                    if (s == at + lost) { plc.Fill(fill, lost); Array.Copy(fill, 0, y, o, lost); o += lost; plc.BlendInto(pkt, P); }
                    plc.Remember(pkt, P);
                    Array.Copy(pkt, 0, y, o, P); o += P;
                }
                // How close the filled-in part is to what was lost, vs silence.
                double e = 0, s2 = 0;
                for (int i = at; i < at + lost; i++) { double d = y[i] - x[i]; e += d * d; s2 += x[i] * (double)x[i]; }
                // Seams: high-frequency energy (triple difference) in 2 ms around each seam, vs the same span of the original.
                Func<float[], int, double> hf = (z, c) => { double h = 0; for (int i = c - 48; i < c + 48; i++) { double d = z[i] - 3 * z[i - 1] + 3 * z[i - 2] - z[i - 3]; h += d * d; } return h; };
                double seam = Math.Max(Db(hf(y, at)) - Db(hf(x, at)), Db(hf(y, at + lost)) - Db(hf(x, at + lost)));
                string name = (glide ? "gliding voice" : "held vowel") + ", " + lostPackets * 3 + " ms lost";
                Console.WriteLine("  " + name + ": filled in " + (Db(s2) - Db(e)).ToString("0.0") + " dB from the real sound (silence: 0 dB), seams " + seam.ToString("+0.0;-0.0") + " dB high frequencies");
                Check(Db(s2) - Db(e) > (glide ? 6 : 15), name + ": close to what was lost");
                Check(seam < 6, name + ": no click at the seams");
            }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
