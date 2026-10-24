using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    /// <summary>The Media Foundation pieces StreamLite uses to read cameras (source reader) - only what's needed.</summary>
    internal static class MF
    {
        public static readonly Guid DevSourceType = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        public static readonly Guid DevSourceVidcap = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        public static readonly Guid DevFriendlyName = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        public static readonly Guid DevSymbolicLink = new Guid("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        public static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid SubType = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        public static readonly Guid NV12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
        public static readonly Guid MJPG = new Guid("47504a4d-0000-0010-8000-00aa00389b71");
        public static readonly Guid YUY2 = new Guid("32595559-0000-0010-8000-00aa00389b71");
        public static readonly Guid RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        public static readonly Guid EnableVideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid EnableAdvancedVideoProcessing = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        public static readonly Guid EnableHardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid LowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
        public const int AllStreams = unchecked((int)0xFFFFFFFE);

        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mf.dll")] public static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activates, out int count);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromMediaSource([MarshalAs(UnmanagedType.IUnknown)] object source, IMFAttributes attributes, out IMFSourceReader reader);

        static bool started;
        public static void Startup() { if (!started) { MFStartup(0x00020070, 0); started = true; } }

        public static ulong Pack(int hi, int lo) { return ((ulong)(uint)hi << 32) | (uint)lo; }
        public static void Unpack(ulong v, out int hi, out int lo) { hi = (int)(v >> 32); lo = (int)(v & 0xFFFFFFFF); }

        /// <summary>Video capture devices as (friendly name, symbolic link, activate object).</summary>
        public static List<Tuple<string, string, IMFActivate>> Cameras()
        {
            Startup();
            var list = new List<Tuple<string, string, IMFActivate>>();
            IMFAttributes a; MFCreateAttributes(out a, 1);
            a.SetGUID(DevSourceType, DevSourceVidcap);
            IntPtr arr; int n;
            if (MFEnumDeviceSources(a, out arr, out n) != 0) return list;
            for (int i = 0; i < n; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                var act = (IMFActivate)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
                list.Add(Tuple.Create(GetString(act, DevFriendlyName), GetString(act, DevSymbolicLink), act));
            }
            Marshal.FreeCoTaskMem(arr);
            Marshal.ReleaseComObject(a);
            return list;
        }

        public static string GetString(IMFAttributes a, Guid key)
        {
            IntPtr s; int len;
            if (a.GetAllocatedString(key, out s, out len) != 0) return null;
            string r = Marshal.PtrToStringUni(s, len);
            Marshal.FreeCoTaskMem(s);
            return r;
        }
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, int size, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buf, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    internal static class AttrExt
    {
        public static int SetGUID(this IMFAttributes a, Guid k, Guid v) { return a.SetGUID(ref k, ref v); }
        public static int SetUINT32(this IMFAttributes a, Guid k, int v) { return a.SetUINT32(ref k, v); }
        public static int SetUINT64(this IMFAttributes a, Guid k, ulong v) { return a.SetUINT64(ref k, v); }
        public static int GetAllocatedString(this IMFAttributes a, Guid k, out IntPtr s, out int len) { return a.GetAllocatedString(ref k, out s, out len); }
        public static Guid GetGUID(this IMFAttributes a, Guid k) { Guid v; return a.GetGUID(ref k, out v) == 0 ? v : Guid.Empty; }
        public static ulong GetUINT64(this IMFAttributes a, Guid k) { ulong v; return a.GetUINT64(ref k, out v) == 0 ? v : 0; }
        public static int GetUINT32(this IMFAttributes a, Guid k, int def) { int v; return a.GetUINT32(ref k, out v) == 0 ? v : def; }
    }

    [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFActivate : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, IntPtr buf, int size, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, IntPtr buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        [PreserveSig] int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, IntPtr buf, int size, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buf, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, IntPtr buf, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes dest);
        [PreserveSig] int GetMajorType(out Guid type);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
        [PreserveSig] int GetRepresentation(Guid rep, out IntPtr ptr);
        [PreserveSig] int FreeRepresentation(Guid rep, IntPtr ptr);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
        [PreserveSig] int IsContiguousFormat(out bool contiguous);
        [PreserveSig] int GetContiguousLength(out int length);
        [PreserveSig] int ContiguousCopyTo(IntPtr dest, int destLength);
        [PreserveSig] int ContiguousCopyFrom(IntPtr src, int srcLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // IMFAttributes (30) - not used, placeholders keep the vtable right.
        void A0(); void A1(); void A2(); void A3(); void A4(); void A5(); void A6(); void A7(); void A8(); void A9();
        void A10(); void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19();
        void A20(); void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29();
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int stream, out bool selected);
        [PreserveSig] int SetStreamSelection(int stream, bool selected);
        [PreserveSig] int GetNativeMediaType(int stream, int index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
        [PreserveSig] int ReadSample(int stream, int flags, out int actualStream, out int streamFlags, out long timestamp, out IMFSample sample);
        [PreserveSig] int Flush(int stream);
        [PreserveSig] int GetServiceForStream(int stream, ref Guid service, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPresentationAttribute(int stream, ref Guid attr, IntPtr value);
    }
}
