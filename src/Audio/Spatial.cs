using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    /// <summary>Everything the 3D audio processor needs, as an immutable snapshot built from the settings.</summary>
    internal sealed class SpatialConfig
    {
        public string HrtfKind = HrtfSet.Ku100, CustomPath;
        public double Room = 0.25;                       // 0..1.5 x natural studio room level
        public double FrontAngle = 30, SideAngle = 90, RearAngle = 145, Elevation = 0;
        public Dictionary<string, EqProfile> DirEq = new Dictionary<string, EqProfile>();
        public Dictionary<string, double> DirGainDb = new Dictionary<string, double>();
        public double FootstepBoost = 0.5, LoudTamer = 0.4, DynSpeed = 0.5;
        public double CueSharpen = 0.3;                  // height / front-back cue enhancer
        public double Width = 1.0;                       // stereo sources
        public bool VirtualizeStereo;                    // play stereo games through two virtual speakers
        public bool Render360;                           // surround: continuous 360° placement (ContinuousRenderer)
        public double Focus360 = 0.8;
        public double MasterDb = 0;
        public bool Limiter = true;
    }

    /// <summary>
    /// Game audio -> headphones. 7.1 input: per-direction EQ + footstep dynamics on each speaker channel, then
    /// HRTF virtual speakers (partitioned FFT convolution, optional room reflections). Stereo input (games with
    /// their own HRTF): left/center/right EQ by where each frequency sits in the stereo image, plus width.
    /// Both: height/front-back cue sharpening, master gain and limiter. Also feeds the sound radar.
    /// </summary>
    internal sealed class SpatialProcessor
    {
        public const int B = 256;                         // block size (5.3 ms)
        public const int Fs = 48000;
        public static readonly string[] Dirs = { "front", "center", "left", "right", "behind", "bass" };
        // Speaker slots: WAVE 7.1 order.
        public const int FL = 0, FR = 1, FC = 2, LFE = 3, BL = 4, BR = 5, SL = 6, SR = 7, Slots = 8;
        public static readonly string[] SlotNames = { "Front left", "Front right", "Center", "Bass (LFE)", "Behind left", "Behind right", "Left", "Right" };

        public enum Source { Silent, Stereo, Surround51, Surround71 }

        readonly BinauralConvolver conv = new BinauralConvolver();
        readonly MultibandDynamics dyn = new MultibandDynamics(Slots, Fs);
        readonly StereoPost post = new StereoPost();
        readonly EqChain[] eq = new EqChain[Slots];
        readonly float[] slotGain = new float[Slots];
        readonly float[] outL = new float[B], outR = new float[B];
        public readonly SoundRadar Radar = new SoundRadar();
        readonly TestSignal test = new TestSignal();
        volatile SpatialConfig cfg = new SpatialConfig();
        Source builtFor = (Source)(-1);
        SpatialConfig builtCfg;
        readonly ContinuousRenderer cont = new ContinuousRenderer();
        readonly BinauralConvolver objConv = new BinauralConvolver();   // sphere test: one source anywhere on the sphere
        HrtfSet curSet; double curRoom; float objAz = float.NaN, objEl = float.NaN;
        readonly float[] roomL = new float[B], roomR = new float[B], lateL = new float[B], lateR = new float[B];
        volatile bool use360;
        HrtfSet waiting360;
        float limGain = 1, masterGain = 1;
        int limHold;
        public volatile Source Current = Source.Silent;
        public volatile float LimiterDb;                  // live, for the UI
        public int ExtraLatency { get { return post.Latency + (use360 ? ContinuousRenderer.Latency : 0); } }
        /// <summary>True while surround sound is being placed continuously (360° mode).</summary>
        public bool Rendering360 { get { return use360; } }

        public SpatialProcessor()
        {
            for (int i = 0; i < Slots; i++) { eq[i] = new EqChain(); slotGain[i] = 1; }
        }

        public MultibandDynamics Dynamics { get { return dyn; } }
        public bool TestRunning { get { return test.Running; } }
        public string TestLabel { get { return test.Label; } }
        public TestSignal.Mode TestMode { get { return test.CurrentMode; } }
        public void StartTest(bool walk360 = false) { test.Start(walk360 ? TestSignal.Mode.Walk360 : TestSignal.Mode.Speakers); }
        /// <summary>Footsteps above, level with and below you (true elevations).</summary>
        public void StartSphereTest() { test.Start(TestSignal.Mode.Sphere); }
        /// <summary>
        /// The footsteps test: full circles around you at ear level, on the floor above and on the floor below - true
        /// elevations. A head model without elevations (a custom 7-speaker HeSuVi file) walks the ear-level circle.
        /// </summary>
        public void StartRingsTest()
        {
            var set = curSet;
            if (set == null) try { set = HrtfSet.Get(cfg.HrtfKind, cfg.CustomPath); } catch { }
            test.Start(set != null && set.SpeakerSet ? TestSignal.Mode.Walk360 : TestSignal.Mode.Rings);
        }
        public void StopTest() { test.Stop(); }

        public void Configure(SpatialConfig c)
        {
            cfg = c;
            if (c.Render360)
                try { ContinuousRenderer.Prepare(HrtfSet.Get(c.HrtfKind, c.CustomPath)); } catch { }
        }

        public static string DirOf(int slot, Source src)
        {
            switch (slot)
            {
                case FL: case FR: return "front";
                case FC: return "center";
                case LFE: return "bass";
                case SL: return "left";
                case SR: return "right";
                case BL: return src == Source.Surround51 ? "left" : "behind";
                default: return src == Source.Surround51 ? "right" : "behind";
            }
        }

        /// <summary>Virtual speaker direction (azimuth anticlockwise, elevation) for a slot.</summary>
        public static void Direction(int slot, Source src, SpatialConfig c, out double az, out double el)
        {
            el = c.Elevation;
            switch (slot)
            {
                case FL: az = c.FrontAngle; break;
                case FR: az = 360 - c.FrontAngle; break;
                case FC: az = 0; break;
                case SL: az = c.SideAngle; el = 0; break;
                case SR: az = 360 - c.SideAngle; el = 0; break;
                case BL: az = src == Source.Surround51 ? Math.Min(130, c.SideAngle + 20) : c.RearAngle; el = 0; break;
                case BR: az = 360 - (src == Source.Surround51 ? Math.Min(130, c.SideAngle + 20) : c.RearAngle); el = 0; break;
                default: az = 0; break;
            }
        }

        void Rebuild(Source src, SpatialConfig c)
        {
            HrtfSet set;
            try { set = HrtfSet.Get(c.HrtfKind, c.CustomPath); }
            catch { set = HrtfSet.Get(HrtfSet.Ku100, null); }
            bool binaural = src == Source.Surround51 || src == Source.Surround71 || (src == Source.Stereo && c.VirtualizeStereo);
            var irs = new List<float[][]>();
            var slotsUsed = new List<int>();
            var azs = new List<double>();
            bool want360 = c.Render360 && binaural && ContinuousRenderer.Supports(set);
            // The 0.25° table takes a moment to build; play the speakers until it is ready (never build on the audio thread).
            waiting360 = want360 && !ContinuousRenderer.IsReady(set) ? set : null;
            if (waiting360 != null) { ContinuousRenderer.Prepare(set); want360 = false; }
            if (binaural)
            {
                for (int s = 0; s < Slots; s++)
                {
                    if (s == LFE) continue;
                    if (src == Source.Stereo && s != FL && s != FR) continue;
                    if (src == Source.Surround51 && (s == SL || s == SR)) continue;
                    double az, el; Direction(s, src, c, out az, out el);
                    float[] l, r; set.Build(az, el, c.Room, out l, out r);
                    if (want360)
                    {
                        // The direct sound comes from the 360° renderer; the convolver adds only this speaker's room.
                        int d = set.Nearest(az, el);
                        for (int k = 0; k < set.Taps; k++) { l[k] -= set.L[d][k]; r[k] -= set.R[d][k]; }
                    }
                    irs.Add(new[] { l, r }); slotsUsed.Add(s); azs.Add(az);
                }
            }
            if (want360)
            {
                cont.Configure(set, slotsUsed.ToArray(), azs.ToArray());
                cont.Focus = (float)c.Focus360;
                Array.Clear(lateL, 0, B); Array.Clear(lateR, 0, B);
            }
            use360 = want360;
            curSet = set; curRoom = c.Room; objAz = float.NaN;
            if (!want360) Radar.Ring = null;
            bool roomOnly = want360 && c.Room > 0.001;
            conv.SetFilters(binaural && (!want360 || roomOnly) ? slotsUsed.ToArray() : new int[0], roomOnly || !want360 ? irs : new List<float[][]>());
            for (int s = 0; s < Slots; s++)
            {
                string dir = src == Source.Stereo ? null : DirOf(s, src);
                EqProfile p = null; double g = 0;
                if (dir != null) { c.DirEq.TryGetValue(dir, out p); c.DirGainDb.TryGetValue(dir, out g); }
                eq[s].Update(p != null && p.Enabled ? p : null, Fs);
                slotGain[s] = (float)Math.Pow(10, g / 20);
            }
            post.Configure(c, src == Source.Stereo && !c.VirtualizeStereo);
            dyn.Configure(c.FootstepBoost, c.LoudTamer, c.DynSpeed);
            masterGain = (float)Math.Pow(10, c.MasterDb / 20);
            Radar.SetLayout(src, c);
            builtFor = src; builtCfg = c;
        }

        /// <summary>
        /// Processes one block. slots = 8 arrays of B samples in WAVE 7.1 order (unused slots zero).
        /// Writes B stereo samples to left/right.
        /// </summary>
        public void Process(float[][] slots, Source src, float[] left, float[] right)
        {
            bool testing = test.Running;
            if (testing) src = Source.Surround71;
            var c = cfg;
            if (src != builtFor || c != builtCfg || (waiting360 != null && ContinuousRenderer.IsReady(waiting360))) Rebuild(src, c);
            // After Rebuild: the test pans between the speaker angles of the current layout.
            if (testing) test.Fill(slots, B, Radar.SlotAz);
            Current = src;

            bool sphere = test.Sphere && curSet != null && !curSet.SpeakerSet;
            Radar.TestSphere = sphere; if (sphere) { Radar.TestAz = test.Az; Radar.TestEl = test.El; }
            Radar.Analyze(slots, src, B);
            bool stereoDirect = src == Source.Stereo && !c.VirtualizeStereo;
            for (int s = 0; s < Slots; s++)
            {
                var x = slots[s];
                if (!stereoDirect && !sphere) eq[s].Process(x, B);
                float g = slotGain[s];
                if (g != 1f) for (int i = 0; i < B; i++) x[i] *= g;
            }
            dyn.Process(slots, B);

            if (sphere)
            {
                // A point source at (az, el) straight from the measured head response (plus the room), not via speakers.
                if (test.Az != objAz || test.El != objEl)
                {
                    objAz = test.Az; objEl = test.El;
                    float[] l, r; curSet.Build(objAz, objEl, curRoom, out l, out r);
                    objConv.SetFilters(new[] { FC }, new List<float[][]> { new[] { l, r } });
                }
                objConv.Process(slots, outL, outR);
            }
            else if (stereoDirect)
            {
                Array.Copy(slots[FL], outL, B); Array.Copy(slots[FR], outR, B);
            }
            else if (use360)
            {
                // Direct sound placed at its exact angle (Latency samples late), room reflections and bass delayed to match.
                const int D = ContinuousRenderer.Latency;
                cont.Process(slots, outL, outR, B);
                conv.Process(slots, roomL, roomR);
                var lfe = slots[LFE];
                for (int i = 0; i < B; i++) { roomL[i] += lfe[i]; roomR[i] += lfe[i]; }
                for (int i = 0; i < D; i++) { outL[i] += lateL[i]; outR[i] += lateR[i]; }
                for (int i = D; i < B; i++) { outL[i] += roomL[i - D]; outR[i] += roomR[i - D]; }
                Array.Copy(roomL, B - D, lateL, 0, D); Array.Copy(roomR, B - D, lateR, 0, D);
                Radar.Ring = cont.RingDb;
            }
            else
            {
                conv.Process(slots, outL, outR);
                // Bass channel: not localized, straight to both ears.
                var lfe = slots[LFE];
                for (int i = 0; i < B; i++) { outL[i] += lfe[i]; outR[i] += lfe[i]; }
                if (src == Source.Stereo && !conv.Active) { Array.Copy(slots[FL], outL, B); Array.Copy(slots[FR], outR, B); }
                Radar.Ring = null;
            }
            post.Process(outL, outR, B, Radar);

            // Master gain + stereo-linked limiter, ceiling -1 dBFS: instant attack, then the gain holds 25 ms and recovers
            // smoothly (~150 ms). Holding through a bass cycle keeps it from wobbling the waveform (audible distortion).
            bool lim = c.Limiter;
            const float ceiling = 0.89f;
            const int holdSamples = Fs / 40;
            const float release = 0.000139f;   // 1 - exp(-1 / (0.15 s * 48 kHz))
            float minGain = 1;
            for (int i = 0; i < B; i++)
            {
                float l = outL[i] * masterGain, r = outR[i] * masterGain;
                if (lim)
                {
                    float pk = Math.Max(Math.Abs(l), Math.Abs(r)) * limGain;
                    if (pk > ceiling) { limGain *= ceiling / pk; limHold = holdSamples; }
                    else if (limHold > 0) limHold--;
                    else limGain += (1f - limGain) * release;
                    l *= limGain; r *= limGain;
                    if (limGain < minGain) minGain = limGain;
                }
                if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
                if (r > 1f) r = 1f; else if (r < -1f) r = -1f;
                left[i] = l; right[i] = r;
            }
            LimiterDb = (float)(20 * Math.Log10(minGain));
        }
    }

    /// <summary>
    /// Uniformly partitioned overlap-save convolution: N input channels -> left/right ears, one FFT per input
    /// pair per block, accumulation in the frequency domain, one inverse FFT for both ears. Filter changes crossfade.
    /// </summary>
    internal sealed class BinauralConvolver
    {
        const int B = SpatialProcessor.B, N = 2 * B, K = B + 1;
        readonly Fft fft = new Fft(N);
        readonly float[] re = new float[N], im = new float[N];
        readonly float[][] prev = new float[SpatialProcessor.Slots][];
        // Frequency-domain input history per slot: ring of partitions, each K complex bins.
        float[][] histRe = new float[SpatialProcessor.Slots][], histIm = new float[SpatialProcessor.Slots][];
        readonly int[] idleBlocks = new int[SpatialProcessor.Slots];
        int ring, ringSize;
        sealed class FilterSet { public int[] Slots; public int P; public float[][] HRe, HIm; }   // [slotIndex*2+ear][p*K+k]
        volatile FilterSet pending;
        FilterSet cur, old;
        readonly float[] yLRe = new float[K], yLIm = new float[K], yRRe = new float[K], yRIm = new float[K];
        readonly float[] oldL = new float[B], oldR = new float[B];

        public BinauralConvolver()
        {
            for (int s = 0; s < prev.Length; s++) prev[s] = new float[B];
        }

        public bool Active { get { return cur != null && cur.Slots.Length > 0; } }

        public void SetFilters(int[] slots, List<float[][]> irs)
        {
            int maxLen = 1;
            foreach (var p in irs) maxLen = Math.Max(maxLen, Math.Max(p[0].Length, p[1].Length));
            int P = (maxLen + B - 1) / B;
            var fs = new FilterSet { Slots = slots, P = P, HRe = new float[slots.Length * 2][], HIm = new float[slots.Length * 2][] };
            var f = new Fft(N); var r = new float[N]; var i2 = new float[N];
            for (int s = 0; s < slots.Length; s++)
                for (int e = 0; e < 2; e++)
                {
                    var ir = irs[s][e];
                    var hr = new float[P * K]; var hi = new float[P * K];
                    for (int p = 0; p < P; p++)
                    {
                        Array.Clear(r, 0, N); Array.Clear(i2, 0, N);
                        int n = Math.Min(B, ir.Length - p * B);
                        if (n > 0) Array.Copy(ir, p * B, r, 0, n);
                        f.Transform(r, i2, false);
                        Array.Copy(r, 0, hr, p * K, K); Array.Copy(i2, 0, hi, p * K, K);
                    }
                    fs.HRe[s * 2 + e] = hr; fs.HIm[s * 2 + e] = hi;
                }
            pending = fs;
        }

        void EnsureRing(int P)
        {
            if (ringSize >= P) return;
            int size = Math.Max(P, 2);
            for (int s = 0; s < histRe.Length; s++) { histRe[s] = new float[size * K]; histIm[s] = new float[size * K]; idleBlocks[s] = int.MaxValue / 2; }
            ringSize = size; ring = 0;
        }

        public void Process(float[][] slots, float[] outL, float[] outR)
        {
            var p = pending;
            if (p != null) { pending = null; old = cur; cur = p; EnsureRing(cur.P); }
            if (cur == null) { Array.Clear(outL, 0, B); Array.Clear(outR, 0, B); return; }
            ring = (ring + 1) % ringSize;

            // Forward FFTs of [previous block, this block], two slots per complex FFT.
            var used = cur.Slots;
            var all = new List<int>(used);
            if (old != null) foreach (var s in old.Slots) if (!all.Contains(s)) all.Add(s);
            for (int a = 0; a < all.Count; a += 2)
            {
                int s1 = all[a], s2 = a + 1 < all.Count ? all[a + 1] : -1;
                bool z1 = Silent(slots[s1]) && Silent(prev[s1]), z2 = s2 < 0 || (Silent(slots[s2]) && Silent(prev[s2]));
                idleBlocks[s1] = z1 ? idleBlocks[s1] + 1 : 0;
                if (s2 >= 0) idleBlocks[s2] = z2 ? idleBlocks[s2] + 1 : 0;
                int off = ring * K;
                if (z1 && z2)
                {
                    Array.Clear(histRe[s1], off, K); Array.Clear(histIm[s1], off, K);
                    if (s2 >= 0) { Array.Clear(histRe[s2], off, K); Array.Clear(histIm[s2], off, K); }
                }
                else
                {
                    Array.Copy(prev[s1], 0, re, 0, B); Array.Copy(slots[s1], 0, re, B, B);
                    if (s2 >= 0) { Array.Copy(prev[s2], 0, im, 0, B); Array.Copy(slots[s2], 0, im, B, B); } else Array.Clear(im, 0, N);
                    fft.Transform(re, im, false);
                    var h1r = histRe[s1]; var h1i = histIm[s1];
                    for (int k = 0; k < K; k++)
                    {
                        int m = (N - k) & (N - 1);
                        float zr = re[k], zi = im[k], mr = re[m], mi = im[m];
                        h1r[off + k] = 0.5f * (zr + mr); h1i[off + k] = 0.5f * (zi - mi);
                        if (s2 >= 0) { histRe[s2][off + k] = 0.5f * (zi + mi); histIm[s2][off + k] = 0.5f * (mr - zr); }
                    }
                }
            }
            foreach (var s in all) Array.Copy(slots[s], prev[s], B);

            Accumulate(cur, outL, outR);
            if (old != null)
            {
                Accumulate(old, oldL, oldR);
                for (int i = 0; i < B; i++)
                {
                    float t = (i + 0.5f) / B;
                    outL[i] = outL[i] * t + oldL[i] * (1 - t);
                    outR[i] = outR[i] * t + oldR[i] * (1 - t);
                }
                old = null;
            }
        }

        static bool Silent(float[] x)
        {
            for (int i = 0; i < x.Length; i++) if (x[i] != 0f) return false;
            return true;
        }

        void Accumulate(FilterSet f, float[] outL, float[] outR)
        {
            Array.Clear(yLRe, 0, K); Array.Clear(yLIm, 0, K); Array.Clear(yRRe, 0, K); Array.Clear(yRIm, 0, K);
            int P = Math.Min(f.P, ringSize);
            for (int si = 0; si < f.Slots.Length; si++)
            {
                int s = f.Slots[si];
                if (idleBlocks[s] > P + 1) continue;      // nothing left ringing in this speaker
                float[] xr = histRe[s], xi = histIm[s];
                float[] hlr = f.HRe[si * 2], hli = f.HIm[si * 2], hrr = f.HRe[si * 2 + 1], hri = f.HIm[si * 2 + 1];
                for (int p = 0; p < P; p++)
                {
                    int xo = ((ring - p + ringSize) % ringSize) * K, ho = p * K;
                    for (int k = 0; k < K; k++)
                    {
                        float a = xr[xo + k], b = xi[xo + k];
                        float c = hlr[ho + k], d = hli[ho + k];
                        yLRe[k] += a * c - b * d; yLIm[k] += a * d + b * c;
                        c = hrr[ho + k]; d = hri[ho + k];
                        yRRe[k] += a * c - b * d; yRIm[k] += a * d + b * c;
                    }
                }
            }
            // Pack both ears into one inverse FFT: Z = YL + i*YR.
            for (int k = 0; k < K; k++) { re[k] = yLRe[k] - yRIm[k]; im[k] = yLIm[k] + yRRe[k]; }
            for (int k = K; k < N; k++) { int m = N - k; re[k] = yLRe[m] + yRIm[m]; im[k] = -yLIm[m] + yRRe[m]; }
            fft.Transform(re, im, true);
            const float sc = 1f / N;
            for (int i = 0; i < B; i++) { outL[i] = re[B + i] * sc; outR[i] = im[B + i] * sc; }
        }
    }

    /// <summary>
    /// "Footstep booster": 3-band dynamics linked across all speakers (so directions keep their balance).
    /// Quiet sounds (footsteps, reloads, distant movement) are lifted - most in the mid band where footsteps live -
    /// and very loud ones (gunfire, explosions) are pulled down.
    /// </summary>
    internal sealed class MultibandDynamics
    {
        readonly int ch; readonly float fs;
        readonly Biquad[][] lp1, hp1, lp2, hp2;      // [ch][4]: two cascaded Butterworth = Linkwitz-Riley 4th order
        readonly float[] env = new float[3];
        readonly float[] gainDb = new float[3];
        readonly float[][] bands = { new float[SpatialProcessor.B], new float[SpatialProcessor.B], new float[SpatialProcessor.B] };
        volatile float boost = 0.5f, tamer = 0.4f, speed = 0.5f;
        public volatile float LiveBoostDb, LiveCutDb;
        const float X1 = 180, X2 = 5000;

        public MultibandDynamics(int channels, float sampleRate)
        {
            ch = channels; fs = sampleRate;
            lp1 = new Biquad[ch][]; hp1 = new Biquad[ch][]; lp2 = new Biquad[ch][]; hp2 = new Biquad[ch][];
            for (int c = 0; c < ch; c++)
            {
                lp1[c] = new[] { Mk("LP", X1), Mk("LP", X1) }; hp1[c] = new[] { Mk("HP", X1), Mk("HP", X1) };
                lp2[c] = new[] { Mk("LP", X2), Mk("LP", X2) }; hp2[c] = new[] { Mk("HP", X2), Mk("HP", X2) };
            }
            for (int b = 0; b < 3; b++) env[b] = -100;
        }

        Biquad Mk(string t, float f) { return Biquad.Create(new EqFilter { Type = t, Freq = f, Q = 0.7071 }, fs); }

        public void Configure(double footstepBoost, double loudTamer, double dynSpeed)
        {
            boost = (float)footstepBoost; tamer = (float)loudTamer; speed = (float)dynSpeed;
        }

        public bool Enabled { get { return boost > 0.001f || tamer > 0.001f; } }

        public void Process(float[][] x, int n)
        {
            if (!Enabled) { LiveBoostDb = LiveCutDb = 0; return; }
            float bst = boost, tam = tamer;
            // Max upward gain per band (low, mid, high) and the loud-sound compressor.
            float[] maxBoost = { 6 * bst, 15 * bst, 9 * bst };
            const float upThr = -34f, floorDb = -72f, downThr = -22f;
            float ratioDown = 1 + 5 * tam;
            float relMs = 260 - 220 * speed;
            float aAtt = (float)Math.Exp(-1.0 / (0.003 * fs)), aRel = (float)Math.Exp(-1.0 / (relMs * 0.001 * fs));
            // Linked detector: loudest speaker per band, measured on this block.
            var peak = new float[3];
            for (int c = 0; c < ch; c++)
            {
                var s = x[c];
                bool any = false;
                for (int i = 0; i < n; i++) if (s[i] != 0f) { any = true; break; }
                if (!any) continue;
                for (int i = 0; i < n; i++)
                {
                    float v = s[i];
                    float lo = lp1[c][1].Process(lp1[c][0].Process(v));
                    float rest = hp1[c][1].Process(hp1[c][0].Process(v));
                    float mid = lp2[c][1].Process(lp2[c][0].Process(rest));
                    float hi = hp2[c][1].Process(hp2[c][0].Process(rest));
                    bands[0][i] = lo; bands[1][i] = mid; bands[2][i] = hi;
                    float a0 = Math.Abs(lo), a1 = Math.Abs(mid), a2 = Math.Abs(hi);
                    if (a0 > peak[0]) peak[0] = a0; if (a1 > peak[1]) peak[1] = a1; if (a2 > peak[2]) peak[2] = a2;
                }
                // Store split bands back temporarily: x[c] becomes the sum after gains below, so keep them per channel.
                if (split == null || split.Length != ch) { split = new float[ch][][]; }
                if (split[c] == null) split[c] = new[] { new float[n], new float[n], new float[n] };
                Array.Copy(bands[0], split[c][0], n); Array.Copy(bands[1], split[c][1], n); Array.Copy(bands[2], split[c][2], n);
                active[c] = true;
            }
            float liveBoost = 0, liveCut = 0;
            float[] target = new float[3];
            for (int b = 0; b < 3; b++)
            {
                float db = 20f * (float)Math.Log10(Math.Max(peak[b], 1e-7f)) - 3f;   // peak -> approx. level
                // Envelope over blocks: fast up, slower down.
                float blockAtt = (float)Math.Pow(aAtt, n), blockRel = (float)Math.Pow(aRel, n);
                env[b] = db > env[b] ? blockAtt * env[b] + (1 - blockAtt) * db : blockRel * env[b] + (1 - blockRel) * db;
                float e = env[b], g = 0;
                if (e < upThr && e > floorDb - 12)
                {
                    g = Math.Min(maxBoost[b], (upThr - e) * 0.6f);
                    if (e < floorDb) g *= (e - (floorDb - 12)) / 12f;        // fade out near silence: don't pump up hiss
                }
                if (e > downThr) g = -(e - downThr) * (1 - 1 / ratioDown);
                target[b] = g;
            }
            for (int b = 0; b < 3; b++)
            {
                float from = gainDb[b], to = target[b];
                // Cuts react fast (protect ears), boosts rise gently.
                float k = to < from ? 0.6f : 0.25f;
                gainDb[b] = from + (to - from) * k;
                if (gainDb[b] > liveBoost) liveBoost = gainDb[b];
                if (gainDb[b] < liveCut) liveCut = gainDb[b];
            }
            float[] g0 = new float[3], g1 = new float[3];
            for (int b = 0; b < 3; b++) { g0[b] = (float)Math.Pow(10, prevGain[b] / 20); g1[b] = (float)Math.Pow(10, gainDb[b] / 20); prevGain[b] = gainDb[b]; }
            for (int c = 0; c < ch; c++)
            {
                if (!active[c]) continue;
                active[c] = false;
                var s = x[c]; var sp = split[c];
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 1f) / n;
                    s[i] = sp[0][i] * (g0[0] + (g1[0] - g0[0]) * t) + sp[1][i] * (g0[1] + (g1[1] - g0[1]) * t) + sp[2][i] * (g0[2] + (g1[2] - g0[2]) * t);
                }
            }
            LiveBoostDb = liveBoost; LiveCutDb = liveCut;
        }

        float[][][] split;
        readonly bool[] active = new bool[SpatialProcessor.Slots];
        readonly float[] prevGain = new float[3];
    }

    /// <summary>
    /// Short-time spectral stage on the final stereo signal:
    ///  - stereo games: left / center / right EQ by where each frequency sits (level + phase difference), and width;
    ///  - always: direction-cue sharpening - deepens the fine spectral peaks/notches (4-14 kHz) that the ear uses to
    ///    tell above/below and front/back (the cues game HRTFs and our virtual speakers both create).
    /// 256-point STFT, 75% overlap, 4 ms latency; bypassed entirely when nothing here is in use.
    /// </summary>
    internal sealed class StereoPost
    {
        const int N = 256, Hop = 64, K = N / 2 + 1;
        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N];
        readonly float[] inL = new float[N], inR = new float[N], olaL = new float[N], olaR = new float[N];
        readonly float[] qL = new float[Hop], qR = new float[Hop];
        readonly float[] lr = new float[N], li = new float[N], rr = new float[N], ri = new float[N], zr2 = new float[N], zi2 = new float[N];
        readonly float[] lat = new float[K], gSm = new float[K], cueL = new float[K], cueR = new float[K], cueSmL = new float[K], cueSmR = new float[K], magL = new float[K], magR = new float[K];
        float[] dbLeft = new float[K], dbCenter = new float[K], dbRight = new float[K];
        bool panEq, active;
        float width = 1, cue;
        int pos;
        readonly int kLo, kHi, kIpd, kIld;

        public StereoPost()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < K; k++) gSm[k] = 1;
            kLo = 4000 * N / SpatialProcessor.Fs; kHi = 14000 * N / SpatialProcessor.Fs;
            kIpd = 700 * N / SpatialProcessor.Fs; kIld = 1500 * N / SpatialProcessor.Fs;
        }

        public int Latency { get { return active ? N - Hop : 0; } }

        public void Configure(SpatialConfig c, bool stereoDirect)
        {
            var l = new float[K]; var m = new float[K]; var r = new float[K];
            bool any = false;
            if (stereoDirect)
            {
                Curve(c, "left", l); Curve(c, "center", m); Curve(c, "right", r);
                for (int k = 0; k < K; k++) if (Math.Abs(l[k]) > 0.05 || Math.Abs(m[k]) > 0.05 || Math.Abs(r[k]) > 0.05) { any = true; break; }
            }
            dbLeft = l; dbCenter = m; dbRight = r;
            panEq = any;
            width = stereoDirect ? (float)c.Width : 1f;
            cue = (float)c.CueSharpen;
            active = panEq || cue > 0.001f;
        }

        static void Curve(SpatialConfig c, string dir, float[] db)
        {
            EqProfile p; double g;
            c.DirEq.TryGetValue(dir, out p); c.DirGainDb.TryGetValue(dir, out g);
            var filters = p != null && p.Enabled ? p.AllFilters() : new List<EqFilter>();
            var bq = filters.Select(f => Biquad.Create(f, SpatialProcessor.Fs)).ToList();
            for (int k = 0; k < K; k++)
            {
                double f = Math.Max(10, k * (double)SpatialProcessor.Fs / N), s = g;
                foreach (var q in bq) s += q.MagnitudeDb(f, SpatialProcessor.Fs);
                db[k] = (float)s;
            }
        }

        public void Process(float[] l, float[] r, int n, SoundRadar radar)
        {
            if (width != 1f)
                for (int i = 0; i < n; i++)
                {
                    float m = 0.5f * (l[i] + r[i]), s = 0.5f * (l[i] - r[i]) * width;
                    l[i] = m + s; r[i] = m - s;
                }
            if (!active) return;
            for (int i = 0; i < n; i++)
            {
                inL[N - Hop + pos] = l[i]; inR[N - Hop + pos] = r[i];
                l[i] = qL[pos]; r[i] = qR[pos];
                if (++pos == Hop) { pos = 0; Frame(radar); }
            }
        }

        void Frame(SoundRadar radar)
        {
            for (int i = 0; i < N; i++) { lr[i] = inL[i] * win[i]; li[i] = inR[i] * win[i]; }
            // Two real FFTs in one: z = l + i r.
            fft.Transform(lr, li, false);
            for (int k = 0; k < K; k++)
            {
                int m = (N - k) & (N - 1);
                float zr = lr[k], zi = li[k], mr = lr[m], mi = li[m];
                rr[k] = 0.5f * (zi + mi); ri[k] = 0.5f * (mr - zr);     // right
                float aR = 0.5f * (zr + mr), aI = 0.5f * (zi - mi);     // left
                lr[k] = aR; li[k] = aI;
            }
            for (int k = 0; k < K; k++)
            {
                float pl = lr[k] * lr[k] + li[k] * li[k], pr = rr[k] * rr[k] + ri[k] * ri[k];
                magL[k] = pl; magR[k] = pr;
                // Lateral position, + = left. Phase difference below 700 Hz, level difference above 1.5 kHz.
                float latIld = (float)(10 * Math.Log10((pl + 1e-12) / (pr + 1e-12)) / 15.0);
                float cr = lr[k] * rr[k] + li[k] * ri[k], ci = li[k] * rr[k] - lr[k] * ri[k];
                float w = 2 * (float)Math.PI * k * SpatialProcessor.Fs / N;
                float latIpd = k == 0 ? 0 : (float)(Math.Atan2(ci, cr) / (w * 0.00066));
                float t = k <= kIpd ? 0 : k >= kIld ? 1 : (float)(k - kIpd) / (kIld - kIpd);
                float v = latIpd * (1 - t) + latIld * t;
                if (v > 1) v = 1; else if (v < -1) v = -1;
                lat[k] = 0.6f * lat[k] + 0.4f * v;
            }

            for (int k = 0; k < K; k++) { cueL[k] = 0; cueR[k] = 0; }
            if (cue > 0.001f)
            {
                // Spectral contrast: fine detail minus a ~1/3-octave smoothed version, expanded.
                for (int k = kLo; k <= kHi && k < K; k++)
                {
                    int half = Math.Max(1, k / 12);
                    double sl = 0, sr = 0; int cnt = 0;
                    for (int j = k - half; j <= k + half; j++) { if (j < 1 || j >= K) continue; sl += Math.Log10(magL[j] + 1e-14); sr += Math.Log10(magR[j] + 1e-14); cnt++; }
                    double dl = 10 * (Math.Log10(magL[k] + 1e-14) - sl / cnt), dr = 10 * (Math.Log10(magR[k] + 1e-14) - sr / cnt);
                    float edge = Math.Min(1f, Math.Min(k - kLo, kHi - k) / 6f + 0.2f);
                    cueL[k] = (float)Math.Max(-8, Math.Min(6, dl * cue * edge));
                    cueR[k] = (float)Math.Max(-8, Math.Min(6, dr * cue * edge));
                    cueL[k] = cueSmL[k] = 0.5f * cueSmL[k] + 0.5f * cueL[k];
                    cueR[k] = cueSmR[k] = 0.5f * cueSmR[k] + 0.5f * cueR[k];
                }
            }
            for (int k = 0; k < K; k++)
            {
                float dbz = 0;
                if (panEq)
                {
                    float p = lat[k];
                    dbz = p >= 0 ? (1 - p) * dbCenter[k] + p * dbLeft[k] : (1 + p) * dbCenter[k] - p * dbRight[k];
                }
                float g = (float)Math.Pow(10, dbz / 20);
                gSm[k] = 0.5f * gSm[k] + 0.5f * g;
                float gl = gSm[k], gr = gSm[k];
                if (cueL[k] != 0) gl *= (float)Math.Pow(10, cueL[k] / 20);
                if (cueR[k] != 0) gr *= (float)Math.Pow(10, cueR[k] / 20);
                lr[k] *= gl; li[k] *= gl; rr[k] *= gr; ri[k] *= gr;
            }
            // Back to time domain, both channels in one inverse FFT.
            for (int k = 0; k < K; k++) { zr2[k] = lr[k] - ri[k]; zi2[k] = li[k] + rr[k]; }
            for (int k = K; k < N; k++) { int m = N - k; zr2[k] = lr[m] + ri[m]; zi2[k] = -li[m] + rr[m]; }
            fft.Transform(zr2, zi2, true);
            const float sc = 1f / (N * 2f);   // sqrt-Hann^2 at 75% overlap sums to 2
            for (int i = 0; i < N; i++) { olaL[i] += zr2[i] * sc * win[i]; olaR[i] += zi2[i] * sc * win[i]; }
            Array.Copy(olaL, 0, qL, 0, Hop); Array.Copy(olaR, 0, qR, 0, Hop);
            Array.Copy(olaL, Hop, olaL, 0, N - Hop); Array.Clear(olaL, N - Hop, Hop);
            Array.Copy(olaR, Hop, olaR, 0, N - Hop); Array.Clear(olaR, N - Hop, Hop);
            Array.Copy(inL, Hop, inL, 0, N - Hop); Array.Copy(inR, Hop, inR, 0, N - Hop);
        }
    }

    /// <summary>
    /// Live map of where game sounds come from. Surround input: level per virtual speaker plus "blips" for sudden
    /// sounds (footsteps, shots), placed by the energy-weighted direction of what just jumped. Stereo input: left/right
    /// position from level and phase differences (front half only - stereo can't say front from back).
    /// </summary>
    internal sealed class SoundRadar
    {
        public struct Blip { public float Az; public float Db; public long Time; }
        readonly object gate = new object();
        readonly List<Blip> blips = new List<Blip>();
        public readonly float[] SlotDb = new float[SpatialProcessor.Slots];
        public readonly float[] SlotAz = new float[SpatialProcessor.Slots];
        public readonly bool[] SlotUsed = new bool[SpatialProcessor.Slots];
        readonly float[] fast = new float[SpatialProcessor.Slots], slow = new float[SpatialProcessor.Slots];
        readonly Biquad[] hp = new Biquad[SpatialProcessor.Slots];
        long lastBlip;
        public volatile bool StereoMode;
        /// <summary>Sphere test: where the test footstep is (azimuth, elevation in degrees).</summary>
        public volatile bool TestSphere;
        public volatile float TestAz, TestEl;
        /// <summary>360° mode: level per 5° sector around the head (dBFS), else null.</summary>
        public volatile float[] Ring;

        public SoundRadar()
        {
            for (int i = 0; i < hp.Length; i++) { hp[i] = Biquad.HighPass(150, SpatialProcessor.Fs); fast[i] = slow[i] = -100; SlotDb[i] = -100; }
        }

        public void SetLayout(SpatialProcessor.Source src, SpatialConfig c)
        {
            StereoMode = src == SpatialProcessor.Source.Stereo;
            for (int s = 0; s < SpatialProcessor.Slots; s++)
            {
                double az, el; SpatialProcessor.Direction(s, src, c, out az, out el);
                SlotAz[s] = (float)az;
                SlotUsed[s] = s != SpatialProcessor.LFE && (src == SpatialProcessor.Source.Surround71 ||
                    (src == SpatialProcessor.Source.Surround51 && s != SpatialProcessor.SL && s != SpatialProcessor.SR) ||
                    (src == SpatialProcessor.Source.Stereo && (s == SpatialProcessor.FL || s == SpatialProcessor.FR)));
            }
        }

        public List<Blip> Snapshot(long now, long maxAgeMs)
        {
            lock (gate)
            {
                blips.RemoveAll(b => now - b.Time > maxAgeMs);
                return new List<Blip>(blips);
            }
        }

        void Add(float az, float db)
        {
            long now = Environment.TickCount;
            lock (gate)
            {
                blips.Add(new Blip { Az = az, Db = db, Time = now });
                if (blips.Count > 40) blips.RemoveAt(0);
            }
            lastBlip = now;
        }

        void AnalyzeStereo(float[] l, float[] r, int n)
        {
            double el = 0, er = 0;
            for (int i = 0; i < n; i++) { float a = hp[0].Process(l[i]), b = hp[1].Process(r[i]); el += a * a; er += b * b; }
            float db = (float)(10 * Math.Log10((el + er) / (2.0 * n) + 1e-12));
            fast[0] = db > fast[0] ? db : 0.7f * fast[0] + 0.3f * db;
            slow[0] = 0.97f * slow[0] + 0.03f * db;
            double p = (el - er) / (el + er + 1e-12);           // + = left
            SlotDb[SpatialProcessor.FL] = (float)(10 * Math.Log10(el / n + 1e-12));
            SlotDb[SpatialProcessor.FR] = (float)(10 * Math.Log10(er / n + 1e-12));
            if (db > -62 && db - slow[0] > 6 && Environment.TickCount - lastBlip > 90) Add((float)(p * 90), db);
        }

        public void Analyze(float[][] slots, SpatialProcessor.Source src, int n)
        {
            if (src == SpatialProcessor.Source.Stereo) { AnalyzeStereo(slots[SpatialProcessor.FL], slots[SpatialProcessor.FR], n); return; }
            double vx = 0, vy = 0, jumpMax = -100;
            for (int s = 0; s < SpatialProcessor.Slots; s++)
            {
                if (!SlotUsed[s]) { SlotDb[s] = -100; continue; }
                double e = 0; var x = slots[s];
                for (int i = 0; i < n; i++) { float v = hp[s].Process(x[i]); e += v * v; }
                float db = (float)(10 * Math.Log10(e / n + 1e-12));
                fast[s] = db > fast[s] ? db : 0.7f * fast[s] + 0.3f * db;
                slow[s] = 0.97f * slow[s] + 0.03f * db;
                SlotDb[s] = fast[s];
                float jump = db - slow[s];
                if (db > -62 && jump > 6)
                {
                    double w = Math.Pow(10, db / 10) * (jump - 6);
                    double a = SlotAz[s] * Math.PI / 180;
                    vx += w * Math.Cos(a); vy += w * Math.Sin(a);
                    if (db > jumpMax) jumpMax = db;
                }
            }
            if (jumpMax > -100 && Environment.TickCount - lastBlip > 90 && vx * vx + vy * vy > 0)
                Add((float)(Math.Atan2(vy, vx) * 180 / Math.PI), (float)jumpMax);
        }

    }

    /// <summary>
    /// Test footsteps. Rings (the Test footsteps button): someone walks a full circle around you at ear level, then a
    /// full circle on the floor above you, then one on the floor below you - every step a point source at its exact
    /// angle and height, straight from the measured head responses. Speakers: three steps from each virtual speaker.
    /// 360° walk: a step every 15°, panned between speakers the way games pan. Sphere: steps above, level with and
    /// below you at the front, left, behind and right, then overhead and underfoot.
    /// </summary>
    internal sealed class TestSignal
    {
        public enum Mode { Speakers, Walk360, Sphere, Rings }
        /// <summary>Rings: the three floors (degrees of elevation) and how many steps make one circle.</summary>
        public static readonly float[] RingLevels = { 0f, 45f, -45f };
        public const int RingSteps = 16;
        static readonly int[] order = { SpatialProcessor.FC, SpatialProcessor.FL, SpatialProcessor.SL, SpatialProcessor.BL, SpatialProcessor.BR, SpatialProcessor.SR, SpatialProcessor.FR };
        static readonly float[][] Steps = FootstepSynth.Bank(10, 11);
        // Sphere route: at each side level / above / below, then straight overhead and underfoot.
        static readonly float[][] SpherePoints = BuildSphere();
        volatile bool running;
        Mode mode;
        long t;
        public string Label = "";
        public bool Running { get { return running; } }
        /// <summary>A single point source at (Az, El) right now (sphere and rings tests).</summary>
        public bool Sphere { get { return running && (mode == Mode.Sphere || mode == Mode.Rings); } }
        public Mode CurrentMode { get { return mode; } }
        public float Az, El;               // sphere mode: where the current step is
        const int StepLen = 21600;          // 0.45 s per step (a natural walking pace)
        const int WalkSteps = 24;

        int panStep = -1, panA, panB; float panGa, panGb;

        public void Start(Mode m) { t = 0; mode = m; panStep = -1; running = true; }
        public void Stop() { running = false; Label = ""; }

        static float[][] BuildSphere()
        {
            var l = new System.Collections.Generic.List<float[]>();
            foreach (float az in new[] { 0f, 90f, 180f, 270f })
                foreach (float el in new[] { 0f, 45f, -45f })
                    l.Add(new[] { az, el });
            l.Add(new[] { 0f, 90f });
            l.Add(new[] { 0f, -90f });
            return l.ToArray();
        }

        static string Where(float az, float el)
        {
            if (el >= 89) return "Directly above you";
            if (el <= -89) return "Directly below you";
            string side = az == 0 ? "in front" : az == 90 ? "to your left" : az == 180 ? "behind you" : "to your right";
            return (el > 0 ? "Above, " : el < 0 ? "Below, " : "Level, ") + side + (el != 0 ? " (" + (el > 0 ? "+" : "") + el + "°)" : "");
        }

        /// <summary>Power-normalised pairwise panning gains (2D VBAP) for a direction, over the 7.1 ring.</summary>
        public static void Pan(double az, float[] slotAz, out int a, out int b, out float ga, out float gb)
        {
            var ring = order.OrderBy(s => (slotAz[s] % 360 + 360) % 360).ToArray();
            double x = ((az % 360) + 360) % 360;
            a = ring[ring.Length - 1]; b = ring[0];
            for (int i = 0; i < ring.Length; i++)
            {
                int s1 = ring[i], s2 = ring[(i + 1) % ring.Length];
                double a1 = (slotAz[s1] % 360 + 360) % 360, a2 = (slotAz[s2] % 360 + 360) % 360;
                if (a2 <= a1) a2 += 360;
                double xx = x < a1 ? x + 360 : x;
                if (xx >= a1 && xx <= a2) { a = s1; b = s2; break; }
            }
            double r = x * Math.PI / 180, ra = slotAz[a] * Math.PI / 180, rb = slotAz[b] * Math.PI / 180;
            double det = Math.Cos(ra) * Math.Sin(rb) - Math.Sin(ra) * Math.Cos(rb);
            if (Math.Abs(det) < 1e-9) { ga = 1; gb = 0; return; }   // both speakers at one angle
            double g1 = (Math.Cos(r) * Math.Sin(rb) - Math.Sin(r) * Math.Cos(rb)) / det;
            double g2 = (Math.Cos(ra) * Math.Sin(r) - Math.Sin(ra) * Math.Cos(r)) / det;
            g1 = Math.Max(0, g1); g2 = Math.Max(0, g2);
            double nrm = Math.Sqrt(g1 * g1 + g2 * g2) + 1e-12;
            ga = (float)(g1 / nrm); gb = (float)(g2 / nrm);
        }

        /// <summary>
        /// Fills the 7.1 slots for this block. Sphere mode puts the mono footstep in the centre slot; the processor
        /// renders it at (Az, El) with the full-sphere head response instead of the speakers.
        /// </summary>
        public void Fill(float[][] slots, int n, float[] slotAz)
        {
            for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, n);
            for (int i = 0; i < n; i++, t++)
            {
                int step = (int)(t / StepLen);
                int slot, slot2 = -1; float g1 = 1, g2 = 0;
                if (mode == Mode.Walk360)
                {
                    if (step >= WalkSteps) { running = false; Label = ""; return; }
                    int az = step * 360 / WalkSteps;
                    if (step != panStep) { Pan(az, slotAz, out panA, out panB, out panGa, out panGb); panStep = step; }
                    slot = panA; slot2 = panB; g1 = panGa; g2 = panGb;
                    Label = az == 0 ? "Front (0°)" : az == 180 ? "Behind (180°)" : az < 180 ? az + "° left" : (360 - az) + "° right";
                }
                else if (mode == Mode.Sphere)
                {
                    int p = step / 2;
                    if (p >= SpherePoints.Length) { running = false; Label = ""; return; }
                    Az = SpherePoints[p][0]; El = SpherePoints[p][1];
                    slot = SpatialProcessor.FC;
                    Label = Where(Az, El);
                }
                else if (mode == Mode.Rings)
                {
                    int ringNo = step / RingSteps;
                    if (ringNo >= RingLevels.Length) { running = false; Label = ""; return; }
                    // Clockwise from the front (azimuths run anticlockwise): front, front-right, right, behind...
                    int az = (360 - (step % RingSteps) * 360 / RingSteps) % 360;
                    Az = az; El = RingLevels[ringNo];
                    slot = SpatialProcessor.FC;
                    string floor = El > 0 ? "Floor above you" : El < 0 ? "Floor below you" : "Ear level";
                    Label = floor + ": " + (az == 0 ? "in front" : az == 180 ? "behind you" : az < 180 ? az + "° left" : (360 - az) + "° right");
                }
                else
                {
                    int spk = step / 3;
                    if (spk >= order.Length) { running = false; Label = ""; return; }
                    slot = order[spk];
                    Label = SpatialProcessor.SlotNames[slot];
                }
                int k = (int)(t % StepLen);
                var sound = Steps[step % Steps.Length];
                float v = k < sound.Length ? sound[k] : 0f;
                slots[slot][i] = v * g1;
                if (slot2 >= 0) slots[slot2][i] += v * g2;
            }
        }
    }

    /// <summary>
    /// Synthesised footsteps that sound like a shoe on a floor, not a drum: a heel strike (pitched thud that drops in
    /// pitch, floor-board resonance, a short contact click), the ball of the foot rolling down ~60 ms later, and a gritty
    /// shoe scuff - with natural decays (no cut-off). Every step differs slightly; left and right feet alternate.
    /// </summary>
    internal static class FootstepSynth
    {
        const int Fs = SpatialProcessor.Fs;

        struct Bp
        {
            double b0, b2, a1, a2, x1, x2, y1, y2;
            public Bp(double f, double q)
            {
                double w = 2 * Math.PI * f / Fs, al = Math.Sin(w) / (2 * q), a0 = 1 + al;
                b0 = al / a0; b2 = -al / a0; a1 = -2 * Math.Cos(w) / a0; a2 = (1 - al) / a0;
                x1 = x2 = y1 = y2 = 0;
            }
            public double Run(double x)
            {
                double y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
        }

        public static float[][] Bank(int count, int seed)
        {
            var r = new Random(seed);
            var bank = new float[count][];
            for (int i = 0; i < count; i++) bank[i] = Make(r, i % 2 == 1);
            return bank;
        }

        static float[] Make(Random r, bool leftFoot)
        {
            int n = (int)(0.42 * Fs);
            var o = new double[n];
            double f0 = 78 * (1 + 0.16 * (r.NextDouble() - 0.5)) * (leftFoot ? 1.04 : 1.0);
            double toeAt = 0.055 + 0.02 * r.NextDouble();
            double heelGain = 0.9 + 0.2 * r.NextDouble(), toeGain = 0.45 + 0.15 * r.NextDouble();
            var res = new Bp(190 + 60 * r.NextDouble(), 3.5);
            var res2 = new Bp(420 + 120 * r.NextDouble(), 4.5);
            var click = new Bp(1900 + 600 * r.NextDouble(), 1.1);
            var click2 = new Bp(3000 + 800 * r.NextDouble(), 1.2);
            var grit = new Bp(4200 + 1500 * r.NextDouble(), 0.8);
            double ph = 0, ph2 = 0, grain = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Fs;
                double w = r.NextDouble() * 2 - 1;
                // Heel: soft attack, quick pitch drop, ~30 ms body.
                double eh = (1 - Math.Exp(-t / 0.0012)) * Math.Exp(-t / 0.028);
                ph += 2 * Math.PI * f0 * (1 + 0.9 * Math.Exp(-t / 0.009)) / Fs;
                double thud = Math.Sin(ph) * eh * heelGain;
                double board = res.Run(w) * Math.Exp(-t / 0.055) * 2.2 * heelGain + res2.Run(w) * Math.Exp(-t / 0.035) * 1.2 * heelGain;
                double tick = click.Run(w) * Math.Exp(-t / 0.0035) * 1.6 * heelGain;
                // Ball of the foot rolling down.
                double tt = t - toeAt, toe = 0;
                if (tt >= 0)
                {
                    double et = (1 - Math.Exp(-tt / 0.002)) * Math.Exp(-tt / 0.022);
                    ph2 += 2 * Math.PI * f0 * 1.35 / Fs;
                    toe = (Math.Sin(ph2) * et * 0.8 + click2.Run(w) * Math.Exp(-tt / 0.003) * 1.4) * toeGain;
                }
                else click2.Run(w);
                // Shoe scuff: sparse grains of grit, rising over 30 ms then fading over ~100 ms.
                if (r.NextDouble() < 0.06) grain = 0.4 + r.NextDouble();
                grain *= 0.985;
                double es = Smooth((t - 0.008) / 0.03) * Math.Exp(-Math.Max(0, t - 0.04) / 0.055);
                double scuff = grit.Run(w) * grain * es * 0.55;
                o[i] = thud * 0.65 + board * 0.22 + tick * 0.35 + toe * 0.55 + scuff;
            }
            // Gentle fade over the last 90 ms (the decays are already near silence there) and normalize.
            int fade = (int)(0.09 * Fs);
            double peak = 1e-9;
            for (int i = 0; i < n; i++) { if (i > n - fade) o[i] *= 0.5 + 0.5 * Math.Cos(Math.PI * (i - (n - fade)) / fade); peak = Math.Max(peak, Math.Abs(o[i])); }
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = (float)(o[i] / peak * 0.5);
            return f;
        }

        static double Smooth(double x) { if (x <= 0) return 0; if (x >= 1) return 1; return x * x * (3 - 2 * x); }
    }
}
