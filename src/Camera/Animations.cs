using System;

namespace StreamLite.Camera
{
    /// <summary>Where the picture is at one moment of an animation: turned (degrees, clockwise), scaled around the
    /// middle and moved (share of the width / height). Outside the picture is black.</summary>
    internal struct CamMotion
    {
        public double Angle, Scale, Dx, Dy;
        public static CamMotion Identity { get { return new CamMotion { Scale = 1 }; } }
        public bool IsIdentity { get { return Angle == 0 && Scale == 1 && Dx == 0 && Dy == 0; } }
    }

    /// <summary>
    /// Short fun camera moves for hotkeys and the Camera page, played on the picture apps see (eased, every frame):
    /// flip (turns upside down - the camera stays that way), spin (a full turn), barrel roll (a full turn swinging round
    /// a small circle), zoom punch (a quick zoom in that springs back) and shake. All but the flip end exactly where
    /// they started.
    /// </summary>
    internal static class CameraAnimation
    {
        public static readonly string[] Kinds = { "flip", "spin", "roll", "punch", "shake" };

        public static string Label(string kind)
        {
            switch (kind)
            {
                case "flip": return "Flip upside down";
                case "spin": return "Spin around";
                case "roll": return "Barrel roll";
                case "punch": return "Zoom punch";
                case "shake": return "Shake";
                default: return kind;
            }
        }

        /// <summary>How long it plays (seconds).</summary>
        public static double Duration(string kind)
        {
            switch (kind)
            {
                case "flip": return 0.9;
                case "spin": return 1.0;
                case "roll": return 1.1;
                case "punch": return 0.7;
                default: return 0.7;   // shake
            }
        }

        /// <summary>The picture's place at p (0 = start, 1 = end) of an animation.</summary>
        public static CamMotion At(string kind, double p)
        {
            var m = CamMotion.Identity;
            if (p <= 0) return m;
            if (p >= 1) { if (kind == "flip") m.Angle = 180; return m; }
            double e = Ease(p), dip = Math.Sin(Math.PI * p);
            switch (kind)
            {
                case "flip": m.Angle = 180 * e; m.Scale = 1 - 0.22 * dip; break;   // a little smaller mid-turn: the corners stay in sight
                case "spin": m.Angle = 360 * e; m.Scale = 1 - 0.3 * dip; break;
                case "roll":
                    m.Angle = 360 * e; m.Scale = 1 - 0.18 * dip;
                    m.Dx = 0.05 * Math.Sin(2 * Math.PI * e); m.Dy = -0.035 * (1 - Math.Cos(2 * Math.PI * e));
                    break;
                case "punch":
                    // In fast (under a quarter of a second), then back with a damped wobble.
                    double k = p < 0.3 ? 1 - (1 - p / 0.3) * (1 - p / 0.3) : Math.Cos((p - 0.3) / 0.7 * 1.5 * Math.PI) * Math.Pow(1 - (p - 0.3) / 0.7, 2);
                    m.Scale = 1 + 0.35 * k;
                    break;
                case "shake":
                    // A few quick jolts that die away (slow enough to stay smooth at 30 frames a second).
                    double a = (1 - p) * (1 - p), t = p * Duration(kind);
                    m.Dx = 0.02 * a * Math.Sin(2 * Math.PI * 6 * t); m.Dy = 0.014 * a * Math.Sin(2 * Math.PI * 8 * t); m.Angle = 1.5 * a * Math.Sin(2 * Math.PI * 5 * t);
                    break;
            }
            return m;
        }

        /// <summary>Ease in and out (cubic): starts and ends gently.</summary>
        static double Ease(double p) { return p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2; }

        /// <summary>
        /// Moves an NV12 picture (W x H, in place) as m says: each output pixel shows the point the motion brought
        /// there (bilinear), black where the picture isn't. tmpY / tmpUv: scratch of the same sizes.
        /// </summary>
        public static void Warp(byte[] y, byte[] uv, int W, int H, CamMotion m, byte[] tmpY, byte[] tmpUv)
        {
            if (m.IsIdentity) return;
            Buffer.BlockCopy(y, 0, tmpY, 0, W * H); Buffer.BlockCopy(uv, 0, tmpUv, 0, W * H / 2);
            double ang = m.Angle % 360, ca, sa;
            // Quarter turns exactly (a finished flip is the picture turned round, pixel for pixel).
            if (ang % 90 == 0) { int q = ((int)(ang / 90) % 4 + 4) % 4; ca = q == 0 ? 1 : q == 2 ? -1 : 0; sa = q == 1 ? 1 : q == 3 ? -1 : 0; }
            else { ca = Math.Cos(ang * Math.PI / 180); sa = Math.Sin(ang * Math.PI / 180); }
            double inv = 1 / Math.Max(1e-3, m.Scale), cx = W / 2.0, cy = H / 2.0, ox = cx + m.Dx * W, oy = cy + m.Dy * H;
            // Output point s -> source point: c + R(-angle) (s - o) / scale.
            System.Threading.Tasks.Parallel.For(0, H, row =>
            {
                double qy = row + 0.5 - oy;
                int o = row * W;
                for (int x = 0; x < W; x++)
                {
                    double qx = x + 0.5 - ox;
                    double sx = cx + (qx * ca + qy * sa) * inv - 0.5, sy = cy + (-qx * sa + qy * ca) * inv - 0.5;
                    y[o + x] = Sample(tmpY, W, H, 1, 0, sx, sy, 16);
                }
            });
            int w2 = W / 2, h2 = H / 2;
            System.Threading.Tasks.Parallel.For(0, h2, row =>
            {
                double qy = row * 2 + 1 - oy;
                int o = row * W;
                for (int x = 0; x < w2; x++)
                {
                    double qx = x * 2 + 1 - ox;
                    double sx = (cx + (qx * ca + qy * sa) * inv) / 2 - 0.5, sy = (cy + (-qx * sa + qy * ca) * inv) / 2 - 0.5;
                    uv[o + x * 2] = Sample(tmpUv, w2, h2, 2, 0, sx, sy, 128);
                    uv[o + x * 2 + 1] = Sample(tmpUv, w2, h2, 2, 1, sx, sy, 128);
                }
            });
        }

        /// <summary>Bilinear sample of a plane (w x h samples of comps interleaved components) at (x, y) pixel centres;
        /// off the picture = outside.</summary>
        static byte Sample(byte[] p, int w, int h, int comps, int c, double x, double y, byte outside)
        {
            if (x < -0.5 || y < -0.5 || x > w - 0.5 || y > h - 0.5) return outside;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int xa = Math.Max(0, x0), xb = Math.Min(w - 1, x0 + 1), ya = Math.Max(0, y0), yb = Math.Min(h - 1, y0 + 1);
            int stride = w * comps;
            double top = p[ya * stride + xa * comps + c] * (1 - fx) + p[ya * stride + xb * comps + c] * fx;
            double bot = p[yb * stride + xa * comps + c] * (1 - fx) + p[yb * stride + xb * comps + c] * fx;
            return (byte)(top * (1 - fy) + bot * fy + 0.5);
        }
    }
}
