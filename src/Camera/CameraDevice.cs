using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.Camera
{
    internal class CameraProperty
    {
        public string Key;
        public string Name;
        public bool IsCameraControl;
        public int Id;
        public int Min, Max, Step, Default, Caps;
        public bool SupportsAuto { get { return (Caps & 1) != 0; } }
        public bool SupportsManual { get { return (Caps & 2) != 0 || Caps == 0; } }
    }

    /// <summary>Hardware camera controls (the same ones the Windows "camera properties" dialog uses).</summary>
    internal sealed class CameraDevice : IDisposable
    {
        public readonly VideoDeviceInfo Info;
        object filter;
        IAMCameraControl cam;
        IAMVideoProcAmp amp;
        public readonly List<CameraProperty> Properties = new List<CameraProperty>();

        static readonly object[][] AmpProps =
        {
            new object[] { 0, "Brightness" }, new object[] { 1, "Contrast" }, new object[] { 3, "Saturation" },
            new object[] { 4, "Sharpness" }, new object[] { 5, "Gamma" }, new object[] { 2, "Hue" },
            new object[] { 7, "White balance" }, new object[] { 8, "Backlight comp." }, new object[] { 9, "Gain" },
        };
        static readonly object[][] CamProps =
        {
            new object[] { 4, "Exposure" }, new object[] { 6, "Focus" }, new object[] { 3, "Zoom" },
            new object[] { 0, "Pan" }, new object[] { 1, "Tilt" }, new object[] { 2, "Roll" }, new object[] { 5, "Iris" },
        };

        public CameraDevice(VideoDeviceInfo info)
        {
            Info = info;
            filter = VideoDevices.BindFilter(info.Moniker);
            cam = filter as IAMCameraControl;
            amp = filter as IAMVideoProcAmp;
            if (amp != null)
                foreach (var p in AmpProps) TryAdd(false, (int)p[0], (string)p[1]);
            if (cam != null)
                foreach (var p in CamProps) TryAdd(true, (int)p[0], (string)p[1]);
        }

        void TryAdd(bool isCam, int id, string name)
        {
            int min, max, step, def, caps;
            int hr = isCam ? cam.GetRange(id, out min, out max, out step, out def, out caps)
                           : amp.GetRange(id, out min, out max, out step, out def, out caps);
            if (hr != 0 || max <= min) return;
            Properties.Add(new CameraProperty
            {
                Key = (isCam ? "cam." : "amp.") + id,
                Name = name, IsCameraControl = isCam, Id = id,
                Min = min, Max = max, Step = Math.Max(1, step), Default = def, Caps = caps
            });
        }

        public bool Get(CameraProperty p, out int value, out bool auto)
        {
            int flags;
            int hr = p.IsCameraControl ? cam.Get(p.Id, out value, out flags) : amp.Get(p.Id, out value, out flags);
            auto = (flags & 1) != 0;
            return hr == 0;
        }

        public bool Set(CameraProperty p, int value, bool auto)
        {
            int flags = auto ? 1 : 2;
            value = Math.Max(p.Min, Math.Min(p.Max, value));
            int hr = p.IsCameraControl ? cam.Set(p.Id, value, flags) : amp.Set(p.Id, value, flags);
            return hr == 0;
        }

        /// <summary>Resolutions/frame rates the camera offers right now (only the active one while another app streams it).</summary>
        public List<VideoMode> Modes()
        {
            try
            {
                var sc = DsFormats.CaptureConfig(filter);
                var list = DsFormats.Distinct(DsFormats.Modes(sc));
                if (sc != null) Marshal.ReleaseComObject(sc);
                return list;
            }
            catch { return new List<VideoMode>(); }
        }

        public void ShowDriverDialog(IWin32Window owner)
        {
            var spp = filter as ISpecifyPropertyPages;
            if (spp == null) return;
            CAUUID pages;
            if (spp.GetPages(out pages) != 0) return;
            try
            {
                object o = filter;
                OleAut.OleCreatePropertyFrame(owner != null ? owner.Handle : IntPtr.Zero, 0, 0, Info.Name, 1, ref o, pages.cElems, pages.pElems, 0, 0, IntPtr.Zero);
            }
            finally { Marshal.FreeCoTaskMem(pages.pElems); }
        }

        public void Dispose()
        {
            if (filter != null) { Marshal.ReleaseComObject(filter); filter = null; }
            cam = null; amp = null;
        }
    }

    /// <summary>Live preview using a DirectShow graph rendered into a WinForms control.</summary>
    internal sealed class CameraPreview : IDisposable
    {
        object graph, builder, source;
        IVideoWindow window;
        IMediaControl control;
        Control host;
        public int VideoWidth = 16, VideoHeight = 9;
        public VideoMode ActiveMode;

        /// <summary>True when a forced resolution/fps was refused or couldn't stream, and the camera default is shown instead.</summary>
        public bool RequestRefused;
        public string RefuseReason;
        IQualProp qual;
        readonly Stopwatch clock = new Stopwatch();
        int lastFrames;

        /// <summary>
        /// Starts the preview. With width > 0 it forces that resolution/fps even if the camera doesn't list it;
        /// if the camera refuses, it falls back to the closest mode the camera really has.
        /// </summary>
        public string Start(VideoDeviceInfo info, Control target, int width, int height, double fps)
        {
            RequestRefused = false;
            RefuseReason = null;
            if (width <= 0) return Build(info, target, 0, 0, 0, false);

            // Exact request first, then the closest real mode, then whatever the camera defaults to.
            string err = Build(info, target, width, height, fps, false);
            if (err != null) { RefuseReason = err; err = Build(info, target, width, height, fps, true); }
            if (err != null) err = Build(info, target, 0, 0, 0, false);
            if (err == null) RequestRefused = ActiveMode == null || ActiveMode.Width != width || ActiveMode.Height != height;
            return err;
        }

        static VideoMode Closest(List<VideoMode> modes, int width, int height, double fps)
        {
            long area = (long)width * height;
            return modes.Where(m => (long)m.Width * m.Height <= area)
                        .OrderByDescending(m => (long)m.Width * m.Height).ThenBy(m => Math.Abs(m.Fps - fps)).FirstOrDefault()
                ?? modes.OrderBy(m => (long)m.Width * m.Height).ThenBy(m => Math.Abs(m.Fps - fps)).FirstOrDefault();
        }

        string Build(VideoDeviceInfo info, Control target, int width, int height, double fps, bool closestNative)
        {
            Dispose();
            host = target;
            try
            {
                graph = Activator.CreateInstance(Type.GetTypeFromCLSID(DsGuids.CLSID_FilterGraph));
                builder = Activator.CreateInstance(Type.GetTypeFromCLSID(DsGuids.CLSID_CaptureGraphBuilder2));
                var cgb = (ICaptureGraphBuilder2)builder;
                cgb.SetFiltergraph((IGraphBuilder)graph);
                source = VideoDevices.BindFilter(info.Moniker);
                int hr = ((IGraphBuilder)graph).AddFilter((IBaseFilter)source, "Camera");
                if (hr < 0) throw new COMException("AddFilter", hr);
                if (width > 0)
                {
                    var sc = DsFormats.CaptureConfig(source);
                    try
                    {
                        var modes = DsFormats.Distinct(DsFormats.Modes(sc));
                        var exact = modes.FirstOrDefault(m => m.Width == width && m.Height == height && Math.Abs(m.Fps - fps) < 0.5);
                        if (closestNative || exact != null)
                        {
                            var pick = exact ?? Closest(modes, width, height, fps);
                            if (pick != null && sc != null) DsFormats.SetMode(sc, pick);   // some cameras reject SetFormat but already run this mode
                        }
                        else if (DsFormats.ForceMode(sc, width, height, fps) == null)
                            throw new InvalidOperationException("The camera refused " + width + "x" + height + " at " + fps + " fps.");
                    }
                    finally { if (sc != null) Marshal.ReleaseComObject(sc); }
                }
                Guid cat = DsGuids.PIN_CATEGORY_PREVIEW, type = DsGuids.MEDIATYPE_Video;
                hr = cgb.RenderStream(ref cat, ref type, source, null, null);
                if (hr < 0)
                {
                    cat = DsGuids.PIN_CATEGORY_CAPTURE;
                    hr = cgb.RenderStream(ref cat, ref type, source, null, null);
                }
                if (hr < 0) throw new COMException("Could not build preview", hr);
                var cfg = DsFormats.CaptureConfig(source);
                ActiveMode = DsFormats.CurrentMode(cfg);
                if (cfg != null) Marshal.ReleaseComObject(cfg);

                var bv = graph as IBasicVideo;
                int w, h;
                if (bv != null && bv.get_VideoWidth(out w) == 0 && bv.get_VideoHeight(out h) == 0 && w > 0 && h > 0) { VideoWidth = w; VideoHeight = h; }

                window = (IVideoWindow)graph;
                window.put_AutoShow(0);
                window.put_Owner(target.Handle);
                window.put_WindowStyle(0x40000000 | 0x04000000 | 0x02000000); // WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
                window.put_MessageDrain(target.Handle);
                Resize();
                control = (IMediaControl)graph;
                hr = control.Run();
                if (hr < 0) throw new COMException("Camera is busy", hr);
                window.put_Visible(-1);
                target.Resize += OnResize;
                qual = FindRenderer();
                lastFrames = Frames();
                clock.Restart();
                return null;
            }
            catch (Exception ex)
            {
                Dispose();
                return "Preview unavailable - the camera may be in use by another app. (" + ex.Message + ")";
            }
        }

        IQualProp FindRenderer()
        {
            IEnumFilters filters;
            if (((IGraphBuilder)graph).EnumFilters(out filters) != 0 || filters == null) return null;
            IntPtr p;
            while (filters.Next(1, out p, IntPtr.Zero) == 0 && p != IntPtr.Zero)
            {
                object o = Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
                var q = o as IQualProp;
                if (q != null) return q;
                Marshal.ReleaseComObject(o);
            }
            return null;
        }

        int Frames()
        {
            if (qual == null) return 0;
            int drawn, dropped;
            if (qual.get_FramesDrawn(out drawn) != 0) drawn = 0;
            if (qual.get_FramesDroppedInRenderer(out dropped) != 0) dropped = 0;
            return drawn + dropped;
        }

        /// <summary>Frames per second actually arriving from the camera since the last call (-1 if unknown).</summary>
        public double SampleFps()
        {
            if (qual == null || control == null) return -1;
            int f = Frames();
            double t = clock.Elapsed.TotalSeconds;
            clock.Restart();
            int d = f - lastFrames;
            lastFrames = f;
            return t > 0.2 ? d / t : -1;
        }

        void OnResize(object sender, EventArgs e) { Resize(); }

        public void Resize()
        {
            if (window == null || host == null) return;
            int cw = host.ClientSize.Width, ch = host.ClientSize.Height;
            double ar = (double)VideoWidth / VideoHeight;
            int w = cw, h = (int)(cw / ar);
            if (h > ch) { h = ch; w = (int)(ch * ar); }
            window.SetWindowPosition((cw - w) / 2, (ch - h) / 2, w, h);
        }

        public bool Active { get { return control != null; } }

        public void Dispose()
        {
            if (host != null) host.Resize -= OnResize;
            try { if (control != null) control.Stop(); } catch { }
            try { if (window != null) { window.put_Visible(0); window.put_Owner(IntPtr.Zero); } } catch { }
            control = null; window = null;
            if (qual != null) { Marshal.ReleaseComObject(qual); qual = null; }
            if (source != null) { Marshal.ReleaseComObject(source); source = null; }
            if (builder != null) { Marshal.ReleaseComObject(builder); builder = null; }
            if (graph != null) { Marshal.ReleaseComObject(graph); graph = null; }
        }
    }

    internal class PnpCamera
    {
        public string Name;
        public string InstanceId;
        public bool Enabled;
    }

    /// <summary>Enabling/disabling a camera hides it from every app; this needs administrator rights.</summary>
    internal static class CameraPnp
    {
        public static List<PnpCamera> List()
        {
            var list = new List<PnpCamera>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Camera' OR PNPClass='Image'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var code = o["ConfigManagerErrorCode"];
                        list.Add(new PnpCamera
                        {
                            Name = (o["Name"] as string) ?? "Camera",
                            InstanceId = o["PNPDeviceID"] as string,
                            Enabled = code == null || Convert.ToInt32(code) != 22
                        });
                    }
            }
            catch { }
            return list;
        }

        public static string SetEnabled(string instanceId, bool enable)
        {
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe", (enable ? "/enable-device " : "/disable-device ") + "\"" + instanceId + "\"")
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    return p.ExitCode == 0 || p.ExitCode == 3010 ? null : "pnputil failed (exit code " + p.ExitCode + ")";
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? "Administrator permission was declined." : ex.Message;
            }
        }
    }
}
