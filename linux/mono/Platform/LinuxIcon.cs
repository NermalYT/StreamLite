using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace StreamLite.Linux
{
    /// <summary>
    /// StreamLite's icon for Mono: app.ico holds PNG pictures, which Mono's Icon can't read, so the small sizes are
    /// rewritten as classic 32-bit icon pictures (the same pixels).
    /// </summary>
    internal static class LinuxIcon
    {
        static Icon cached;

        public static Icon Load()
        {
            if (cached != null) return cached;
            using (var s = typeof(LinuxIcon).Assembly.GetManifestResourceStream("app.ico"))
            {
                if (s == null) return null;
                var ico = new byte[s.Length];
                int got = 0; while (got < ico.Length) { int n = s.Read(ico, got, ico.Length - got); if (n <= 0) break; got += n; }
                return cached = Classic(ico);
            }
        }

        static Icon Classic(byte[] ico)
        {
            int count = BitConverter.ToUInt16(ico, 4);
            var pictures = new List<byte[]>(); var sizes = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int o = 6 + 16 * i, w = ico[o] == 0 ? 256 : ico[o];
                if (w > 64) continue;
                int size = BitConverter.ToInt32(ico, o + 8), off = BitConverter.ToInt32(ico, o + 12);
                using (var b = new Bitmap(new MemoryStream(ico, off, size))) { pictures.Add(Dib(b)); sizes.Add(b.Width); }
            }
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)pictures.Count);
            int at = 6 + 16 * pictures.Count;
            for (int i = 0; i < pictures.Count; i++)
            {
                bw.Write((byte)sizes[i]); bw.Write((byte)sizes[i]); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32); bw.Write(pictures[i].Length); bw.Write(at);
                at += pictures[i].Length;
            }
            foreach (var p in pictures) bw.Write(p);
            bw.Flush(); ms.Position = 0;
            return new Icon(ms);
        }

        /// <summary>One picture as an icon bitmap: header, 32-bit pixels bottom-up, an empty mask (the alpha does the shape).</summary>
        static byte[] Dib(Bitmap b)
        {
            int w = b.Width, h = b.Height, mask = (w + 31) / 32 * 4 * h;
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4 + mask); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++) { var c = b.GetPixel(x, y); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A); }
            bw.Write(new byte[mask]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
