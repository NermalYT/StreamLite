using System;
using System.IO;
using System.Threading;
using StreamLite.Audio;

namespace StreamLite.Camera
{
    /// <summary>Where a hand is: a square of the camera's picture (centre and side in camera pixels), turned so the
    /// hand stands upright in it (radians).</summary>
    internal struct HandRoi
    {
        public double Cx, Cy, Side, Angle;
    }

    /// <summary>
    /// Hand gestures, like the Insta360 Link's: an open palm turns tracking on or off, a V turns the whiteboard view
    /// (or air drawing) on or off, an L (thumb and index finger) zooms while you move your hand up (in) or down (out).
    /// Only hands near a face count (raised beside your head), held for under half a second. Each sign only fires
    /// what its own switch allows (CameraFx.RouteGesture).
    /// MediaPipe's palm detector and hand landmark model (OpenCV model zoo, Apache-2.0) on the CPU, on their own
    /// thread, a few times a second; the camera thread only cuts out the area around your head.
    /// Fast (air drawing): it looks at every frame and follows the hand from its last landmarks - only the landmark
    /// model runs, on a crop where the hand just was (MediaPipe's tracking) - so the fingertip is seen 20-30 times a
    /// second; the palm detector only runs again once the hand is lost.
    /// </summary>
    internal sealed class HandGestures : IDisposable
    {
        public const string PalmFile = "hand_palm.onnx", LandmarkFile = "hand_landmarks.onnx";
        public static bool Installed { get { return OnnxSession.Available && File.Exists(Path.Combine(OnnxSession.Folder, PalmFile)) && File.Exists(Path.Combine(OnnxSession.Folder, LandmarkFile)); } }

        /// <summary>A gesture was made and held: "palm" or "v" (once per gesture).</summary>
        public event Action<string> Gesture;
        /// <summary>The L gesture is held and the hand moved: zoom by this factor.</summary>
        public event Action<double> Zoom;
        /// <summary>
        /// Fast only, after every look: the index fingertip (camera 0..1; NaN = no hand), the hand's pose ("point",
        /// "v", "palm", "l", "fist" or "") and when its frame was taken (seconds, the time given to Offer).
        /// </summary>
        public event Action<double, double, string, double> Tip;
        /// <summary>Air drawing: look at every frame offered and follow the hand (else a few looks a second).</summary>
        public volatile bool Fast;
        /// <summary>What it sees right now ("", "palm", "v", "l", "point", "fist"), how long a look takes (ms,
        /// averaged), how many looks it made in the last second, and whether the last one followed the hand
        /// (landmarks only) or searched for it (palm detector first).</summary>
        public volatile string Seeing = "";
        public volatile float LookMs, LooksPerSecond;
        public volatile bool Following;
        public volatile string Error;

        const int TileSize = 384, CropSize = 224;

        /// <summary>What the camera thread cut out for the next look.</summary>
        sealed class Job
        {
            public readonly byte[] Tile = new byte[TileSize * TileSize * 3], Crop = new byte[CropSize * CropSize * 3];
            public bool Track;            // a crop where the hand was (Crop) instead of the area around the face (Tile)
            public double X0, Y0, Step;   // Tile pixel -> camera pixel: x0 + tx * step
            public HandRoi Roi;           // where Crop was cut
            public double FaceH, Time;
            public int W, H;
        }

        readonly Job job = new Job();
        Job ready;                                // the job waiting for (or being looked at by) the worker; null = none
        HandRoi roi; bool roiValid; double roiTime;   // where the hand was at the last look (worker -> camera thread)
        double lastFaceH = 0.2;                   // camera thread
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool stop;
        static readonly float[] Anchors = MakeAnchors();

        public HandGestures()
        {
            worker = new Thread(Work) { IsBackground = true, Name = "StreamLite Camera gestures", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        /// <summary>
        /// Camera thread: the next look, from the camera's NV12 frame (W x H) taken at time t (seconds). While Fast and
        /// the hand was found at the last look: a crop where it was. Else the area around this face (camera 0..1; null
        /// = no face, no look). False while the last look is still being looked at.
        /// </summary>
        public bool Offer(byte[] y, byte[] uv, int W, int H, FaceBox? face, double t)
        {
            bool track; HandRoi r;
            lock (gate)
            {
                if (ready != null) return false;
                track = Fast && roiValid && t - roiTime < 0.5;
                r = roi;
            }
            if (!track && face == null) return false;
            var j = job;
            if (face != null) lastFaceH = face.Value.H;
            if (track) { CutCrop(y, uv, W, H, r, j.Crop); j.Roi = r; }
            else
            {
                var f = face.Value;
                // A square a little over five face-heights wide, centred a bit below the face: your raised hands.
                double side = Math.Max(0.2 * H, f.H * H * 5.2), cx = f.Cx * W, cy = f.Cy * H + f.H * H * 0.6;
                double x0 = cx - side / 2, y0 = cy - side / 2, step = side / TileSize;
                var dst = j.Tile;
                for (int ty = 0; ty < TileSize; ty++)
                {
                    int sy = (int)(y0 + (ty + 0.5) * step);
                    int o = ty * TileSize * 3;
                    if (sy < 0 || sy >= H) { Array.Clear(dst, o, TileSize * 3); continue; }
                    int rowY = sy * W, rowC = (sy >> 1) * W;
                    for (int tx = 0; tx < TileSize; tx++, o += 3)
                    {
                        int sx = (int)(x0 + (tx + 0.5) * step);
                        if (sx < 0 || sx >= W) { dst[o] = dst[o + 1] = dst[o + 2] = 0; continue; }
                        int Y = (y[rowY + sx] - 16) * 298, ci = rowC + (sx & ~1), U = uv[ci] - 128, V = uv[ci + 1] - 128;
                        dst[o] = Clamp((Y + 409 * V + 128) >> 8); dst[o + 1] = Clamp((Y - 100 * U - 208 * V + 128) >> 8); dst[o + 2] = Clamp((Y + 516 * U + 128) >> 8);
                    }
                }
                j.X0 = x0; j.Y0 = y0; j.Step = step;
            }
            j.Track = track; j.FaceH = lastFaceH; j.Time = t; j.W = W; j.H = H;
            lock (gate) ready = j;
            wake.Set();
            return true;
        }

        /// <summary>A point of the landmark model's input (crop pixels, as it reports landmarks) -> camera pixels.</summary>
        internal static void CropToCamera(HandRoi r, double lx, double ly, out double x, out double y)
        {
            double k = r.Side / CropSize, ca = Math.Cos(r.Angle), sa = Math.Sin(r.Angle);
            double qx = (lx - CropSize / 2.0) * k, qy = (ly - CropSize / 2.0) * k;
            x = r.Cx + qx * ca - qy * sa; y = r.Cy + qx * sa + qy * ca;
        }

        /// <summary>The landmark model's 224 x 224 RGB input, cut straight from the camera's NV12 frame at a hand's square
        /// (bilinear luma; outside the picture = black).</summary>
        internal static void CutCrop(byte[] y, byte[] uv, int W, int H, HandRoi r, byte[] dst)
        {
            double k = r.Side / CropSize, ca = Math.Cos(r.Angle), sa = Math.Sin(r.Angle);
            int o = 0;
            for (int i = 0; i < CropSize; i++)
            {
                double qy = (i + 0.5 - CropSize / 2.0) * k;
                for (int j = 0; j < CropSize; j++, o += 3)
                {
                    double qx = (j + 0.5 - CropSize / 2.0) * k;
                    double X = r.Cx + qx * ca - qy * sa - 0.5, Y = r.Cy + qx * sa + qy * ca - 0.5;
                    int x0 = (int)Math.Floor(X), y0 = (int)Math.Floor(Y);
                    if (x0 < 0 || y0 < 0 || x0 + 1 >= W || y0 + 1 >= H) { dst[o] = dst[o + 1] = dst[o + 2] = 0; continue; }
                    double fx = X - x0, fy = Y - y0;
                    int p = y0 * W + x0;
                    double l = (y[p] * (1 - fx) + y[p + 1] * fx) * (1 - fy) + (y[p + W] * (1 - fx) + y[p + W + 1] * fx) * fy;
                    int ci = (y0 >> 1) * W + (x0 & ~1);
                    int Yv = ((int)(l + 0.5) - 16) * 298, U = uv[ci] - 128, V = uv[ci + 1] - 128;
                    dst[o] = Clamp((Yv + 409 * V + 128) >> 8); dst[o + 1] = Clamp((Yv - 100 * U - 208 * V + 128) >> 8); dst[o + 2] = Clamp((Yv + 516 * U + 128) >> 8);
                }
            }
        }

        static byte Clamp(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        /// <summary>Tests: the next Fast look follows a hand at this square (as if it had been found there).</summary>
        internal void FollowAt(HandRoi r, double t) { lock (gate) { roi = r; roiValid = true; roiTime = t; } }

        // ------------------------------------------------------------------ the worker

        void Work()
        {
            OnnxSession palm = null, marks = null;
            try
            {
                palm = new OnnxSession(Path.Combine(OnnxSession.Folder, PalmFile), false, 2);
                marks = new OnnxSession(Path.Combine(OnnxSession.Folder, LandmarkFile), false, 2);
                var pin = new float[192 * 192 * 3]; var raw = new float[2016 * 18]; var sc = new float[2016];
                var hin = new float[224 * 224 * 3]; var lm = new float[63]; var conf = new float[1]; var hand = new float[1]; var world = new float[63];
                var cam = new double[42];
                long[][] pShape = { new long[] { 1, 192, 192, 3 } }, hShape = { new long[] { 1, 224, 224, 3 } };
                Array[] pIn = { pin }, pOut = { raw, sc }, hIn = { hin }, hOut = { lm, conf, hand, world };
                var sw = new System.Diagnostics.Stopwatch();
                var rate = System.Diagnostics.Stopwatch.StartNew(); int looks = 0;
                while (!stop)
                {
                    wake.WaitOne(500);
                    if (stop) break;
                    Job j; lock (gate) j = ready;
                    if (j == null) continue;
                    // Drawing: keep up with the camera even when the PC is busy; else stay out of the way.
                    var want = Fast ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
                    if (Thread.CurrentThread.Priority != want) Thread.CurrentThread.Priority = want;
                    sw.Restart();
                    bool found = false; float score = 0;
                    if (j.Track)
                    {
                        // Following: only the landmark model, on the crop where the hand was.
                        for (int i = 0; i < hin.Length; i++) hin[i] = j.Crop[i] / 255f;
                        marks.Run(hIn, hShape, hOut);
                        score = conf[0];
                        if (score > 0.5)
                        {
                            found = true;
                            for (int i = 0; i < 21; i++) CropToCamera(j.Roi, lm[i * 3], lm[i * 3 + 1], out cam[i * 2], out cam[i * 2 + 1]);
                        }
                    }
                    else
                    {
                        // Searching: the palm detector on the tile (192 x 192), then the landmarks of the best palm.
                        Resample(j.Tile, TileSize, pin, 192, 0, 0, TileSize / 192.0, 0, TileSize / 2.0, TileSize / 2.0);
                        palm.Run(pIn, pShape, pOut);
                        int best = -1; double bestS = 0.55;
                        for (int i = 0; i < 2016; i++)
                        {
                            double s = 1 / (1 + Math.Exp(-sc[i]));
                            if (s > bestS) { bestS = s; best = i; }
                        }
                        if (best >= 0)
                        {
                            // Box and the 7 palm points, in tile pixels.
                            double ax = Anchors[best * 2], ay = Anchors[best * 2 + 1], k = TileSize / 192.0;
                            double bx = (raw[best * 18] + ax * 192) * k, by = (raw[best * 18 + 1] + ay * 192) * k;
                            var kp = new double[14];
                            for (int q = 0; q < 7; q++) { kp[q * 2] = (raw[best * 18 + 4 + q * 2] + ax * 192) * k; kp[q * 2 + 1] = (raw[best * 18 + 5 + q * 2] + ay * 192) * k; }
                            // Turn the hand upright (wrist -> middle finger's base pointing up), MediaPipe's way.
                            double ang = Math.PI / 2 - Math.Atan2(-(kp[5] - kp[1]), kp[4] - kp[0]);
                            ang -= 2 * Math.PI * Math.Floor((ang + Math.PI) / (2 * Math.PI));
                            double ca = Math.Cos(ang), sa = Math.Sin(ang);
                            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                            for (int q = 0; q < 7; q++)
                            {
                                double dx = kp[q * 2] - bx, dy = kp[q * 2 + 1] - by;
                                double qx = dx * ca + dy * sa, qy = -dx * sa + dy * ca;   // turned (screen counter-clockwise)
                                minX = Math.Min(minX, qx); maxX = Math.Max(maxX, qx); minY = Math.Min(minY, qy); maxY = Math.Max(maxY, qy);
                            }
                            double bw = maxX - minX, bh = maxY - minY;
                            double ccx = (minX + maxX) / 2, ccy = (minY + maxY) / 2 - 0.4 * bh, side = 3 * Math.Max(bw, bh);
                            Resample(j.Tile, TileSize, hin, 224, ccx, ccy, side / 224, ang, bx, by);
                            marks.Run(hIn, hShape, hOut);
                            score = conf[0];
                            if (score > 0.8)
                            {
                                found = true;
                                // Landmarks: crop -> tile -> camera pixels.
                                double k2 = side / 224;
                                for (int i = 0; i < 21; i++)
                                {
                                    double qx = (lm[i * 3] - 112) * k2 + ccx, qy = (lm[i * 3 + 1] - 112) * k2 + ccy;
                                    double tx = bx + qx * ca - qy * sa, ty = by + qx * sa + qy * ca;
                                    cam[i * 2] = j.X0 + tx * j.Step; cam[i * 2 + 1] = j.Y0 + ty * j.Step;
                                }
                            }
                        }
                    }
                    bool tracked = j.Track;
                    double time = j.Time, faceH = j.FaceH; int W = j.W, H = j.H;
                    // Where to look next time (Fast): the hand's square from these landmarks, if it's a sensible size.
                    HandRoi next = found ? RoiFrom(cam) : new HandRoi();
                    bool keep = found && Fast && next.Side > 0.03 * H && next.Side < 1.2 * H;
                    lock (gate) { roiValid = keep; if (keep) { roi = next; roiTime = time; } ready = null; }   // the camera thread can cut the next one now

                    float ms = (float)sw.Elapsed.TotalMilliseconds;
                    LookMs = LookMs <= 0 ? ms : LookMs + 0.1f * (ms - LookMs);
                    Following = tracked;
                    looks++;
                    if (rate.ElapsedMilliseconds >= 1000) { LooksPerSecond = looks * 1000f / rate.ElapsedMilliseconds; looks = 0; rate.Restart(); }
                    string g = found && score > 0.8 ? Classify(lm) : "";
                    Seeing = g;
                    Decide(g, found ? cam[19] / H : double.NaN, faceH, time);
                    var tip = Tip;
                    if (tip != null && Fast) tip(found ? cam[16] / W : double.NaN, found ? cam[17] / H : double.NaN, g, time);
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { if (palm != null) palm.Dispose(); if (marks != null) marks.Dispose(); }
        }

        /// <summary>
        /// The square to look at next for a hand seen with these landmarks (21 x, y in camera pixels), MediaPipe's
        /// tracking rule: turned by wrist -> middle of the finger bases, around the palm and finger joints (not the
        /// fingertips), twice as big and nudged towards the fingers.
        /// </summary>
        internal static HandRoi RoiFrom(double[] p)
        {
            double x0 = p[0], y0 = p[1];
            double x1 = ((p[10] + p[26]) / 2 + p[18]) / 2, y1 = ((p[11] + p[27]) / 2 + p[19]) / 2;   // landmarks 5, 13 and 9
            double ang = Math.PI / 2 - Math.Atan2(-(y1 - y0), x1 - x0);
            ang -= 2 * Math.PI * Math.Floor((ang + Math.PI) / (2 * Math.PI));
            double ca = Math.Cos(ang), sa = Math.Sin(ang);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (int i in Partial)
            {
                double dx = p[i * 2] - x0, dy = p[i * 2 + 1] - y0;
                double qx = dx * ca + dy * sa, qy = -dx * sa + dy * ca;
                minX = Math.Min(minX, qx); maxX = Math.Max(maxX, qx); minY = Math.Min(minY, qy); maxY = Math.Max(maxY, qy);
            }
            double bw = maxX - minX, bh = maxY - minY;
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2 - 0.1 * bh;
            return new HandRoi { Cx = x0 + cx * ca - cy * sa, Cy = y0 + cx * sa + cy * ca, Side = 2 * Math.Max(bw, bh), Angle = ang };
        }

        static readonly int[] Partial = { 0, 1, 2, 3, 5, 6, 9, 10, 13, 14, 17, 18 };

        // ------------------------------------------------------------------ deciding

        /// <summary>A sign counts once held this long (seconds), and the same one fires again only after the hand left
        /// it for longer than RearmSeconds (a missed look or two in between doesn't count as a new sign).</summary>
        internal const double HoldSeconds = 0.4, RearmSeconds = 0.7;
        string held = ""; double heldSince; bool fired; string lastFired = ""; double lastFiredSeen = -1e9; double lZoomRef = double.NaN;

        /// <summary>
        /// What a look saw (g), at time now (seconds): palm and V fire once per showing; the L zooms continuously by how
        /// far the hand moved since the L appeared (a face-height up = 1.6x closer). Pointing (the pen) and a fist do
        /// nothing here.
        /// </summary>
        internal void Decide(string g, double handY, double faceH, double now)
        {
            if (g != held)
            {
                held = g; heldSince = now; lZoomRef = double.NaN;
                fired = g != "" && g == lastFired && now - lastFiredSeen < RearmSeconds;
            }
            if (g != "" && g == lastFired) lastFiredSeen = now;
            if (g == "" || now - heldSince < HoldSeconds - 1e-9) return;
            if (g == "l")
            {
                if (double.IsNaN(handY)) return;
                if (double.IsNaN(lZoomRef)) { lZoomRef = handY; return; }
                double moved = (lZoomRef - handY) / Math.Max(0.02, faceH);   // + = up
                if (Math.Abs(moved) < 0.15) return;
                lZoomRef = handY;
                var z = Zoom; if (z != null) z(Math.Pow(1.6, moved));
                return;
            }
            if (g != "palm" && g != "v") return;
            if (fired) return;
            fired = true; lastFired = g; lastFiredSeen = now;
            var e = Gesture; if (e != null) e(g);
        }

        /// <summary>
        /// Bilinear RGB samples from the tile into an n x n float image (0..1): output pixel centre i -> point
        /// (cx, cy) + ((i + 0.5) - n/2) * step in a frame turned by ang around (ox, oy).
        /// </summary>
        static void Resample(byte[] t, int T, float[] dst, int n, double cx, double cy, double step, double ang, double ox, double oy)
        {
            double ca = Math.Cos(ang), sa = Math.Sin(ang);
            bool plain = ang == 0 && cx == 0 && cy == 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double qx = (j + 0.5 - n / 2.0) * step, qy = (i + 0.5 - n / 2.0) * step;
                    double px, py;
                    if (plain) { px = ox + qx; py = oy + qy; }
                    else { qx += cx; qy += cy; px = ox + qx * ca - qy * sa; py = oy + qx * sa + qy * ca; }
                    px -= 0.5; py -= 0.5;
                    int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
                    double fx = px - x0, fy = py - y0;
                    int o = (i * n + j) * 3;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double a = Px(t, T, x0, y0, ch) * (1 - fx) + Px(t, T, x0 + 1, y0, ch) * fx;
                        double b = Px(t, T, x0, y0 + 1, ch) * (1 - fx) + Px(t, T, x0 + 1, y0 + 1, ch) * fx;
                        dst[o + ch] = (float)((a * (1 - fy) + b * fy) / 255.0);
                    }
                }
        }

        static double Px(byte[] t, int T, int x, int y, int ch) { return x < 0 || y < 0 || x >= T || y >= T ? 0 : t[(y * T + x) * 3 + ch]; }

        /// <summary>
        /// The pose of a hand from its 21 landmarks (x, y, z; any one scale, any turn): "palm" (all five fingers out),
        /// "v" (index and middle out, ring and little finger in), "l" (thumb and index out at an angle, the other three
        /// in), "point" (only the index finger out - the pen), "fist" (all four fingers in), or "".
        /// </summary>
        internal static string Classify(float[] lm)
        {
            Func<int, int, double> d = (a, b) => Math.Sqrt((lm[a * 3] - lm[b * 3]) * (lm[a * 3] - lm[b * 3]) + (lm[a * 3 + 1] - lm[b * 3 + 1]) * (lm[a * 3 + 1] - lm[b * 3 + 1]));
            double palm = Math.Max(1e-3, d(0, 9));
            Func<int, int, int, bool> outF = (mcp, pip, tip) => d(0, tip) > d(0, pip) * 1.2 && d(tip, mcp) > palm * 0.55;
            Func<int, int, int, bool> inF = (mcp, pip, tip) => d(0, tip) < d(0, pip) * 1.1 || d(tip, mcp) < palm * 0.45;
            bool index = outF(5, 6, 8), middle = outF(9, 10, 12), ring = outF(13, 14, 16), pinky = outF(17, 18, 20);
            bool indexIn = inF(5, 6, 8), middleIn = inF(9, 10, 12), ringIn = inF(13, 14, 16), pinkyIn = inF(17, 18, 20);
            bool thumb = d(4, 5) > palm * 0.6 && d(0, 4) > d(0, 3) * 1.03;
            if (index && middle && ring && pinky && thumb) return "palm";
            if (index && middle && ringIn && pinkyIn && d(8, 12) > palm * 0.3) return "v";
            if (index && middleIn && ringIn && pinkyIn)
            {
                if (thumb)
                {
                    double tx = lm[12] - lm[6], ty = lm[13] - lm[7], ix = lm[24] - lm[15], iy = lm[25] - lm[16];   // thumb 2->4, index 5->8
                    double cos = (tx * ix + ty * iy) / Math.Max(1e-6, Math.Sqrt(tx * tx + ty * ty) * Math.Sqrt(ix * ix + iy * iy));
                    if (cos < Math.Cos(50 * Math.PI / 180)) return "l";
                }
                return "point";
            }
            if (indexIn && middleIn && ringIn && pinkyIn) return "fist";
            return "";
        }

        /// <summary>MediaPipe's palm-detection anchors for 192 x 192 (strides 8, 16, 16, 16; centres only, 2016 of them).</summary>
        internal static float[] MakeAnchors()
        {
            var a = new float[2016 * 2]; int n = 0;
            foreach (var layer in new[] { new[] { 24, 2 }, new[] { 12, 6 } })
            {
                int fm = layer[0], per = layer[1];
                for (int yy = 0; yy < fm; yy++)
                    for (int xx = 0; xx < fm; xx++)
                        for (int k = 0; k < per; k++) { a[n * 2] = (xx + 0.5f) / fm; a[n * 2 + 1] = (yy + 0.5f) / fm; n++; }
            }
            return a;
        }

        public void Dispose() { stop = true; wake.Set(); }
    }
}
