using System;
using System.Collections.Generic;

namespace StreamLite.Camera
{
    /// <summary>
    /// Finds a whiteboard in the camera's picture: the largest bright, colourless, solid four-sided area (a board with
    /// writing on it still counts - the writing is closed over). Its four corners, as the camera sees them, steadied
    /// over time. Works on a 320 x 180 copy of the frame, a couple of times a second.
    /// </summary>
    internal sealed class WhiteboardFinder
    {
        public const int GW = 320, GH = 180;
        readonly byte[] lum = new byte[GW * GH], sat = new byte[GW * GH];
        readonly bool[] mask = new bool[GW * GH], tmp = new bool[GW * GH], seen = new bool[GW * GH];
        readonly int[] queue = new int[GW * GH];
        double[] smooth;
        int misses;

        /// <summary>The board's corners TL TR BR BL (camera 0..1, x/y pairs), or null when there's none.</summary>
        public double[] Corners { get { return smooth; } }

        /// <summary>One look at an NV12 frame (W x H).</summary>
        public double[] Look(byte[] y, byte[] uv, int W, int H)
        {
            for (int gy = 0; gy < GH; gy++)
            {
                int sy = (int)((gy + 0.5) * H / GH);
                for (int gx = 0; gx < GW; gx++)
                {
                    int sx = (int)((gx + 0.5) * W / GW);
                    int c = (sy >> 1) * W + (sx & ~1);
                    lum[gy * GW + gx] = y[sy * W + sx];
                    sat[gy * GW + gx] = (byte)Math.Min(255, Math.Abs(uv[c] - 128) + Math.Abs(uv[c + 1] - 128));
                }
            }
            var found = Find(lum, sat);
            if (found == null)
            {
                if (++misses > 4) smooth = null;   // gone for a couple of seconds
                return smooth;
            }
            misses = 0;
            if (smooth == null) smooth = found;
            else
            {
                // Steady: small changes (noise, someone passing) are followed slowly, a moved camera quickly.
                double move = 0;
                for (int i = 0; i < 8; i++) move = Math.Max(move, Math.Abs(found[i] - smooth[i]));
                double a = move > 0.08 ? 0.6 : 0.15;
                var s = (double[])smooth.Clone();
                for (int i = 0; i < 8; i++) s[i] += (found[i] - s[i]) * a;
                smooth = s;
            }
            return smooth;
        }

        /// <summary>The board in a 320 x 180 picture (luma and colourfulness), or null.</summary>
        internal double[] Find(byte[] l, byte[] s)
        {
            int n = GW * GH;
            // Bright: well above the picture's typical level; colourless: a board is white or light grey.
            var hist = new int[256];
            for (int i = 0; i < n; i++) hist[l[i]]++;
            int acc = 0, p60 = 255;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= n * 0.6) { p60 = v; break; } }
            int thr = Math.Max(120, Math.Min(200, p60 + 12));
            for (int i = 0; i < n; i++) mask[i] = l[i] >= thr && s[i] < 26;
            // Close over the writing: grow by 2, shrink by 2.
            Morph(mask, tmp, true); Morph(tmp, mask, true); Morph(mask, tmp, false); Morph(tmp, mask, false);
            // The biggest connected area.
            Array.Clear(seen, 0, n);
            int bestStart = -1, bestArea = 0;
            for (int i = 0; i < n; i++)
            {
                if (!mask[i] || seen[i]) continue;
                int area = Fill(i, false);
                if (area > bestArea) { bestArea = area; bestStart = i; }
            }
            if (bestArea < n * 0.04 || bestArea > n * 0.85) return null;
            Array.Clear(seen, 0, n);
            Fill(bestStart, true);
            // Its corners: the points furthest towards each corner of the picture.
            double tl = double.MaxValue, br = double.MinValue, tr = double.MinValue, bl = double.MaxValue;
            int[] c = new int[8];
            for (int i = 0; i < n; i++)
            {
                if (!seen[i]) continue;
                int x = i % GW, yy = i / GW;
                if (x + yy < tl) { tl = x + yy; c[0] = x; c[1] = yy; }
                if (x - yy > tr) { tr = x - yy; c[2] = x; c[3] = yy; }
                if (x + yy > br) { br = x + yy; c[4] = x; c[5] = yy; }
                if (x - yy < bl) { bl = x - yy; c[6] = x; c[7] = yy; }
            }
            // A real board fills the quadrilateral its corners make (a wall with a doorway, a window, a lamp don't).
            double quad = Math.Abs(Shoelace(c));
            if (quad < 1 || bestArea / quad < 0.82) return null;
            // A board has borders: it's whole in the picture (a bright wall runs off its edge) and clearly brighter
            // than what's just outside it on at least three sides.
            int bx0 = GW, by0 = GH, bx1 = 0, by1 = 0;
            for (int i = 0; i < 4; i++) { bx0 = Math.Min(bx0, c[i * 2]); bx1 = Math.Max(bx1, c[i * 2]); by0 = Math.Min(by0, c[i * 2 + 1]); by1 = Math.Max(by1, c[i * 2 + 1]); }
            if (bx0 <= 2 || by0 <= 2 || bx1 >= GW - 3 || by1 >= GH - 3) return null;
            int framed = 0;
            for (int e = 0; e < 4; e++)
            {
                int a = e, b = (e + 1) % 4;
                double ex = c[b * 2] - c[a * 2], ey = c[b * 2 + 1] - c[a * 2 + 1], len = Math.Sqrt(ex * ex + ey * ey);
                if (len < 4) continue;
                double nx = ey / len, ny = -ex / len;   // outward for TL -> TR -> BR -> BL (clockwise on screen)
                double inside = 0, outside = 0; int n2 = 0;
                for (int k = 1; k < 10; k++)
                {
                    double px = c[a * 2] + ex * k / 10.0, py = c[a * 2 + 1] + ey * k / 10.0;
                    int ix = (int)(px - nx * 5), iy = (int)(py - ny * 5), ox = (int)(px + nx * 5), oy = (int)(py + ny * 5);
                    if (ox < 0 || oy < 0 || ox >= GW || oy >= GH || ix < 0 || iy < 0 || ix >= GW || iy >= GH) continue;
                    inside += l[iy * GW + ix]; outside += l[oy * GW + ox]; n2++;
                }
                if (n2 >= 5 && (inside - outside) / n2 > 30) framed++;
            }
            if (framed < 3) return null;
            double w = (Dist(c, 0, 1) + Dist(c, 3, 2)) / 2, h = (Dist(c, 0, 3) + Dist(c, 1, 2)) / 2;
            double aspect = w / Math.Max(1, h) * GW / GH * 9 / 16;   // in camera proportions (the grid is 16:9 already)
            if (aspect < 0.6 || aspect > 4) return null;
            var r = new double[8];
            for (int i = 0; i < 4; i++) { r[i * 2] = (c[i * 2] + 0.5) / GW; r[i * 2 + 1] = (c[i * 2 + 1] + 0.5) / GH; }
            return r;
        }

        static double Dist(int[] c, int a, int b) { double dx = c[a * 2] - c[b * 2], dy = c[a * 2 + 1] - c[b * 2 + 1]; return Math.Sqrt(dx * dx + dy * dy); }

        static double Shoelace(int[] c)
        {
            double s = 0;
            for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; s += c[i * 2] * (double)c[j * 2 + 1] - c[j * 2] * (double)c[i * 2 + 1]; }
            return s / 2;
        }

        static void Morph(bool[] src, bool[] dst, bool grow)
        {
            for (int yy = 0; yy < GH; yy++)
                for (int x = 0; x < GW; x++)
                {
                    bool v = !grow;
                    for (int dy = -1; dy <= 1 && v == !grow; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int X = x + dx, Y = yy + dy;
                            bool b = X >= 0 && Y >= 0 && X < GW && Y < GH ? src[Y * GW + X] : !grow;
                            if (b == grow) { v = grow; break; }
                        }
                    dst[yy * GW + x] = v;
                }
        }

        int Fill(int start, bool keep)
        {
            int head = 0, tail = 0, area = 0;
            queue[tail++] = start; seen[start] = true;
            while (head < tail)
            {
                int i = queue[head++]; area++;
                int x = i % GW, yy = i / GW;
                if (x > 0) Push(i - 1, ref tail);
                if (x < GW - 1) Push(i + 1, ref tail);
                if (yy > 0) Push(i - GW, ref tail);
                if (yy < GH - 1) Push(i + GW, ref tail);
            }
            return area;
        }

        void Push(int i, ref int tail) { if (mask[i] && !seen[i]) { seen[i] = true; queue[tail++] = i; } }
    }
}
