using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid f, int p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public uint uintValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    // Undocumented but stable since Windows 7: used by the Sound control panel to change default devices.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient { }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Windows spatial sound on an output (Windows Sonic, Dolby Atmos, DTS:X): the first methods are enough.</summary>
    [ComImport, Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpatialAudioClient
    {
        [PreserveSig] int GetStaticObjectPosition(int type, out float x, out float y, out float z);
        [PreserveSig] int GetNativeStaticObjectTypeMask(out int mask);
        [PreserveSig] int GetMaxDynamicObjectCount(out uint value);
    }

    internal static class CoreAudioConst
    {
        public static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public const int CLSCTX_ALL = 23;
        public const int DEVICE_STATE_ACTIVE = 1;
        public const int STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const int STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        public const int STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        public const uint BUFFERFLAGS_SILENT = 2;
        public const uint BUFFERFLAGS_TIMESTAMP_ERROR = 4;
        public static PropertyKey PKEY_Device_FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        public static PropertyKey PKEY_DeviceInterface_FriendlyName = new PropertyKey(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    }

    internal static class NativeOle
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);
    }

    internal class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public string Adapter;
        /// <summary>The driver's device instance (e.g. {1}.ROOT\MEDIA\0000): identical for both ends of a cable, never renamed.</summary>
        public string Instance = "";
        public override string ToString() { return Name; }
    }

    internal static class AudioDevices
    {
        public static List<AudioDeviceInfo> List(EDataFlow flow) { return List(flow, CoreAudioConst.DEVICE_STATE_ACTIVE); }

        /// <summary>Endpoints in the given states (1 active, 2 disabled, 4 not present, 8 unplugged; may be combined).</summary>
        public static List<AudioDeviceInfo> List(EDataFlow flow, int stateMask)
        {
            var list = new List<AudioDeviceInfo>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(flow, stateMask, out col) != 0 || col == null) return list;
                int count;
                col.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    var info = Describe(dev);
                    if (info != null) list.Add(info);
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public static string GetDefaultId(EDataFlow flow, ERole role)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(flow, role, out dev) != 0 || dev == null) return null;
                string id;
                dev.GetId(out id);
                Marshal.ReleaseComObject(dev);
                return id;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>Default for games/media only (Console + Multimedia); voice chat (Communications) stays where it is.</summary>
        public static bool SetDefaultPlayback(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr = pc.SetDefaultEndpoint(id, ERole.Console);
                pc.SetDefaultEndpoint(id, ERole.Multimedia);
                return hr == 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        public static bool SetDefaultRole(string id, ERole role)
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try { return pc.SetDefaultEndpoint(id, role) == 0; }
            catch { return false; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        public static IntPtr MakeFormat(int channels, int mask, int rate, bool floatSamples, int bits)
        {
            IntPtr p = Marshal.AllocHGlobal(40);
            var b = new byte[40];
            int block = channels * bits / 8;
            BitConverter.GetBytes((short)-2).CopyTo(b, 0);                  // WAVE_FORMAT_EXTENSIBLE
            BitConverter.GetBytes((short)channels).CopyTo(b, 2);
            BitConverter.GetBytes(rate).CopyTo(b, 4);
            BitConverter.GetBytes(rate * block).CopyTo(b, 8);
            BitConverter.GetBytes((short)block).CopyTo(b, 12);
            BitConverter.GetBytes((short)bits).CopyTo(b, 14);
            BitConverter.GetBytes((short)22).CopyTo(b, 16);
            BitConverter.GetBytes((short)bits).CopyTo(b, 18);
            BitConverter.GetBytes(mask).CopyTo(b, 20);
            new Guid(floatSamples ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(b, 24);
            Marshal.Copy(b, 0, p, 40);
            return p;
        }

        /// <summary>Reads channel count, channel mask and rate of a device's shared-mode mix format.</summary>
        public static bool MixFormat(string id, out int channels, out int mask, out int rate)
        {
            channels = 0; mask = 0; rate = 0;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object o = null;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return false;
                Guid iid = CoreAudioConst.IID_IAudioClient;
                if (dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o) != 0) return false;
                IntPtr f;
                if (((IAudioClient)o).GetMixFormat(out f) != 0) return false;
                short tag = Marshal.ReadInt16(f, 0);
                channels = Marshal.ReadInt16(f, 2); rate = Marshal.ReadInt32(f, 4);
                mask = tag == -2 ? Marshal.ReadInt32(f, 20) : channels == 2 ? 3 : channels == 1 ? 4 : 0;
                Marshal.FreeCoTaskMem(f);
                return true;
            }
            catch { return false; }
            finally
            {
                if (o != null) Marshal.ReleaseComObject(o);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>Same as "Configure speakers: 7.1 Surround" in the Sound control panel.</summary>
        public static string SetSurround71(string id)
        {
            const int mask = 0x63F;   // FL FR FC LFE BL BR SL SR
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr = -1;
                foreach (int bits in new[] { 24, 16, 32 })
                {
                    IntPtr ep = MakeFormat(8, mask, 48000, false, bits), mix = MakeFormat(8, mask, 48000, true, 32);
                    hr = pc.SetDeviceFormat(id, ep, mix);
                    Marshal.FreeHGlobal(ep); Marshal.FreeHGlobal(mix);
                    if (hr == 0) break;
                }
                if (hr != 0) return "This device refused 7.1 (0x" + hr.ToString("X8") + ").";
                var speakers = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 3);
                var full = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 6);
                var pv = new PropVariant { vt = 19, uintValue = (uint)mask };   // VT_UI4
                pc.SetPropertyValue(id, 0, ref speakers, ref pv);
                pv = new PropVariant { vt = 19, uintValue = (uint)mask };
                pc.SetPropertyValue(id, 0, ref full, ref pv);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        public static bool SetDefault(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr = pc.SetDefaultEndpoint(id, ERole.Console);
                pc.SetDefaultEndpoint(id, ERole.Multimedia);
                pc.SetDefaultEndpoint(id, ERole.Communications);
                return hr == 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        static AudioDeviceInfo Describe(IMMDevice dev)
        {
            string id;
            if (dev.GetId(out id) != 0) return null;
            var info = new AudioDeviceInfo { Id = id, Name = id, Adapter = "" };
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) == 0 && store != null)
            {
                info.Name = ReadString(store, CoreAudioConst.PKEY_Device_FriendlyName) ?? id;
                info.Adapter = ReadString(store, CoreAudioConst.PKEY_DeviceInterface_FriendlyName) ?? "";
                info.Instance = ReadString(store, new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2)) ?? "";
                Marshal.ReleaseComObject(store);
            }
            return info;
        }

        static string ReadString(IPropertyStore store, PropertyKey key)
        {
            PropVariant pv;
            if (store.GetValue(ref key, out pv) != 0) return null;
            string s = null;
            if (pv.vt == 31 && pv.pointerValue != IntPtr.Zero) s = Marshal.PtrToStringUni(pv.pointerValue);
            NativeOle.PropVariantClear(ref pv);
            return s;
        }

        /// <summary>Endpoint state: 1 active, 2 disabled, 4 not present, 8 unplugged (0 = unknown).</summary>
        public static int State(string id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return 0;
                int s; return dev.GetState(out s) == 0 ? s : 0;
            }
            catch { return 0; }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(en); }
        }

        /// <summary>Enables/disables an endpoint, like Sound control panel > Disable (needs admin rights).</summary>
        public static bool SetEnabled(string id, bool enabled)
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try { return pc.SetEndpointVisibility(id, enabled ? 1 : 0) == 0; }
            catch { return false; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        static readonly PropertyKey KeyAdapter1 = new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);
        static readonly PropertyKey KeyAdapter2 = new PropertyKey(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
        static readonly PropertyKey KeyFriendly = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        static readonly PropertyKey KeyKsPath = new PropertyKey(new Guid("233164c8-1b2c-4c7d-bc68-b671687a2567"), 1);

        public static string GetString(string id, PropertyKey key)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; IPropertyStore store = null;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return null;
                if (dev.OpenPropertyStore(0, out store) != 0 || store == null) return null;
                PropVariant v;
                if (store.GetValue(ref key, out v) != 0 || v.vt != 31) return null;
                return Marshal.PtrToStringUni(v.pointerValue);
            }
            catch { return null; }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>The endpoint's kernel-streaming filter (e.g. \\?\root#media#0000#{...}\vbaudiovacwdm2022_out1_wave...).</summary>
        public static string KsFilterPath(string id)
        {
            string p = GetString(id, KeyKsPath);
            if (p == null) return null;
            int at = p.IndexOf(@"\\?\", StringComparison.Ordinal);
            return at >= 0 ? p.Substring(at) : null;
        }

        /// <summary>
        /// Renames the device (adapter) part of an endpoint's name: what Discord and others show as the main label
        /// ("VB-Audio Virtual Cable" -> "StreamLite Mic"). Needs admin rights; Windows may restore it after a driver update,
        /// so StreamLite re-applies it at start.
        /// </summary>
        public static bool RenameAdapter(string id, string adapter)
        {
            bool ok = SetString(id, KeyAdapter1, adapter);
            ok &= SetString(id, KeyAdapter2, adapter);
            string desc = GetString(id, new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2));
            if (desc != null) SetString(id, KeyFriendly, desc + " (" + adapter + ")");
            return ok;
        }

        public static string AdapterName(string id) { return GetString(id, KeyAdapter2) ?? GetString(id, KeyAdapter1); }

        static bool SetString(string id, PropertyKey key, string value)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; IPropertyStore store = null;
            IntPtr str = IntPtr.Zero;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return false;
                if (dev.OpenPropertyStore(2, out store) != 0 || store == null) return false;
                str = Marshal.StringToCoTaskMemUni(value);
                var pv = new PropVariant { vt = 31, pointerValue = str };
                if (store.SetValue(ref key, ref pv) != 0) return false;
                return store.Commit() == 0;
            }
            catch { return false; }
            finally
            {
                if (str != IntPtr.Zero) Marshal.FreeCoTaskMem(str);
                if (store != null) Marshal.ReleaseComObject(store);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>Renames an endpoint the way Sound control panel > Properties does (needs admin rights).</summary>
        public static bool Rename(string id, string description)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; IPropertyStore store = null;
            IntPtr str = IntPtr.Zero;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return false;
                if (dev.OpenPropertyStore(2, out store) != 0 || store == null) return false;   // STGM_READWRITE
                var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);   // PKEY_Device_DeviceDesc
                str = Marshal.StringToCoTaskMemUni(description);
                var pv = new PropVariant { vt = 31, pointerValue = str };
                if (store.SetValue(ref key, ref pv) != 0) return false;
                return store.Commit() == 0;
            }
            catch { return false; }
            finally
            {
                if (str != IntPtr.Zero) Marshal.FreeCoTaskMem(str);
                if (store != null) Marshal.ReleaseComObject(store);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>The device's Windows volume slider (0..1) and what it does in dB; false if unavailable.</summary>
        public static bool GetVolume(string id, out float level, out float db)
        {
            level = 1; db = 0;
            var v = EndpointVolume(id);
            if (v == null) return false;
            try { return v.GetMasterVolumeLevelScalar(out level) == 0 && v.GetMasterVolumeLevel(out db) == 0; }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static bool SetVolume(string id, float level)
        {
            var v = EndpointVolume(id);
            if (v == null) return false;
            try { Guid ctx = Guid.Empty; return v.SetMasterVolumeLevelScalar(level, ref ctx) == 0; }
            finally { Marshal.ReleaseComObject(v); }
        }

        /// <summary>
        /// Windows spatial sound switched on for an output (Sound settings > the device > Spatial sound: Windows Sonic,
        /// Dolby Atmos, DTS): how many audio objects games may send it. 0 = off (games send a channel mix), -1 = unknown.
        /// </summary>
        public static int SpatialObjects(string id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object o = null;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return -1;
                Guid iid = typeof(ISpatialAudioClient).GUID;
                if (dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o) != 0 || o == null) return 0;
                uint n;
                return ((ISpatialAudioClient)o).GetMaxDynamicObjectCount(out n) == 0 ? (int)n : 0;
            }
            catch { return -1; }
            finally
            {
                if (o != null) Marshal.ReleaseComObject(o);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
            }
        }

        /// <summary>Windows' own mute of a device (as the volume mixer shows it). null = unknown.</summary>
        public static bool? GetMute(string id)
        {
            var v = EndpointVolume(id);
            if (v == null) return null;
            try { bool m; return v.GetMute(out m) == 0 ? (bool?)m : null; }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static bool SetMute(string id, bool mute)
        {
            var v = EndpointVolume(id);
            if (v == null) return false;
            try { Guid ctx = Guid.Empty; return v.SetMute(mute, ref ctx) == 0; }
            finally { Marshal.ReleaseComObject(v); }
        }

        static IAudioEndpointVolume EndpointVolume(string id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null;
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                return dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o) == 0 ? o as IAudioEndpointVolume : null;
            }
            catch { return null; }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(en); }
        }

        public static bool LooksVirtual(AudioDeviceInfo d)
        {
            string s = (d.Name + " " + d.Adapter).ToLowerInvariant();
            return s.Contains("cable") || s.Contains("voicemeeter") || s.Contains("virtual") || s.Contains("streamlite")
                || (d.Instance ?? "").IndexOf("ROOT\\MEDIA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Same physical (or virtual) device: both ends of one cable. Uses the driver instance, so renames don't matter.</summary>
        public static bool SameDevice(AudioDeviceInfo a, AudioDeviceInfo b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.Instance) && !string.IsNullOrEmpty(b.Instance)) return a.Instance == b.Instance;
            return a.Adapter.Length > 0 && a.Adapter == b.Adapter;
        }

        public static string DeviceKey(AudioDeviceInfo d)
        {
            return !string.IsNullOrEmpty(d.Instance) ? d.Instance : d.Adapter;
        }
    }
}
