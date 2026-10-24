using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// Geometry of the 3D sound cube shown on the 3D Audio page (shared by the Windows and Linux front ends).
    /// The cube is [-1, 1] on every axis: X = right, Y = front, Z = up. It has three floors - below (Z = -1), ear level
    /// (Z = 0) and above (Z = +1) - each split 3 x 3 into front-left, front, front-right, left, centre, right,
    /// behind-left, behind and behind-right, so every sound lands in one of 27 distinct places. The view is an
    /// orthographic projection that the user turns with the right mouse button.
    /// </summary>
    public sealed class SoundCube
    {
        public const double DefaultYaw = -28, DefaultPitch = 24;
        double yaw = DefaultYaw, pitch = DefaultPitch;
        double cy, sy, cp, sp;

        public SoundCube() { Update(); }

        /// <summary>Turn around the vertical axis (degrees, + = the cube turns clockwise seen from above).</summary>
        public double Yaw { get { return yaw; } set { yaw = ((value % 360) + 540) % 360 - 180; Update(); } }
        /// <summary>Look-down angle (degrees): 0 = from the side, 90 = from straight above, negative = from below.</summary>
        public double Pitch { get { return pitch; } set { pitch = Math.Max(-85, Math.Min(85, value)); Update(); } }

        public void Reset() { yaw = DefaultYaw; pitch = DefaultPitch; Update(); }

        void Update()
        {
            double a = yaw * Math.PI / 180, b = pitch * Math.PI / 180;
            cy = Math.Cos(a); sy = Math.Sin(a); cp = Math.Cos(b); sp = Math.Sin(b);
        }

        /// <summary>
        /// Screen position of a cube point: sx to the right, sy downwards, both in cube units (multiply by the
        /// radius and add the centre). depth &gt; 0 means towards the viewer.
        /// </summary>
        public void Project(double x, double y, double z, out double sx, out double syOut, out double depth)
        {
            // Turn around Z, then tilt around the screen's X axis. The viewer stands behind the listener (-Y).
            double xr = x * cy - y * sy, yr = x * sy + y * cy;
            sx = xr;
            syOut = -(z * cp + yr * sp);
            depth = -yr * cp + z * sp;
        }

        /// <summary>
        /// A direction (azimuth anticlockwise from the front, elevation up, degrees) as a point on the cube: on the
        /// ear-level floor for level sounds, on the top / bottom floor for sounds above / below.
        /// </summary>
        public static void Direction(double azDeg, double elDeg, out double x, out double y, out double z)
        {
            double a = azDeg * Math.PI / 180, e = elDeg * Math.PI / 180;
            double dx = -Math.Sin(a) * Math.Cos(e), dy = Math.Cos(a) * Math.Cos(e), dz = Math.Sin(e);
            double m = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (m < 1e-9) { x = y = z = 0; return; }
            x = dx / m; y = dy / m; z = dz / m;
            // Snap the height onto a floor: within +-20 degrees is ear level, beyond it the floor above / below.
            if (Math.Abs(elDeg) <= 20) z = 0;
            else
            {
                double h = Math.Sqrt(dx * dx + dy * dy);
                double k = h < 1e-6 ? 0 : 1 / Math.Max(Math.Abs(dx), Math.Abs(dy)) * Math.Min(1, h / Math.Abs(dz));
                x = dx * k; y = dy * k; z = Math.Sign(dz);
            }
        }

        /// <summary>Which floor a height belongs to: -1 below, 0 ear level, +1 above.</summary>
        public static int Floor(double elDeg) { return elDeg > 20 ? 1 : elDeg < -20 ? -1 : 0; }

        /// <summary>Name of the cell a direction falls in, e.g. "above, front-left".</summary>
        public static string CellName(double azDeg, double elDeg)
        {
            double x, y, z; Direction(azDeg, elDeg, out x, out y, out z);
            string fb = y > 1.0 / 3 ? "front" : y < -1.0 / 3 ? "behind" : "";
            string lr = x > 1.0 / 3 ? "right" : x < -1.0 / 3 ? "left" : "";
            string cell = fb.Length > 0 && lr.Length > 0 ? fb + "-" + lr : fb.Length > 0 ? fb : lr.Length > 0 ? lr : "centre";
            int f = Floor(elDeg);
            return (f > 0 ? "above" : f < 0 ? "below" : "ear level") + ", " + cell;
        }
    }
}
