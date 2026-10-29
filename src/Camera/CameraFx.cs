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
    /// (ai/camfx_*.onnx, built by tools/make_camfx.py): the view the director picks (crop + perspective warp: tracking,
    /// zoom, presets, level, whiteboard / desk view), 4K supersampling, tone curve, HDR, whiteboard enhancement,
    /// colour, denoise, skin smoothing, sharpen, Robust Video Matting cut-out with blur / picture / video / green
    /// backgrounds, mirror, 9:16 guide, NV12 out; plus UltraFace face detection on the full frame and a luma thumbnail
    /// for auto light. Hand gestures and the whiteboard finder look at the full frame on the CPU. Then, on the CPU,
    /// air drawing's lines and the hotkey animations go onto the picture apps get.
    /// Runs only while an app watches the virtual camera or the preview is open.
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
        /// <summary>The camera operator: what part of the picture apps see (tracking, presets, whiteboard...).</summary>
        public readonly CameraDirector Director = new CameraDirector();
        /// <summary>Privacy (hotkey / button): apps see a "camera paused" picture instead of you.</summary>
        public volatile bool Privacy;
        /// <summary>The privacy picture is showing (by hand, or nobody in front of the camera with auto privacy).</summary>
        public volatile bool PrivacyShowing;
        /// <summary>Hand gestures running, and what they see (status).</summary>
        public volatile string GestureStatus = "";
        /// <summary>The hand models while they run (look time, looks a second, errors), else null.</summary>
        public HandGestures Hands { get { return gestures; } }
        /// <summary>A face is in the picture for the gestures to find hands beside (they need the AI effects running).</summary>
        public volatile bool HandsHaveFace;
        /// <summary>Air drawing: the lines your pointing finger leaves on the picture.</summary>
        public readonly AirDrawing Ink = new AirDrawing();
        /// <summary>The camera animation playing now (CameraAnimation.Kinds), or null.</summary>
        public volatile string Animation;
        /// <summary>The whole camera picture, raw (640x360 BGRA), a few times a second while FullFrameWanted (zone editor).</summary>
        public event Action<byte[]> FullFrameReady;
        public volatile bool FullFrameWanted;
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
        volatile Thread worker;
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
            inkUpsideDown = s.CamFx.UpsideDown;
            monitor = new Thread(Monitor) { IsBackground = true, Name = "StreamLite Camera" };
            monitor.Start();
        }

        // ------------------------------------------------------------------ lifecycle

        void Monitor()
        {
            long nextTry = 0; int fails = 0; bool asked = false;
            while (!quit)
            {
                try
                {
                    bool enabled = C.Enabled && VirtualCamera.Supported && ModelsInstalled;
                    if (enabled && !CamOk && clock.ElapsedMilliseconds >= nextTry)
                    {
                        // Not set up yet and no admin rights: Windows asks once (again after the camera is switched off and on).
                        if (!asked && VirtualCamera.Register() == VirtualCamera.NeedsAdmin)
                        {
                            asked = true;
                            Status = Os.Linux ? "Waiting for your password to add the camera..." : "Waiting for Windows' permission to set up the camera...";
                            string err = VirtualCamera.RegisterWithPrompt();
                            if (err != null) Cam.Error = err;
                        }
                        CamOk = Cam.Start();
                        if (CamOk) { fails = 0; Error = null; }
                        else
                        {
                            // Often temporary (Windows still removing the camera of a StreamLite that just closed): retry
                            // after 3, 6, 12... up to 60 seconds.
                            Error = Cam.Error == VirtualCamera.NeedsAdmin && asked ? (Os.Linux ? "The password prompt was cancelled" : "Windows' permission prompt was declined") + " - turn the camera off and on to be asked again." : Cam.Error;
                            Cam.Dispose();
                            nextTry = clock.ElapsedMilliseconds + Math.Min(60000, 3000L << Math.Min(fails++, 5));
                        }
                    }
                    if (!enabled && CamOk) { Cam.Dispose(); CamOk = false; nextTry = 0; }
                    if (!enabled) asked = false;
                    bool watched = CamOk && Cam.SinceLastConsumer < 2500;
                    bool want = enabled && (watched || PreviewWanted);
                    if (want) lastWanted = clock.ElapsedMilliseconds;
                    bool running = worker != null;
                    if (want && !running) StartPipeline();
                    else if (!want && running && clock.ElapsedMilliseconds - lastWanted > 4000) StopPipeline();
                    if (!enabled) Status = !C.Enabled ? "Off" : !VirtualCamera.Supported ? VirtualCamera.WhyNot : !ModelsInstalled ? "AI files missing - reinstall" : "Off";
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
                var crop = new long[4]; var p = new float[24]; var warp = new float[9]; var lut = new float[256];
                var faces = new FaceBox[8]; int nFaces = 0;
                var states = new float[4][]; var statesOut = new float[4][];
                for (int i = 0; i < 4; i++) { states[i] = new float[RS[i][0] * RS[i][1] * RS[i][2]]; statesOut[i] = new float[states[i].Length]; }
                var yOut = new byte[OW * OH]; var uvOut = new byte[OW * OH / 2]; var preview = new byte[360 * 640 * 4];
                var thumb = new byte[90 * 160]; var scores = new float[4420 * 2]; var boxes = new float[4420 * 4];
                var shapes = new[]
                {
                    new long[] { GH, GW }, new long[] { GH / 2, GW }, new long[] { 4 }, new long[] { 24 }, new long[] { 9 }, new long[] { 256 }, new long[] { 540, 960, 3 },
                    new long[] { 1, RS[0][0], RS[0][1], RS[0][2] }, new long[] { 1, RS[1][0], RS[1][1], RS[1][2] },
                    new long[] { 1, RS[2][0], RS[2][1], RS[2][2] }, new long[] { 1, RS[3][0], RS[3][1], RS[3][2] },
                };
                byte[] none = new byte[540 * 960 * 3], green = Backgrounds.Solid(0, 255, 0);
                byte[] privY = null, privUv = null, privPreview = null, fullPreview = null;
                var finder = new WhiteboardFinder();
                double lastFaceAt = clock.Elapsed.TotalSeconds;
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

                    // The view (tracking, presets, whiteboard...), effect parameters, tone curve, background.
                    var c = C;
                    double now = clock.Elapsed.TotalSeconds;
                    // A flip animation ended on the last frame: the camera is upside down (or right way up) from now on.
                    if (flipEnded) { flipEnded = false; c.UpsideDown = !c.UpsideDown; Director.SettingsChanged = true; }
                    // Turned round (flip, hotkey, Camera page): the drawing turns with the picture.
                    if (c.UpsideDown != inkUpsideDown) { Ink.Rotate180(); inkUpsideDown = c.UpsideDown; }
                    if (c.ViewMode == "whiteboard") { if (processed % 15 == 0) Director.Board = finder.Look(y, uv, GW, GH); }
                    else if (Director.Board != null) Director.Board = null;
                    Director.Step(c, faces, nFaces, GW, GH, crop, warp);
                    light.Build(c, lut);
                    FillParams(c, p, Director);
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
                    }
                    else
                    {
                        // The same two argument lists every frame (no garbage at 30 fps); only the background and the
                        // swapped state buffers change.
                        if (runIn == null) { runIn = new Array[] { y, uv, crop, p, warp, lut, bg, null, null, null, null }; runOut = new Array[] { yOut, uvOut, preview, thumb, scores, boxes, null, null, null, null }; }
                        runIn[6] = bg;
                        for (int i = 0; i < 4; i++) { runIn[7 + i] = states[i]; runOut[6 + i] = statesOut[i]; }
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

                    if (!plain)
                    {
                        nFaces = Faces.Decode(scores, boxes, faces);
                        FaceFound = nFaces > 0;
                        if (nFaces > 0) lastFaceAt = clock.Elapsed.TotalSeconds;
                        var d = Director;
                        if (c.FaceExposure && d.FaceOutX1 > d.FaceOutX0) light.Measure(thumb, d.FaceOutX0, d.FaceOutY0, d.FaceOutX1, d.FaceOutY1);
                        else light.Measure(thumb);
                        // Hands near the followed (or the biggest) face, about 8 times a second - with tracking on or off:
                        // the faces come from the effects' own face detector every frame, so gestures never need (or
                        // turn on) tracking. Air drawing: every frame, following the hand wherever it goes.
                        bool drawing = Ink.On && c.GesturePoint;
                        bool wantHands = (c.GesturePalm || c.GestureV || c.GestureL || drawing) && HandGestures.Installed;
                        HandsHaveFace = nFaces > 0;
                        if (wantHands)
                        {
                            if (gestures == null)
                            {
                                var g = new HandGestures();
                                g.Gesture += s => RouteGesture(C, s, Director, Ink);
                                g.Zoom += f => RouteZoom(C, f, Director);
                                g.Tip += OnTip;
                                gestures = g;
                            }
                            gestures.Fast = drawing;
                            if (drawing || processed % 4 == 0)
                                gestures.Offer(y, uv, GW, GH, nFaces > 0 ? faces[GestureFace(c, faces, nFaces, Director.FollowedIndex)] : (FaceBox?)null, now);
                        }
                        else if (gestures != null) { gestures.Dispose(); gestures = null; }
                        var hg = gestures;
                        GestureStatus = hg == null ? "" : hg.Error != null ? "gestures failed: " + hg.Error : hg.Seeing;
                    }
                    else HandsHaveFace = false;

                    // Air drawing and animations, on the picture apps get (the preview then shows the same).
                    bool inked = Ink.Render(yOut, uvOut, c, now);
                    bool moved = Animate(yOut, uvOut, now);
                    if (PreviewReady != null && (plain || inked || moved)) Nv12.Preview(yOut, uvOut, OW, OH, preview);
                    // Privacy: by hand, or nobody in front of the camera for a while (auto privacy).
                    bool priv = Privacy || (c.AutoPrivacy && !plain && clock.Elapsed.TotalSeconds - lastFaceAt > c.PrivacySeconds);
                    PrivacyShowing = priv;
                    if (priv && privY == null) Backgrounds.Privacy(out privY, out privUv, out privPreview);
                    if (CamOk) { if (priv) Cam.Publish(privY, privUv); else Cam.Publish(yOut, uvOut); }
                    var pr = PreviewReady;
                    if (pr != null) pr(priv ? privPreview : preview);
                    var ff = FullFrameReady;
                    if (ff != null && FullFrameWanted && processed % 6 == 0)
                    {
                        if (fullPreview == null) fullPreview = new byte[360 * 640 * 4];
                        Nv12.Preview(y, uv, GW, GH, fullPreview);
                        ff(fullPreview);
                    }

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
                if (gestures != null) { gestures.Dispose(); gestures = null; }
                if (reader != null) { reader.FrameArrived -= OnFrame; reader.Dispose(); reader = null; }
                if (session != null) session.Dispose();
                if (video != null) video.Dispose();
                CaptureW = CaptureH = 0;
            }
        }

        volatile HandGestures gestures;
        bool flipEnded, inkUpsideDown;

        /// <summary>
        /// A held hand sign -> what it does, each only while its own switch is on. None of them turns tracking on by
        /// itself: only the open palm, when you show it, switches tracking (on or off). The V turns air drawing off
        /// while it's on, else does what "V does" says (whiteboard view or air drawing).
        /// </summary>
        internal static void RouteGesture(CamFxSettings c, string g, CameraDirector d, AirDrawing ink)
        {
            if (g == "palm" && c.GesturePalm) d.ToggleTracking();
            else if (g == "v" && c.GestureV)
            {
                if (ink.On) ink.SetOn(false);
                else if (c.VAction == "draw") ink.SetOn(true);
                else d.SetViewMode("whiteboard");
            }
        }

        /// <summary>The L gesture's zoom, while its switch is on.</summary>
        internal static void RouteZoom(CamFxSettings c, double factor, CameraDirector d) { if (c.GestureL) d.GestureZoom(factor); }

        /// <summary>The face whose hands count: the one tracking follows, else the biggest (with tracking off the
        /// director's choice is stale).</summary>
        internal static int GestureFace(CamFxSettings c, FaceBox[] faces, int n, int followed)
        {
            if (c.FaceTracking && followed >= 0 && followed < n) return followed;
            int best = 0;
            for (int i = 1; i < n; i++) if (faces[i].H > faces[best].H) best = i;
            return best;
        }

        /// <summary>Gesture thread, air drawing: the fingertip seen (camera 0..1) -> the pen, on the picture as apps see it.</summary>
        void OnTip(double x, double y, string pose, double t) { FeedTip(C, Director, Ink, x, y, pose, t); }

        /// <summary>One look's fingertip (camera 0..1, NaN = no hand) -> the pen: down only while the hand points and
        /// "Point: draw" is on.</summary>
        internal static void FeedTip(CamFxSettings c, CameraDirector d, AirDrawing ink, double x, double y, string pose, double t)
        {
            if (!ink.On) return;
            if (double.IsNaN(x) || double.IsNaN(y)) { ink.Feed(double.NaN, double.NaN, false, t); return; }
            double u, v; TipToOutput(c, d, x, y, out u, out v);
            ink.Feed(u, v, pose == "point" && c.GesturePoint, t);
        }

        /// <summary>A point of the camera's picture (0..1) -> where apps see it (0..1): through the view, then mirrored
        /// when the picture is - so the line is drawn right under your fingertip, upside down or mirrored or not.</summary>
        internal static void TipToOutput(CamFxSettings c, CameraDirector d, double x, double y, out double u, out double v)
        {
            d.CameraToOutput(x, y, out u, out v);
            if (Mirrored(c, d)) u = 1 - u;
        }

        /// <summary>The picture apps get is mirrored (a whiteboard or desk view never is: writing stays readable).</summary>
        internal static bool Mirrored(CamFxSettings c, CameraDirector d)
        {
            bool squared = c.ViewMode == "whiteboard" && d.Board != null || c.ViewMode == "desk";
            return c.Mirror && !squared;
        }

        // ------------------------------------------------------------------ animations

        volatile string animWanted;
        string animKind; double animStart;
        byte[] animY, animUv;

        /// <summary>The camera's effects are running (an app watches it, or the preview is on).</summary>
        public bool Running { get { return worker != null; } }

        /// <summary>
        /// Plays a camera animation (CameraAnimation.Kinds) on what apps see; any thread. False: unknown, or the camera
        /// isn't running - a flip then just turns the picture round.
        /// </summary>
        public bool Play(string kind)
        {
            if (Array.IndexOf(CameraAnimation.Kinds, kind) < 0) return false;
            if (!Running) { if (kind == "flip") { C.UpsideDown = !C.UpsideDown; Director.SettingsChanged = true; } return false; }
            animWanted = kind;
            return true;
        }

        /// <summary>Camera thread: the animation's frame at time now onto the output picture. False: none playing.</summary>
        bool Animate(byte[] yo, byte[] uvo, double now)
        {
            var w = animWanted;
            if (w != null) { animWanted = null; if (animKind == null) { animKind = w; animStart = now; } }
            if (animKind == null) return false;
            double p = Math.Min(1, (now - animStart) / CameraAnimation.Duration(animKind));
            if (animY == null) { animY = new byte[OW * OH]; animUv = new byte[OW * OH / 2]; }
            CameraAnimation.Warp(yo, uvo, OW, OH, CameraAnimation.At(animKind, p), animY, animUv);
            if (p >= 1) { if (animKind == "flip") flipEnded = true; animKind = null; }
            Animation = animKind;
            return true;
        }

        internal static void FillParams(CamFxSettings c, float[] p, CameraDirector d)
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
            p[8] = (float)d.EllipseX; p[9] = (float)d.EllipseY; p[10] = (float)Math.Max(1e-4, d.EllipseRx); p[11] = (float)Math.Max(1e-4, d.EllipseRy);
            p[12] = Mirrored(c, d) ? 1 : 0;   // a whiteboard or desk is never mirrored (writing stays readable)
            p[13] = c.EdgeSharpness / 100f;
            p[14] = c.ViewMode == "whiteboard" && d.Board != null ? 1 : 0;
            p[15] = c.Hdr / 100f;
            p[16] = c.Vertical ? 1 : 0;
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
    /// Auto light: a tone curve from the picture's own levels (slow, so it never pumps): stretches dark rooms,
    /// lifts the mid-tones towards a natural exposure, then your brightness/contrast. Output in limited-range luma.
    /// </summary>
    internal sealed class AutoLight
    {
        double lo = 16 / 255.0, mid = 0.45, hi = 235 / 255.0;
        bool measured;
        readonly int[] hist = new int[256];

        /// <summary>Face exposure: the face's part of the thumbnail (output 0..1) is metered for the mid-tones.</summary>
        public void Measure(byte[] thumb, double fx0, double fy0, double fx1, double fy1)
        {
            int x0 = Math.Max(0, (int)(fx0 * 160)), x1 = Math.Min(160, (int)Math.Ceiling(fx1 * 160)), y0 = Math.Max(0, (int)(fy0 * 90)), y1 = Math.Min(90, (int)Math.Ceiling(fy1 * 90));
            double sum = 0; int n = 0;
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) { sum += thumb[yy * 160 + xx]; n++; }
            Measure(thumb);
            // Mostly the face (a face against a bright window comes up, a lit face in a dark room comes down).
            if (n >= 4) { faceMid += (faceMeasured ? 0.03 : 1) * (sum / n / 255.0 - faceMid); faceMeasured = true; }
            faceSeen = n >= 4;
        }
        double faceMid = 0.45; bool faceMeasured, faceSeen;

        public void Measure(byte[] thumb)
        {
            faceSeen = false;
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
                double mm = faceSeen && c.FaceExposure ? 0.7 * faceMid + 0.3 * mid : mid;
                double m = Math.Max(0.02, Math.Min(0.98, (mm - l) / (h - l)));
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
