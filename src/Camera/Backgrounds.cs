using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using StreamLite.Interop;

namespace StreamLite.Camera
{
    /// <summary>Backgrounds for the AI green screen, as 960x540 RGB (the effect graph scales them to the output).</summary>
    internal static class Backgrounds
    {
        public const int W = 960, H = 540;

        public static byte[] Solid(byte r, byte g, byte b)
        {
            var o = new byte[W * H * 3];
            for (int i = 0; i < o.Length; i += 3) { o[i] = r; o[i + 1] = g; o[i + 2] = b; }
            return o;
        }

        /// <summary>A picture, scaled to fill 16:9 (centre-cropped).</summary>
        public static byte[] Picture(string path)
        {
            using (var src = Image.FromFile(path))
            using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    double s = Math.Max((double)W / src.Width, (double)H / src.Height);
                    float dw = (float)(src.Width * s), dh = (float)(src.Height * s);
                    g.DrawImage(src, (W - dw) / 2, (H - dh) / 2, dw, dh);
                }
                return FromBitmap(bmp);
            }
        }

        public static byte[] FromBitmap(Bitmap bmp)
        {
            var o = new byte[W * H * 3];
            var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[W * 3];
                for (int y = 0; y < H; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, W * 3);
                    for (int x = 0; x < W; x++) { o[(y * W + x) * 3] = row[x * 3 + 2]; o[(y * W + x) * 3 + 1] = row[x * 3 + 1]; o[(y * W + x) * 3 + 2] = row[x * 3]; }
                }
            }
            finally { bmp.UnlockBits(d); }
            return o;
        }

        /// <summary>
        /// What apps see in privacy mode: a dark picture saying the camera is paused (1920x1080 NV12, and the 640x360
        /// BGRA preview of it).
        /// </summary>
        public static void Privacy(out byte[] y, out byte[] uv, out byte[] preview)
        {
            const int w = 1920, h = 1080;
            y = new byte[w * h]; uv = new byte[w * h / 2]; preview = new byte[640 * 360 * 4];
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var big = new Font("Segoe UI Semibold", 54f, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", 28f, GraphicsUnit.Pixel))
                using (var text = new SolidBrush(Color.FromArgb(225, 228, 232)))
                using (var muted = new SolidBrush(Color.FromArgb(120, 128, 138)))
                using (var ring = new Pen(Color.FromArgb(90, 98, 108), 6f))
                {
                    g.Clear(Color.FromArgb(22, 24, 28));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    // A camera with a line through it.
                    g.DrawRectangle(ring, 860, 410, 150, 104); g.DrawPolygon(ring, new[] { new Point(1010, 440), new Point(1062, 412), new Point(1062, 512), new Point(1010, 484) });
                    g.DrawLine(ring, 850, 540, 1072, 390);
                    var fmt = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString("Camera paused", big, text, new RectangleF(0, 580, w, 80), fmt);
                    g.DrawString("StreamLite", small, muted, new RectangleF(0, 664, w, 40), fmt);
                }
                var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[w * 3];
                    for (int yy = 0; yy < h; yy++)
                    {
                        Marshal.Copy(d.Scan0 + yy * d.Stride, row, 0, w * 3);
                        for (int x = 0; x < w; x++)
                        {
                            int b = row[x * 3], gr = row[x * 3 + 1], r = row[x * 3 + 2];
                            y[yy * w + x] = (byte)((66 * r + 129 * gr + 25 * b + 128 >> 8) + 16);
                            if ((yy & 1) == 0 && (x & 1) == 0)
                            {
                                int o = (yy / 2) * w + x;
                                uv[o] = (byte)((-38 * r - 74 * gr + 112 * b + 128 >> 8) + 128);
                                uv[o + 1] = (byte)((112 * r - 94 * gr - 18 * b + 128 >> 8) + 128);
                            }
                            if (yy % 3 == 0 && x % 3 == 0)
                            {
                                int o = ((yy / 3) * 640 + x / 3) * 4;
                                preview[o] = (byte)b; preview[o + 1] = (byte)gr; preview[o + 2] = (byte)r; preview[o + 3] = 255;
                            }
                        }
                    }
                }
                finally { bmp.UnlockBits(d); }
            }
        }
    }

    /// <summary>A looping video background: Media Foundation decodes and scales to 960x540, frames follow the clock.</summary>
    internal sealed class VideoBackground : IDisposable
    {
        IMFSourceReader reader;
        readonly Stopwatch clock = Stopwatch.StartNew();
        long startOffset, nextTs = -1;
        byte[] current = new byte[Backgrounds.W * Backgrounds.H * 3];
        byte[] bgra = new byte[Backgrounds.W * Backgrounds.H * 4];
        public string Error;

        public VideoBackground(string path)
        {
            MF.Startup();
            IMFAttributes a; MF.MFCreateAttributes(out a, 2);
            a.SetUINT32(MF.EnableAdvancedVideoProcessing, 1);
            int hr = MF.MFCreateSourceReaderFromURL(path, a, out reader);
            Marshal.ReleaseComObject(a);
            if (hr < 0) { Error = "Couldn't open the video (0x" + hr.ToString("X8") + ")."; reader = null; return; }
            reader.SetStreamSelection(MF.AllStreams, false);
            reader.SetStreamSelection(MF.FirstVideoStream, true);
            IMFMediaType t; MF.MFCreateMediaType(out t);
            t.SetGUID(MF.MajorType, MF.Video);
            t.SetGUID(MF.SubType, MF.RGB32);
            t.SetUINT64(MF.FrameSize, MF.Pack(Backgrounds.W, Backgrounds.H));
            hr = reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, t);
            Marshal.ReleaseComObject(t);
            if (hr < 0) { Error = "This video format can't be decoded (0x" + hr.ToString("X8") + ")."; Marshal.ReleaseComObject(reader); reader = null; }
        }

        /// <summary>The frame for "now" (advances through the file at normal speed, loops at the end).</summary>
        public byte[] Frame()
        {
            if (reader == null) return current;
            long now = clock.ElapsedTicks * 10000000L / Stopwatch.Frequency;   // 100 ns
            // startOffset = wall time of the clip's start; nextTs = clip time of the frame on show (-1: none yet).
            for (int guard = 0; guard < 6 && (nextTs < 0 || nextTs + 333333 <= now - startOffset); guard++)
            {
                int stream, flags; long ts; IMFSample sample;
                if (reader.ReadSample(MF.FirstVideoStream, 0, out stream, out flags, out ts, out sample) < 0) break;
                if ((flags & 0x2) != 0)   // end of the clip: from the top
                {
                    if (sample != null) Marshal.ReleaseComObject(sample);
                    Rewind();
                    startOffset = now; nextTs = -1;
                    continue;
                }
                if (sample == null) continue;
                if (nextTs < 0) startOffset = now - ts;   // first frame (after open or a loop) shows now
                nextTs = ts;
                Decode(sample);
                Marshal.ReleaseComObject(sample);
            }
            return current;
        }

        void Rewind()
        {
            IntPtr pv = Marshal.AllocCoTaskMem(24);
            try
            {
                for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 20);   // VT_I8, position 0
                Guid none = Guid.Empty;
                reader.SetCurrentPosition(ref none, pv);
            }
            finally { Marshal.FreeCoTaskMem(pv); }
        }

        void Decode(IMFSample sample)
        {
            IMFMediaBuffer buf;
            if (sample.GetBufferByIndex(0, out buf) != 0) return;
            try
            {
                var b2 = buf as IMF2DBuffer;
                IntPtr p; int pitch; bool two = false;
                if (b2 != null && b2.Lock2D(out p, out pitch) == 0) two = true;
                else { int max, cur; if (buf.Lock(out p, out max, out cur) != 0) return; pitch = Backgrounds.W * 4; }
                try
                {
                    int W = Backgrounds.W, H = Backgrounds.H;
                    for (int y = 0; y < H; y++) Marshal.Copy(p + y * pitch, bgra, y * W * 4, W * 4);
                    var next = new byte[W * H * 3];
                    for (int i = 0, j = 0; i < W * H; i++, j += 4) { next[i * 3] = bgra[j + 2]; next[i * 3 + 1] = bgra[j + 1]; next[i * 3 + 2] = bgra[j]; }
                    current = next;
                }
                finally { if (two) b2.Unlock2D(); else buf.Unlock(); }
            }
            finally { Marshal.ReleaseComObject(buf); }
        }

        public void Dispose()
        {
            if (reader != null) { try { Marshal.FinalReleaseComObject(reader); } catch { } reader = null; }
        }
    }
}
