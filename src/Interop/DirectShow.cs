using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace StreamLite.Interop
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker enumMoniker, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IBaseFilter { }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CAUUID { public int cElems; public IntPtr pElems; }

    [ComImport, Guid("B196B28B-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpecifyPropertyPages
    {
        [PreserveSig] int GetPages(out CAUUID pages);
    }

    // Only the leading methods we call are declared; COM vtables allow prefix declarations.
    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphBuilder
    {
        [PreserveSig] int AddFilter([In] IBaseFilter filter, [In, MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int RemoveFilter(IntPtr filter);
        [PreserveSig] int EnumFilters(out IEnumFilters filters);
    }

    [ComImport, Guid("56a86893-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumFilters
    {
        [PreserveSig] int Next(int count, out IntPtr filter, IntPtr fetched);
    }

    // Implemented by video renderers: lets us count frames actually delivered.
    [ComImport, Guid("1bd0ecb0-f8e2-11ce-aac6-0020af0b99a3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IQualProp
    {
        [PreserveSig] int get_FramesDroppedInRenderer(out int frames);
        [PreserveSig] int get_FramesDrawn(out int frames);
    }

    [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureGraphBuilder2
    {
        [PreserveSig] int SetFiltergraph([In] IGraphBuilder graph);
        [PreserveSig] int GetFiltergraph(out IntPtr graph);
        [PreserveSig] int SetOutputFileName(IntPtr type, IntPtr file, IntPtr filter, IntPtr sink);
        [PreserveSig] int FindInterface(IntPtr category, IntPtr type, IntPtr filter, IntPtr riid, IntPtr iface);
        [PreserveSig] int RenderStream([In] ref Guid category, [In] ref Guid type, [In, MarshalAs(UnmanagedType.IUnknown)] object source, [In] IBaseFilter compressor, [In] IBaseFilter renderer);
    }

    [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
    }

    [ComImport, Guid("56a868b4-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IVideoWindow
    {
        [PreserveSig] int put_Caption([MarshalAs(UnmanagedType.BStr)] string caption);
        [PreserveSig] int get_Caption([MarshalAs(UnmanagedType.BStr)] out string caption);
        [PreserveSig] int put_WindowStyle(int style);
        [PreserveSig] int get_WindowStyle(out int style);
        [PreserveSig] int put_WindowStyleEx(int style);
        [PreserveSig] int get_WindowStyleEx(out int style);
        [PreserveSig] int put_AutoShow(int auto);
        [PreserveSig] int get_AutoShow(out int auto);
        [PreserveSig] int put_WindowState(int state);
        [PreserveSig] int get_WindowState(out int state);
        [PreserveSig] int put_BackgroundPalette(int palette);
        [PreserveSig] int get_BackgroundPalette(out int palette);
        [PreserveSig] int put_Visible(int visible);
        [PreserveSig] int get_Visible(out int visible);
        [PreserveSig] int put_Left(int left);
        [PreserveSig] int get_Left(out int left);
        [PreserveSig] int put_Width(int width);
        [PreserveSig] int get_Width(out int width);
        [PreserveSig] int put_Top(int top);
        [PreserveSig] int get_Top(out int top);
        [PreserveSig] int put_Height(int height);
        [PreserveSig] int get_Height(out int height);
        [PreserveSig] int put_Owner(IntPtr owner);
        [PreserveSig] int get_Owner(out IntPtr owner);
        [PreserveSig] int put_MessageDrain(IntPtr drain);
        [PreserveSig] int get_MessageDrain(out IntPtr drain);
        [PreserveSig] int get_BorderColor(out int color);
        [PreserveSig] int put_BorderColor(int color);
        [PreserveSig] int get_FullScreenMode(out int mode);
        [PreserveSig] int put_FullScreenMode(int mode);
        [PreserveSig] int SetWindowForeground(int focus);
        [PreserveSig] int NotifyOwnerMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int SetWindowPosition(int left, int top, int width, int height);
    }

    [ComImport, Guid("56a868b5-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IBasicVideo
    {
        [PreserveSig] int get_AvgTimePerFrame(out double t);
        [PreserveSig] int get_BitRate(out int rate);
        [PreserveSig] int get_BitErrorRate(out int rate);
        [PreserveSig] int get_VideoWidth(out int width);
        [PreserveSig] int get_VideoHeight(out int height);
    }

    internal static class DsGuids
    {
        public static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        public static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        public static readonly Guid CLSID_FilterGraph = new Guid("e436ebb3-524f-11ce-9f53-0020af0ba770");
        public static readonly Guid CLSID_CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
        public static readonly Guid PIN_CATEGORY_PREVIEW = new Guid("fb6c4282-0353-11d1-905f-0000c0cc16ba");
        public static readonly Guid PIN_CATEGORY_CAPTURE = new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");
        public static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid IID_IBaseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
    }

    internal static class OleAut
    {
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
        public static extern int OleCreatePropertyFrame(IntPtr owner, int x, int y, string caption, int cObjects,
            [In, MarshalAs(UnmanagedType.Interface)] ref object objects, int cPages, IntPtr pageClsids, int lcid, int reserved, IntPtr reserved2);
    }

    internal class VideoDeviceInfo
    {
        public string Name;
        public string DevicePath;
        public IMoniker Moniker;
        public override string ToString() { return Name; }

        /// <summary>Converts \\?\usb#vid_..#..#{guid}\global to the PnP instance id USB\VID_..\...</summary>
        public string PnpInstanceId
        {
            get
            {
                if (string.IsNullOrEmpty(DevicePath) || !DevicePath.StartsWith(@"\\?\")) return null;
                string s = DevicePath.Substring(4);
                int i = s.IndexOf("#{");
                if (i < 0) return null;
                return s.Substring(0, i).Replace('#', '\\').ToUpperInvariant();
            }
        }

        public string Key { get { return string.IsNullOrEmpty(DevicePath) ? Name : DevicePath; } }
    }

    internal static class VideoDevices
    {
        public static List<VideoDeviceInfo> List()
        {
            var list = new List<VideoDeviceInfo>();
            object enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(DsGuids.CLSID_SystemDeviceEnum));
            var devEnum = (ICreateDevEnum)enumObj;
            IEnumMoniker em;
            Guid cat = DsGuids.CLSID_VideoInputDeviceCategory;
            int hr = devEnum.CreateClassEnumerator(ref cat, out em, 0);
            if (hr == 0 && em != null)
            {
                var arr = new IMoniker[1];
                while (em.Next(1, arr, IntPtr.Zero) == 0)
                {
                    var info = new VideoDeviceInfo { Moniker = arr[0] };
                    try
                    {
                        object bagObj;
                        Guid bagId = typeof(IPropertyBag).GUID;
                        arr[0].BindToStorage(null, null, ref bagId, out bagObj);
                        var bag = (IPropertyBag)bagObj;
                        object v;
                        if (bag.Read("FriendlyName", out v, IntPtr.Zero) == 0) info.Name = v as string;
                        if (bag.Read("DevicePath", out v, IntPtr.Zero) == 0) info.DevicePath = v as string;
                        Marshal.ReleaseComObject(bagObj);
                    }
                    catch { }
                    if (string.IsNullOrEmpty(info.Name)) info.Name = "Camera";
                    list.Add(info);
                }
                Marshal.ReleaseComObject(em);
            }
            Marshal.ReleaseComObject(enumObj);

            // Give duplicate names a suffix so the dropdown stays readable.
            var seen = new Dictionary<string, int>();
            foreach (var d in list)
            {
                int c;
                seen.TryGetValue(d.Name, out c);
                seen[d.Name] = c + 1;
                if (c > 0) d.Name = d.Name + " (" + (c + 1) + ")";
            }
            return list;
        }

        public static object BindFilter(IMoniker moniker)
        {
            object filter;
            Guid iid = DsGuids.IID_IBaseFilter;
            moniker.BindToObject(null, null, ref iid, out filter);
            return filter;
        }
    }
}
