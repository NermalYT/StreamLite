using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamLite.Interop
{
    /// <summary>A device's "Default Format" (Sound settings > device > Format): what Windows mixes and sends to the hardware.</summary>
    internal struct DeviceFormat
    {
        public int Channels, Mask, Rate, Container, Valid;
        public bool Float;

        public override string ToString()
        {
            return Channels + " ch, " + Valid + " bit, " + Rate + " Hz";
        }

        public bool SameAs(DeviceFormat o)
        {
            return Channels == o.Channels && Rate == o.Rate && Valid == o.Valid && Container == o.Container && Float == o.Float && (Mask == o.Mask || Mask == 0 || o.Mask == 0);
        }
    }

    /// <summary>
    /// Picks and applies the best format a device actually runs. Windows lists every format the driver claims, including
    /// ones nothing produces (16 channels on a stereo cable, 384 kHz): those only add resampling, CPU and, for channels
    /// nobody fills, quieter downmixes. Everything StreamLite, games, Discord (Opus) and browsers produce is 48 kHz, so
    /// the best working format is the device's highest native bit depth at 48 kHz (44.1 kHz or the nearest rate if it
    /// has no 48 kHz), checked by running a real stream at it before it is kept.
    /// </summary>
    internal static class DeviceFormats
    {
        const int AUDCLNT_SHAREMODE_EXCLUSIVE = 1;
        static readonly int[] Rates = { 48000, 44100, 96000, 88200, 192000, 176400, 32000, 24000, 22050, 16000, 11025, 8000 };

        public static bool Get(string id, out DeviceFormat f)
        {
            f = new DeviceFormat();
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                IntPtr p;
                if (pc.GetDeviceFormat(id, 0, out p) != 0 || p == IntPtr.Zero) return false;
                f = Read(p);
                Marshal.FreeCoTaskMem(p);
                return f.Rate > 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(pc); }
        }

        static DeviceFormat Read(IntPtr p)
        {
            short tag = Marshal.ReadInt16(p, 0);
            var f = new DeviceFormat { Channels = Marshal.ReadInt16(p, 2), Rate = Marshal.ReadInt32(p, 4), Container = Marshal.ReadInt16(p, 14) };
            f.Valid = tag == -2 ? Marshal.ReadInt16(p, 18) : f.Container;
            if (f.Valid == 0) f.Valid = f.Container;
            f.Mask = tag == -2 ? Marshal.ReadInt32(p, 20) : f.Channels == 2 ? 3 : f.Channels == 1 ? 4 : 0;
            f.Float = tag == 3 || (tag == -2 && Marshal.ReadInt16(p, 24) == 3);
            return f;
        }

        static IntPtr Make(DeviceFormat f)
        {
            IntPtr p = AudioDevices.MakeFormat(f.Channels, f.Mask, f.Rate, f.Float, f.Container);
            Marshal.WriteInt16(p, 18, (short)f.Valid);
            return p;
        }

        static IAudioClient Client(string id, out IMMDevice dev)
        {
            dev = null;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) return null;
                Guid iid = CoreAudioConst.IID_IAudioClient; object o;
                if (dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o) != 0) return null;
                return (IAudioClient)o;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>The hardware takes this format natively (the same test Windows uses to fill the Format list).</summary>
        public static bool Supported(string id, DeviceFormat f)
        {
            IMMDevice dev; var ac = Client(id, out dev);
            if (ac == null) return false;
            IntPtr p = Make(f), closest;
            try { return ac.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, p, out closest) == 0; }
            catch { return false; }
            finally { Marshal.FreeHGlobal(p); Marshal.ReleaseComObject(ac); if (dev != null) Marshal.ReleaseComObject(dev); }
        }

        /// <summary>Highest native bit depth at the given channel layout, at 48 kHz or the nearest rate the device has.</summary>
        public static DeviceFormat? Best(string id, int channels, int mask)
        {
            foreach (int rate in Rates)
                foreach (var b in new[] { new[] { 32, 32 }, new[] { 24, 24 }, new[] { 32, 24 }, new[] { 16, 16 } })   // container, valid bits
                {
                    var f = new DeviceFormat { Channels = channels, Mask = mask, Rate = rate, Container = b[0], Valid = b[1] };
                    if (Supported(id, f)) return f;
                }
            return null;
        }

        public static int Set(string id, DeviceFormat f)
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            IntPtr ep = Make(f), mix = AudioDevices.MakeFormat(f.Channels, f.Mask, f.Rate, true, 32);
            try { return pc.SetDeviceFormat(id, ep, mix); }
            catch (Exception ex) { return ex.HResult; }
            finally { Marshal.FreeHGlobal(ep); Marshal.FreeHGlobal(mix); Marshal.ReleaseComObject(pc); }
        }

        /// <summary>
        /// Runs a short shared-mode stream (silence for outputs) and checks the device really moves audio at the rate:
        /// drivers that list a format but stall, drift or refuse it fail here. Returns null when it works.
        /// </summary>
        public static string Verify(string id, bool capture, int rate, int ms = 500)
        {
            IMMDevice dev = null; IAudioClient ac = null; object svc = null;
            IntPtr mix = IntPtr.Zero;
            try
            {
                ac = Client(id, out dev);
                if (ac == null) return "device not available";
                if (ac.GetMixFormat(out mix) != 0) return "no mix format";
                int mixRate = Marshal.ReadInt32(mix, 4);
                if (mixRate != rate) return "Windows kept " + mixRate + " Hz";
                int hr = ac.Initialize(0, 0, 2000000, 0, mix, IntPtr.Zero);
                if (hr != 0) return "stream refused (0x" + hr.ToString("X8") + ")";
                Guid iid = capture ? CoreAudioConst.IID_IAudioCaptureClient : CoreAudioConst.IID_IAudioRenderClient;
                if (ac.GetService(ref iid, out svc) != 0) return "no stream service";
                uint size; ac.GetBufferSize(out size);
                long frames = 0, written = 0;
                IntPtr data;
                if (!capture)
                {
                    var rc = (IAudioRenderClient)svc;
                    if (rc.GetBuffer(size, out data) == 0) { rc.ReleaseBuffer(size, CoreAudioConst.BUFFERFLAGS_SILENT); written = size; }
                }
                var sw = Stopwatch.StartNew();
                if (ac.Start() != 0) return "stream would not start";
                while (sw.ElapsedMilliseconds < ms)
                {
                    Thread.Sleep(10);
                    if (capture)
                    {
                        var cc = (IAudioCaptureClient)svc; uint p;
                        while (cc.GetNextPacketSize(out p) == 0 && p > 0)
                        {
                            uint n, fl; ulong a, b;
                            if (cc.GetBuffer(out data, out n, out fl, out a, out b) != 0) break;
                            frames += n; cc.ReleaseBuffer(n);
                        }
                    }
                    else
                    {
                        var rc = (IAudioRenderClient)svc; uint pad;
                        if (ac.GetCurrentPadding(out pad) != 0) return "stream stopped";
                        uint free = size - pad;
                        if (free > 0 && rc.GetBuffer(free, out data) == 0) { rc.ReleaseBuffer(free, CoreAudioConst.BUFFERFLAGS_SILENT); written += free; }
                    }
                }
                double secs = sw.Elapsed.TotalSeconds;
                if (!capture) { uint pad; ac.GetCurrentPadding(out pad); frames = written - pad; }
                ac.Stop();
                double ratio = frames / (rate * secs);
                // Capture starts a period late and render plays out a pre-filled buffer, so allow a wide window: a stalled
                // or wrong-rate device lands far outside it.
                if (ratio < 0.6 || ratio > 1.6) return "ran at " + (ratio * rate).ToString("0") + " Hz instead of " + rate;
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
                if (svc != null) Marshal.ReleaseComObject(svc);
                if (ac != null) Marshal.ReleaseComObject(ac);
                if (dev != null) Marshal.ReleaseComObject(dev);
            }
        }

        /// <summary>
        /// Switches the device to <paramref name="want"/> unless it already runs it; with <paramref name="verify"/> a real
        /// stream must run at the new format, otherwise the previous one is put back. Returns what happened, or null if
        /// nothing needed doing.
        /// </summary>
        public static string Apply(string id, bool capture, DeviceFormat want, bool verify)
        {
            DeviceFormat cur;
            bool had = Get(id, out cur);
            if (had && cur.SameAs(want)) return null;
            int hr = Set(id, want);
            if (hr != 0) return "kept " + (had ? cur.ToString() : "its format") + " (Windows refused " + want + ": 0x" + hr.ToString("X8") + ")";
            if (verify)
            {
                Thread.Sleep(300);   // the audio engine restarts the endpoint
                string why = Verify(id, capture, want.Rate);
                if (why != null)
                {
                    if (had) Set(id, cur);
                    return "kept " + (had ? cur.ToString() : "its format") + " (" + want + " didn't work: " + why + ")";
                }
            }
            return (had ? cur + " -> " : "") + want;
        }

        /// <summary>Bluetooth endpoints: the codec decides the format, and opening a headset mic switches it to call quality.</summary>
        public static bool IsBluetooth(AudioDeviceInfo d)
        {
            return (d.Instance ?? "").IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0
                || (d.Id ?? "").IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
