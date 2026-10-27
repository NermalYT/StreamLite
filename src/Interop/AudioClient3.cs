using System;
using System.Runtime.InteropServices;

namespace StreamLite.Interop
{
    /// <summary>
    /// IAudioClient3 (Windows 10+): shared-mode streams with a smaller engine period than the usual 10 ms, when the
    /// driver supports it - the low-latency path for the mic. Same vtable as IAudioClient, then IAudioClient2, then 3.
    /// </summary>
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient3
    {
        // IAudioClient
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
        // IAudioClient2
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
        // IAudioClient3
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultFrames, out uint fundamentalFrames, out uint minFrames, out uint maxFrames);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentFrames);
        [PreserveSig] int InitializeSharedAudioStream(int streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
    }

    /// <summary>IAudioClient2 (Windows 8.1+): the stream's properties - here, raw mode for the mic.</summary>
    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient2
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
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
    }

    /// <summary>
    /// The mic's raw sound (4.0.5): Windows runs effects on a microphone before any app gets it - its own enhancements and
    /// whatever the mic's maker installed (Logitech G HUB's Blue VO!CE on a Yeti: noise reduction, gate, compressor),
    /// also Equalizer APO. StreamLite cleans the mic itself, so it asks for the raw stream, which skips all of them -
    /// when the device offers one (most do). Exclusive streams are raw anyway.
    /// </summary>
    internal static class RawCapture
    {
        /// <summary>Raw streams wanted (the Microphone page's "Raw mic" switch).</summary>
        public static volatile bool Enabled = true;
        static readonly PropertyKey RawSupported = new PropertyKey(new Guid("8943b373-388c-4395-b557-bc6dbaffafdb"), 2);   // PKEY_Devices_AudioDevice_RawProcessingSupported

        /// <summary>The device offers a raw stream.</summary>
        public static bool Supported(string deviceId) { return AudioDevices.GetBool(deviceId, RawSupported) == true; }

        /// <summary>Asks for the raw stream, before Initialize. true = it will be raw.</summary>
        public static bool Request(object client, string deviceId)
        {
            if (!Enabled || !Supported(deviceId)) return false;
            var c2 = client as IAudioClient2;
            if (c2 == null) return false;
            // AudioClientProperties: size, not offloaded, category Other, options RAW.
            IntPtr p = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.WriteInt32(p, 0, 16); Marshal.WriteInt32(p, 4, 0); Marshal.WriteInt32(p, 8, 0); Marshal.WriteInt32(p, 12, 1);
                return c2.SetClientProperties(p) == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(p); }
        }
    }
}
