using System;
using StreamLite.Audio;

// The game -> headphones path's buffering in simulated time: game sound arrives in Windows' 10 ms loopback packets
// (up to `jitter` ms late), is cut into the 3D processor's blocks of B frames (a leftover waits for the next packet),
// and leaves in the headphones' exclusive 3 ms steps. Prints where the time goes: waiting for a block to fill, and the
// ring. Usage: GamePathProbe [block frames] [jitter ms] [conceal]
class GamePathProbe
{
    static void Main(string[] args)
    {
        int B = args.Length > 0 ? int.Parse(args[0]) : 256;
        double jitter = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        bool conceal = args.Length > 2 && args[2] == "conceal";
        double t = 0;
        var ring = new StereoRing(96) { Now = () => t, Conceal = conceal };
        ring.SetBaseMargin(96, 480);
        var rnd = new Random(4);
        long k = 1; double nextPkt = 10 + rnd.NextDouble() * jitter, lastPkt = 0, nextRead = 3;
        int fill = 0; var blkL = new float[B]; var blkR = new float[B];
        var outp = new float[144 * 2];
        double waitSum = 0; long waitN = 0;                    // frames waiting for their block, frame-weighted ms
        double ringSum = 0; long ringN = 0; int minFill = int.MaxValue;
        long frameNo = 0; var arrival = new double[B];
        for (t = 0; t < 120000; t += 0.05)
        {
            if (t >= nextPkt)
            {
                for (int i = 0; i < 480; i++)
                {
                    blkL[fill] = blkR[fill] = (float)Math.Sin(frameNo++ * 0.05); arrival[fill] = t;
                    if (++fill == B)
                    {
                        for (int j = 0; j < B; j++) { waitSum += t - arrival[j]; waitN++; }
                        ring.Write(blkL, blkR, B); fill = 0;
                    }
                }
                ring.InputArrived();
                k++; lastPkt = nextPkt; nextPkt = Math.Max(lastPkt, k * 10 + rnd.NextDouble() * jitter);   // due every 10 ms, each up to `jitter` late
            }
            if (t >= nextRead)
            {
                ring.Read(outp, 144);
                if (t > 10000) { ringSum += ring.Fill; ringN++; minFill = Math.Min(minFill, ring.Fill); }
                nextRead += 3;
            }
        }
        Console.WriteLine("block " + B + " frames, jitter " + jitter + " ms" + (conceal ? ", fill-in" : "") + ": waiting for a block " + (waitSum / waitN).ToString("0.0") + " ms, ring " + (ringSum / ringN / 48).ToString("0.0") +
            " ms (lowest " + (minFill / 48.0).ToString("0.0") + " ms), margin " + (ring.Margin / 48.0).ToString("0.0") + " ms, gaps " + ring.Underruns + ", filled in " + ring.Concealed);
    }
}
