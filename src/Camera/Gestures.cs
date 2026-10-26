using System;
using System.IO;
using System.Threading;
using StreamLite.Audio;

namespace StreamLite.Camera
{
    /// <summary>
    /// Hand gestures, like the Insta360 Link's: an open palm turns tracking on or off, a V turns the whiteboard view on
    /// or off, an L (thumb and index finger) zooms while you move your hand up (in) or down (out). Only hands near a
    /// face count (raised beside your head), held for half a second.
    /// MediaPipe's palm detector and hand landmark model (OpenCV model zoo, Apache-2.0) on the CPU, on their own
    /// thread, a few times a second; the camera thread only cuts out the area around your head.
    /// </summary>
    internal sealed class HandGestures : IDisposable
    {
        public const string PalmFile = "hand_palm.onnx", LandmarkFile = "hand_landmarks.onnx";
        public static bool Installed { get { return OnnxSession.Available && File.Exists(Path.Combine(OnnxSession.Folder, PalmFile)) && File.Exists(Path.Combine(OnnxSession.Folder, LandmarkFile)); } }

        /// <summary>A gesture was made and held: "palm" or "v" (once per gesture).</summary>
        public event Action<string> Gesture;
        /// <summary>The L gesture is held and the hand moved: zoom by this factor.</summary>
        public event Action<double> Zoom;
        /// <summary>What it sees right now ("", "palm", "v", "l") and how long a look takes (status).</summary>
        public volatile string Seeing = "";
        public volatile float LookMs;
        public volatile string Error;

        const int Tile = 384;
        readonly byte[] tileA = new byte[Tile * Tile * 3], tileB = new byte[Tile * Tile * 3];
        byte[] ready;                      // the tile waiting for the worker (null = none)
        double readyY0, readyStepY, readyFaceH;   // tile row -> camera 0..1: y = y0 + row * stepY
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
        /// Camera thread: the area around this face, from the camera's NV12 frame (W x H), for the next look (skipped
        /// while the last one is still being looked at). Face in camera 0..1.
        /// </summary>
        public bool Offer(byte[] y, byte[] uv, int W, int H, FaceBox face)
        {
            byte[] dst;
            lock (gate) { if (ready != null) return false; dst = ready == tileA ? tileB : tileA; }
            // A square a little over five face-heights wide, centred a bit below the face: your raised hands.
            double side = Math.Max(0.2 * H, face.H * H * 5.2), cx = face.Cx * W, cy = face.Cy * H + face.H * H * 0.6;
            double x0 = cx - side / 2, y0 = cy - side / 2, step = side / Tile;
            for (int ty = 0; ty < Tile; ty++)
            {
                int sy = (int)(y0 + (ty + 0.5) * step);
                int o = ty * Tile * 3;
                if (sy < 0 || sy >= H) { Array.Clear(dst, o, Tile * 3); continue; }
                int rowY = sy * W, rowC = (sy >> 1) * W;
                for (int tx = 0; tx < Tile; tx++, o += 3)
                {
                    int sx = (int)(x0 + (tx + 0.5) * step);
                    if (sx < 0 || sx >= W) { dst[o] = dst[o + 1] = dst[o + 2] = 0; continue; }
                    int Y = (y[rowY + sx] - 16) * 298, ci = rowC + (sx & ~1), U = uv[ci] - 128, V = uv[ci + 1] - 128;
                    dst[o] = Clamp((Y + 409 * V + 128) >> 8); dst[o + 1] = Clamp((Y - 100 * U - 208 * V + 128) >> 8); dst[o + 2] = Clamp((Y + 516 * U + 128) >> 8);
                }
            }
            lock (gate) { ready = dst; readyY0 = y0 / H; readyStepY = step / H; readyFaceH = face.H; }
            wake.Set();
            return true;
        }

        static byte Clamp(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        // ------------------------------------------------------------------ the worker

        string held = ""; int heldFor; bool fired; double lZoomRef = double.NaN;

        void Work()
        {
            OnnxSession palm = null, marks = null;
            try
            {
                palm = new OnnxSession(Path.Combine(OnnxSession.Folder, PalmFile), false, 2);
                marks = new OnnxSession(Path.Combine(OnnxSession.Folder, LandmarkFile), false, 2);
                var pin = new float[192 * 192 * 3]; var raw = new float[2016 * 18]; var sc = new float[2016];
                var hin = new float[224 * 224 * 3]; var lm = new float[63]; var conf = new float[1]; var hand = new float[1]; var world = new float[63];
                long[][] pShape = { new long[] { 1, 192, 192, 3 } }, hShape = { new long[] { 1, 224, 224, 3 } };
                var sw = new System.Diagnostics.Stopwatch();
                while (!stop)
                {
                    wake.WaitOne(500);
                    if (stop) break;
                    byte[] t; double y0, stepY, faceH;
                    lock (gate) { t = ready; y0 = readyY0; stepY = readyStepY; faceH = readyFaceH; }
                    if (t == null) continue;
                    sw.Restart();
                    string g = "";
                    double handY = double.NaN;
                    // Palm detector on the tile (192 x 192).
                    Resample(t, Tile, pin, 192, 0, 0, Tile / 192.0, 0, Tile / 2.0, Tile / 2.0);
                    palm.Run(new[] { pin }, pShape, new[] { raw, sc });
                    int best = -1; double bestS = 0.55;
                    for (int i = 0; i < 2016; i++)
                    {
                        double s = 1 / (1 + Math.Exp(-sc[i]));
                        if (s > bestS) { bestS = s; best = i; }
                    }
                    if (best >= 0)
                    {
                        // Box and the 7 palm points, in tile pixels.
                        double ax = Anchors[best * 2], ay = Anchors[best * 2 + 1], k = Tile / 192.0;
                        double bx = (raw[best * 18] + ax * 192) * k, by = (raw[best * 18 + 1] + ay * 192) * k;
                        var kp = new double[14];
                        for (int j = 0; j < 7; j++) { kp[j * 2] = (raw[best * 18 + 4 + j * 2] + ax * 192) * k; kp[j * 2 + 1] = (raw[best * 18 + 5 + j * 2] + ay * 192) * k; }
                        // Turn the hand upright (wrist -> middle finger's base pointing up), MediaPipe's way.
                        double ang = Math.PI / 2 - Math.Atan2(-(kp[5] - kp[1]), kp[4] - kp[0]);
                        ang -= 2 * Math.PI * Math.Floor((ang + Math.PI) / (2 * Math.PI));
                        double ca = Math.Cos(ang), sa = Math.Sin(ang);
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                        for (int j = 0; j < 7; j++)
                        {
                            double dx = kp[j * 2] - bx, dy = kp[j * 2 + 1] - by;
                            double qx = dx * ca + dy * sa, qy = -dx * sa + dy * ca;   // turned (screen counter-clockwise)
                            minX = Math.Min(minX, qx); maxX = Math.Max(maxX, qx); minY = Math.Min(minY, qy); maxY = Math.Max(maxY, qy);
                        }
                        double bw = maxX - minX, bh = maxY - minY;
                        double ccx = (minX + maxX) / 2, ccy = (minY + maxY) / 2 - 0.4 * bh, side = 3 * Math.Max(bw, bh);
                        Resample(t, Tile, hin, 224, ccx, ccy, side / 224, ang, bx, by);
                        marks.Run(new[] { hin }, hShape, new[] { lm, conf, hand, world });
                        if (conf[0] > 0.8) { g = Classify(lm); handY = y0 + by * stepY; }
                    }
                    LookMs = (float)sw.Elapsed.TotalMilliseconds;
                    Seeing = g;
                    Decide(g, handY, faceH);
                    lock (gate) ready = null;
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { if (palm != null) palm.Dispose(); if (marks != null) marks.Dispose(); }
        }

        /// <summary>
        /// A gesture counts once seen 3 looks in a row (about half a second); palm and V fire once, until the hand goes.
        /// The L zooms continuously by how far the hand moved since the L appeared: a face-height up = 1.6x closer.
        /// </summary>
        void Decide(string g, double handY, double faceH)
        {
            if (g == held) heldFor++; else { held = g; heldFor = 1; fired = false; lZoomRef = double.NaN; }
            if (g == "" || heldFor < 3) return;
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
            if (fired) return;
            fired = true;
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
        /// The gesture of a hand from its 21 landmarks (x, y, z; image pixels, hand roughly upright): "palm" (all five
        /// fingers out), "v" (index and middle out, ring and little finger in), "l" (thumb and index out at an angle,
        /// the other three in), or "".
        /// </summary>
        internal static string Classify(float[] lm)
        {
            Func<int, int, double> d = (a, b) => Math.Sqrt((lm[a * 3] - lm[b * 3]) * (lm[a * 3] - lm[b * 3]) + (lm[a * 3 + 1] - lm[b * 3 + 1]) * (lm[a * 3 + 1] - lm[b * 3 + 1]));
            double palm = Math.Max(1e-3, d(0, 9));
            Func<int, int, int, bool> outF = (mcp, pip, tip) => d(0, tip) > d(0, pip) * 1.2 && d(tip, mcp) > palm * 0.55;
            Func<int, int, int, bool> inF = (mcp, pip, tip) => d(0, tip) < d(0, pip) * 1.1 || d(tip, mcp) < palm * 0.45;
            bool index = outF(5, 6, 8), middle = outF(9, 10, 12), ring = outF(13, 14, 16), pinky = outF(17, 18, 20);
            bool middleIn = inF(9, 10, 12), ringIn = inF(13, 14, 16), pinkyIn = inF(17, 18, 20);
            bool thumb = d(4, 5) > palm * 0.6 && d(0, 4) > d(0, 3) * 1.03;
            if (index && middle && ring && pinky && thumb) return "palm";
            if (index && middle && ringIn && pinkyIn && d(8, 12) > palm * 0.3) return "v";
            if (index && middleIn && ringIn && pinkyIn && thumb)
            {
                double tx = lm[12] - lm[6], ty = lm[13] - lm[7], ix = lm[24] - lm[15], iy = lm[25] - lm[16];   // thumb 2->4, index 5->8
                double cos = (tx * ix + ty * iy) / Math.Max(1e-6, Math.Sqrt(tx * tx + ty * ty) * Math.Sqrt(ix * ix + iy * iy));
                if (cos < Math.Cos(50 * Math.PI / 180)) return "l";
            }
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
