using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Camera
{
    /// <summary>
    /// Reads a physical camera with Media Foundation (the way modern apps do, sharing the camera through the Frame
    /// Server) and delivers NV12 frames on its own thread. Windows decodes MJPEG / converts YUY2 for us.
    /// </summary>
    internal sealed class CameraReader : IDisposable
    {
        public int Width, Height;
        public double Fps;
        public string Format = "";
        public string Error;
        public volatile int Frames;
        /// <summary>y (Width x Height), uv (Width x Height/2), capture time (ticks).</summary>
        public event Action<byte[], byte[], long> FrameArrived;

        IMFSourceReader reader;
        object source;
        Thread thread;
        volatile bool stop;
        byte[] y, uv;

        /// <summary>Opens the camera whose symbolic link (or name) matches; picks the mode closest to the wish.</summary>
        public bool Open(string devicePath, string name, int wantW, int wantH, double wantFps)
        {
            try
            {
                MF.Startup();
                var cams = MF.Cameras();
                var cam = cams.FirstOrDefault(c => devicePath != null && string.Equals(c.Item2, devicePath, StringComparison.OrdinalIgnoreCase))
                       ?? cams.FirstOrDefault(c => name != null && c.Item1 == name);
                foreach (var c in cams) if (c != cam) Marshal.ReleaseComObject(c.Item3);
                if (cam == null) { Error = "Camera not found."; return false; }
                Guid iid = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");   // IMFMediaSource
                int hr = cam.Item3.ActivateObject(ref iid, out source);
                if (hr < 0) { Error = "The camera is busy or blocked (0x" + hr.ToString("X8") + ")."; return false; }
                IMFAttributes ra; MF.MFCreateAttributes(out ra, 3);
                ra.SetUINT32(MF.EnableAdvancedVideoProcessing, 1);
                ra.SetUINT32(MF.LowLatency, 1);
                hr = MF.MFCreateSourceReaderFromMediaSource(source, ra, out reader);
                Marshal.ReleaseComObject(ra);
                if (hr < 0) { Error = "Couldn't read the camera (0x" + hr.ToString("X8") + ")."; return false; }

                // Pick the native mode: right size first, then frame rate, then the cheapest format to decode.
                IMFMediaType best = null; double bestScore = double.MaxValue; Guid bestSub = Guid.Empty;
                for (int i = 0; ; i++)
                {
                    IMFMediaType t;
                    if (reader.GetNativeMediaType(MF.FirstVideoStream, i, out t) != 0) break;
                    int w, h, num, den; MF.Unpack(t.GetUINT64(MF.FrameSize), out w, out h); MF.Unpack(t.GetUINT64(MF.FrameRate), out num, out den);
                    double fps = den > 0 ? (double)num / den : 0;
                    Guid sub = t.GetGUID(MF.SubType);
                    double score = Math.Abs(w - wantW) * 10 + Math.Abs(h - wantH) * 10 + Math.Abs(fps - wantFps) * 50
                        + (sub == MF.NV12 ? 0 : sub == MF.YUY2 ? 1 : sub == MF.MJPG ? 2 : 1000);
                    if (score < bestScore) { if (best != null) Marshal.ReleaseComObject(best); best = t; bestScore = score; bestSub = sub; Width = w; Height = h; Fps = fps; }
                    else Marshal.ReleaseComObject(t);
                }
                if (best == null) { Error = "The camera reported no video formats."; return false; }
                reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, best);
                Format = bestSub == MF.MJPG ? "MJPG" : bestSub == MF.YUY2 ? "YUY2" : bestSub == MF.NV12 ? "NV12" : "other";
                Marshal.ReleaseComObject(best);
                // Ask for NV12 out (the reader adds the MJPEG decoder / converter).
                IMFMediaType o; MF.MFCreateMediaType(out o);
                o.SetGUID(MF.MajorType, MF.Video);
                o.SetGUID(MF.SubType, MF.NV12);
                o.SetUINT64(MF.FrameSize, MF.Pack(Width, Height));
                hr = reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, o);
                Marshal.ReleaseComObject(o);
                if (hr < 0) { Error = "The camera's video couldn't be converted (0x" + hr.ToString("X8") + ")."; return false; }
                y = new byte[Width * Height]; uv = new byte[Width * Height / 2];
                thread = new Thread(Run) { IsBackground = true, Name = "Camera reader", Priority = ThreadPriority.AboveNormal };
                thread.Start();
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        void Run()
        {
            while (!stop)
            {
                int stream, flags; long ts; IMFSample sample;
                int hr = reader.ReadSample(MF.FirstVideoStream, 0, out stream, out flags, out ts, out sample);
                if (hr < 0) { Error = "Camera stopped (0x" + hr.ToString("X8") + ")."; break; }
                if ((flags & 0x2) != 0) { Error = "Camera disconnected."; break; }   // end of stream / error flags
                if (sample == null) continue;
                try { Copy(sample); }
                finally { Marshal.ReleaseComObject(sample); }
                Frames++;
                var cb = FrameArrived;
                if (cb != null) cb(y, uv, DateTime.UtcNow.Ticks);
            }
        }

        void Copy(IMFSample sample)
        {
            IMFMediaBuffer buf;
            if (sample.GetBufferByIndex(0, out buf) != 0) return;
            try
            {
                var b2 = buf as IMF2DBuffer;
                IntPtr p; int pitch;
                if (b2 != null && b2.Lock2D(out p, out pitch) == 0)
                {
                    try { CopyPlanes(p, pitch); } finally { b2.Unlock2D(); }
                    return;
                }
                int max, cur;
                if (buf.Lock(out p, out max, out cur) != 0) return;
                try { CopyPlanes(p, cur >= Width * Height * 3 / 2 ? Math.Max(Width, cur * 2 / (Height * 3)) : Width); }
                finally { buf.Unlock(); }
            }
            finally { Marshal.ReleaseComObject(buf); }
        }

        void CopyPlanes(IntPtr p, int pitch)
        {
            if (pitch < 0) pitch = -pitch;
            if (pitch == Width)
            {
                Marshal.Copy(p, y, 0, Width * Height);
                Marshal.Copy(p + Width * Height, uv, 0, Width * Height / 2);
                return;
            }
            for (int r = 0; r < Height; r++) Marshal.Copy(p + r * pitch, y, r * Width, Width);
            IntPtr uvp = p + pitch * Height;
            for (int r = 0; r < Height / 2; r++) Marshal.Copy(uvp + r * pitch, uv, r * Width, Width);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ShutdownFn(IntPtr self);

        /// <summary>IMFMediaSource::Shutdown (vtable slot 12): frees the camera and wakes a blocked ReadSample.</summary>
        static void ShutdownSource(object src)
        {
            IntPtr unk = Marshal.GetIUnknownForObject(src), ms;
            Guid iid = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");
            if (Marshal.QueryInterface(unk, ref iid, out ms) == 0)
            {
                IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(ms), 12 * IntPtr.Size);
                ((ShutdownFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(ShutdownFn)))(ms);
                Marshal.Release(ms);
            }
            Marshal.Release(unk);
        }

        public void Dispose()
        {
            stop = true;
            if (thread != null) thread.Join(2000);                    // ReadSample returns with the next frame
            if (reader != null) { try { Marshal.FinalReleaseComObject(reader); } catch { } reader = null; }
            if (source != null)
            {
                try { ShutdownSource(source); } catch { }
                try { Marshal.FinalReleaseComObject(source); } catch { }
                source = null;
            }
        }
    }
}
