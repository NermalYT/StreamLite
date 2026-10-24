using System;
using System.Runtime.InteropServices;
using StreamLite.Interop;

[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient3
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
    // IAudioClient2
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(IntPtr props);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
    // IAudioClient3
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint def, out uint fundamental, out uint min, out uint max);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint period);
    [PreserveSig] int InitializeSharedAudioStream(int flags, uint period, IntPtr format, IntPtr session);
}

class PeriodProbe
{
    static void Main()
    {
        foreach (var d in AudioDevices.List(EDataFlow.Render))
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev; en.GetDevice(d.Id, out dev);
            object o; Guid iid = new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
            int hr = dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
            if (hr != 0) { Console.WriteLine(d.Name + ": no IAudioClient3"); continue; }
            var c = (IAudioClient3)o;
            IntPtr mix; c.GetMixFormat(out mix);
            int rate = Marshal.ReadInt32(mix, 4);
            uint def, fund, min, max;
            hr = c.GetSharedModeEnginePeriod(mix, out def, out fund, out min, out max);
            IntPtr cf; uint cur; c.GetCurrentSharedModeEnginePeriod(out cf, out cur);
            Console.WriteLine(string.Format("{0}: rate {1}, engine period default {2} min {3} max {4} fundamental {5} current {6} frames ({7:0.00} ms min)", d.Name, rate, def, min, max, fund, cur, min * 1000.0 / rate));
        }
    }
}
