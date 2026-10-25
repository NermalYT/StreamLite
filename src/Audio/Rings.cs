using System;
using System.Linq;

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
        /// <summary>How long the last one lasted (ms).</summary>
        public double LastGapMs { get { return ring.LastGapMs; } }

        /// <summary>
        /// The safety margin for the mic's packet size: 13 ms for 10 ms shared-mode packets, ~2 ms for the 3 ms
        /// packets of an exclusive (low-latency) mic (1 and 1.5 ms still ran dry once in 10 simulated minutes). It still grows by itself after a real dropout.
        /// </summary>
        /// <param name="driven">The cable is topped up right after each packet (the low-latency path): the ring then
        /// only bridges the moment between write and read, so a 0.7 ms margin is plenty.</param>
        /// <param name="learned">The floor recurring gaps set in an earlier run on this mic (0 = none).</param>
        public void SetPacket(int frames, bool driven = false, int learned = 0)
        {
            if (frames >= 480) ring.SetBaseMargin(SharedMargin);   // its 13 ms margin already covers a packet
            else ring.SetBaseMargin(driven ? 32 : Math.Max(96, frames * 2 / 3), driven ? 0 : frames);
            if (learned > 0) ring.SetFloor(learned);
        }

        /// <summary>The margin calm periods settle to (the base, or the floor recurring gaps set).</summary>
        public int Least { get { return ring.Least; } }

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
        /// <summary>The input stream just started: see StereoRing.Grace.</summary>
        public void Grace(int ms) { ring.Grace(ms); }
    }

    /// <summary>
    /// Stereo FIFO between the game clock and the headphone clock. It steers the LOWEST fill level seen between
    /// reads (not the average) towards a small safety margin, by nudging the read speed (+-0.3%) - so the delay stays
    /// minimal whatever the phase between the two devices' 10 ms cycles, and clock drift never clicks.
    /// </summary>
    internal sealed class StereoRing
    {
        const int Size = 1 << 15, Mask = Size - 1;
        /// <summary>The most margin ever kept (15 ms), and how long a gap may last and still count as one (longer: the input stopped).</summary>
        public const int MaxMargin = 720;
        const double GapMs = 30;
        const int Fade = 32;                    // a gap fades out and back in over 0.7 ms instead of clicking
        readonly float[] l = new float[Size], r = new float[Size];
        long write; double read;
        readonly object gate = new object();
        bool primed;
        int margin, reads, overWindows, fadeIn;
        double troughWin = double.MaxValue, step = 1;
        /// <summary>Gaps: ran dry while the input was still delivering (bridged with a short fade).</summary>
        public int Underruns;
        /// <summary>How long the last gap lasted (ms) - a long one means the input itself stalled (a game loading, a busy driver).</summary>
        public double LastGapMs;
        int baseMargin;
        double dryAt;                           // when it last ran dry (ms on Now), 0 = none
        double lastGrow, lastFloorDrop;
        readonly System.Collections.Generic.List<double[]> gaps = new System.Collections.Generic.List<double[]>();   // recent gaps: {when, frames that would have bridged it}

        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Milliseconds (tests run the ring in simulated time).</summary>
        public Func<double> Now = () => Clock.Elapsed.TotalMilliseconds;

        /// <param name="startFrames">Margin learned in an earlier run: kept as this session's floor until it proves too much.</param>
        public StereoRing(int marginFrames, int startFrames = 0)
        {
            baseMargin = marginFrames;
            margin = Math.Max(marginFrames, Math.Min(MaxMargin, startFrames));
            proven = floor = margin > marginFrames ? margin : 0;
        }
        int proven;                             // the floor that bridged recurring gaps (a gap while trying less goes straight back to it)
        double holdUntil;                       // no trying a lower floor before this
        public int Margin { get { lock (gate) return margin; } }

        /// <summary>A new base margin (the input's packet size changed); the reader steers to it from now on. packet =
        /// the input's packet size: starting (or restarting after running dry) keeps one packet extra, because the
        /// fill is lowest just before the next packet arrives.</summary>
        public void SetBaseMargin(int frames, int packet = 0) { lock (gate) { baseMargin = frames; margin = frames; floor = proven = 0; holdUntil = 0; gaps.Clear(); startExtra = packet; lastGrow = Now(); } }
        /// <summary>A floor learned in an earlier run (recurring gaps on this device): kept from the start, lowered by calm periods as usual.</summary>
        public void SetFloor(int frames)
        {
            lock (gate)
            {
                if (frames <= baseMargin) return;
                proven = floor = Math.Min(MaxMargin, frames); margin = Math.Max(margin, floor);
                lastGapAt = Now(); lastFloorDrop = lastGapAt;   // the 30 calm minutes start now
            }
        }
        int startExtra;
        /// <summary>
        /// The least margin this session keeps: set only by gaps that keep coming back (3 within 10 minutes), to what
        /// would have bridged most of them - so a level that keeps failing isn't tried again and again, while a one-off
        /// hiccup (a game loading, a driver busy for a moment) never adds delay for good. 10 calm minutes lower it again.
        /// </summary>
        int floor;
        public int Floor { get { lock (gate) return floor; } }
        /// <summary>The margin calm periods settle to: the base, or the floor when gaps have set one.</summary>
        public int Least { get { lock (gate) return Math.Max(baseMargin, floor); } }
        public int LastUnderrunTick { get { lock (gate) return lastUnderrun; } }
        int lastUnderrun;
        double lastGapAt;
        /// <summary>
        /// A capture stream just (re)started: Windows hands out its first packets unevenly for about a second. Running
        /// dry then is bridged as usual but isn't counted and doesn't raise the margin.
        /// </summary>
        public void Grace(int ms) { lock (gate) graceUntil = Now() + ms; }
        double graceUntil;
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
        /// Input arrived. If we ran dry shortly before, the input was still flowing: that was a real gap. It gets
        /// covered at once (the margin grows to what would have bridged it, and is given back within minutes when
        /// calm); only gaps that keep coming back raise the floor. (Running dry because the game went silent is
        /// expected and ignored.)
        /// </summary>
        public void InputArrived()
        {
            lock (gate)
            {
                double now = Now();
                if (dryAt != 0 && now < graceUntil) dryAt = 0;   // a stream just started
                if (dryAt != 0 && now - dryAt < GapMs)
                {
                    double gap = now - dryAt;
                    Underruns++; LastGapMs = gap; lastGapAt = now; lastUnderrun = Environment.TickCount | 1;
                    int need = Math.Min(MaxMargin, (int)(gap * 48) + 24);
                    // Cover a repeat right away - but a one-off long pause (the input itself stopped for a moment)
                    // gets at most 4 ms more: more would only add delay. If long ones recur, the floor takes them.
                    margin = Math.Min(MaxMargin, Math.Max(margin + 24, Math.Min(need, Math.Max(baseMargin, floor) + 192)));
                    lastGrow = now;
                    gaps.Add(new[] { now, need });
                    gaps.RemoveAll(g => now - g[0] > 600000);
                    if (proven > floor)
                    {
                        // Trying less than what worked made a gap: back to it at once, and no more trying for an hour.
                        floor = proven; holdUntil = now + 3600000;
                    }
                    else if (gaps.Count >= 3)
                    {
                        // Recurring: keep what bridges most of them (the median - one long stall doesn't set it).
                        var sorted = gaps.Select(g => g[1]).OrderBy(v => v).ToList();
                        floor = Math.Max(floor, Math.Min(MaxMargin, (int)sorted[sorted.Count / 2]));
                        proven = floor;
                    }
                    if (margin < floor) margin = floor;
                }
                dryAt = 0;
                // Calm: give margin back towards the floor (12 frames every 5 s). The floor itself is tried lower only
                // after 30 minutes without a gap, slowly (12 frames every 2 minutes).
                int least = Math.Max(baseMargin, floor);
                if (margin > least && now - lastGrow > 5000) { margin = Math.Max(least, margin - 12); lastGrow = now; }
                if (floor > baseMargin && now - lastGapAt > 1800000 && now > holdUntil && now - lastFloorDrop > 120000)
                {
                    floor = Math.Max(baseMargin, floor - 12); lastFloorDrop = now;
                    if (floor == baseMargin) floor = 0;
                }
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
                    if (write > margin + frames + startExtra) fadeIn = Fade;   // after a gap: fade back in, no click
                }
                for (int i = 0; i < frames; i++)
                {
                    if (write - read < Half + 1)
                    {
                        // Ran dry: fade out what this read already produced, silence for the rest, wait until the
                        // buffer fills again.
                        int m = Math.Min(i, Fade);
                        for (int k = 0; k < m; k++) { float g = (float)(m - k) / (m + 1); int o = (i - m + k) * 2; inter[o] *= g; inter[o + 1] *= g; }
                        Array.Clear(inter, i * 2, (frames - i) * 2);
                        if (write != 0) dryAt = Now();
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
                    if (fadeIn > 0) { float g = (float)(Fade - fadeIn + 1) / (Fade + 1); sl *= g; sr *= g; fadeIn--; }
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
