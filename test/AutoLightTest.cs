// Auto light with your face metered (4.4): the room must not flicker when the face detector loses you for a moment - a
// hand over your mouth, half of you out of the picture. It used to blend your face in or out in one frame.
// Usage: run.ps1 AutoLightTest
using System;
using StreamLite;
using StreamLite.Camera;

class AutoLightTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    // A dark room with a bright face in the middle (the first image's case).
    static byte[] Picture(int room, int face)
    {
        var t = new byte[90 * 160];
        for (int y = 0; y < 90; y++) for (int x = 0; x < 160; x++) t[y * 160 + x] = (byte)(x >= 60 && x < 100 && y >= 20 && y < 70 ? face : room);
        return t;
    }

    /// <summary>The largest change of the picture's mid-tones from one frame to the next (0..255 levels).</summary>
    static double Run(Func<int, bool> seen, Func<int, double[]> box, int frames, out double settled)
    {
        var c = new CamFxSettings { AutoLight = true, FaceExposure = true };
        var light = new AutoLight(); var lut = new float[256]; var thumb = Picture(60, 170);
        double worst = 0, last = double.NaN; settled = 0;
        for (int f = 0; f < 300 + frames; f++)
        {
            light.Build(c, lut);
            double v = lut[110] * 255;
            if (f >= 300 && !double.IsNaN(last)) worst = Math.Max(worst, Math.Abs(v - last));
            last = v; settled = v;
            bool see = f < 300 || seen(f - 300);
            if (see) { var b = f < 300 ? new[] { 0.375, 0.22, 0.625, 0.78 } : box(f - 300); light.Measure(thumb, b[0], b[1], b[2], b[3]); }
            else light.Measure(thumb);
        }
        return worst;
    }

    static void Main()
    {
        double[] whole = { 0.375, 0.22, 0.625, 0.78 };
        double end;
        double steady = Run(i => true, i => whole, 120, out end);
        Console.WriteLine("  face seen all along: largest step " + steady.ToString("0.00") + " levels");
        Check(steady < 0.5, "a steady face: a steady picture");

        double flick = Run(i => i % 6 < 3, i => whole, 300, out end);
        Console.WriteLine("  detector losing you every 3 frames: largest step " + flick.ToString("0.00") + " levels");
        Check(flick < 0.5, "the face found and lost again and again: no flicker");

        double gone = Run(i => i < 30, i => whole, 300, out end);
        Console.WriteLine("  you leave the picture: largest step " + gone.ToString("0.00") + " levels");
        Check(gone < 1.5, "leaving the picture: the light eases over");

        // Half your face out of the picture: the box is small and at the edge, and what's in it isn't your face's level.
        double half = Run(i => true, i => i % 40 < 20 ? whole : new[] { 0.0, 0.3, 0.1, 0.6 }, 300, out end);
        Console.WriteLine("  half out of the picture: largest step " + half.ToString("0.00") + " levels");
        Check(half < 0.5, "half out of the picture: no flicker");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
