using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StreamLite.Audio
{
    /// <summary>
    /// Real game footsteps for Personalize 3D (4.0.6): Counter-Strike 2's own footstep recordings, read straight from
    /// CS2 on this PC (Steam) while the test runs - never copied or shipped, they're Valve's. Recorded steps carry the
    /// 4-14 kHz detail your ears tell in front / behind / above / below by, which a synthesized knock lacks. Each step is
    /// a concrete step with the CT gear rattle on top, as CS2 plays them. Null when CS2 isn't installed (the test uses
    /// StreamLite's own footsteps then).
    /// </summary>
    internal static class GameFootsteps
    {
        const string Folder = "sounds/player/footsteps";

        /// <summary>CS2's archive directory (pak01_dir.vpk) on this PC, or null.</summary>
        public static string FindCs2()
        {
            foreach (var lib in SteamLibraries())
            {
                string p = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "pak01_dir.vpk");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
#if LINUX
            // Steam on Linux (4.1.4): the native one, the Flatpak and the Snap - CS2 runs natively, its files are the same.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var r in new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam", "snap/steam/common/.local/share/Steam" })
                roots.Add(Path.Combine(home, r));
            var cmp = StringComparer.Ordinal;
#else
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) { var s = k != null ? k.GetValue("SteamPath") as string : null; if (s != null) roots.Add(s.Replace('/', '\\')); } } catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var cmp = StringComparer.OrdinalIgnoreCase;
#endif
            var libs = new List<string>();
            foreach (var r in roots.Distinct(cmp))
            {
                libs.Add(r);
                try
                {
                    string vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            return libs.Distinct(cmp);
        }

        /// <summary>
        /// CS2's footsteps at 48 kHz, each peaking at peak: count steps (fewer if there are fewer), or null without CS2.
        /// </summary>
        public static float[][] LoadCs2(int count, float peak)
        {
            string dir = FindCs2();
            if (dir == null) return null;
            try
            {
                var files = ReadDirectory(dir);
                Func<string, float[]> load = name => { Entry e; return files.TryGetValue(name, out e) ? Decode(dir, e) : null; };
                var steps = files.Keys.Where(k => Regex.IsMatch(k, @"^concrete_ct_\d+$")).OrderBy(k => k).Select(load).Where(s => s != null).ToList();
                var gear = files.Keys.Where(k => Regex.IsMatch(k, @"^suit_ct_\d+$")).OrderBy(k => k).Select(load).Where(s => s != null).ToList();
                if (steps.Count == 0) return null;
                var result = new List<float[]>();
                for (int i = 0; i < Math.Min(count, steps.Count); i++)
                {
                    var s = (float[])steps[i].Clone();
                    if (gear.Count > 0) { var g = gear[i % gear.Count]; for (int j = 0; j < Math.Min(s.Length, g.Length); j++) s[j] += 0.5f * g[j]; }
                    float pk = s.Max(v => Math.Abs(v));
                    if (pk > 0) for (int j = 0; j < s.Length; j++) s[j] *= peak / pk;
                    result.Add(s);
                }
                return result.ToArray();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Valve's archives (VPK 2) and sounds (vsnd_c, version 4: PCM)

        sealed class Entry { public int Archive, Offset, Length; }

        /// <summary>The footstep sounds listed in the archive directory: name (no extension) -> where its bytes are.</summary>
        static Dictionary<string, Entry> ReadDirectory(string dirFile)
        {
            var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            using (var r = new BinaryReader(new MemoryStream(File.ReadAllBytes(dirFile))))
            {
                if (r.ReadUInt32() != 0x55AA1234 || r.ReadUInt32() != 2) return found;
                r.ReadBytes(4 + 16);   // tree size, then the other sections' sizes
                while (true)
                {
                    string ext = CString(r); if (ext.Length == 0) break;
                    while (true)
                    {
                        string path = CString(r); if (path.Length == 0) break;
                        while (true)
                        {
                            string name = CString(r); if (name.Length == 0) break;
                            r.ReadUInt32();   // CRC
                            int preload = r.ReadUInt16(), archive = r.ReadUInt16(), offset = r.ReadInt32(), length = r.ReadInt32();
                            r.ReadUInt16();   // terminator
                            if (preload > 0) r.ReadBytes(preload);
                            if (ext == "vsnd_c" && path == Folder && archive != 0x7FFF && preload == 0)
                                found[name] = new Entry { Archive = archive, Offset = offset, Length = length };
                        }
                    }
                }
            }
            return found;
        }

        static string CString(BinaryReader r)
        {
            var b = new List<byte>();
            for (byte c; (c = r.ReadByte()) != 0;) b.Add(c);
            return Encoding.UTF8.GetString(b.ToArray());
        }

        /// <summary>A footstep's samples (mono, 48 kHz), from its archive; null when it isn't 16-bit PCM.</summary>
        static float[] Decode(string dirFile, Entry e)
        {
            string pak = Path.Combine(Path.GetDirectoryName(dirFile), string.Format("pak01_{0:000}.vpk", e.Archive));
            byte[] b;
            using (var f = File.OpenRead(pak)) { f.Seek(e.Offset, SeekOrigin.Begin); b = new byte[e.Length]; int got = 0; while (got < b.Length) { int n = f.Read(b, got, b.Length - got); if (n <= 0) break; got += n; } }
            return DecodeSound(b);
        }

        /// <summary>
        /// A compiled sound resource (vsnd_c, version 4): its DATA block holds the format (rate, encoding, channels); the
        /// samples follow the resource. Only 16-bit PCM (what CS2's footsteps are); stereo is mixed to mono.
        /// </summary>
        internal static float[] DecodeSound(byte[] b)
        {
            int size = BitConverter.ToInt32(b, 0), version = BitConverter.ToUInt16(b, 6), blockOffset = BitConverter.ToInt32(b, 8), blocks = BitConverter.ToInt32(b, 12);
            if (version != 4 || size <= 0 || size >= b.Length) return null;
            int data = -1;
            for (int i = 0; i < blocks; i++)
            {
                int p = 8 + blockOffset + i * 12;
                if (Encoding.ASCII.GetString(b, p, 4) == "DATA") data = p + 4 + BitConverter.ToInt32(b, p + 4);
            }
            if (data < 0) return null;
            int rate = BitConverter.ToUInt16(b, data), encoding = b[data + 2], channels = b[data + 3];
            if (encoding != 0 || channels < 1 || channels > 2 || rate < 8000) return null;
            int frames = (b.Length - size) / (2 * channels);
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float s = 0;
                for (int c = 0; c < channels; c++) s += BitConverter.ToInt16(b, size + 2 * (i * channels + c)) / 32768f;
                mono[i] = s / channels;
            }
            return Resample(mono, rate, SpatialProcessor.Fs);
        }

        /// <summary>To another sample rate with a windowed sinc (16 taps a side): the treble the ears need kept intact.</summary>
        internal static float[] Resample(float[] x, int from, int to)
        {
            if (from == to) return x;
            const int A = 16;
            double ratio = (double)from / to, cut = Math.Min(1.0, 1 / ratio);
            int n = (int)(x.Length / ratio);
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i * ratio; int c = (int)Math.Floor(t); double sum = 0;
                for (int k = c - A + 1; k <= c + A; k++)
                {
                    if (k < 0 || k >= x.Length) continue;
                    double d = t - k, sinc = d == 0 ? 1 : Math.Sin(Math.PI * d * cut) / (Math.PI * d * cut), w = Math.Abs(d) < A ? 0.5 + 0.5 * Math.Cos(Math.PI * d / A) : 0;
                    sum += x[k] * sinc * w * cut;
                }
                y[i] = (float)sum;
            }
            return y;
        }
    }
}
