using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Audio;

namespace StreamLite.Camera
{
    /// <summary>
    /// "StreamLite Camera": camera -> AI effects on the graphics card -> virtual camera. One ONNX graph per frame
    /// (ai/camfx_*.onnx, built by tools/make_camfx.py): zoom/crop for face tracking, 4K supersampling, tone curve,
    /// colour, denoise, skin smoothing, sharpen, Robust Video Matting cut-out with blur / picture / video / green
    /// backgrounds, mirror, NV12 out; plus UltraFace face detection on the full frame and a luma thumbnail for auto
    /// light. Runs only while an app watches the virtual camera or the preview is open.
    /// </summary>
    internal sealed class CameraFx : IDisposable
    {
        public const int OW = 1920, OH = 1080;
        internal static readonly int[][] RS = { new[] { 16, 135, 240 }, new[] { 20, 68, 120 }, new[] { 40, 34, 60 }, new[] { 64, 17, 30 } };

        readonly AppSettings S;
        CamFxSettings C { get { return S.CamFx; } }
        public readonly VirtualCamera Cam = new VirtualCamera();
        public volatile bool CamOk;
        public volatile string Status = "Off", Error;
        public volatile bool PreviewWanted;
        public volatile float Fps, GpuMs;
        public volatile bool FaceFound;
        /// <summary>Why the camera plays without effects right now (null = with effects).</summary>
        public volatile string PlainWhy;
        /// <summary>Effects on the CPU slower than this per frame (under 20 fps): the camera plays without them.</summary>
        internal const double PlainAboveMs = 50;
        public string CameraName = "";
        public int CaptureW, CaptureH;
        /// <summary>360x640 BGRA of what the virtual camera sends (latest frame).</summary>
        public event Action<byte[]> PreviewReady;

        readonly Thread monitor;
        volatile bool quit;
        Thread worker;
        volatile bool stopWorker;
        CameraReader reader;
        readonly object frameLock = new object();
        byte[] pendY, pendUv; bool pending;
        readonly AutoResetEvent frameEvent = new AutoResetEvent(false);
        long lastWanted;
        readonly Stopwatch clock = Stopwatch.StartNew();

        [System.Runtime.InteropServices.DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

        public static bool ModelsInstalled { get { return OnnxSession.Available && File.Exists(Path.Combine(OnnxSession.Folder, "camfx_1080.onnx")); } }

        public CameraFx(AppSettings s)
        {
            S = s;
            monitor = new Thread(Monitor) { IsBackground = true, Name = "StreamLite Camera" };
            monitor.Start();
        }

        // ------------------------------------------------------------------ lifecycle

        void Monitor()
        {
            long nextTry = 0; int fails = 0;
            while (!quit)
            {
                try
                {
                    bool enabled = C.Enabled && VirtualCamera.Supported && ModelsInstalled;
                    if (enabled && !CamOk && clock.ElapsedMilliseconds >= nextTry)
                    {
                        CamOk = Cam.Start();
                        if (CamOk) { fails = 0; Error = null; }
                        else
                        {
                            // Often temporary (Windows still removing the camera of a StreamLite that just closed): retry
                            // after 3, 6, 12... up to 60 seconds.
                            Error = Cam.Error;
                            Cam.Dispose();
                            nextTry = clock.ElapsedMilliseconds + Math.Min(60000, 3000L << Math.Min(fails++, 5));
                        }
                    }
                    if (!enabled && CamOk) { Cam.Dispose(); CamOk = false; nextTry = 0; }
                    bool watched = CamOk && Cam.SinceLastConsumer < 2500;
                    bool want = enabled && (watched || PreviewWanted);
                    if (want) lastWanted = clock.ElapsedMilliseconds;
                    bool running = worker != null;
                    if (want && !running) StartPipeline();
                    else if (!want && running && clock.ElapsedMilliseconds - lastWanted > 4000) StopPipeline();
                    if (!enabled) Status = !C.Enabled ? "Off" : Environment.OSVersion.Version.Build < 22000 ? "Needs Windows 11" : !VirtualCamera.Supported ? "Virtual camera file (cam\\StreamLiteCam.dll) missing - reinstall" : !ModelsInstalled ? "AI files missing - reinstall" : "Off";
                    else if (!CamOk) Status = "Virtual camera unavailable: " + (Error ?? "");
                    else if (worker == null) Status = "Ready - pick \"" + VirtualCamera.FriendlyName + "\" in Discord, OBS, Teams...";
                    else Status = (watched ? "In use" : "Preview") + " - " + Fps.ToString("0") + " fps, " +
                        (PlainWhy != null ? "no effects: " + PlainWhy : GpuMs.ToString("0.0") + " ms per frame on the " + (C.Gpu ? "GPU" : "CPU") + (lowInUse ? " (low VRAM)" : "")) +
                        (CaptureW > 0 ? " - " + CameraName + " " + CaptureW + "x" + CaptureH : "") + (Error != null ? " - " + Error : "");
                }
                catch (Exception ex) { Error = ex.Message; }
                Thread.Sleep(250);
            }
        }

        /// <summary>Restart the camera side (source camera / 4K / Low VRAM choice changed).</summary>
        public void Restart() { StopPipeline(); }

        void StartPipeline()
        {
            Error = null;
            stopWorker = false;
            worker = new Thread(Work) { IsBackground = true, Name = "StreamLite Camera effects", Priority = ThreadPriority.AboveNormal };
            worker.Start();
        }

        void StopPipeline()
        {
            var w = worker;
            if (w == null) return;
            stopWorker = true;
            frameEvent.Set();
            w.Join(3000);
            worker = null;
            Fps = 0;
        }

        string SourcePath()
        {
            if (!string.IsNullOrEmpty(C.Source)) return C.Source;
            return S.LastCamera;
        }

        void OnFrame(byte[] y, byte[] uv, long t)
        {
            lock (frameLock)
            {
                if (pendY == null || pendY.Length != y.Length) { pendY = new byte[y.Length]; pendUv = new byte[uv.Length]; }
                Buffer.BlockCopy(y, 0, pendY, 0, y.Length);
                Buffer.BlockCopy(uv, 0, pendUv, 0, uv.Length);
                pending = true;
            }
            frameEvent.Set();
        }

        // ------------------------------------------------------------------ the effects loop

        /// <summary>Low VRAM (tools/make_camfx.py lowvram): the same effects for 1080p capture with far fewer full-size
        /// images in flight, in 16-bit floats - ~140 MB of video memory instead of ~380.</summary>
        public const string LowVramModel = "camfx_1080_low.onnx";
        public static bool LowVramInstalled { get { return File.Exists(Path.Combine(OnnxSession.Folder, LowVramModel)); } }
        volatile bool lowInUse;

        /// <summary>The effects model for a capture size (1080p or 4K), on the graphics card or the CPU; Low VRAM
        /// (graphics card, 1080p): the small model with DirectML's low-memory settings.</summary>
        internal static OnnxSession OpenSession(int w, int h, bool gpu, bool lowVram, bool hq = false)
        {
            if (gpu && lowVram && w == OW && h == OH && LowVramInstalled)
                return new OnnxSession(Path.Combine(OnnxSession.Folder, LowVramModel), true, 1, fuse: false, lowVram: true);
            bool k4 = w == 3840 && h == 2160;
            // High VRAM (graphics card): the cut-out worked out at full HD (tools/make_camfx.py hq).
            string hqFile = Path.Combine(OnnxSession.Folder, k4 ? "camfx_2160_hq.onnx" : "camfx_1080_hq.onnx");
            if (gpu && hq && File.Exists(hqFile)) return new OnnxSession(hqFile, true, 1, fuse: false);
            return new OnnxSession(Path.Combine(OnnxSession.Folder, k4 ? "camfx_2160.onnx" : "camfx_1080.onnx"), gpu, Environment.ProcessorCount / 2, fuse: false);
        }

        /// <summary>The matting model's recurrent state sizes (channels, height, width) as this effects model declares them.</summary>
        internal static int[][] StateSizes(OnnxSession s)
        {
            var r = new int[4][];
            for (int i = 0; i < 4; i++)
            {
                int k = Array.IndexOf(s.InputNames, "r" + (i + 1) + "i");
                var sh = k >= 0 ? s.InputShapes[k] : null;
                r[i] = sh != null && sh.Length == 4 && sh[1] > 0 && sh[2] > 0 && sh[3] > 0 ? new[] { (int)sh[1], (int)sh[2], (int)sh[3] } : RS[i];
            }
            return r;
        }

        void Work()
        {
            OnnxSession session = null;
            VideoBackground video = null; string videoPath = null;
            byte[] imageBg = null; string imagePath = null;
            try
            {
                // Camera: 4K when wanted and available (lossless zoom, cleaner 1080p), else 1080p. Low VRAM: always 1080p.
                reader = new CameraReader();
                string path = SourcePath();
                bool low = S.LowVram && C.Gpu;
                bool k4 = C.Capture4K && !low;
                if (!reader.Open(path, null, k4 ? 3840 : 1920, k4 ? 2160 : 1080, 30))
                {
                    var any = Interop.MF.Cameras().FirstOrDefault(c => c.Item1.IndexOf(VirtualCamera.FriendlyName, StringComparison.OrdinalIgnoreCase) < 0);
                    reader.Dispose(); reader = new CameraReader();
                    if (any == null || !reader.Open(any.Item2, null, k4 ? 3840 : 1920, k4 ? 2160 : 1080, 30)) { Error = "No camera: " + reader.Error; return; }
                }
                CaptureW = reader.Width; CaptureH = reader.Height;
                CameraName = (Interop.MF.Cameras().FirstOrDefault(c => string.Equals(c.Item2, path, StringComparison.OrdinalIgnoreCase)) ?? Tuple.Create("Camera", "", (Interop.IMFActivate)null)).Item1;
                bool native4k = CaptureW == 3840 && CaptureH == 2160 && !low;
                int GW = native4k ? 3840 : 1920, GH = native4k ? 2160 : 1080;
                bool direct = CaptureW == GW && CaptureH == GH;   // else resized to GWxGH on the CPU first
                session = OpenSession(GW, GH, C.Gpu, low, S.AiTier == AiTiers.High);
                var RS = StateSizes(session);
                lowInUse = low && LowVramInstalled;
                reader.FrameArrived += OnFrame;

                var y = new byte[GW * GH]; var uv = new byte[GW * GH / 2];
                var crop = new long[4]; var p = new float[16]; var lut = new float[256];
                var states = new float[4][]; var statesOut = new float[4][];
                for (int i = 0; i < 4; i++) { states[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; statesOut[i] = new float[states[i].Length]; }
                var yOut = new byte[OW * OH]; var uvOut = new byte[OW * OH / 2]; var preview = new byte[360 * 640 * 4];
                var thumb = new byte[90 * 160]; var scores = new float[4420 * 2]; var boxes = new float[4420 * 4];
                var shapes = new[]
                {
                    new long[] { GH, GW }, new long[] { GH / 2, GW }, new long[] { 4 }, new long[] { 16 }, new long[] { 256 }, new long[] { 540, 960, 3 },
                    new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] },
                    new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] },
                };
                byte[] none = new byte[540 * 960 * 3], green = Backgrounds.Solid(0, 255, 0);
                var tracker = new FaceTracker();
                Array[] runIn = null, runOut = null;
                var light = new AutoLight();
                int frames = 0, processed = 0, cpuFrames = 0; long fpsStart = clock.ElapsedMilliseconds; double gpuSum = 0, cpuMs = 0;
                bool plain = false; PlainWhy = null;
                var sw = new Stopwatch();

                while (!stopWorker)
                {
                    frameEvent.WaitOne(200);
                    if (stopWorker) break;
                    bool got = false;
                    lock (frameLock)
                    {
                        if (pending)
                        {
                            if (direct) { Buffer.BlockCopy(pendY, 0, y, 0, y.Length); Buffer.BlockCopy(pendUv, 0, uv, 0, uv.Length); }
                            else Nv12.Resize(pendY, pendUv, CaptureW, CaptureH, y, uv, GW, GH);
                            pending = false; got = true;
                        }
                    }
                    if (!got) { if (reader.Error != null) { Error = reader.Error; break; } continue; }

                    // Framing (face tracking / zoom), effect parameters, tone curve, background.
                    var c = C;
                    tracker.Speed = c.TrackSpeed / 100f;
                    tracker.Zoom = c.Zoom / 100f;
                    tracker.Enabled = c.FaceTracking;
                    tracker.Crop(GW, GH, crop);
                    light.Build(c, lut);
                    FillParams(c, p, tracker, crop, GW, GH);
                    byte[] bg = none;
                    if (c.Background == "green") bg = green;
                    else if (c.Background == "image")
                    {
                        if (c.BackgroundImage != imagePath) { imagePath = c.BackgroundImage; imageBg = null; try { if (File.Exists(imagePath)) imageBg = Backgrounds.Picture(imagePath); } catch { imageBg = null; } }
                        bg = imageBg ?? green;
                    }
                    else if (c.Background == "video")
                    {
                        if (c.BackgroundVideo != videoPath)
                        {
                            if (video != null) video.Dispose();
                            videoPath = c.BackgroundVideo; video = File.Exists(videoPath ?? "") ? new VideoBackground(videoPath) : null;
                        }
                        bg = video != null && video.Error == null ? video.Frame() : green;
                    }
                    else if (video != null) { video.Dispose(); video = null; videoPath = null; }

                    sw.Restart();
                    if (plain)
                    {
                        // No effects: the camera picture as it comes, at the full frame rate.
                        if (GW == OW && GH == OH) { Buffer.BlockCopy(y, 0, yOut, 0, yOut.Length); Buffer.BlockCopy(uv, 0, uvOut, 0, uvOut.Length); }
                        else Nv12.Resize(y, uv, GW, GH, yOut, uvOut, OW, OH);
                        if (PreviewReady != null) Nv12.Preview(yOut, uvOut, OW, OH, preview);
                    }
                    else
                    {
                        // The same two argument lists every frame (no garbage at 30 fps); only the background and the
                        // swapped state buffers change.
                        if (runIn == null) { runIn = new Array[] { y, uv, crop, p, lut, bg, null, null, null, null }; runOut = new Array[] { yOut, uvOut, preview, thumb, scores, boxes, null, null, null, null }; }
                        runIn[5] = bg;
                        for (int i = 0; i < 4; i++) { runIn[6 + i] = states[i]; runOut[6 + i] = statesOut[i]; }
                        session.Run(runIn, shapes, runOut);
                        var t = states; states = statesOut; statesOut = t;
                    }
                    double ms = sw.Elapsed.TotalMilliseconds;
                    // The effects on the CPU (the graphics card can't be used, or you switched it off) manage 2-5 fps even
                    // on a 16-core CPU (test/PerfTest.cs). Under 20 fps the camera plays without effects instead of choppy.
                    if (!plain && !session.OnGpu && ++cpuFrames > 5)
                    {
                        cpuMs += ms;
                        if (cpuFrames == 20 && cpuMs / 15 > PlainAboveMs)
                        {
                            plain = true;
                            PlainWhy = "the effects need a graphics card (on the CPU they manage " + (1000 / (cpuMs / 15)).ToString("0") + " fps)";
                        }
                    }

                    if (CamOk) Cam.Publish(yOut, uvOut);
                    if (!plain)
                    {
                        tracker.Update(scores, boxes, GW, GH);
                        FaceFound = tracker.HasFace;
                        light.Measure(thumb);
                    }
                    var pr = PreviewReady;
                    if (pr != null) pr(preview);

                    // After warm-up: the graphics driver's one-time setup memory goes back to Windows (see DeepFilter).
                    if (++processed == 30 && C.Gpu) try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
                    frames++; gpuSum += ms;
                    long el = clock.ElapsedMilliseconds - fpsStart;
                    if (el >= 1000) { Fps = frames * 1000f / el; GpuMs = (float)(gpuSum / frames); frames = 0; gpuSum = 0; fpsStart = clock.ElapsedMilliseconds; }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally
            {
                if (reader != null) { reader.FrameArrived -= OnFrame; reader.Dispose(); reader = null; }
                if (session != null) session.Dispose();
                if (video != null) video.Dispose();
                CaptureW = CaptureH = 0;
            }
        }

        internal static void FillParams(CamFxSettings c, float[] p, FaceTracker tr, long[] crop, int W, int H)
        {
            Array.Clear(p, 0, p.Length);
            p[0] = c.Denoise / 100f;
            p[1] = c.Sharpen / 100f * 1.2f;
            p[2] = c.Saturation / 100f;
            p[3] = c.Warmth / 50f;
            p[4] = c.SkinSmoothing / 100f;
            float blur = c.BlurAmount / 100f;
            switch (c.Background)
            {
                case "blur": p[5] = blur; break;
                case "image": case "video": case "green": p[6] = 1; break;
                case "dim": p[7] = 1; break;
            }
            // Face ellipse in output coordinates (skin smoothing).
            if (tr.HasFace)
            {
                double top = crop[0], left = crop[1], ch = crop[2], cw = crop[3];
                p[8] = (float)((tr.FaceX * W - left) / cw); p[9] = (float)((tr.FaceY * H - top) / ch);
                p[10] = (float)(tr.FaceW * W / cw * 0.62); p[11] = (float)(tr.FaceH * H / ch * 0.72);
            }
            else { p[8] = 0.5f; p[9] = 0.4f; p[10] = 1e-4f; p[11] = 1e-4f; }
            p[12] = c.Mirror ? 1 : 0;
            p[13] = c.EdgeSharpness / 100f;
        }

        public void Dispose()
        {
            quit = true;
            StopPipeline();
            monitor.Join(1000);
            Cam.Dispose();
        }
    }

    /// <summary>
    /// The "camera operator": picks your face among UltraFace's detections (steady: prefers the one it followed),
    /// frames it at the chosen zoom with headroom, ignores small movements and glides to new framings.
    /// Without a face for a while it eases back out.
    /// </summary>
    internal sealed class FaceTracker
    {
        public bool Enabled;
        public float Zoom = 1.4f, Speed = 0.4f;
        public bool HasFace;
        public double FaceX = 0.5, FaceY = 0.4, FaceW, FaceH;      // full-frame normalized
        double cx = 0.5, cy = 0.5, z = 1;                            // current framing
        double tx = 0.5, ty = 0.5, tz = 1;                           // target framing
        int lostFrames = 999;

        public void Update(float[] scores, float[] boxes, int W, int H)
        {
            int best = -1; double bestScore = 0;
            for (int i = 0; i < 4420; i++)
            {
                float s = scores[i * 2 + 1];
                if (s < 0.75f) continue;
                double x0 = boxes[i * 4], y0 = (boxes[i * 4 + 1] * 240 - 30) / 180, x1 = boxes[i * 4 + 2], y1 = (boxes[i * 4 + 3] * 240 - 30) / 180;
                double fx = (x0 + x1) / 2, fy = (y0 + y1) / 2;
                double size = (x1 - x0) * (y1 - y0);
                double stay = HasFace ? Math.Sqrt((fx - FaceX) * (fx - FaceX) + (fy - FaceY) * (fy - FaceY)) : 0;
                double score = s + size * 2 - stay * 1.5;   // confident, big (closest), and the same person as before
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0)
            {
                if (++lostFrames > 45) HasFace = false;   // 1.5 s
                return;
            }
            lostFrames = 0;
            double bx0 = boxes[best * 4], by0 = (boxes[best * 4 + 1] * 240 - 30) / 180, bx1 = boxes[best * 4 + 2], by1 = (boxes[best * 4 + 3] * 240 - 30) / 180;
            double a = HasFace ? 0.5 : 1;   // light smoothing of the detection itself
            FaceX += a * ((bx0 + bx1) / 2 - FaceX); FaceY += a * ((by0 + by1) / 2 - FaceY);
            FaceW += a * ((bx1 - bx0) - FaceW); FaceH += a * ((by1 - by0) - FaceH);
            HasFace = true;
        }

        /// <summary>Crop (top, left, height, width) in capture pixels, even numbers, 16:9.</summary>
        public void Crop(int W, int H, long[] crop)
        {
            if (Enabled && HasFace)
            {
                double ntz = Math.Max(1, Zoom);
                double ntx = FaceX, nty = FaceY + 0.11 / ntz;   // face a little above centre (headroom)
                // Dead zone: small moves (talking, nodding) don't move the frame.
                double half = 0.5 / ntz;
                if (Math.Abs(ntx - tx) > half * 0.18 || Math.Abs(nty - ty) > half * 0.18 || Math.Abs(ntz - tz) > 0.05) { tx = ntx; ty = nty; tz = ntz; }
            }
            else if (Enabled) { tx = 0.5; ty = 0.5; tz = 1; }
            else { tx = 0.5; ty = 0.5; tz = Math.Max(1, Zoom); }   // tracking off: the zoom is a centred digital zoom
            double k = 0.01 + 0.12 * Speed * Speed;
            cx += (tx - cx) * k; cy += (ty - cy) * k; z += (tz - z) * k;
            double cw = W / z, ch = H / z;
            double left = cx * W - cw / 2, top = cy * H - ch / 2;
            left = Math.Max(0, Math.Min(W - cw, left)); top = Math.Max(0, Math.Min(H - ch, top));
            long iw = Math.Max(2, (long)Math.Round(cw / 2) * 2), ih = Math.Max(2, (long)Math.Round(ch / 2) * 2);
            long il = Math.Min(W - iw, (long)Math.Round(left / 2) * 2), it = Math.Min(H - ih, (long)Math.Round(top / 2) * 2);
            crop[0] = Math.Max(0, it); crop[1] = Math.Max(0, il); crop[2] = ih; crop[3] = iw;
        }
    }

    /// <summary>
    /// Auto light: a tone curve from the picture's own levels (slow, so it never pumps): stretches dark rooms,
    /// lifts the mid-tones towards a natural exposure, then your brightness/contrast. Output in limited-range luma.
    /// </summary>
    internal sealed class AutoLight
    {
        double lo = 16 / 255.0, mid = 0.45, hi = 235 / 255.0;
        bool measured;
        readonly int[] hist = new int[256];

        public void Measure(byte[] thumb)
        {
            Array.Clear(hist, 0, 256);
            foreach (var v in thumb) hist[v]++;
            int n = thumb.Length, acc = 0, p2 = -1, p50 = -1, p98 = -1;
            for (int i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (p2 < 0 && acc >= n * 0.02) p2 = i;
                if (p50 < 0 && acc >= n * 0.5) p50 = i;
                if (p98 < 0 && acc >= n * 0.98) { p98 = i; break; }
            }
            double a = measured ? 0.03 : 1;   // ~1 s settling
            lo += a * (p2 / 255.0 - lo); mid += a * (p50 / 255.0 - mid); hi += a * (p98 / 255.0 - hi);
            measured = true;
        }

        public void Build(CamFxSettings c, float[] lut)
        {
            double black = 16 / 255.0, white = 235 / 255.0;
            double l = black, h = white, gamma = 1;
            if (c.AutoLight && measured)
            {
                // Levels: pull black/white points 70% of the way to the measured ones (never clip hard).
                l = black + 0.7 * (Math.Min(lo, 0.3) - black);
                h = white + 0.7 * (Math.Max(hi, 0.55) - white);
                if (h - l < 0.25) h = l + 0.25;
                double m = Math.Max(0.02, Math.Min(0.98, (mid - l) / (h - l)));
                gamma = Math.Max(0.55, Math.Min(1.35, Math.Log(0.46) / Math.Log(m)));   // median -> ~46%
            }
            double contrast = 1 + c.Contrast / 100.0, bright = c.Brightness / 200.0;
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                double n = Math.Max(0, Math.Min(1, (x - l) / (h - l)));
                n = Math.Pow(n, gamma);
                n = (n - 0.5) * contrast + 0.5 + bright;
                n = Math.Max(0, Math.Min(1, n));
                lut[i] = (float)(black + n * (white - black));
            }
        }
    }

    /// <summary>NV12 bilinear resize (only for cameras that give neither 4K nor 1080p).</summary>
    internal static class Nv12
    {
        /// <summary>640x360 BGRA preview of an NV12 picture (BT.709, limited range), for the camera without effects.</summary>
        public static void Preview(byte[] y, byte[] uv, int w, int h, byte[] bgra)
        {
            for (int py = 0; py < 360; py++)
            {
                int sy = py * h / 360;
                for (int px = 0; px < 640; px++)
                {
                    int sx = px * w / 640;
                    double Y = 1.164 * (y[sy * w + sx] - 16), U = uv[(sy / 2) * w + (sx / 2) * 2] - 128, V = uv[(sy / 2) * w + (sx / 2) * 2 + 1] - 128;
                    int o = (py * 640 + px) * 4;
                    bgra[o] = Clamp(Y + 2.112 * U); bgra[o + 1] = Clamp(Y - 0.213 * U - 0.533 * V); bgra[o + 2] = Clamp(Y + 1.793 * V); bgra[o + 3] = 255;
                }
            }
        }

        static byte Clamp(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5); }

        public static void Resize(byte[] sy, byte[] suv, int sw, int sh, byte[] dy, byte[] duv, int dw, int dh)
        {
            Plane(sy, sw, sh, sw, 1, dy, dw, dh, dw);
            Plane(suv, sw / 2, sh / 2, sw, 2, duv, dw / 2, dh / 2, dw);
        }

        static void Plane(byte[] s, int sw, int sh, int sp, int comps, byte[] d, int dw, int dh, int dp)
        {
            System.Threading.Tasks.Parallel.For(0, dh, y =>
            {
                double fy = Math.Max(0, (y + 0.5) * sh / dh - 0.5); int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1); double ay = fy - y0;
                for (int x = 0; x < dw; x++)
                {
                    double fx = Math.Max(0, (x + 0.5) * sw / dw - 0.5); int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1); double ax = fx - x0;
                    for (int c = 0; c < comps; c++)
                    {
                        double a = s[y0 * sp + x0 * comps + c] * (1 - ax) + s[y0 * sp + x1 * comps + c] * ax;
                        double b = s[y1 * sp + x0 * comps + c] * (1 - ax) + s[y1 * sp + x1 * comps + c] * ax;
                        d[y * dp + x * comps + c] = (byte)(a * (1 - ay) + b * ay + 0.5);
                    }
                }
            });
        }
    }
}
