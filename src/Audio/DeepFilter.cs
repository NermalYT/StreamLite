using System;
using System.IO;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// DeepFilterNet 3 noise suppression (Schröter et al., MIT/Apache-2.0; streaming ONNX export from the torchDF
    /// project) on the CPU or on the graphics card (DirectML). The network runs on its own thread, one 10 ms frame at a
    /// time with its recurrent state carried over; the mic thread only hands frames over and picks results up a fixed
    /// number of frames later, so a busy GPU can never stall the microphone. A frame that isn't back in time is replaced
    /// by the input at the level the network was just applying (rare; counted in Late).
    /// </summary>
    internal sealed class DeepFilter : IDisposable
    {
        public const int Frame = 480;                    // 10 ms at 48 kHz
        public const int States = 45304;
        public const string ModelFile = "deepfilternet3.onnx";
        /// <summary>Low VRAM: the same network with its GRUs in 16-bit floats (tools/make_dfn_lean.py).</summary>
        public const string LowVramFile = "deepfilternet3_low.onnx";
        const int Ring = 16;

        public readonly bool Gpu;
        /// <summary>On the graphics card with the Low VRAM model and settings (~35 MB instead of ~150 MB).</summary>
        public readonly bool LowVram;
        /// <summary>
        /// Frames between hand-over and pick-up: 1 (10 ms - the worker has a whole frame's time, and the graphics card
        /// needs about 1 ms), or 2 for the rest of the session once frames came back late (3 within 5 s: a game
        /// keeping the graphics card or CPU busy). The switch waits for a pause in your voice, so it's never heard.
        /// </summary>
        public volatile int Buffer = 1;
        /// <summary>The buffer went to 2 frames (late frames); for health.log.</summary>
        public volatile bool Widened;
        long windowFrame; int windowLate; bool widen;
        public volatile float AttenLimitDb = 100;       // how far noise may be turned down (100 = no limit)
        public volatile float Lsnr;                     // the network's speech-to-noise estimate, dB
        /// <summary>Extra turn-down while nobody is talking (removes the faint residue the network leaves in pauses).</summary>
        public volatile float CleanupDb = 15;
        public volatile string Error;
        public volatile bool Ready;
        /// <summary>How long loading and warming up took (status.txt).</summary>
        public long LoadMs;
        public int Late;                                // frames that weren't back in time
        public bool Synchronous;
        internal static bool TrimAfterLoad = Environment.GetEnvironmentVariable("STREAMLITE_NOTRIM") == null;                        // offline: wait for each frame (voice test clip)
        public double AvgMs, MaxMs;                     // inference time per frame

        readonly float[][] inRing = new float[Ring][], outRing = new float[Ring][];
        readonly long[] doneFrame = new long[Ring];
        readonly float[] lsnrRing = new float[Ring];
        readonly bool[] speechRing = new bool[Ring];   // worker's per-frame "sounds like a voice" verdict
        float pauseLsnr = -5;                           // the network's typical SNR score in pauses
        float floorDb = -50;                            // average level of what the network leaves in pauses
        float gateGain = 1; int gateHold;
        internal Action<float, float, bool, float> Trace;   // tests: lsnr, output dB, voice verdict, floor dB
        long submitted = -1;                            // last frame handed over (mic thread)
        long processed = -1;                            // last frame finished (worker)
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool stop;
        float levelRatio = 0.25f;                       // output/input level of recent frames (for late frames); low until measured

        [System.Runtime.InteropServices.DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

        public static string ModelPath { get { return Path.Combine(OnnxSession.Folder, ModelFile); } }
        public static bool Installed { get { return OnnxSession.Available && File.Exists(ModelPath); } }
        /// <summary>The Low VRAM model is there (checked once; the mic thread asks every block).</summary>
        public static bool LowVramInstalled
        {
            get { if (lowInstalled < 0) lowInstalled = File.Exists(Path.Combine(OnnxSession.Folder, LowVramFile)) ? 1 : 0; return lowInstalled == 1; }
        }
        static int lowInstalled = -1;

        /// <param name="lowVram">Low VRAM (graphics card only; the CPU always runs the full model).</param>
        public DeepFilter(bool gpu, bool lowVram = false)
        {
            Gpu = gpu;
            LowVram = gpu && lowVram && LowVramInstalled;
            for (int i = 0; i < Ring; i++) { inRing[i] = new float[Frame]; outRing[i] = new float[Frame]; doneFrame[i] = -1; }
            worker = new Thread(Work) { IsBackground = true, Name = "DeepFilterNet", Priority = ThreadPriority.AboveNormal };
            worker.Start();
        }

        /// <summary>Mic thread: hands over one input frame and returns the output frame from Buffer frames ago.</summary>
        public void Process(float[] input, float[] output)
        {
            long n = submitted + 1;
            Array.Copy(input, inRing[n % Ring], Frame);
            Interlocked.Exchange(ref submitted, n);
            wake.Set();
            if (Synchronous && Ready) WaitIdle(60);
            // Late frames keep coming: one frame more of buffer, switched in a pause (this call repeats the previous,
            // quiet frame once).
            if (widen && Buffer == 1 && gateHold == 0) { Buffer = 2; Widened = true; widen = false; }
            long m = n - Buffer;
            if (m < 0) { Array.Clear(output, 0, Frame); return; }
            int slot = (int)(m % Ring);
            if (Ready && Interlocked.Read(ref doneFrame[slot]) == m) { Array.Copy(outRing[slot], output, Frame); Cleanup(m, output); }
            else
            {
                // Not back in time (or still loading): the input of that frame at the level the network was applying.
                var src = inRing[slot];
                float g = Ready ? levelRatio : 0.1f;
                for (int i = 0; i < Frame; i++) output[i] = src[i] * g;
                if (Ready) Late++;
            }
            if (Ready && Buffer == 1)
            {
                if (n - windowFrame >= 500) { windowFrame = n; windowLate = Late; }   // 5 s windows
                else if (Late - windowLate >= 3) widen = true;
            }
        }

        /// <summary>
        /// Pauses: a frame counts as voice when the network rates it speech AND it stands out from the residue level
        /// (or it's clearly speech). A lone frame (a key click) doesn't open; two in a row - looking one frame back and
        /// at the frames already processed ahead - do. Opens at once, holds 150 ms, then eases down by CleanupDb.
        /// </summary>
        void Cleanup(long m, float[] output)
        {
            long last = Interlocked.Read(ref processed);
            Func<long, int> verdict = k =>
            {
                if (k < 0 || k > last) return -1;
                int sl = (int)(k % Ring);
                if (Interlocked.Read(ref doneFrame[sl]) != k) return -1;
                return speechRing[sl] ? (lsnrRing[sl] > 12 ? 2 : 1) : 0;
            };
            int here = verdict(m);
            bool open = here == 2 || (here == 1 && (verdict(m - 1) >= 1 || verdict(m + 1) >= 1));
            if (open) gateHold = 15; else if (gateHold > 0) gateHold--;
            float floor = (float)Math.Pow(10, -CleanupDb / 20);
            float target = gateHold > 0 ? 1f : floor;
            float from = gateGain;
            float to = target > from ? target : from + (target - from) * 0.25f;   // open at once, close over ~40 ms
            for (int i = 0; i < Frame; i++) output[i] *= from + (to - from) * (i + 1) / Frame;
            gateGain = to;
        }

        void Work()
        {
            OnnxSession s = null;
            var state = new float[States]; var newState = new float[States];
            var lim = new float[1]; var lsnr = new float[1];
            var frameShape = new long[] { Frame }; var stateShape = new long[] { States }; var scalar = new long[0];
            var y = new float[Frame];
            var ins = new float[][] { null, null, lim };
            var shapes = new[] { frameShape, stateShape, scalar };
            var outs = new float[][] { y, null, lsnr };
            var loadClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // STREAMLITE_NO_GPU=1: keep the AI off the graphics card (a misbehaving GPU driver, or testing the fallback).
                if (Gpu && Environment.GetEnvironmentVariable("STREAMLITE_NO_GPU") == "1") throw new InvalidOperationException("graphics card turned off with STREAMLITE_NO_GPU");
                s = LowVram ? new OnnxSession(Path.Combine(OnnxSession.Folder, LowVramFile), true, lowVram: true) : new OnnxSession(ModelPath, Gpu);
                // Warm up (first runs compile kernels / allocate), then start from a clean state.
                var z = new float[Frame];
                for (int i = 0; i < 5; i++) s.Run(new[] { z, state, new[] { 100f } }, new[] { frameShape, stateShape, scalar }, new[] { y, newState, lsnr });
                Array.Clear(state, 0, States);
                // Start with the frames the mic thread will ask for next (handed over up to Buffer frames ago), so the
                // switch from the stand-in doesn't begin with a late, unfiltered frame.
                Interlocked.Exchange(ref processed, Math.Max(-1, Interlocked.Read(ref submitted) - Buffer));
                LoadMs = loadClock.ElapsedMilliseconds;
                Ready = true;
                // The graphics driver commits a few hundred MB of setup memory it never touches again; let Windows page
                // it out now instead of holding it in RAM (steady use afterwards is ~15 MB; the model lives in video memory).
                if (Gpu && TrimAfterLoad) try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
            }
            catch (Exception ex) { Error = ex.Message; if (s != null) s.Dispose(); return; }

            var sw = new System.Diagnostics.Stopwatch();
            long count = 0; double total = 0;
            try
            {
                while (!stop)
                {
                    wake.WaitOne(50);
                    while (!stop)
                    {
                        long last = Interlocked.Read(ref submitted), next = processed + 1;
                        if (next > last) break;
                        if (last - next >= Ring - 2) next = last;            // far behind: jump to the newest frame
                        int slot = (int)(next % Ring);
                        var x = inRing[slot];
                        lim[0] = AttenLimitDb;
                        sw.Restart();
                        ins[0] = x; ins[1] = state; outs[1] = newState;
                        s.Run(ins, shapes, outs);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        var t = state; state = newState; newState = t;
                        double ei = 0, eo = 0;
                        for (int i = 0; i < Frame; i++) { ei += x[i] * x[i]; eo += y[i] * y[i]; }
                        if (ei > 1e-9) levelRatio = 0.8f * levelRatio + 0.2f * (float)Math.Min(1, Math.Sqrt(eo / ei));
                        Array.Copy(y, outRing[slot], Frame);
                        lsnrRing[slot] = lsnr[0];
                        float outDb = (float)(10 * Math.Log10(eo / Frame + 1e-12));
                        // Relative to pauses: the network's SNR score in pauses drops in loud rooms, and so does quiet speech.
                        bool voice = lsnr[0] > 10 || (lsnr[0] > Math.Min(5f, pauseLsnr + 10) && outDb > floorDb + 12);
                        if (!voice)
                        {
                            floorDb += (outDb - floorDb) * (outDb > floorDb ? 0.02f : 0.1f);   // average residue level in pauses
                            pauseLsnr += (lsnr[0] - pauseLsnr) * 0.03f;
                        }
                        speechRing[slot] = voice;
                        if (Trace != null) Trace(lsnr[0], outDb, speechRing[slot], floorDb);
                        Interlocked.Exchange(ref doneFrame[slot], next);
                        processed = next;
                        Lsnr = lsnr[0];
                        count++; total += ms; AvgMs = total / count; if (ms > MaxMs && count > 20) MaxMs = ms;
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; Ready = false; }
            finally { s.Dispose(); }
        }

        /// <summary>Tests: waits until every handed-over frame is done (simulates a fast enough device).</summary>
        public bool WaitIdle(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Error != null) return false;
                if (Ready && processed >= Interlocked.Read(ref submitted)) return true;
                Thread.Sleep(0);
            }
            return false;
        }

        public void Dispose() { stop = true; wake.Set(); }
    }
}
