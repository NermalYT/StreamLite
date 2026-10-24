using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    // Same IID as IBaseFilter, declared up to EnumPins so we can walk the camera's pins.
    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IBaseFilterPins
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int timeout, out int state);
        [PreserveSig] int SetSyncSource(IntPtr clock);
        [PreserveSig] int GetSyncSource(out IntPtr clock);
        [PreserveSig] int EnumPins(out IEnumPins pins);
    }

    [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumPins
    {
        [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
    }

    [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPin
    {
        [PreserveSig] int Connect(IntPtr receivePin, IntPtr mt);
        [PreserveSig] int ReceiveConnection(IntPtr connector, IntPtr mt);
        [PreserveSig] int Disconnect();
        [PreserveSig] int ConnectedTo(out IntPtr pin);
        [PreserveSig] int ConnectionMediaType(IntPtr mt);
        [PreserveSig] int QueryPinInfo(IntPtr info);
        [PreserveSig] int QueryDirection(out int dir);
    }

    [ComImport, Guid("C6E13340-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(IntPtr mt);
        [PreserveSig] int GetFormat(out IntPtr mt);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mt, IntPtr caps);
    }

    internal class VideoMode
    {
        public int Width, Height;
        public double Fps;
        public string Subtype;
        public int CapsIndex;
        public override string ToString() { return Width + "x" + Height + "  " + Math.Round(Fps) + " fps"; }
        public string Key { get { return Width + "x" + Height + "@" + Math.Round(Fps); } }
    }

    internal static class DsFormats
    {
        static readonly Guid FORMAT_VideoInfo = new Guid("05589f80-c356-11ce-bf01-00aa0055595a");
        static readonly Guid FORMAT_VideoInfo2 = new Guid("f72a76A0-eb0a-11d0-ace4-0000c0cc16ba");
        static readonly string[] SubtypePreference = { "MJPG", "NV12", "YUY2" };

        static int Rank(string subtype)
        {
            int i = Array.IndexOf(SubtypePreference, subtype);
            return i < 0 ? 9 : i;
        }

        public static IAMStreamConfig CaptureConfig(object filter)
        {
            var f = filter as IBaseFilterPins;
            if (f == null) return null;
            IEnumPins en;
            if (f.EnumPins(out en) != 0 || en == null) return null;
            var arr = new IPin[1];
            while (en.Next(1, arr, IntPtr.Zero) == 0)
            {
                int dir;
                if (arr[0].QueryDirection(out dir) == 0 && dir == 1)
                {
                    var sc = arr[0] as IAMStreamConfig;
                    if (sc != null) return sc;   // first output pin is the capture pin
                }
                Marshal.ReleaseComObject(arr[0]);
            }
            return null;
        }

        public static List<VideoMode> Modes(IAMStreamConfig sc)
        {
            var list = new List<VideoMode>();
            int count, size;
            if (sc == null || sc.GetNumberOfCapabilities(out count, out size) != 0) return list;
            IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 128));
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr mt;
                    if (sc.GetStreamCaps(i, out mt, caps) != 0 || mt == IntPtr.Zero) continue;
                    var m = Parse(mt);
                    if (m != null)
                    {
                        m.CapsIndex = i;
                        // Some drivers list one entry per resolution with a frame-interval range.
                        long minInterval = Marshal.ReadInt64(caps, 104);
                        if (minInterval > 0) { double best = 1e7 / minInterval; if (best > m.Fps + 0.5) m.Fps = best; }
                        list.Add(m);
                    }
                    Free(mt);
                }
            }
            finally { Marshal.FreeCoTaskMem(caps); }
            return list;
        }

        static VideoMode Parse(IntPtr mt)
        {
            var sub = (Guid)Marshal.PtrToStructure(mt + 16, typeof(Guid));
            var fmt = (Guid)Marshal.PtrToStructure(mt + 44, typeof(Guid));
            IntPtr pb = Marshal.ReadIntPtr(mt, 80);
            if (pb == IntPtr.Zero) return null;
            int bmi;
            if (fmt == FORMAT_VideoInfo) bmi = 48; else if (fmt == FORMAT_VideoInfo2) bmi = 72; else return null;
            long avg = Marshal.ReadInt64(pb, 40);
            return new VideoMode
            {
                Width = Marshal.ReadInt32(pb, bmi + 4),
                Height = Math.Abs(Marshal.ReadInt32(pb, bmi + 8)),
                Fps = avg > 0 ? 1e7 / avg : 0,
                Subtype = SubtypeName(sub)
            };
        }

        static string SubtypeName(Guid g)
        {
            byte[] b = g.ToByteArray();
            string fourcc = System.Text.Encoding.ASCII.GetString(b, 0, 4);
            foreach (char c in fourcc) if (c < 32 || c > 126) return "RGB";
            return fourcc.Trim();
        }

        static void Free(IntPtr mt)
        {
            IntPtr pb = Marshal.ReadIntPtr(mt, 80);
            if (pb != IntPtr.Zero) Marshal.FreeCoTaskMem(pb);
            IntPtr unk = Marshal.ReadIntPtr(mt, 64);
            if (unk != IntPtr.Zero) Marshal.Release(unk);
            Marshal.FreeCoTaskMem(mt);
        }

        /// <summary>The format the pin is actually set to (after the graph is connected, what's being streamed).</summary>
        public static VideoMode CurrentMode(IAMStreamConfig sc)
        {
            IntPtr mt;
            if (sc == null || sc.GetFormat(out mt) != 0 || mt == IntPtr.Zero) return null;
            try { return Parse(mt); }
            finally { Free(mt); }
        }

        /// <summary>One entry per resolution + frame rate, preferring formats that reach full speed (MJPG/NV12).</summary>
        public static List<VideoMode> Distinct(List<VideoMode> all)
        {
            return all.Where(m => m.Fps > 0)
                .GroupBy(m => m.Width + "x" + m.Height + "@" + Math.Round(m.Fps))
                .Select(g => g.OrderBy(m => Rank(m.Subtype)).First())
                .OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Fps)
                .ToList();
        }

        /// <summary>Applies the capability's media type with the frame interval for the wanted fps.</summary>
        public static int SetMode(IAMStreamConfig sc, VideoMode mode)
        {
            int count, size;
            sc.GetNumberOfCapabilities(out count, out size);
            IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 128));
            IntPtr mt;
            int hr = sc.GetStreamCaps(mode.CapsIndex, out mt, caps);
            Marshal.FreeCoTaskMem(caps);
            if (hr != 0) return hr;
            try
            {
                IntPtr pb = Marshal.ReadIntPtr(mt, 80);
                if (pb != IntPtr.Zero && mode.Fps > 0) Marshal.WriteInt64(pb, 40, (long)Math.Round(1e7 / mode.Fps));
                return sc.SetFormat(mt);
            }
            finally { Free(mt); }
        }

        /// <summary>
        /// Asks the camera for an exact resolution and frame rate, even one it doesn't advertise, by rewriting
        /// each of its media types (compressed first - only those can carry 4K/60 over USB). Returns the
        /// subtype the driver accepted, or null if it refused all of them. Acceptance here is not proof the
        /// camera will stream it; the preview measures what actually arrives.
        /// </summary>
        public static string ForceMode(IAMStreamConfig sc, int width, int height, double fps)
        {
            int count, size;
            if (sc == null || sc.GetNumberOfCapabilities(out count, out size) != 0) return null;
            var candidates = Modes(sc)
                .GroupBy(m => m.Subtype)
                .Select(g => g.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Fps).First())
                .OrderBy(m => Rank(m.Subtype))
                .ToList();
            IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 128));
            try
            {
                foreach (var c in candidates)
                {
                    IntPtr mt;
                    if (sc.GetStreamCaps(c.CapsIndex, out mt, caps) != 0 || mt == IntPtr.Zero) continue;
                    try
                    {
                        if (!Rewrite(mt, width, height, fps)) continue;
                        if (sc.SetFormat(mt) == 0) return c.Subtype;
                    }
                    finally { Free(mt); }
                }
            }
            finally { Marshal.FreeCoTaskMem(caps); }
            return null;
        }

        static bool Rewrite(IntPtr mt, int width, int height, double fps)
        {
            var fmt = (Guid)Marshal.PtrToStructure(mt + 44, typeof(Guid));
            IntPtr pb = Marshal.ReadIntPtr(mt, 80);
            if (pb == IntPtr.Zero) return false;
            int bmi;
            if (fmt == FORMAT_VideoInfo) bmi = 48; else if (fmt == FORMAT_VideoInfo2) bmi = 72; else return false;
            string sub = SubtypeName((Guid)Marshal.PtrToStructure(mt + 16, typeof(Guid)));
            int bits = Marshal.ReadInt16(pb, bmi + 14);
            long bytes = sub == "NV12" ? (long)width * height * 3 / 2
                       : sub == "MJPG" || bits <= 0 ? (long)width * height * 2
                       : (long)width * height * bits / 8;
            for (int i = 0; i < 32; i++) Marshal.WriteByte(pb, i, 0);                 // rcSource / rcTarget = whole frame
            Marshal.WriteInt32(pb, 32, (int)Math.Min(int.MaxValue, bytes * 8 * (long)Math.Round(fps)));
            Marshal.WriteInt64(pb, 40, (long)Math.Round(1e7 / fps));
            int oldHeight = Marshal.ReadInt32(pb, bmi + 8);
            Marshal.WriteInt32(pb, bmi + 4, width);
            Marshal.WriteInt32(pb, bmi + 8, oldHeight < 0 ? -height : height);
            Marshal.WriteInt32(pb, bmi + 20, (int)bytes);
            Marshal.WriteInt32(mt, 40, (int)bytes);                                       // lSampleSize
            return true;
        }
    }
}
