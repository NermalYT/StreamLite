using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StreamLite.Audio
{
    /// <summary>
    /// A set of head-related impulse responses (what a sound from a given direction looks like at each eardrum).
    /// Built-in sets are the SADIE II dummy-head measurements (University of York, Apache-2.0): Neumann KU100 and
    /// KEMAR, 48 kHz, diffuse-field normalized. Custom sets can be loaded from HeSuVi / Impulcifer 14- or 16-channel WAVs.
    /// Azimuth is in degrees anticlockwise (90 = left), elevation in degrees (positive = up).
    /// </summary>
    internal sealed class HrtfSet
    {
        public string Name;
        public int Taps;
        public float[] Az, El;
        public float[][] L, R;
        // Room reflections (binaural room impulse response tails), horizontal plane only. Optional.
        public int RoomTaps;
        public float[] RoomAz;
        public float[][] RoomL, RoomR;
        public int RoomOffset = 78;          // where the tail starts relative to the direct sound, in samples
        /// <summary>True for a speaker-specific set (HeSuVi): one response per virtual speaker, room already included.</summary>
        public bool SpeakerSet;

        public const string Ku100 = "ku100", Kemar = "kemar", Custom = "custom";
        /// <summary>The 18 measured human heads of SADIE II (H3-H20): "sadie-h3" ... "sadie-h20".</summary>
        public const string HumanPrefix = "sadie-h";
        static readonly Dictionary<string, HrtfSet> cache = new Dictionary<string, HrtfSet>();
        static HrtfSet builtInRoom;

        /// <summary>Where the full-resolution head files are (hrtf next to StreamLite, or the source tree's for tests).</summary>
        public static string Folder
        {
            get
            {
                if (folder != null) return folder;
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int up = 0; up < 5 && dir != null; up++, dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                    if (File.Exists(Path.Combine(Path.Combine(dir, "hrtf"), "ku100.blh"))) return folder = Path.Combine(dir, "hrtf");
                return folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hrtf");
            }
        }
        static string folder;

        static string FileOf(string kind) { return Path.Combine(Folder, kind + ".blh"); }

        /// <summary>Every head that can be used: the two dummy heads and the human heads that are installed.</summary>
        public static List<string> Heads()
        {
            var l = new List<string> { Ku100, Kemar };
            for (int h = 3; h <= 20; h++) if (File.Exists(FileOf(HumanPrefix + h))) l.Add(HumanPrefix + h);
            return l;
        }

        public static bool Available(string kind) { return kind == Ku100 || kind == Kemar || (kind != null && kind.StartsWith(HumanPrefix) && File.Exists(FileOf(kind))); }

        public static string DisplayName(string kind)
        {
            if (kind == Kemar) return "KEMAR";
            if (kind == Custom) return "your own file";
            if (kind != null && kind.StartsWith(HumanPrefix)) return "Human head " + kind.Substring(HumanPrefix.Length).ToUpperInvariant().TrimStart('H');
            return "Neumann KU100";
        }

        /// <summary>A head's responses, with the time between the ears scaled for a head size (1 = as measured).</summary>
        public static HrtfSet Get(string kind, string customPath, double headSize)
        {
            if (Math.Abs(headSize - 1) < 0.005 || kind == Custom) return Get(kind, customPath);
            string key = kind + "@" + headSize.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            lock (cache)
            {
                HrtfSet s;
                if (cache.TryGetValue(key, out s)) return s;
                s = Get(kind, customPath).WithHeadSize(headSize);
                cache[key] = s;
                return s;
            }
        }

        public static HrtfSet Get(string kind, string customPath)
        {
            if (kind != Custom && !Available(kind)) kind = Ku100;
            string key = kind == Custom ? "custom:" + customPath : kind;
            lock (cache)
            {
                HrtfSet s;
                if (cache.TryGetValue(key, out s)) return s;
                if (kind == Custom) s = LoadHeSuVi(customPath);
                else
                {
                    // The full measurement when it's installed (every direction, 256 taps, 24-bit), else the built-in
                    // 242 directions.
                    string file = FileOf(kind);
                    if (File.Exists(file))
                    {
                        try { s = LoadFile(file, DisplayName(kind) + " (SADIE II)"); } catch { s = null; }
                    }
                    if (s == null) s = LoadResource(kind == Kemar ? "hrtf_kemar.bin" : "hrtf_ku100.bin", DisplayName(kind) + " (SADIE II)");
                    // Room reflections are the KU100 room's (only that set has them).
                    if (builtInRoom == null) builtInRoom = LoadResource("hrtf_ku100.bin", "room");
                    s.RoomTaps = builtInRoom.RoomTaps; s.RoomAz = builtInRoom.RoomAz; s.RoomL = builtInRoom.RoomL; s.RoomR = builtInRoom.RoomR;
                }
                // Keep only the current custom set in memory.
                if (kind == Custom) foreach (var k in new List<string>(cache.Keys)) if (k.StartsWith("custom:")) cache.Remove(k);
                cache[key] = s;
                return s;
            }
        }

        /// <summary>Lets go of every head but these (Personalize 3D loads all of them while it compares).</summary>
        public static void Forget(params string[] keepKinds)
        {
            lock (cache)
                foreach (var k in new List<string>(cache.Keys))
                {
                    string kind = k.Split('@')[0];
                    if (!k.StartsWith("custom:") && Array.IndexOf(keepKinds, kind) < 0) cache.Remove(k);
                }
        }

        /// <summary>A full head file (tools/make_hrtf.py): gzip, "BLH2", 24-bit responses.</summary>
        static HrtfSet LoadFile(string path, string name)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                var b = ms.GetBuffer(); int len = (int)ms.Length;
                if (len < 16 || b[0] != 'B' || b[1] != 'L' || b[2] != 'H' || b[3] != '2') throw new InvalidDataException("Bad HRTF file");
                int count = BitConverter.ToInt32(b, 4), taps = BitConverter.ToInt32(b, 8); float scale = BitConverter.ToSingle(b, 12);
                if ((long)16 + (long)count * (8 + 6 * taps) > len) throw new InvalidDataException("Short HRTF file");
                var set = new HrtfSet { Name = name, Taps = taps, Az = new float[count], El = new float[count], L = new float[count][], R = new float[count][] };
                int o = 16;
                for (int i = 0; i < count; i++)
                {
                    set.Az[i] = BitConverter.ToSingle(b, o); set.El[i] = BitConverter.ToSingle(b, o + 4); o += 8;
                    for (int ear = 0; ear < 2; ear++)
                    {
                        var h = new float[taps];
                        for (int k = 0; k < taps; k++, o += 3) h[k] = ((b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) << 8 >> 8) * scale;
                        if (ear == 0) set.L[i] = h; else set.R[i] = h;
                    }
                }
                return WithoutRepeats(set);
            }
        }


        /// <summary>
        /// SADIE II measured each head on two grids: a regular one (whole degrees) and a Lebedev grid. Where a Lebedev
        /// point lies within 1° of a regular one it's the same direction measured again, a little differently - and
        /// blending the two smears the response, so the repeat is left out. Every direction stays covered.
        /// </summary>
        static HrtfSet WithoutRepeats(HrtfSet s)
        {
            int n = s.Az.Length;
            Func<float, bool> whole = v => Math.Abs(v - Math.Round(v)) < 1e-3;
            var grid = new List<int>(); var other = new List<int>();
            for (int i = 0; i < n; i++) (whole(s.Az[i]) && whole(s.El[i]) ? grid : other).Add(i);
            if (grid.Count == 0 || other.Count == 0) return s;
            var gx = new double[grid.Count]; var gy = new double[grid.Count]; var gz = new double[grid.Count];
            for (int j = 0; j < grid.Count; j++) { double a = s.Az[grid[j]] * Math.PI / 180, e = s.El[grid[j]] * Math.PI / 180; gx[j] = Math.Cos(e) * Math.Cos(a); gy[j] = Math.Cos(e) * Math.Sin(a); gz[j] = Math.Sin(e); }
            double lim = Math.Cos(1.0 * Math.PI / 180);
            var keep = new List<int>(grid);
            foreach (int i in other)
            {
                double a = s.Az[i] * Math.PI / 180, e = s.El[i] * Math.PI / 180, x = Math.Cos(e) * Math.Cos(a), y = Math.Cos(e) * Math.Sin(a), z = Math.Sin(e);
                bool repeat = false;
                for (int j = 0; j < gx.Length && !repeat; j++) if (x * gx[j] + y * gy[j] + z * gz[j] > lim) repeat = true;
                if (!repeat) keep.Add(i);
            }
            if (keep.Count == n) return s;
            keep.Sort();
            return new HrtfSet { Name = s.Name, Taps = s.Taps, Az = keep.Select(i => s.Az[i]).ToArray(), El = keep.Select(i => s.El[i]).ToArray(), L = keep.Select(i => s.L[i]).ToArray(), R = keep.Select(i => s.R[i]).ToArray() };
        }

        /// <summary>
        /// The same head with the time between the ears scaled (a bigger or smaller head): each direction's far ear
        /// moves later and the near ear earlier by half the change, exactly (in the frequency domain). The spectra - the
        /// ears' shapes - stay as measured.
        /// </summary>
        HrtfSet WithHeadSize(double scale)
        {
            EnsureOnsets();
            var s = new HrtfSet { Name = Name, Taps = Taps, Az = Az, El = El, L = new float[Az.Length][], R = new float[Az.Length][], RoomTaps = RoomTaps, RoomAz = RoomAz, RoomL = RoomL, RoomR = RoomR, RoomOffset = RoomOffset };
            int P = 1; while (P < Taps + 64) P <<= 1;
            var fft = new Fft(P); var re = new float[P]; var im = new float[P];
            for (int i = 0; i < Az.Length; i++)
            {
                double change = (scale - 1) * (onR[i] - onL[i]);   // + = the right ear later still
                s.L[i] = Shift(fft, re, im, P, L[i], -change / 2);
                s.R[i] = Shift(fft, re, im, P, R[i], change / 2);
            }
            s.onL = new float[Az.Length]; s.onR = new float[Az.Length];
            for (int i = 0; i < Az.Length; i++)
            {
                double change = (scale - 1) * (onR[i] - onL[i]);
                s.onL[i] = (float)(onL[i] - change / 2); s.onR[i] = (float)(onR[i] + change / 2);
            }
            return s;
        }

        float[] Shift(Fft fft, float[] re, float[] im, int P, float[] h, double by)
        {
            Array.Clear(re, 0, P); Array.Clear(im, 0, P); Array.Copy(h, re, Taps);
            fft.Transform(re, im, false);
            for (int k = 0; k < P; k++)
            {
                double ph = -2 * Math.PI * (k <= P / 2 ? k : k - P) / P * by;
                double c = Math.Cos(ph), sn = Math.Sin(ph);
                float a = re[k], b2 = im[k];
                re[k] = (float)(a * c - b2 * sn); im[k] = (float)(a * sn + b2 * c);
            }
            im[P / 2] = 0;
            fft.Transform(re, im, true);
            var o = new float[Taps];
            for (int k = 0; k < Taps; k++) o[k] = re[k] / P;
            return o;
        }

        static HrtfSet LoadResource(string res, string name)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
            {
                if (st == null) throw new InvalidOperationException("Missing HRTF data " + res);
                var r = new BinaryReader(st);
                var set = new HrtfSet { Name = name };
                if (new string(r.ReadChars(4)) != "BLH1") throw new InvalidDataException("Bad HRTF data");
                int count = r.ReadInt32(); set.Taps = r.ReadInt32(); float scale = r.ReadSingle();
                set.Az = new float[count]; set.El = new float[count]; set.L = new float[count][]; set.R = new float[count][];
                for (int i = 0; i < count; i++)
                {
                    set.Az[i] = r.ReadSingle(); set.El[i] = r.ReadSingle();
                    set.L[i] = ReadI16(r, set.Taps, scale); set.R[i] = ReadI16(r, set.Taps, scale);
                }
                if (st.Position < st.Length && new string(r.ReadChars(4)) == "ROOM")
                {
                    int rc = r.ReadInt32(); set.RoomTaps = r.ReadInt32(); float rs = r.ReadSingle();
                    set.RoomAz = new float[rc]; set.RoomL = new float[rc][]; set.RoomR = new float[rc][];
                    for (int i = 0; i < rc; i++)
                    {
                        set.RoomAz[i] = r.ReadSingle(); r.ReadSingle();
                        set.RoomL[i] = ReadI16(r, set.RoomTaps, rs); set.RoomR[i] = ReadI16(r, set.RoomTaps, rs);
                    }
                }
                return set;
            }
        }

        static float[] ReadI16(BinaryReader r, int n, float scale)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadInt16() * scale;
            return a;
        }

        static double AngleDist(double a, double b) { double d = Math.Abs(((a - b) % 360 + 540) % 360 - 180); return d; }

        /// <summary>Closest measured direction (great-circle distance).</summary>
        public int Nearest(double az, double el)
        {
            EnsureUnits();
            double a1 = az * Math.PI / 180, e1 = el * Math.PI / 180;
            double x = Math.Cos(e1) * Math.Cos(a1), y = Math.Cos(e1) * Math.Sin(a1), z = Math.Sin(e1);
            int best = 0; double bc = double.MinValue;
            for (int i = 0; i < ux.Length; i++) { double c = x * ux[i] + y * uy[i] + z * uz[i]; if (c > bc) { bc = c; best = i; } }
            return best;
        }

        // Every measured direction as a unit vector (the nearest ones are a dot product away, even with 8802 of them).
        float[] ux, uy, uz;
        void EnsureUnits()
        {
            if (uz != null) return;
            int n = Az.Length; var x = new float[n]; var y = new float[n]; var z = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = Az[i] * Math.PI / 180, e = El[i] * Math.PI / 180;
                x[i] = (float)(Math.Cos(e) * Math.Cos(a)); y[i] = (float)(Math.Cos(e) * Math.Sin(a)); z[i] = (float)Math.Sin(e);
            }
            ux = x; uy = y; uz = z;
        }

        public int NearestRoom(double az)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < RoomAz.Length; i++) { double d = AngleDist(az, RoomAz[i]); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        /// <summary>
        /// Full impulse response pair for a virtual speaker: direct HRIR plus (optionally) scaled room reflections.
        /// </summary>
        public void Build(double az, double el, double room, out float[] left, out float[] right)
        {
            int i = Nearest(az, el);
            int len = Taps;
            bool useRoom = !SpeakerSet && room > 0.001 && RoomL != null;
            if (useRoom) len = Math.Max(len, RoomOffset + RoomTaps);
            left = new float[len]; right = new float[len];
            Array.Copy(L[i], left, Taps); Array.Copy(R[i], right, Taps);
            if (!useRoom) return;
            int ri = NearestRoom(az);
            float g = (float)room;
            for (int k = 0; k < RoomTaps; k++) { left[RoomOffset + k] += g * RoomL[ri][k]; right[RoomOffset + k] += g * RoomR[ri][k]; }
        }

        /// <summary>
        /// The head response for any direction, between the measured ones (Build takes the nearest: up to ~8 degrees
        /// off on this grid). The four nearest measurements are averaged by closeness after lining up their onsets (so
        /// two different arrival times don't smear into a comb), then placed at the averaged onset per ear - the time
        /// difference between the ears moves smoothly with the direction. The shifts are exact (in the frequency domain:
        /// no treble lost). Plus the room, as Build.
        /// </summary>
        public void BuildPrecise(double az, double el, double room, out float[] left, out float[] right)
        {
            if (SpeakerSet || Az.Length < 4) { Build(az, el, room, out left, out right); return; }
            EnsureOnsets();
            const int M = 4;
            var idx = new int[M]; var dot = new double[M];
            for (int m = 0; m < M; m++) dot[m] = double.MinValue;
            EnsureUnits();
            double a1 = az * Math.PI / 180, e1 = el * Math.PI / 180;
            double vx = Math.Cos(e1) * Math.Cos(a1), vy = Math.Cos(e1) * Math.Sin(a1), vz = Math.Sin(e1);
            for (int i = 0; i < ux.Length; i++)
            {
                double c = vx * ux[i] + vy * uy[i] + vz * uz[i];
                if (c <= dot[M - 1]) continue;
                for (int m = 0; m < M; m++)
                    if (c > dot[m]) { for (int q = M - 1; q > m; q--) { dot[q] = dot[q - 1]; idx[q] = idx[q - 1]; } dot[m] = c; idx[m] = i; break; }
            }
            var dist = new double[M];
            for (int m = 0; m < M; m++) dist[m] = Math.Acos(Math.Max(-1, Math.Min(1, dot[m]))) * 180 / Math.PI;
            var w = new double[M]; double ws = 0;
            for (int m = 0; m < M; m++) { w[m] = 1 / Math.Pow(dist[m] + 0.05, 2); ws += w[m]; }
            for (int m = 0; m < M; m++) w[m] /= ws;
            int len = Taps;
            bool useRoom = room > 0.001 && RoomL != null;
            if (useRoom) len = Math.Max(len, RoomOffset + RoomTaps);
            left = new float[len]; right = new float[len];
            int P = 1; while (P < Taps + 128) P <<= 1;
            var fft = new Fft(P);
            var re = new float[P]; var im = new float[P]; var ar = new double[P]; var ai = new double[P];
            for (int ear = 0; ear < 2; ear++)
            {
                Array.Clear(ar, 0, P); Array.Clear(ai, 0, P);
                double onset = 0;
                for (int m = 0; m < M; m++)
                {
                    var h = ear == 0 ? L[idx[m]] : R[idx[m]];
                    double on = ear == 0 ? onL[idx[m]] : onR[idx[m]];
                    onset += w[m] * on;
                    Array.Clear(re, 0, P); Array.Clear(im, 0, P); Array.Copy(h, re, Taps);
                    fft.Transform(re, im, false);
                    // Moved earlier by its onset: all of them start together.
                    for (int k = 0; k < P; k++)
                    {
                        double ph = 2 * Math.PI * (k <= P / 2 ? k : k - P) / P * on;
                        double c = Math.Cos(ph), s = Math.Sin(ph);
                        ar[k] += w[m] * (re[k] * c - im[k] * s); ai[k] += w[m] * (re[k] * s + im[k] * c);
                    }
                }
                // ... and placed at the averaged onset.
                for (int k = 0; k < P; k++)
                {
                    double ph = -2 * Math.PI * (k <= P / 2 ? k : k - P) / P * onset;
                    double c = Math.Cos(ph), s = Math.Sin(ph);
                    re[k] = (float)(ar[k] * c - ai[k] * s); im[k] = (float)(ar[k] * s + ai[k] * c);
                }
                if (P % 2 == 0) im[P / 2] = 0;
                fft.Transform(re, im, true);
                var o = ear == 0 ? left : right;
                for (int k = 0; k < Taps; k++) o[k] = re[k] / P;
            }
            if (!useRoom) return;
            int ri = NearestRoom(az);
            float g = (float)room;
            for (int k = 0; k < RoomTaps; k++) { left[RoomOffset + k] += g * RoomL[ri][k]; right[RoomOffset + k] += g * RoomR[ri][k]; }
        }

        float[] onL, onR;

        /// <summary>Where a measured response starts at the left / right ear (fractional samples).</summary>
        public float OnsetL(int i) { EnsureOnsets(); return onL[i]; }
        public float OnsetR(int i) { EnsureOnsets(); return onR[i]; }

        /// <summary>
        /// Each measured response's onset per ear (fractional samples): the near ear's from where it first reaches 20% of
        /// its peak (a sharp edge), the far ear's from the near one plus the time between the ears (their correlation) -
        /// the far ear's own edge is too dull to find reliably.
        /// </summary>
        void EnsureOnsets()
        {
            if (onL != null) return;
            var a = new float[Az.Length]; var b = new float[Az.Length];
            for (int i = 0; i < Az.Length; i++)
            {
                double el2 = 0, er2 = 0; for (int k = 0; k < Taps; k++) { el2 += L[i][k] * L[i][k]; er2 += R[i][k] * R[i][k]; }
                double itd = Itd(L[i], R[i]);          // + = the right ear later
                if (el2 >= er2) { a[i] = Onset(L[i]); b[i] = (float)(a[i] + itd); }
                else { b[i] = Onset(R[i]); a[i] = (float)(b[i] - itd); }
            }
            onR = b; onL = a;
        }

        /// <summary>
        /// Time between the ears (samples, + = the right ear later): where the two responses line up best below 1.5 kHz
        /// (where the ears hear time; above it the fine detail of the ear shapes would pull the peak off).
        /// </summary>
        static double Itd(float[] l0, float[] r0)
        {
            const int Max = 64;
            var l = Low(l0); var r = Low(r0);
            var c = new double[2 * Max + 1]; int best = 0; double bc = double.MinValue;
            for (int lag = -Max; lag <= Max; lag++)
            {
                double s = 0; for (int i = 0; i < l.Length; i++) { int j = i + lag; if (j >= 0 && j < r.Length) s += l[i] * r[j]; }
                c[lag + Max] = s; if (s > bc) { bc = s; best = lag; }
            }
            int k = best + Max; if (k <= 0 || k >= 2 * Max) return best;
            double y0 = c[k - 1], y1 = c[k], y2 = c[k + 1], den = y0 - 2 * y1 + y2;
            return best + (Math.Abs(den) > 1e-20 ? 0.5 * (y0 - y2) / den : 0);
        }

        static float[] Low(float[] h)
        {
            var a = Biquad.LowPass(1500, 48000); var b = Biquad.LowPass(1500, 48000);
            var o = new float[h.Length];
            for (int i = 0; i < h.Length; i++) o[i] = b.Process(a.Process(h[i]));
            return o;
        }

        static float Onset(float[] h)
        {
            float pk = 0; foreach (var v in h) pk = Math.Max(pk, Math.Abs(v));
            float th = pk * 0.2f;
            for (int k = 0; k < h.Length; k++)
                if (Math.Abs(h[k]) >= th)
                {
                    if (k == 0) return 0;
                    float p = Math.Abs(h[k - 1]), q = Math.Abs(h[k]);
                    return k - 1 + (th - p) / Math.Max(1e-9f, q - p);
                }
            return 0;
        }
        // ---- HeSuVi / Impulcifer WAV ----

        // HeSuVi 14-track order (from Impulcifer, MIT): FL-L FL-R SL-L SL-R BL-L BL-R FC-L FR-R FR-L SR-R SR-L BR-R BR-L FC-R
        static readonly string[] HesuviOrder = { "FL-L", "FL-R", "SL-L", "SL-R", "BL-L", "BL-R", "FC-L", "FR-R", "FR-L", "SR-R", "SR-L", "BR-R", "BR-L", "FC-R" };
        // 16-track "hexadecagonal" order including LFE.
        static readonly string[] Order16 = { "FL-L", "FL-R", "FR-L", "FR-R", "FC-L", "FC-R", "LFE-L", "LFE-R", "BL-L", "BL-R", "BR-L", "BR-R", "SL-L", "SL-R", "SR-L", "SR-R" };
        static readonly string[] SpeakerNames = { "FL", "FR", "FC", "SL", "SR", "BL", "BR" };
        static readonly float[] SpeakerAz = { 30, 330, 0, 90, 270, 150, 210 };
        public const int MaxCustomTaps = 24000;   // 0.5 s

        public static HrtfSet LoadHeSuVi(string path)
        {
            int ch, rate;
            float[][] data = ReadWav(path, out ch, out rate);
            string[] order = ch == 14 ? HesuviOrder : ch == 16 ? Order16 : null;
            if (order == null) throw new InvalidDataException("Expected a 14-channel (HeSuVi) or 16-channel WAV, this file has " + ch + " channels.");
            if (rate != 48000) for (int c = 0; c < ch; c++) data[c] = Resample(data[c], rate, 48000);
            int len = Math.Min(MaxCustomTaps, data[0].Length);
            // Trim trailing silence.
            int last = 0;
            for (int c = 0; c < ch; c++) for (int i = 0; i < len; i++) if (Math.Abs(data[c][i]) > 1e-5f && i > last) last = i;
            len = Math.Max(64, last + 1);
            var set = new HrtfSet { Name = Path.GetFileNameWithoutExtension(path), Taps = len, SpeakerSet = true };
            set.Az = new float[7]; set.El = new float[7]; set.L = new float[7][]; set.R = new float[7][];
            for (int s = 0; s < 7; s++)
            {
                set.Az[s] = SpeakerAz[s];
                int li = Array.IndexOf(order, SpeakerNames[s] + "-L"), ri = Array.IndexOf(order, SpeakerNames[s] + "-R");
                set.L[s] = new float[len]; set.R[s] = new float[len];
                Array.Copy(data[li], set.L[s], Math.Min(len, data[li].Length));
                Array.Copy(data[ri], set.R[s], Math.Min(len, data[ri].Length));
            }
            // Match the built-in level: normalize so the front pair has similar energy to the KU100 set.
            double e = 0;
            for (int s = 0; s < 2; s++) for (int i = 0; i < len; i++) e += set.L[s][i] * set.L[s][i] + set.R[s][i] * set.R[s][i];
            var reference = Get(Ku100, null);
            int fi = reference.Nearest(30, 0);
            double er = 0;
            for (int i = 0; i < reference.Taps; i++) er += reference.L[fi][i] * reference.L[fi][i] + reference.R[fi][i] * reference.R[fi][i];
            float g = (float)Math.Sqrt(er * 2 / Math.Max(1e-12, e));
            for (int s = 0; s < 7; s++) for (int i = 0; i < len; i++) { set.L[s][i] *= g; set.R[s][i] *= g; }
            return set;
        }

        static float[][] ReadWav(string path, out int channels, out int rate)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file");
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file");
                int fmt = 0, bits = 0; channels = 0; rate = 0;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    string id = new string(r.ReadChars(4)); int size = r.ReadInt32();
                    long next = r.BaseStream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        fmt = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                        if (fmt == -2 && size >= 40) { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); fmt = r.ReadInt16(); }
                    }
                    else if (id == "data")
                    {
                        int bps = bits / 8, frames = size / (bps * channels);
                        frames = Math.Min(frames, rate * 2);
                        var outp = new float[channels][];
                        for (int c = 0; c < channels; c++) outp[c] = new float[frames];
                        byte[] raw = r.ReadBytes(frames * bps * channels);
                        for (int f = 0; f < frames; f++)
                            for (int c = 0; c < channels; c++)
                            {
                                int o = (f * channels + c) * bps;
                                float v;
                                if (fmt == 3 && bits == 32) v = BitConverter.ToSingle(raw, o);
                                else if (fmt == 3 && bits == 64) v = (float)BitConverter.ToDouble(raw, o);
                                else if (bits == 16) v = BitConverter.ToInt16(raw, o) / 32768f;
                                else if (bits == 24) v = ((raw[o] | (raw[o + 1] << 8) | (raw[o + 2] << 16)) << 8 >> 8) / 8388608f;
                                else if (bits == 32) v = BitConverter.ToInt32(raw, o) / 2147483648f;
                                else throw new InvalidDataException("Unsupported WAV sample format");
                                outp[c][f] = v;
                            }
                        return outp;
                    }
                    r.BaseStream.Position = next;
                }
                throw new InvalidDataException("WAV file has no audio data");
            }
        }

        /// <summary>Windowed-sinc resampler for loading impulse responses at other sample rates.</summary>
        static float[] Resample(float[] x, int from, int to)
        {
            double ratio = (double)to / from;
            int n = (int)Math.Ceiling(x.Length * ratio);
            var y = new float[n];
            double cutoff = Math.Min(1.0, ratio) * 0.95;
            const int half = 32;
            for (int i = 0; i < n; i++)
            {
                double t = i / ratio;
                int c = (int)Math.Floor(t);
                double sum = 0;
                for (int k = c - half + 1; k <= c + half; k++)
                {
                    if (k < 0 || k >= x.Length) continue;
                    double d = t - k;
                    double sinc = Math.Abs(d) < 1e-9 ? 1 : Math.Sin(Math.PI * d * cutoff) / (Math.PI * d * cutoff);
                    double w = 0.5 * (1 + Math.Cos(Math.PI * d / half));
                    sum += x[k] * sinc * w * cutoff;
                }
                y[i] = (float)sum;
            }
            return y;
        }
    }
}
