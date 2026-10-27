// Devices of every kind (3.5.9): what a microphone or interface hands over - 16-bit, 24-bit, 32-bit integer (also
// 24 bits in a 32-bit container) or 32-bit float - comes out as the same sound, full scale both ways, no wrap-around.
// Usage: run.ps1 DeviceCompatTest
using System;
using StreamLite.Audio;

class DeviceCompatTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static byte[] Pack(double[] x, int bytes, bool padded24)
    {
        var o = new byte[x.Length * bytes];
        for (int i = 0; i < x.Length; i++)
        {
            long v;
            if (bytes == 2) v = (long)Math.Round(Math.Max(-1, Math.Min(32767 / 32768.0, x[i])) * 32768);
            else if (bytes == 3) v = (long)Math.Round(Math.Max(-1, Math.Min(8388607 / 8388608.0, x[i])) * 8388608);
            else if (padded24) v = (long)Math.Round(Math.Max(-1, Math.Min(8388607 / 8388608.0, x[i])) * 8388608) << 8;
            else v = (long)Math.Round(Math.Max(-1, Math.Min(2147483647 / 2147483648.0, x[i])) * 2147483648.0);
            for (int b = 0; b < bytes; b++) o[i * bytes + b] = (byte)(v >> (8 * b));
        }
        return o;
    }

    static void Main()
    {
        var x = new double[] { 0, 0.5, -0.5, 0.999, -1, 0.25, -0.0001, 1e-5 };
        var d = new float[x.Length];
        foreach (var kind in new[] { new { B = 2, P = false, Name = "16-bit", Tol = 1.0 / 32768 }, new { B = 3, P = false, Name = "24-bit", Tol = 1.0 / 8388608 },
                                     new { B = 4, P = true, Name = "24-bit in 32", Tol = 1.0 / 8388608 }, new { B = 4, P = false, Name = "32-bit", Tol = 1e-7 } })
        {
            AudioEngine.Decode(Pack(x, kind.B, kind.P), x.Length, kind.B, false, d);
            double worst = 0; for (int i = 0; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(d[i] - x[i]));
            Check(worst <= kind.Tol * 1.01 && d[4] == -1f && d[3] > 0.99f, kind.Name + " integer: the same sound (worst error " + worst.ToString("0.0e0") + "), full scale both ways");
        }
        var f = new byte[x.Length * 4];
        Buffer.BlockCopy(Array.ConvertAll(x, v => (float)v), 0, f, 0, f.Length);
        AudioEngine.Decode(f, x.Length, 4, true, d);
        bool same = true; for (int i = 0; i < x.Length; i++) if (d[i] != (float)x[i]) same = false;
        Check(same, "32-bit float: passed through exactly");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
