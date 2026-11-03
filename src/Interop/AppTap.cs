using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamLite.Interop
{
    /// <summary>
    /// One app's own sound for the listening EQ (4.2): Windows' per-app capture of the app (and the programs it started),
    /// 48 kHz stereo floats. Per-app capture keeps only a few packets, so a thread of its own takes each one as Windows
    /// signals it and keeps the last second; Read hands over what came since. It hears the app before any device
    /// effects - never StreamLite's own EQ. (Linux: linux/mono/Interop/AppTap.cs, the same members.)
    /// </summary>
    internal sealed class AppTap : IDisposable
    {
        IAudioClient client;
        IAudioCaptureClient cap;
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        readonly float[] ring = new float[2 * 48000];
        int head, count;
        readonly object gate = new object();
        volatile bool stop;
        Thread thread;

        public static AppTap Open(int pid)
        {
            var client = ProcessLoopback.Open(pid);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
            var t = new AppTap();
            try
            {
                int hr = client.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("per-app capture init failed (0x" + hr.ToString("X8") + ")", hr);
                object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("per-app capture service failed", hr);
                t.client = client; t.cap = (IAudioCaptureClient)svc;
                client.SetEventHandle(t.ready.SafeWaitHandle.DangerousGetHandle());
                client.Start();
                t.thread = new Thread(t.Pump) { IsBackground = true, Name = "StreamLite listening (per-app capture)" };
                t.thread.Start();
                return t;
            }
            catch { if (t.client == null) Marshal.ReleaseComObject(client); t.Dispose(); throw; }
            finally { Marshal.FreeHGlobal(fmt); }
        }

        void Pump()
        {
            var tmp = new float[2 * 4800];
            while (!stop)
            {
                ready.WaitOne(200);
                try
                {
                    uint packet;
                    if (cap == null || cap.GetNextPacketSize(out packet) != 0) continue;
                    while (packet > 0 && !stop)
                    {
                        IntPtr data; uint frames, flags; ulong p1, p2;
                        if (cap.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) break;
                        int n = (int)Math.Min(frames, (uint)(tmp.Length / 2));
                        if ((flags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0) Array.Clear(tmp, 0, n * 2);
                        else Marshal.Copy(data, tmp, 0, n * 2);
                        cap.ReleaseBuffer(frames);
                        lock (gate)
                            for (int i = 0; i < n * 2; i++)
                            {
                                ring[(head + count) % ring.Length] = tmp[i];
                                if (count < ring.Length) count++; else head = (head + 1) % ring.Length;
                            }
                        if (cap.GetNextPacketSize(out packet) != 0) break;
                    }
                }
                catch { }
            }
        }

        /// <summary>What the app played since the last read (up to a second), as interleaved stereo: frames.</summary>
        public int Read(float[] stereo)
        {
            lock (gate)
            {
                int n = Math.Min(count, stereo.Length) & ~1;
                for (int i = 0; i < n; i++) stereo[i] = ring[(head + i) % ring.Length];
                head = (head + n) % ring.Length; count -= n;
                return n / 2;
            }
        }

        public void Dispose()
        {
            stop = true;
            if (thread != null) thread.Join(1000);
            try { if (client != null) client.Stop(); } catch { }
            try { if (cap != null) Marshal.ReleaseComObject(cap); } catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
            cap = null; client = null;
        }
    }
}
