// Performance across hardware tiers, automated. Each tier is emulated on this PC in its own process: only so many CPU
// threads (process affinity), a "game" keeping part of them busy, and the graphics card used fully, with the Low VRAM
// models, or not at all. In each tier StreamLite's work runs in real time, paced like the live engines:
//   - the mic chain (3 ms packets, the noise remover this tier gets by default) with two added mics mixed in
//   - 3D audio (7.1 -> true 360 binaural, 5.3 ms blocks) with two added outputs, each with its EQ and drift ring
//   - StreamLite Camera (the effects graph at 30 fps)
// and measures dropouts (a packet later than the buffer can bridge), processing time, latency, CPU, RAM and video
// memory. Probes run each noise engine and camera graph on its own per tier, which is what the defaults (HardwareTuning)
// are tuned from. Results are compared with the previous release's (perf/baseline.json) and can be saved there.
// Usage: run.ps1 PerfTest [quick] [tiers=low,mid,high] [save] [baseline=<file>]   (child mode: PerfTest child <tier> <seconds>)
// An earlier release: check out its tag (git worktree), copy this file into its test folder and run it there with
// baseline= pointing at this repo's perf/baseline.json - the same workloads, measured on its code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Camera;

public class PerfProbe { public string What, Ran, Note; public double CpuPct, AvgMs, P99Ms, Fps, DfnMs, Headroom; public int Misses, Late, Frames; }

public class PerfResult
{
    public string Version, Tier, Desc, Machine, Engine, EngineRan, Fallback, Camera;
    public int Threads, LoadThreads, Seconds;
    public double CpuPct, RamMb, VramMb;
    public int MicFrames, MicMisses, DfnLate, GameBlocks, GameMisses, CamOffered, CamFrames;
    public double MicHeadroom, GameHeadroom, CamHeadroom, DfnMs;
    public double MicAvgMs, MicP99Ms, MicMaxLateMs, MicLatencyMs, GameAvgMs, GameP99Ms, GameMaxLateMs, GameLatencyMs, CamFps, CamAvgMs;
    public List<PerfProbe> Probes = new List<PerfProbe>();
}

public class PerfBaseline { public List<PerfResult> Results = new List<PerfResult>(); }

class PerfTest
{
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    const int Fs = 48000;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double NowMs { get { return Clock.Elapsed.TotalMilliseconds; } }
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    sealed class Tier { public string Name, Desc, Gpu; public int Threads, Load; }

    static List<Tier> Tiers()
    {
        int all = Environment.ProcessorCount;
        return new List<Tier>
        {
            new Tier { Name = "low", Threads = 2, Load = 1, Gpu = "none", Desc = "2 CPU threads, a game busy on one, no usable graphics card" },
            new Tier { Name = "mid", Threads = 4, Load = 2, Gpu = "low", Desc = "4 CPU threads, a game busy on two, a 4 GB graphics card (Low VRAM)" },
            new Tier { Name = "high", Threads = all, Load = all / 2, Gpu = "full", Desc = all + " CPU threads, a game busy on half, the full graphics card" },
        };
    }

    // ------------------------------------------------------------------ helpers

    [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    // CPU use from cycle counters: Windows' CPU times are sampled every 15.6 ms and miss the short bursts of
    // high-priority audio threads; the cycle counters count every one.
    static double ThreadCycles() { ulong c; QueryThreadCycleTime(GetCurrentThread(), out c); return c; }
    static double ProcessCycles() { ulong c; QueryProcessCycleTime(GetCurrentProcess(), out c); return c; }
    static double cyclesPerMs;

    /// <summary>How many cycles one CPU thread counts per millisecond (spins 300 ms on its own).</summary>
    static void Calibrate()
    {
        double best = 0;
        for (int i = 0; i < 3; i++)
        {
            double c0 = ThreadCycles(), w0 = NowMs, x = 1;
            while (NowMs - w0 < 100) x = Math.Sqrt(x + 1);
            best = Math.Max(best, (ThreadCycles() - c0) / (NowMs - w0));
            if (x < 0) Console.WriteLine(x);
        }
        cyclesPerMs = best;
    }

    /// <summary>A game: threads at normal priority, busy all the time (StreamLite's audio threads still run first, as live).</summary>
    sealed class GameLoad
    {
        volatile bool stop;
        readonly List<Thread> threads = new List<Thread>();
        readonly double[] cycles;
        public GameLoad(int n)
        {
            cycles = new double[n];
            for (int i = 0; i < n; i++)
            {
                int me = i;
                var t = new Thread(() =>
                {
                    double x = 1;
                    while (!stop) { for (int k = 0; k < 5000; k++) x = Math.Sqrt(x * 1.0000001 + k); Volatile.Write(ref cycles[me], ThreadCycles()); }
                    if (x < 0) Console.WriteLine(x);
                }) { IsBackground = true, Priority = ThreadPriority.Normal, Name = "game load" };
                threads.Add(t); t.Start();
            }
        }
        /// <summary>The game's cycles so far (each thread publishes its count every few microseconds).</summary>
        public double Cycles { get { double s = 0; for (int i = 0; i < cycles.Length; i++) s += Volatile.Read(ref cycles[i]); return s; } }
        public void Stop() { stop = true; foreach (var t in threads) t.Join(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [ThreadStatic] static IntPtr timer;

    /// <summary>
    /// Waits until a time (ms on Clock) on a high-resolution timer - no spinning, so the CPU measured is the work
    /// itself, like the live engines that wake on the audio device's events.
    /// </summary>
    static void WaitUntil(double t)
    {
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2 /* HIGH_RESOLUTION */, 0x1F0003);
        double left = t - NowMs;
        if (left <= 0.05) return;
        if (timer == IntPtr.Zero) { Thread.Sleep((int)Math.Ceiling(left)); return; }
        long due = -(long)(left * 10000);
        SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
        WaitForSingleObject(timer, 0xFFFFFFFF);
    }

    static double P99(List<double> v) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))]; }

    /// <summary>Speech-like sound (voiced bursts with pauses) over pink-ish room noise, 8 s, looped.</summary>
    static float[] Voice()
    {
        var r = new Random(1); var x = new float[Fs * 8]; double b = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Fs, seg = t % 1.6, f0 = 110 + 25 * Math.Sin(t * 1.3), v = 0;
            if (seg < 1.1) { double env = Math.Min(1, seg * 30) * Math.Min(1, (1.1 - seg) * 30); for (int k = 1; k < 14; k++) v += Math.Sin(2 * Math.PI * k * f0 * t) / k; v *= 0.15 * env; }
            b = 0.98 * b + 0.02 * (r.NextDouble() * 2 - 1);
            x[i] = (float)(v + 0.01 * b + 0.002 * (r.NextDouble() * 2 - 1));
        }
        return x;
    }

    // ------------------------------------------------------------------ the mic chain, paced like the capture thread

    sealed class MicRun
    {
        public List<double> Ms = new List<double>(); public int Frames, Misses, DfnLate; public double MaxLate, DelayMs, DfnMs;
        /// <summary>How many times slower a CPU core could be and this still keeps real time (each 3 ms packet done in 3 ms on average, the slowest 1% within the buffer, DeepFilterNet within its 10 ms frame).</summary>
        public double Headroom { get { if (Ms.Count == 0) return 0; double h = Math.Min(3.0 / Ms.Average(), 13.0 / P99(Ms)); return Ran == NoiseEngines.DfnCpu && DfnMs > 0 ? Math.Min(h, 10.0 / DfnMs) : h; } }
        public volatile bool Stop; public Thread T; public string Error;
        /// <summary>The noise remover that really ran, and why another one stood in (StreamLite's own fallbacks).</summary>
        public string Ran, Fallback;
        readonly string engine; readonly bool lowVram; readonly int extraMics;
        public MicRun(string engine, bool lowVram, int extraMics) { this.engine = engine; this.lowVram = lowVram; this.extraMics = extraMics; }

        public void Start(ManualResetEvent ready, ManualResetEvent go)
        {
            T = new Thread(() =>
            {
                try { Run(ready, go); }
                catch (Exception ex) { Error = ex.Message; ready.Set(); }
            }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "perf mic" };
            T.Start();
        }

        void Run(ManualResetEvent ready, ManualResetEvent go)
        {
            const int P = 144;                              // 3 ms exclusive-mode packets
            var S = new AppSettings { NsEngine = engine, LowVram = lowVram };
            var chain = new VoiceChain(); chain.Configure(S);
            var mixer = new MicMixer { AutoMix = true };
            var mics = Enumerable.Range(0, extraMics).Select(i => new ExtraMic("perf" + i, 0)).ToArray();
            mixer.Use(mics);
            var voice = Voice(); var other = Voice().Reverse().ToArray();
            var buf = new float[P]; var pkt = new float[480];
            IntPtr mm = AudioEngine.ProAudio();
            // Warm-up at the real pace (faster than real time, DeepFilterNet would rightly give up as "too slow"):
            // while DeepFilterNet loads in the background, then one more second.
            double w0 = NowMs, loaded = -1; long wk = 0;
            while (NowMs - w0 < 30000)
            {
                WaitUntil(w0 + wk * 3.0); wk++;
                Array.Copy(voice, (int)(wk * P % (voice.Length - P)), buf, 0, P); mixer.Mix(buf, P); chain.Process(buf, P, false);
                var d = chain.Suppressor.DfnEngine;
                bool done = chain.Suppressor.Dfn == 0 || (d != null && (d.Ready || d.Error != null));
                if (done && loaded < 0) loaded = NowMs;
                if (loaded >= 0 && NowMs - loaded > 1000) break;
            }
            var dfn = chain.Suppressor.DfnEngine;
            int late0 = dfn != null ? dfn.Late : 0;
            DelayMs = chain.Suppressor.DelayMs;
            ready.Set(); go.WaitOne();
            double start = NowMs; long k2 = 0; int pos = 0, fed = 0;
            while (!Stop)
            {
                double arrival = start + k2 * 3.0;
                WaitUntil(arrival);
                double t0 = NowMs;
                // The added mics' 10 ms packets arrive on their own threads; here they're handed over as their time comes.
                while (fed * 480 <= (k2 + 1) * P) { int o = (fed * 480) % (other.Length - 480); Array.Copy(other, o, pkt, 0, 480); foreach (var m in mics) m.Feed(pkt, 480); fed++; }
                Array.Copy(voice, pos, buf, 0, P); pos = (pos + P) % (voice.Length - P);
                mixer.Mix(buf, P);
                chain.Process(buf, P, false);
                double t1 = NowMs;
                Ms.Add(t1 - t0); Frames++;
                double late = t1 - (arrival + 3.0);
                if (late > MaxLate) MaxLate = late;
                if (late > 10) Misses++;           // more than the ~10 ms the virtual mic's buffer bridges
                k2++;
            }
            var now = chain.Suppressor.DfnEngine;
            DfnLate = now != null && now == dfn ? dfn.Late - late0 : now != null ? now.Late : 0;
            DfnMs = now != null ? now.AvgMs : 0;
            Ran = now != null && now.Ready && now.Error == null ? (now.Gpu ? NoiseEngines.DfnGpu : NoiseEngines.DfnCpu) : engine == NoiseEngines.Classic ? NoiseEngines.Classic : NoiseEngines.Ai;
            Fallback = chain.Suppressor.FallbackNote;
            if (Ran != engine && Fallback == null && dfn != null && dfn.Error != null) Fallback = dfn.Error;
            chain.Suppressor.ReleaseEngine();
        }
    }

    // ------------------------------------------------------------------ 3D audio + added outputs, paced like the render thread

    sealed class GameRun
    {
        public List<double> Ms = new List<double>(); public int Blocks, Misses; public double MaxLate, LatencyMs;
        public double Headroom { get { const double period = SpatialProcessor.B * 1000.0 / Fs; return Ms.Count == 0 ? 0 : Math.Min(period / Ms.Average(), (period + 10) / P99(Ms)); } }
        public volatile bool Stop; public Thread T; public string Error;
        readonly int outputs;
        public GameRun(int outputs) { this.outputs = outputs; }

        public void Start(ManualResetEvent ready, ManualResetEvent go)
        {
            T = new Thread(() => { try { Run(ready, go); } catch (Exception ex) { Error = ex.Message; ready.Set(); } }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "perf 3D" };
            T.Start();
        }

        void Run(ManualResetEvent ready, ManualResetEvent go)
        {
            const int B = SpatialProcessor.B;
            var g = new AppSettings().Game; g.Repair(); GamePresets.Apply(g, "Footsteps (competitive)");
            var cfg = g.ToConfig();
            var sp = new SpatialProcessor(); sp.Configure(cfg);
            var set = HrtfSet.Get(cfg.HrtfKind, cfg.CustomPath);
            for (int w = 0; w < 1500 && !ContinuousRenderer.IsReady(set); w++) Thread.Sleep(10);
            var src = new float[SpatialProcessor.Slots][]; var rnd = new Random(5);
            for (int s = 0; s < src.Length; s++) { src[s] = new float[Fs * 4]; double bb = 0; for (int i = 0; i < src[s].Length; i++) { bb = 0.97 * bb + 0.03 * (rnd.NextDouble() * 2 - 1); src[s][i] = s == SpatialProcessor.LFE ? 0 : (float)(bb * 0.3 * (1 + Math.Sin(i * 0.0007 * (s + 1)))); } }
            var slots = new float[SpatialProcessor.Slots][]; for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B];
            // Each added output: its own copy, its EQ (10 bands, both sides) and a drift-corrected ring to its device.
            var eqs = new List<EqChain[]>(); var rings = new List<StereoRing>(); var ol = new float[B]; var orr = new float[B]; var inter = new float[B * 2];
            for (int o = 0; o < outputs; o++)
            {
                var e = new EqProfile(); for (int k = 0; k < 10; k++) e.Bands[k] = (k % 3) - 1;
                var el = new EqChain(); var er = new EqChain(); el.Update(e, Fs); er.Update(e, Fs);
                eqs.Add(new[] { el, er }); rings.Add(new StereoRing(48, 480));
            }
            int pos = 0;
            for (int k = 0; k < 200; k++) Block(sp, src, slots, l, r, ref pos, eqs, rings, ol, orr, inter);   // warm up (and fill the rings)
            LatencyMs = B * 1000.0 / Fs + sp.ExtraLatency * 1000.0 / Fs;
            IntPtr mm = AudioEngine.ProAudio();
            ready.Set(); go.WaitOne();
            double start = NowMs, period = B * 1000.0 / Fs; long n = 0;
            while (!Stop)
            {
                double arrival = start + n * period;
                WaitUntil(arrival);
                double t0 = NowMs;
                Block(sp, src, slots, l, r, ref pos, eqs, rings, ol, orr, inter);
                double t1 = NowMs;
                Ms.Add(t1 - t0); Blocks++;
                double late = t1 - (arrival + period);
                if (late > MaxLate) MaxLate = late;
                if (late > 10) Misses++;
                n++;
            }
        }

        static void Block(SpatialProcessor sp, float[][] src, float[][] slots, float[] l, float[] r, ref int pos, List<EqChain[]> eqs, List<StereoRing> rings, float[] ol, float[] orr, float[] inter)
        {
            const int B = SpatialProcessor.B;
            for (int s = 0; s < slots.Length; s++) Array.Copy(src[s], pos, slots[s], 0, B);
            pos = (pos + B) % (src[0].Length - B);
            sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);
            for (int o = 0; o < eqs.Count; o++)
            {
                Array.Copy(l, ol, B); Array.Copy(r, orr, B);
                eqs[o][0].Process(ol, B); eqs[o][1].Process(orr, B);
                rings[o].Write(ol, orr, B);
                rings[o].Read(inter, B);
            }
        }
    }

    // ------------------------------------------------------------------ StreamLite Camera at 30 fps

    sealed class CamRun
    {
        public List<double> Ms = new List<double>(); public int Offered, Frames; public string Error;
        /// <summary>Played without effects (too slow on the CPU), as StreamLite does.</summary>
        public bool Plain;
        public volatile bool Stop; public Thread T;
        readonly bool gpu, lowVram, k4; readonly int cpuThreads;
        public CamRun(bool gpu, bool lowVram, bool k4, int cpuThreads) { this.gpu = gpu; this.lowVram = lowVram; this.k4 = k4; this.cpuThreads = cpuThreads; }
        public double Headroom { get { return Ms.Count == 0 ? 0 : (1000.0 / 30) / Ms.Average(); } }
        public string Name { get { return (k4 ? "4K" : "1080p") + " on the " + (gpu ? (lowVram ? "GPU (Low VRAM)" : "GPU") : "CPU") + (Plain ? ", no effects" : ""); } }

        public void Start(ManualResetEvent ready, ManualResetEvent go)
        {
            T = new Thread(() => { try { Run(ready, go); } catch (Exception ex) { Error = ex.Message; ready.Set(); } }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "perf camera" };
            T.Start();
        }

        void Run(ManualResetEvent ready, ManualResetEvent go)
        {
            int W = k4 ? 3840 : 1920, H = k4 ? 2160 : 1080;
            OnnxSession s = gpu && lowVram && !k4
                ? new OnnxSession(Path.Combine(OnnxSession.Folder, CameraFx.LowVramModel), true, 1, fuse: false, lowVram: true)
                : new OnnxSession(Path.Combine(OnnxSession.Folder, k4 ? "camfx_2160.onnx" : "camfx_1080.onnx"), gpu, Math.Max(1, cpuThreads / 2), fuse: false);
            try
            {
                // A synthetic picture (soft gradients and sensor noise): the graph's work doesn't depend on what's in it.
                var y = new byte[W * H]; var uv = new byte[W * H / 2]; var rnd = new Random(3);
                for (int i = 0; i < y.Length; i++) { int px = i % W, py = i / W; y[i] = (byte)Math.Min(255, 60 + (px * 120 / W) + (py * 40 / H) + rnd.Next(8)); }
                for (int i = 0; i < uv.Length; i++) uv[i] = (byte)(128 + rnd.Next(-6, 6));
                var RS = CameraFx.RS;
                var crop = new long[4]; var p = new float[16]; var lut = new float[256];
                var st = new float[4][]; var so = new float[4][];
                for (int i = 0; i < 4; i++) { st[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; so[i] = new float[st[i].Length]; }
                var yOut = new byte[1920 * 1080]; var uvOut = new byte[1920 * 540]; var preview = new byte[360 * 640 * 4];
                int anchors = k4 ? 8840 : 4420;
                var thumb = new byte[90 * 160]; var scores = new float[anchors * 2]; var boxes = new float[anchors * 4];
                var shapes = new[] { new long[] { H, W }, new long[] { H / 2, W }, new long[] { 4 }, new long[] { 16 }, new long[] { 256 }, new long[] { 540, 960, 3 },
                    new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] }, new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] } };
                var c = new CamFxSettings { FaceTracking = true, Background = "blur", SkinSmoothing = 30 };
                var tr = new FaceTracker(); var light = new AutoLight(); var bg = new byte[540 * 960 * 3];
                Action frame = () =>
                {
                    tr.Enabled = c.FaceTracking; tr.Zoom = c.Zoom / 100f; tr.Speed = c.TrackSpeed / 100f;
                    tr.Crop(W, H, crop); light.Build(c, lut); CameraFx.FillParams(c, p, tr, crop, W, H);
                    s.Run(new Array[] { y, uv, crop, p, lut, bg, st[0], st[1], st[2], st[3] }, shapes, new Array[] { yOut, uvOut, preview, thumb, scores, boxes, so[0], so[1], so[2], so[3] });
                    var t = st; st = so; so = t;
                    tr.Update(scores, boxes, W, H); light.Measure(thumb);
                };
                for (int i = 0; i < 5; i++) frame();   // warm-up (graph compile, first-frame allocations)
                // As StreamLite does (3.0+): effects on the CPU slower than PlainAboveMs -> the camera plays without them.
                var plainAbove = typeof(CameraFx).GetField("PlainAboveMs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var previewFn = typeof(Nv12).GetMethod("Preview");
                if (!gpu && plainAbove != null && previewFn != null)
                {
                    var probe = Stopwatch.StartNew(); for (int i = 0; i < 3; i++) frame();
                    if (probe.Elapsed.TotalMilliseconds / 3 > (double)plainAbove.GetValue(null))
                    {
                        Plain = true;
                        frame = () =>
                        {
                            if (W == 1920) { Buffer.BlockCopy(y, 0, yOut, 0, yOut.Length); Buffer.BlockCopy(uv, 0, uvOut, 0, uvOut.Length); }
                            else Nv12.Resize(y, uv, W, H, yOut, uvOut, 1920, 1080);
                            previewFn.Invoke(null, new object[] { yOut, uvOut, 1920, 1080, preview });
                        };
                    }
                }
                ready.Set(); go.WaitOne();
                // The camera delivers a frame every 33.3 ms; the newest one waiting is processed (older ones are dropped).
                double start = NowMs, period = 1000.0 / 30; long next = 0;
                while (!Stop)
                {
                    double now = NowMs;
                    long due = (long)((now - start) / period);
                    if (due < next) { WaitUntil(start + next * period); continue; }
                    Offered = (int)due + 1;
                    next = due + 1;
                    double t0 = NowMs; frame(); Ms.Add(NowMs - t0); Frames++;
                }
                Offered = (int)((NowMs - start) / period);
            }
            finally { s.Dispose(); }
        }
    }

    static double VramMb()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            string tag = "pid_" + Process.GetCurrentProcess().Id + "_"; long ded = 0;
            foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
                using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
            return ded / 1048576.0;
        }
        catch { return -1; }
    }

    /// <summary>Runs the given parts together for some seconds under the tier's game load; returns StreamLite's CPU %.</summary>
    static double RunTogether(Tier t, double seconds, MicRun mic, GameRun game, CamRun cam, out double vram)
    {
        var ready = new List<ManualResetEvent>(); var go = new ManualResetEvent(false);
        Action<Action<ManualResetEvent, ManualResetEvent>> start = s => { var e = new ManualResetEvent(false); ready.Add(e); s(e, go); };
        if (mic != null) start(mic.Start);
        if (game != null) start(game.Start);
        if (cam != null) start(cam.Start);
        foreach (var e in ready) e.WaitOne(120000);
        var load = new GameLoad(t.Load);
        Thread.Sleep(300);
        double cpu0 = ProcessCycles(), game0 = load.Cycles, w0 = NowMs;
        go.Set();
        Thread.Sleep((int)(seconds * 1000));
        double cpu1 = ProcessCycles(), game1 = load.Cycles, wall = NowMs - w0;
        vram = VramMb();
        if (mic != null) mic.Stop = true;
        if (game != null) game.Stop = true;
        if (cam != null) cam.Stop = true;
        if (mic != null) mic.T.Join();
        if (game != null) game.T.Join();
        if (cam != null) cam.T.Join();
        load.Stop();
        return Math.Max(0, (cpu1 - cpu0 - (game1 - game0)) / (cyclesPerMs * wall * t.Threads) * 100);
    }

    // ------------------------------------------------------------------ child: one tier

    static void Child(string tierName, int seconds)
    {
        timeBeginPeriod(1);
        Calibrate();
        var t = Tiers().First(x => x.Name == tierName);
        long mask = 0; for (int i = 0; i < Math.Min(t.Threads, 63); i++) mask |= 1L << i;
        Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        // The defaults this tier gets (HardwareTuning), on a fresh install.
        var S = new AppSettings();
        long vram = t.Gpu == "none" ? 0 : t.Gpu == "low" ? 4096 : Math.Max(4097, HardwareTuning.GpuMemoryMb());
        HardwareTuning.Apply(S, t.Threads, vram, DeepFilter.Installed);
        bool gpu = t.Gpu != "none";
        var r = new PerfResult { Version = Program.Version.ToString(3), Tier = t.Name, Desc = t.Desc, Machine = Machine(), Threads = t.Threads, LoadThreads = t.Load, Seconds = seconds, Engine = S.NsEngine };
        int probe = Math.Max(5, seconds / 4);

        // Probes: each noise engine on its own, then each camera graph on its own.
        var engines = new List<string> { NoiseEngines.Classic, NoiseEngines.Ai };
        if (DeepFilter.Installed) { engines.Add(NoiseEngines.DfnCpu); if (gpu) engines.Add(NoiseEngines.DfnGpu); }
        foreach (var e in engines)
        {
            var m = new MicRun(e, t.Gpu == "low", 0); double v;
            double cpu = RunTogether(t, probe, m, null, null, out v);
            r.Probes.Add(new PerfProbe { What = "mic " + e, Ran = m.Ran, Note = m.Fallback, DfnMs = m.DfnMs, Headroom = m.Headroom, CpuPct = cpu, AvgMs = m.Ms.Count > 0 ? m.Ms.Average() : 0, P99Ms = P99(m.Ms), Misses = m.Misses, Late = m.DfnLate, Frames = m.Frames });
            Console.WriteLine("    " + t.Name + " mic " + e.PadRight(8) + " CPU " + cpu.ToString("0.0").PadLeft(5) + "%  p99 " + P99(m.Ms).ToString("0.00") + " ms  misses " + m.Misses + "  DFN late " + m.DfnLate + (m.Error != null ? "  ERROR " + m.Error : ""));
        }
        var cams = new List<CamRun>();
        if (t.Gpu == "none") cams.Add(new CamRun(false, false, false, t.Threads));
        if (t.Gpu == "low") { cams.Add(new CamRun(true, true, false, t.Threads)); cams.Add(new CamRun(false, false, false, t.Threads)); }
        if (t.Gpu == "full") { cams.Add(new CamRun(true, false, true, t.Threads)); cams.Add(new CamRun(true, false, false, t.Threads)); cams.Add(new CamRun(false, false, true, t.Threads)); }
        foreach (var c in cams)
        {
            double v;
            double cpu = RunTogether(t, probe, null, null, c, out v);
            double fps = c.Offered > 0 ? 30.0 * c.Frames / c.Offered : 0;
            r.Probes.Add(new PerfProbe { What = "camera " + c.Name, Headroom = c.Headroom, CpuPct = cpu, AvgMs = c.Ms.Count > 0 ? c.Ms.Average() : 0, P99Ms = P99(c.Ms), Fps = fps, Frames = c.Frames, Misses = c.Offered - c.Frames });
            Console.WriteLine("    " + t.Name + " camera " + c.Name.PadRight(30) + " CPU " + cpu.ToString("0.0").PadLeft(5) + "%  " + fps.ToString("0.0") + " fps  " + (c.Ms.Count > 0 ? c.Ms.Average() : 0).ToString("0.0") + " ms/frame" + (c.Error != null ? "  ERROR " + c.Error : ""));
        }

        // Everything at once with this tier's defaults: mic + 2 added mics, 3D + 2 added outputs, the camera.
        var mic = new MicRun(S.NsEngine, S.LowVram, 2);
        var game = new GameRun(2);
        var cam = gpu ? new CamRun(true, S.LowVram, !S.LowVram && t.Gpu == "full", t.Threads) : new CamRun(false, false, false, t.Threads);
        GC.Collect();
        double vr;
        r.CpuPct = RunTogether(t, seconds, mic, game, cam, out vr);
        r.Camera = cam.Name;
        r.VramMb = vr;
        var pr = Process.GetCurrentProcess(); pr.Refresh(); r.RamMb = pr.PrivateMemorySize64 / 1048576.0;
        r.EngineRan = mic.Ran; r.Fallback = mic.Fallback; r.MicHeadroom = mic.Headroom; r.GameHeadroom = game.Headroom; r.CamHeadroom = cam.Headroom; r.DfnMs = mic.DfnMs; r.MicFrames = mic.Frames; r.MicMisses = mic.Misses; r.DfnLate = mic.DfnLate; r.MicAvgMs = mic.Ms.Count > 0 ? mic.Ms.Average() : 0; r.MicP99Ms = P99(mic.Ms); r.MicMaxLateMs = Math.Max(0, mic.MaxLate);
        r.MicLatencyMs = 3 + P99(mic.Ms) + mic.DelayMs;
        r.GameBlocks = game.Blocks; r.GameMisses = game.Misses; r.GameAvgMs = game.Ms.Count > 0 ? game.Ms.Average() : 0; r.GameP99Ms = P99(game.Ms); r.GameMaxLateMs = Math.Max(0, game.MaxLate);
        r.GameLatencyMs = game.LatencyMs + P99(game.Ms);
        r.CamOffered = cam.Offered; r.CamFrames = cam.Frames; r.CamFps = cam.Offered > 0 ? 30.0 * cam.Frames / cam.Offered : 0; r.CamAvgMs = cam.Ms.Count > 0 ? cam.Ms.Average() : 0;
        foreach (var err in new[] { mic.Error, game.Error, cam.Error }.Where(x => x != null)) Console.WriteLine("    ERROR " + err);
        Console.WriteLine("RESULT " + new JavaScriptSerializer().Serialize(r));
    }

    static string Machine()
    {
        string cpu = "", gpu = "";
        try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) cpu = ((string)k.GetValue("ProcessorNameString") ?? "").Trim(); } catch { }
        try
        {
            using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                foreach (var n in cls.GetSubKeyNames())
                    try { using (var k = cls.OpenSubKey(n)) { var d = k.GetValue("DriverDesc") as string; if (d != null && k.GetValue("HardwareInformation.qwMemorySize") != null) { gpu = d; break; } } } catch { }
        }
        catch { }
        return cpu + ", " + Environment.ProcessorCount + " threads" + (gpu.Length > 0 ? ", " + gpu + " " + (HardwareTuning.GpuMemoryMb() / 1024.0).ToString("0") + " GB" : "");
    }

    // ------------------------------------------------------------------ parent: every tier, checks, baselines

    static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "child") { Child(args[1], int.Parse(args[2])); return; }
        bool quick = args.Contains("quick"), save = args.Contains("save");
        var want = args.Where(a => a.StartsWith("tiers=")).Select(a => a.Substring(6).Split(',')).FirstOrDefault();
        int seconds = quick ? 16 : 40;
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\.."));
        string basePath = args.Where(a => a.StartsWith("baseline=")).Select(a => Path.GetFullPath(a.Substring(9))).FirstOrDefault() ?? Path.Combine(root, "perf", "baseline.json");
        Console.WriteLine("Machine: " + Machine());
        var results = new List<PerfResult>();
        foreach (var t in Tiers().Where(x => want == null || want.Contains(x.Name)))
        {
            Console.WriteLine("== " + t.Name + ": " + t.Desc);
            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "child " + t.Name + " " + seconds) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                    if (line.StartsWith("RESULT ")) results.Add(new JavaScriptSerializer().Deserialize<PerfResult>(line.Substring(7)));
                    else Console.WriteLine(line);
                p.WaitForExit();
            }
        }

        Console.WriteLine();
        Console.WriteLine("Everything at once (mic + 2 added mics, 3D + 2 added outputs, camera) under game load:");
        foreach (var r in results)
        {
            Console.WriteLine("  " + r.Tier.PadRight(5) + "noise " + r.Engine.PadRight(7) + "camera " + r.Camera.PadRight(30) + "CPU " + r.CpuPct.ToString("0.0").PadLeft(5) + "%  RAM " + r.RamMb.ToString("0") + " MB  VRAM " + r.VramMb.ToString("0") + " MB");
            Console.WriteLine("        mic   " + r.MicFrames + " packets, " + r.MicMisses + " dropouts, DFN late " + r.DfnLate + ", p99 " + r.MicP99Ms.ToString("0.00") + " ms, worst late " + r.MicMaxLateMs.ToString("0.0") + " ms, latency " + r.MicLatencyMs.ToString("0.0") + " ms, keeps real time on cores " + r.MicHeadroom.ToString("0.0") + "x slower");
            Console.WriteLine("        3D    " + r.GameBlocks + " blocks, " + r.GameMisses + " dropouts, p99 " + r.GameP99Ms.ToString("0.00") + " ms, worst late " + r.GameMaxLateMs.ToString("0.0") + " ms, latency " + r.GameLatencyMs.ToString("0.0") + " ms, keeps real time on cores " + r.GameHeadroom.ToString("0.0") + "x slower");
            Console.WriteLine("        camera " + r.CamFps.ToString("0.0") + " fps (" + r.CamFrames + " of " + r.CamOffered + "), " + r.CamAvgMs.ToString("0.0") + " ms/frame (" + r.CamHeadroom.ToString("0.0") + "x the speed 30 fps needs)");
            Check(r.MicMisses == 0 && r.DfnLate <= r.MicFrames / 1000 && r.EngineRan == r.Engine, r.Tier + ": mic - no dropouts with this tier's default noise remover (" + r.Engine + (r.EngineRan != r.Engine ? ", but " + r.EngineRan + " ran: " + r.Fallback : "") + ")");
            Check(r.GameMisses == 0, r.Tier + ": 3D audio and both added outputs - no dropouts");
            if (r.Camera.Contains("GPU")) Check(r.CamFps >= 28, r.Tier + ": camera at full frame rate on the graphics card (" + r.CamFps.ToString("0.0") + " fps)");
            else if (r.Camera.Contains("no effects")) Check(r.CamFps >= 28, r.Tier + ": without a graphics card the camera plays without effects, at full frame rate (" + r.CamFps.ToString("0.0") + " fps)");
            else Console.WriteLine("  INFO " + r.Tier + ": camera without a graphics card: " + r.CamFps.ToString("0.0") + " fps");
        }

        // Tuning: which noise remover keeps real time from how many threads, and the camera on the CPU.
        Console.WriteLine();
        Console.WriteLine("Tuning (probes: each part on its own, under the game load):");
        foreach (var r in results)
            foreach (var p in r.Probes)
                Console.WriteLine("  " + r.Tier.PadRight(5) + p.What.PadRight(40) + "x" + p.Headroom.ToString("0.0").PadRight(6) + "CPU " + p.CpuPct.ToString("0.0").PadLeft(5) + "%  avg " + p.AvgMs.ToString("0.00") + " ms  p99 " + p.P99Ms.ToString("0.00") + " ms" +
                    (p.Fps > 0 ? "  " + p.Fps.ToString("0.0") + " fps" : "  misses " + p.Misses + ", late " + p.Late + (p.DfnMs > 0 ? ", DFN " + p.DfnMs.ToString("0.00") + " ms/frame" : "") + (p.Ran != null && "mic " + p.Ran != p.What ? ", ran " + p.Ran : "")));
        // The rule HardwareTuning.DfnMinThreads follows: DeepFilterNet on the CPU is the default from the fewest threads
        // where it keeps real time under a game's load, and on cores 2.5x slower than this PC's (a budget CPU) still
        // costs under 6% of the whole CPU while it runs (only while an app records your mic).
        const double SlowerCores = 2.5, BudgetPct = 6;
        var dfn = results.SelectMany(r => r.Probes.Where(p => p.What == "mic " + NoiseEngines.DfnCpu && p.Ran == NoiseEngines.DfnCpu).Select(p => new { r.Threads, P = p })).ToList();
        if (dfn.Count > 0)
        {
            double cores = dfn.Average(x => x.P.CpuPct / 100 * x.Threads);
            bool realtime = dfn.All(x => x.P.Misses == 0 && x.P.Late <= Math.Max(1, x.P.Frames / 1000) && x.P.Headroom >= SlowerCores);
            int rec = new[] { 2, 4, 6, 8, 12, 16, 24, 32 }.FirstOrDefault(n => cores * SlowerCores / n * 100 <= BudgetPct);
            Console.WriteLine("  DeepFilterNet on the CPU: " + (cores * 100).ToString("0") + "% of one core here, keeps real time on every tier: " + realtime +
                " (" + dfn.Min(x => x.P.Headroom).ToString("0.0") + "x headroom); on " + SlowerCores + "x slower cores under " + BudgetPct + "% of the CPU from " + rec + " threads. HardwareTuning: from " + HardwareTuning.DfnMinThreads + ".");
            // At the measured tier or one above (the measurement wobbles at a tier's edge; erring towards lighter is fine).
            int[] tiers = { 2, 4, 6, 8, 12, 16, 24, 32 };
            int at = Array.IndexOf(tiers, rec), set = Array.IndexOf(tiers, HardwareTuning.DfnMinThreads);
            Check(realtime && set >= at && set <= at + 1, "the DeepFilterNet (CPU) default threshold fits the measurements (" + rec + " threads measured, " + HardwareTuning.DfnMinThreads + " set)");
        }
        var hi = results.FirstOrDefault(r => r.Tier == "high");
        if (hi != null)
        {
            var k4 = hi.Probes.FirstOrDefault(p => p.What == "camera 4K on the GPU"); var hd = hi.Probes.FirstOrDefault(p => p.What == "camera 1080p on the GPU");
            if (k4 != null && hd != null)
            {
                Console.WriteLine("  Camera on the graphics card: 4K capture " + k4.AvgMs.ToString("0.0") + " ms vs 1080p " + hd.AvgMs.ToString("0.0") + " ms per frame (4K stays the default: lossless zoom for " + ((k4.AvgMs / hd.AvgMs - 1) * 100).ToString("0") + "% more GPU time)");
                Check(k4.AvgMs < hd.AvgMs * 1.5 && k4.Fps >= 28, "4K capture costs the graphics card little more than 1080p");
            }
        }

        // Release to release: the same tiers on the same machine.
        var bl = File.Exists(basePath) ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<PerfBaseline>(File.ReadAllText(basePath)) : new PerfBaseline();
        string ver = Program.Version.ToString(3);
        foreach (var r in results)
        {
            var prev = bl.Results.Where(b => b.Machine == r.Machine && b.Tier == r.Tier && b.Version != ver).OrderByDescending(b => new Version(b.Version)).FirstOrDefault();
            if (prev == null) { Console.WriteLine("  " + r.Tier + ": no earlier release measured on this machine yet"); continue; }
            Console.WriteLine("  " + r.Tier + " vs " + prev.Version + ": CPU " + prev.CpuPct.ToString("0.0") + " -> " + r.CpuPct.ToString("0.0") + "%, mic p99 " + prev.MicP99Ms.ToString("0.00") + " -> " + r.MicP99Ms.ToString("0.00") +
                " ms, 3D p99 " + prev.GameP99Ms.ToString("0.00") + " -> " + r.GameP99Ms.ToString("0.00") + " ms, RAM " + prev.RamMb.ToString("0") + " -> " + r.RamMb.ToString("0") + " MB, VRAM " + prev.VramMb.ToString("0") + " -> " + r.VramMb.ToString("0") + " MB, dropouts " +
                (prev.MicMisses + prev.GameMisses) + " -> " + (r.MicMisses + r.GameMisses));
            bool sameSetup = prev.Engine == r.Engine && prev.Camera == r.Camera;
            if (sameSetup)
            {
                Check(r.MicMisses + r.GameMisses <= prev.MicMisses + prev.GameMisses, r.Tier + ": no more dropouts than " + prev.Version);
                Check(r.CpuPct <= prev.CpuPct * 1.2 + 0.5, r.Tier + ": CPU within 20% of " + prev.Version);
                Check(r.RamMb <= prev.RamMb * 1.2 + 20 && r.VramMb <= prev.VramMb * 1.2 + 20, r.Tier + ": RAM and video memory within 20% of " + prev.Version);
            }
            else Console.WriteLine("  INFO " + r.Tier + ": defaults differ from " + prev.Version + " (" + prev.Engine + " / " + prev.Camera + "), compared for information only");
        }
        if (save && results.Count > 0)
        {
            bl.Results.RemoveAll(b => b.Version == ver && results.Any(r => r.Machine == b.Machine && r.Tier == b.Tier));
            bl.Results.AddRange(results);
            Directory.CreateDirectory(Path.GetDirectoryName(basePath));
            File.WriteAllText(basePath, Pretty(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(bl)));
            Console.WriteLine("  saved " + results.Count + " results for " + ver + " to perf/baseline.json");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }

    /// <summary>One result per line, so baseline changes read well in a diff.</summary>
    static string Pretty(string json) { return json.Replace("{\"Version\"", "\n  {\"Version\"").Replace("]}", "\n]}"); }
}
