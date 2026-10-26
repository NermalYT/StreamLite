using System;
using System.Collections.Generic;
using System.IO;
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
        static readonly Dictionary<string, HrtfSet> cache = new Dictionary<string, HrtfSet>();

        public static HrtfSet Get(string kind, string customPath)
        {
            string key = kind == Custom ? "custom:" + customPath : kind;
            lock (cache)
            {
                HrtfSet s;
                if (cache.TryGetValue(key, out s)) return s;
                if (kind == Custom) s = LoadHeSuVi(customPath);
                else if (kind == Kemar)
                {
                    s = LoadResource("hrtf_kemar.bin", "KEMAR (SADIE II)");
                    // KEMAR set has no room data of its own: borrow the KU100 room.
                    var room = Get(Ku100, null);   // lock is re-entrant
                    s.RoomTaps = room.RoomTaps; s.RoomAz = room.RoomAz; s.RoomL = room.RoomL; s.RoomR = room.RoomR;
                }
                else s = LoadResource("hrtf_ku100.bin", "Neumann KU100 (SADIE II)");
                // Keep only the current custom set in memory.
                if (kind == Custom) foreach (var k in new List<string>(cache.Keys)) if (k.StartsWith("custom:")) cache.Remove(k);
                cache[key] = s;
                return s;
            }
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
            int best = 0; double bd = double.MaxValue;
            double a1 = az * Math.PI / 180, e1 = el * Math.PI / 180;
            for (int i = 0; i < Az.Length; i++)
            {
                double a2 = Az[i] * Math.PI / 180, e2 = El[i] * Math.PI / 180;
                double c = Math.Sin(e1) * Math.Sin(e2) + Math.Cos(e1) * Math.Cos(e2) * Math.Cos(a1 - a2);
                double d = -c;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
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
            var idx = new int[M]; var dist = new double[M];
            for (int m = 0; m < M; m++) dist[m] = double.MaxValue;
            double a1 = az * Math.PI / 180, e1 = el * Math.PI / 180;
            for (int i = 0; i < Az.Length; i++)
            {
                double a2 = Az[i] * Math.PI / 180, e2 = El[i] * Math.PI / 180;
                double c = Math.Sin(e1) * Math.Sin(e2) + Math.Cos(e1) * Math.Cos(e2) * Math.Cos(a1 - a2);
                double d = Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
                for (int m = 0; m < M; m++)
                    if (d < dist[m]) { for (int q = M - 1; q > m; q--) { dist[q] = dist[q - 1]; idx[q] = idx[q - 1]; } dist[m] = d; idx[m] = i; break; }
            }
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

        static double Itd(float[] l, float[] r)
        {
            const int Max = 48;
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
