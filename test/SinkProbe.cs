using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Which render endpoints accept 7.1, and does "CABLE In 16ch" leak into "CABLE Output"?
class SinkProbe
{
    const int Fs = 48000;

    static IntPtr Fmt(int ch, int mask)
    {
        IntPtr p = Marshal.AllocHGlobal(40);
        var b = new byte[40];
        BitConverter.GetBytes((short)(ch > 2 ? -2 : 3)).CopyTo(b, 0);     // 0xFFFE extensible / 3 float
        BitConverter.GetBytes((short)ch).CopyTo(b, 2);
        BitConverter.GetBytes(Fs).CopyTo(b, 4);
        BitConverter.GetBytes(Fs * 4 * ch).CopyTo(b, 8);
        BitConverter.GetBytes((short)(4 * ch)).CopyTo(b, 12);
        BitConverter.GetBytes((short)32).CopyTo(b, 14);
        if (ch > 2)
        {
            BitConverter.GetBytes((short)22).CopyTo(b, 16);
            BitConverter.GetBytes((short)32).CopyTo(b, 18);
            BitConverter.GetBytes(mask).CopyTo(b, 20);
            new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(b, 24);
        }
        Marshal.Copy(b, 0, p, 40);
        return p;
    }

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static void Main()
    {
        string cable16 = null, cableOut = null;
        foreach (var d in AudioDevices.List(EDataFlow.Render))
        {
            var c = Open(d.Id);
            IntPtr mix; c.GetMixFormat(out mix);
            short ch = Marshal.ReadInt16(mix, 2); int sr = Marshal.ReadInt32(mix, 4);
            IntPtr f = Fmt(8, 0x63F), closest;
            int hr = c.IsFormatSupported(0, f, out closest);
            Console.WriteLine(d.Name + " | mix " + ch + "ch " + sr + " | 7.1 float shared: 0x" + hr.ToString("X8"));
            if (d.Name.Contains("16ch")) cable16 = d.Id;
        }
        foreach (var d in AudioDevices.List(EDataFlow.Capture)) if (d.Name.StartsWith("CABLE Output")) cableOut = d.Id;
        if (cable16 == null || cableOut == null) return;

        // Loopback-capture CABLE In 16ch at 8 ch, capture CABLE Output at 2 ch, while playing a 1 kHz tone into CABLE In 16ch (front left only).
        double loopPow = 0, outPow1k = 0, outPowRef = 0; int loopFrames = 0, loopCh = 0;
        var stop = false;
        var t1 = new Thread(() =>
        {
            var c = Open(cable16);
            IntPtr f = Fmt(2, 0);
            int hr = c.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 500000, 0, f, IntPtr.Zero);
            if (hr != 0) { Console.WriteLine("play init 0x" + hr.ToString("X8")); return; }
            uint buf; c.GetBufferSize(out buf);
            object svc; Guid iid = CoreAudioConst.IID_IAudioRenderClient; c.GetService(ref iid, out svc);
            var rc = (IAudioRenderClient)svc; c.Start();
            long n = 0; var tmp = new float[buf * 2];
            while (!stop)
            {
                uint pad; c.GetCurrentPadding(out pad); uint av = buf - pad;
                if (av > 0)
                {
                    IntPtr data; rc.GetBuffer(av, out data);
                    for (int i = 0; i < av; i++) { float v = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * (n + i) / Fs)); tmp[2 * i] = v; tmp[2 * i + 1] = 0; }
                    n += av; Marshal.Copy(tmp, 0, data, (int)av * 2); rc.ReleaseBuffer(av, 0);
                }
                Thread.Sleep(10);
            }
            c.Stop();
        });
        var t2 = new Thread(() =>
        {
            var c = Open(cable16);
            IntPtr mix; c.GetMixFormat(out mix);
            loopCh = Marshal.ReadInt16(mix, 2);
            int hr = c.Initialize(0, 0x00020000, 500000, 0, mix, IntPtr.Zero);
            if (hr != 0) { Console.WriteLine("loopback init 0x" + hr.ToString("X8")); return; }
            object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; c.GetService(ref iid, out svc);
            var cc = (IAudioCaptureClient)svc; c.Start();
            var tmp = new float[48000 * 8];
            while (!stop)
            {
                Thread.Sleep(10);
                uint pk; cc.GetNextPacketSize(out pk);
                while (pk > 0)
                {
                    IntPtr data; uint fr, fl; ulong a, b; cc.GetBuffer(out data, out fr, out fl, out a, out b);
                    Marshal.Copy(data, tmp, 0, (int)fr * loopCh);
                    for (int i = 0; i < fr; i++) loopPow += tmp[i * loopCh] * tmp[i * loopCh];
                    loopFrames += (int)fr; cc.ReleaseBuffer(fr); cc.GetNextPacketSize(out pk);
                }
            }
        });
        var t3 = new Thread(() =>
        {
            var c = Open(cableOut);
            IntPtr f = Fmt(2, 0);
            int hr = c.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 500000, 0, f, IntPtr.Zero);
            if (hr != 0) { Console.WriteLine("cable out init 0x" + hr.ToString("X8")); return; }
            object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; c.GetService(ref iid, out svc);
            var cc = (IAudioCaptureClient)svc; c.Start();
            var tmp = new float[48000 * 2]; long n = 0;
            double re1 = 0, im1 = 0, re2 = 0, im2 = 0;
            while (!stop)
            {
                Thread.Sleep(10);
                uint pk; cc.GetNextPacketSize(out pk);
                while (pk > 0)
                {
                    IntPtr data; uint fr, fl; ulong a, b; cc.GetBuffer(out data, out fr, out fl, out a, out b);
                    Marshal.Copy(data, tmp, 0, (int)fr * 2);
                    for (int i = 0; i < fr; i++, n++)
                    {
                        double v = tmp[2 * i];
                        re1 += v * Math.Cos(2 * Math.PI * 1000 * n / Fs); im1 += v * Math.Sin(2 * Math.PI * 1000 * n / Fs);
                        re2 += v * Math.Cos(2 * Math.PI * 1370 * n / Fs); im2 += v * Math.Sin(2 * Math.PI * 1370 * n / Fs);
                    }
                    cc.ReleaseBuffer(fr); cc.GetNextPacketSize(out pk);
                }
            }
            outPow1k = Math.Sqrt(re1 * re1 + im1 * im1) / Math.Max(1, n) * 2; outPowRef = Math.Sqrt(re2 * re2 + im2 * im2) / Math.Max(1, n) * 2;
        });
        t2.Start(); t3.Start(); Thread.Sleep(300); t1.Start();
        Thread.Sleep(2500); stop = true; t1.Join(); t2.Join(); t3.Join();
        Console.WriteLine("Loopback of CABLE In 16ch: " + loopCh + " ch, " + loopFrames + " frames, FL rms " + Math.Sqrt(loopPow / Math.Max(1, loopFrames)).ToString("0.000"));
        Console.WriteLine("CABLE Output 1 kHz amplitude " + outPow1k.ToString("0.0000") + " (reference bin " + outPowRef.ToString("0.0000") + ")");
    }
}
