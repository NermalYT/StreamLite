using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// The mic's buffer between the microphone clock and the cable's clock. They always drift apart a little (USB
    /// clocks are off by tens to hundreds of ppm), which used to end in a dropped chunk or a gap every few minutes;
    /// this reads at a continuously corrected speed instead (the same windowed-sinc drift correction as the 3D path).
    /// </summary>
    internal sealed class SampleRing
    {
        public const int SharedMargin = 624;   // ~13 ms kept in reserve below each 10 ms shared-mode mic packet (rides out short system hiccups)
        readonly StereoRing ring = new StereoRing(SharedMargin);
        float[] inter = new float[0];

        public SampleRing(int capacity, int maxLatencySamples) { }

        /// <summary>Real dropouts (ran dry while the mic was still delivering).</summary>
        public int Underruns { get { return ring.Underruns; } }

        /// <summary>
        /// The safety margin for the mic's packet size: 13 ms for 10 ms shared-mode packets, ~2 ms for the 3 ms
        /// packets of an exclusive (low-latency) mic (1 and 1.5 ms still ran dry once in 10 simulated minutes). It still grows by itself after a real dropout.
        /// </summary>
        /// <param name="driven">The cable is topped up right after each packet (the low-latency path): the ring then
        /// only bridges the moment between write and read, so a 0.7 ms margin is plenty.</param>
        public void SetPacket(int frames, bool driven = false)
        {
            if (frames >= 480) ring.SetBaseMargin(SharedMargin);   // its 13 ms margin already covers a packet
            else ring.SetBaseMargin(driven ? 32 : Math.Max(96, frames * 2 / 3), driven ? 0 : frames);
        }

        /// <summary>Samples queued right now, and the safety margin the reader steers towards.</summary>
        public int Fill { get { return ring.Fill; } }
        public int Margin { get { return ring.Margin; } }

        public void Write(float[] src, int n)
        {
            ring.Write(src, src, n);
            ring.InputArrived();
        }

        public void Read(float[] dst, int n)
        {
            if (inter.Length < n * 2) inter = new float[n * 2];
            ring.Read(inter, n);
            for (int i = 0; i < n; i++) dst[i] = inter[2 * i];
        }

        public void Clear() { ring.Clear(); }
    }

    /// <summary>
    /// Stereo FIFO between the game clock and the headphone clock. It steers the LOWEST fill level seen between
    /// reads (not the average) towards a small safety margin, by nudging the read speed (+-0.3%) - so the delay stays
    /// minimal whatever the phase between the two devices' 10 ms cycles, and clock drift never clicks.
    /// </summary>
    internal sealed class StereoRing
    {
        const int Size = 1 << 15, Mask = Size - 1;
        readonly float[] l = new float[Size], r = new float[Size];
        long write; double read;
        readonly object gate = new object();
        bool primed;
        int margin, reads, overWindows;
        double troughWin = double.MaxValue, step = 1;
        public int Underruns;
        int baseMargin;
        int pendingUnderrun;                    // tick of an unconfirmed run-dry, 0 = none
        int lastGrow = Environment.TickCount;

        public StereoRing(int marginFrames, int startFrames = 0) { baseMargin = marginFrames; margin = Math.Max(marginFrames, Math.Min(720, startFrames)); }
        public int Margin { get { lock (gate) return margin; } }

        /// <summary>A new base margin (the input's packet size changed); the reader steers to it from now on. packet =
        /// the input's packet size: starting (or restarting after running dry) keeps one packet extra, because the
        /// fill is lowest just before the next packet arrives.</summary>
        public void SetBaseMargin(int frames, int packet = 0) { lock (gate) { baseMargin = frames; margin = frames; floor = 0; startExtra = packet; lastGrow = Environment.TickCount; } }
        int startExtra;
        /// <summary>
        /// One step above the highest margin that ran dry: calm periods give margin back only down to here, so the
        /// level that just failed isn't tried again every 30 s (which made a dropout about once a minute).
        /// </summary>
        int floor;
        public int Floor { get { lock (gate) return floor; } }
        public int LastUnderrunTick { get { lock (gate) return lastUnderrun; } }
        int lastUnderrun;
        // 16-tap Kaiser-windowed sinc, 64 fractional phases (+1 for interpolating between them), cut off at 21 kHz.
        const int Taps = 16, Half = Taps / 2, Phases = 64;
        static readonly float[][] Kernel = BuildKernel();

        static float[][] BuildKernel()
        {
            const double cutoff = 21000.0 / 24000.0, beta = 8.0;
            Func<double, double> i0 = x => { double s = 1, t = 1; for (int k = 1; k < 30; k++) { t *= (x / 2) / k; s += t * t; } return s; };
            var table = new float[Phases + 1][];
            for (int p = 0; p <= Phases; p++)
            {
                double frac = (double)p / Phases, sum = 0;
                var h = new double[Taps];
                for (int t = 0; t < Taps; t++)
                {
                    double x = t - (Half - 1) - frac;                       // distance from the read position
                    double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                    double u = x / Half;
                    double win = Math.Abs(u) >= 1 ? 0 : i0(beta * Math.Sqrt(1 - u * u)) / i0(beta);
                    h[t] = sinc * win; sum += h[t];
                }
                table[p] = new float[Taps];
                for (int t = 0; t < Taps; t++) table[p][t] = (float)(h[t] / sum);   // unity gain at every phase
            }
            return table;
        }

        /// <summary>
        /// Game audio arrived. If we ran dry shortly before, the game was still playing: that was a real dropout, so
        /// keep a bigger safety margin. (Running dry because the game went silent is expected and ignored.)
        /// </summary>
        public void InputArrived()
        {
            lock (gate)
            {
                int now = Environment.TickCount;
                if (pendingUnderrun != 0 && now - pendingUnderrun < 30)
                {
                    Underruns++;
                    floor = Math.Max(floor, Math.Min(720, margin + 24));
                    margin = Math.Min(720, margin + 24); lastGrow = now; lastUnderrun = now | 1;
                }
                pendingUnderrun = 0;
                // Calm for a while: give some of the margin back (never below what has run dry before).
                int least = Math.Max(baseMargin, floor);
                if (margin > least && now - lastGrow > 30000) { margin = Math.Max(least, margin - 12); lastGrow = now; }
            }
        }

        public int Fill { get { lock (gate) return (int)(write - read); } }

        public void Clear() { lock (gate) { read = write; primed = false; } }

        public void Write(float[] a, float[] b, int n)
        {
            lock (gate)
            {
                for (int i = 0; i < n; i++) { int k = (int)((write + i) & Mask); l[k] = a[i]; r[k] = b[i]; }
                write += n;
                if (write - read > Size - 4096) read = write - margin - 480;    // reader stalled: don't let it overflow
            }
        }

        public void Read(float[] inter, int frames)
        {
            lock (gate)
            {
                double fill = write - read;
                if (!primed)
                {
                    if (fill < margin + frames + startExtra) { Array.Clear(inter, 0, frames * 2); return; }
                    primed = true; step = 1; troughWin = double.MaxValue; reads = 0;
                    read = write - (margin + frames + startExtra);             // start with the margin (+ a packet) left over
                }
                for (int i = 0; i < frames; i++)
                {
                    if (write - read < Half + 1)
                    {
                        // Ran dry: silence for the rest, wait until the buffer fills again, keep a bigger margin.
                        Array.Clear(inter, i * 2, (frames - i) * 2);
                        if (write != 0) pendingUnderrun = Environment.TickCount | 1;
                        primed = false;
                        return;
                    }
                    // Windowed-sinc interpolation at the fractional read position (linear interpolation dulled
                    // and wobbled the treble by up to 6 dB at 16 kHz as the drift correction moved the fraction).
                    long i0 = (long)Math.Floor(read);
                    double pos = (read - i0) * Phases;
                    int ph = (int)pos;
                    float mix = (float)(pos - ph);
                    float[] h0 = Kernel[ph], h1 = Kernel[ph + 1];
                    float sl = 0, sr = 0;
                    for (int t = 0; t < Taps; t++)
                    {
                        float h = h0[t] + (h1[t] - h0[t]) * mix;
                        int k = (int)((i0 - Half + 1 + t) & Mask);
                        sl += l[k] * h; sr += r[k] * h;
                    }
                    inter[i * 2] = sl;
                    inter[i * 2 + 1] = sr;
                    read += step;
                }
                double after = write - read;
                if (after < troughWin) troughWin = after;
                if (++reads >= 40)
                {
                    double err = troughWin - margin;
                    // Far too much queued for a while (e.g. game sound just resumed): resync at once.
                    if (err > 240) { if (++overWindows >= 2) { read += err; overWindows = 0; err = 0; } }
                    else overWindows = 0;
                    step = 1 + Math.Max(-0.003, Math.Min(0.003, err / 480 * 0.003));
                    troughWin = double.MaxValue; reads = 0;
                }
            }
        }
    }
}
