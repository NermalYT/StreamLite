using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// One footstep of "Personalize 3D", as 3D audio would play a sound at that exact point: the head response for its
    /// direction from your head as it's turned right now (between the measured ones), quieter with distance, the floor
    /// cue (above / below) and cue sharpening of the setting being tried, plus your room setting.
    /// </summary>
    internal sealed class StepRenderer
    {
        const int Fs = SpatialProcessor.Fs;
        readonly float[][] bank = FootstepSynth.Bank(10, 23);

        public void Render(Building3D.Variant v, Building3D.Vec pos, int floor, double yaw, double pitch, double room, int step, out float[] left, out float[] right)
        {
            double az, el, dist;
            Building3D.Direction(pos, yaw, pitch, out az, out el, out dist);
            HrtfSet set;
            try { set = HrtfSet.Get(v.Hrtf, null, v.HeadSize / 100.0); } catch { set = HrtfSet.Get(HrtfSet.Ku100, null); }
            float[] hl, hr; set.BuildPrecise(az, el, room, out hl, out hr);
            var s = bank[step % bank.Length];
            float g = (float)(0.5 * 2.5 / Math.Max(1.2, dist));
            left = Convolve(s, hl, g); right = Convolve(s, hr, g);
            // The floor cue for where the steps are (not just the angle): what's being tuned.
            new FloorCues().Process(left, right, left.Length, floor == 2 ? 45f : floor == 0 ? -45f : 0f, v.Contrast / 100f);
            var post = new StereoPost();
            post.Configure(new SpatialConfig { CueSharpen = v.Cue / 100.0 }, false);
            // Sharpening works in short frames with a 4 ms delay: run the tail through too.
            int lat = post.Latency, n = left.Length;
            var l2 = new float[n + lat]; var r2 = new float[n + lat];
            Array.Copy(left, l2, n); Array.Copy(right, r2, n);
            post.Process(l2, r2, n + lat, null);
            left = new float[n]; right = new float[n];
            Array.Copy(l2, lat, left, 0, n); Array.Copy(r2, lat, right, 0, n);
            for (int i = 0; i < n; i++) { left[i] = Math.Max(-0.98f, Math.Min(0.98f, left[i])); right[i] = Math.Max(-0.98f, Math.Min(0.98f, right[i])); }
        }

        static float[] Convolve(float[] x, float[] h, float g)
        {
            int n = x.Length + h.Length - 1, size = 1;
            while (size < n) size <<= 1;
            var fft = new Fft(size);
            var ar = new float[size]; var ai = new float[size]; var br = new float[size]; var bi = new float[size];
            Array.Copy(x, ar, x.Length); Array.Copy(h, br, h.Length);
            fft.Transform(ar, ai, false); fft.Transform(br, bi, false);
            for (int k = 0; k < size; k++) { float re = ar[k] * br[k] - ai[k] * bi[k], im = ar[k] * bi[k] + ai[k] * br[k]; ar[k] = re; ai[k] = im; }
            fft.Transform(ar, ai, true);
            var o = new float[n]; float sc = g / size;
            for (int i = 0; i < n; i++) o[i] = ar[i] * sc;
            return o;
        }
    }

    /// <summary>
    /// Plays a timeline to the headphones: sounds are added at a frame position ahead of what's playing, and it streams
    /// in 10 ms steps - through 3D audio's exclusive stream while it holds the headphones, otherwise a normal stream.
    /// </summary>
    internal sealed class StereoTimeline : IDisposable
    {
        const int Size = 1 << 18, Mask = Size - 1, Fs = 48000;
        readonly float[] tl = new float[Size], tr = new float[Size];
        readonly object gate = new object();
        long played;
        volatile bool stop;
        readonly Thread worker;
        public volatile string Error;
        /// <summary>Frames handed to the headphones so far (a sound added at Now + 2400 plays ~50 ms from now).</summary>
        public long Now { get { lock (gate) return played; } }

        public StereoTimeline(string deviceId)
        {
            worker = new Thread(() => { try { Run(deviceId); } catch (Exception ex) { Error = ex.Message; } }) { IsBackground = true, Name = "Personalize 3D audio", Priority = ThreadPriority.AboveNormal };
            worker.Start();
        }

        public void Add(float[] l, float[] r, long at)
        {
            lock (gate)
            {
                if (at < played) at = played;
                for (int i = 0; i < l.Length; i++) { int k = (int)((at + i) & Mask); tl[k] += l[i]; tr[k] += r[i]; }
            }
        }

        /// <summary>Silence from now on (what was scheduled is dropped).</summary>
        public void Clear() { lock (gate) { Array.Clear(tl, 0, Size); Array.Clear(tr, 0, Size); } }

        void Take(float[] l, float[] r, int n)
        {
            lock (gate)
            {
                for (int i = 0; i < n; i++) { int k = (int)((played + i) & Mask); l[i] = tl[k]; r[i] = tr[k]; tl[k] = 0; tr[k] = 0; }
                played += n;
            }
        }

        void Run(string id)
        {
            var l = new float[480]; var r = new float[480];
            var direct = HeadphonesDirect.Open(id);
            if (direct != null)
            {
                try
                {
                    while (!stop)
                    {
                        if (!HeadphonesDirect.WaitRoom(direct, () => stop)) { if (stop) break; Thread.Sleep(20); continue; }
                        Take(l, r, 480); direct.Write(l, r, 480); direct.InputArrived();
                    }
                }
                finally { HeadphonesDirect.Close(direct); }
                return;
            }
            IMMDevice dev;
            var ac = AudioEngine.OpenClient(id, out dev);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
            object svc = null; var ev = new AutoResetEvent(false);
            try
            {
                int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 400000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Couldn't play on your headphones (0x" + hr.ToString("X8") + ")", hr);
                ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint size; ac.GetBufferSize(out size);
                Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
                var rc = (IAudioRenderClient)svc;
                var inter = new float[size * 2]; var bl = new float[size]; var br = new float[size];
                ac.Start();
                while (!stop)
                {
                    ev.WaitOne(50);
                    uint pad; if (ac.GetCurrentPadding(out pad) != 0) break;
                    int n = (int)(size - pad);
                    if (n <= 0) continue;
                    IntPtr data; if (rc.GetBuffer((uint)n, out data) != 0) continue;
                    Take(bl, br, n);
                    for (int i = 0; i < n; i++) { inter[2 * i] = bl[i]; inter[2 * i + 1] = br[i]; }
                    Marshal.Copy(inter, 0, data, n * 2); rc.ReleaseBuffer((uint)n, 0);
                }
                ac.Stop();
            }
            finally { Marshal.FreeHGlobal(fmt); if (svc != null) Marshal.ReleaseComObject(svc); Marshal.ReleaseComObject(ac); Marshal.ReleaseComObject(dev); ev.Dispose(); }
        }

        public void Dispose() { stop = true; worker.Join(1500); }
    }
}
