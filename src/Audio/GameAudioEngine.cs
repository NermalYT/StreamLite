using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// Game 3D audio: games play into a virtual 7.1 device; this records that device's output (WASAPI loopback,
    /// all channels), runs the spatial processor, and plays the binaural result on the headphones.
    /// </summary>
    internal sealed class GameAudioEngine
    {
        public readonly SpatialProcessor Processor = new SpatialProcessor();
        public volatile string Status = "Off";
        public volatile bool Running;
        public volatile int InputChannels, InputMask;
        public volatile float CpuPercent, LatencyMs;
        public volatile int Underruns;
        /// <summary>Smaller buffers: ~8 ms less delay, but may crackle on a busy PC.</summary>
        public volatile bool LowLatency;

        string sinkId, phonesId;
        Thread capThread, renThread;
        volatile bool stop;
        StereoRing ring;
        /// <summary>Times game sound ran dry on the way to the headphones while the game was still playing (status.txt).</summary>
        public int Dropouts { get { var r = ring; return r != null ? r.Underruns : 0; } }
        /// <summary>Margin to start with (learned in an earlier run, so it isn't re-learned with dropouts each start).</summary>
        public int StartMargin;
        public int LearnedMargin { get { var r = ring; return r != null ? r.Margin : 0; } }
        long lastInputTicks;

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        public void Start(string sink, string headphones)
        {
            Stop();
            sinkId = sink; phonesId = headphones;
            if (string.IsNullOrEmpty(phonesId)) { Status = "Choose your headphones."; return; }
            stop = false; Running = true;
            ring = new StereoRing(LowLatency ? 48 : 144, StartMargin);
            timeBeginPeriod(1);
            capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "3D audio in", Priority = ThreadPriority.Highest };
            renThread = new Thread(RenderLoop) { IsBackground = true, Name = "3D audio out", Priority = ThreadPriority.Highest };
            capThread.Start(); renThread.Start();
        }

        public void Stop()
        {
            if (!Running) return;
            stop = true;
            if (capThread != null) capThread.Join(1500);
            if (renThread != null) renThread.Join(1500);
            capThread = renThread = null;
            timeEndPeriod(1);
            Running = false;
            InputChannels = 0;
            Status = "Off";
        }

        static IAudioClient Open(string id, out IMMDevice dev)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) throw new InvalidOperationException("Device not found (unplugged?)");
                object o; Guid iid = CoreAudioConst.IID_IAudioClient;
                int hr = dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
                if (hr != 0) throw new COMException("Could not open device", hr);
                return (IAudioClient)o;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        void CaptureLoop()
        {
            if (string.IsNullOrEmpty(sinkId)) { RunIdle(); return; }
            while (!stop)
            {
                try { RunCapture(); }
                catch (Exception ex) { Status = "Game audio device: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        /// <summary>No game device yet: still run in real time so the test sound and radar work.</summary>
        void RunIdle()
        {
            Status = "No game audio device - test sound only";
            const int B = SpatialProcessor.B;
            var slots = new float[SpatialProcessor.Slots][];
            for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var outL = new float[B]; var outR = new float[B];
            var sw = Stopwatch.StartNew();
            long next = 0, step = (long)(B * (double)Stopwatch.Frequency / SpatialProcessor.Fs);
            while (!stop)
            {
                Thread.Sleep(2);
                while (sw.ElapsedTicks >= next)
                {
                    foreach (var s in slots) Array.Clear(s, 0, B);
                    Processor.Process(slots, Processor.TestRunning ? SpatialProcessor.Source.Surround71 : SpatialProcessor.Source.Silent, outL, outR);
                    ring.Write(outL, outR, B);
                    next += step;
                }
            }
        }

        static SpatialProcessor.Source Classify(int ch, int mask, out int[] map)
        {
            // map[inputChannel] = slot (or -1); bits follow the WAVE channel-mask order.
            map = new int[ch];
            int[] bitSlot = { SpatialProcessor.FL, SpatialProcessor.FR, SpatialProcessor.FC, SpatialProcessor.LFE, SpatialProcessor.BL, SpatialProcessor.BR,
                              SpatialProcessor.FL, SpatialProcessor.FR, SpatialProcessor.BL, SpatialProcessor.SL, SpatialProcessor.SR,
                              SpatialProcessor.FC, SpatialProcessor.FL, SpatialProcessor.FC, SpatialProcessor.FR, SpatialProcessor.BL, SpatialProcessor.BL, SpatialProcessor.BR };
            bool hasSide = (mask & 0x600) != 0, hasBack = (mask & 0x30) != 0;
            int c = 0;
            for (int bit = 0; bit < 18 && c < ch; bit++)
            {
                if ((mask & (1 << bit)) == 0) continue;
                int slot = bitSlot[bit];
                // 5.1 with side surrounds: put them in the surround (BL/BR) slots.
                if (hasSide && !hasBack && slot == SpatialProcessor.SL) slot = SpatialProcessor.BL;
                if (hasSide && !hasBack && slot == SpatialProcessor.SR) slot = SpatialProcessor.BR;
                map[c++] = slot;
            }
            for (; c < ch; c++) map[c] = -1;
            if (ch == 1) { map[0] = -2; return SpatialProcessor.Source.Stereo; }          // mono -> both
            if (ch == 2 || (mask & ~0x7) == 0 && ch <= 3) return SpatialProcessor.Source.Stereo;
            return hasSide && hasBack ? SpatialProcessor.Source.Surround71 : SpatialProcessor.Source.Surround51;
        }

        void RunCapture()
        {
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(sinkId, out ch, out mask, out rate)) throw new InvalidOperationException("Device not found");
            IMMDevice dev;
            var client = Open(sinkId, out dev);
            IntPtr fmt = AudioDevices.MakeFormat(ch, mask, SpatialProcessor.Fs, true, 32);
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                const int LOOPBACK = 0x00020000;
                int flags = LOOPBACK | CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                bool events = hr == 0;
                if (!events)
                {
                    Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                    client = Open(sinkId, out dev);
                    hr = client.Initialize(0, LOOPBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 200000, 0, fmt, IntPtr.Zero);
                }
                if (hr != 0) throw new COMException("Could not listen to the game audio device (0x" + hr.ToString("X8") + ")", hr);
                if (events) client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Capture service failed", hr);
                var cap = (IAudioCaptureClient)svc;
                int[] map;
                var deviceSrc = Classify(ch, mask, out map);
                // The game device is 7.1, but browsers, videos and stereo games only fill front left/right. Treat
                // content as stereo until a surround channel actually carries sound (then stay surround for 20 s), so
                // stereo keeps its full left/right separation and the stereo settings apply.
                var src = deviceSrc == SpatialProcessor.Source.Stereo ? deviceSrc : SpatialProcessor.Source.Stereo;
                int surroundHold = 0, holdBlocks = 20 * SpatialProcessor.Fs / SpatialProcessor.B;
                InputChannels = ch; InputMask = mask;
                hr = client.Start();
                if (hr != 0) throw new COMException("Start failed", hr);
                Status = "Running";

                const int B = SpatialProcessor.B;
                var slots = new float[SpatialProcessor.Slots][];
                for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
                var outL = new float[B]; var outR = new float[B];
                var buf = new float[SpatialProcessor.Fs * ch];
                int fill = 0;
                var sw = Stopwatch.StartNew();
                double busy = 0; long window = sw.ElapsedTicks;
                long lastBlock = sw.ElapsedTicks;
                while (!stop)
                {
                    if (events) ev.WaitOne(20); else Thread.Sleep(2);
                    uint packet;
                    hr = cap.GetNextPacketSize(out packet);
                    if (hr != 0) throw new COMException("Game audio device disconnected", hr);
                    bool got = false, sound = false;
                    while (packet > 0 && !stop)
                    {
                        IntPtr data; uint frames, bflags; ulong p1, p2;
                        hr = cap.GetBuffer(out data, out frames, out bflags, out p1, out p2);
                        if (hr != 0) throw new COMException("Read failed", hr);
                        int n = (int)Math.Min(frames, (uint)(buf.Length / ch));
                        if ((bflags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0) Array.Clear(buf, 0, n * ch);
                        else { Marshal.Copy(data, buf, 0, n * ch); sound = true; }
                        cap.ReleaseBuffer(frames);
                        got = true;
                        for (int f = 0; f < n; f++)
                        {
                            int o = f * ch;
                            for (int c = 0; c < ch; c++)
                            {
                                int s = map[c];
                                if (s >= 0) slots[s][fill] += buf[o + c];
                                else if (s == -2) { slots[SpatialProcessor.FL][fill] = buf[o]; slots[SpatialProcessor.FR][fill] = buf[o]; }
                            }
                            if (++fill == B)
                            {
                                if (deviceSrc != SpatialProcessor.Source.Stereo)
                                {
                                    float pk = 0;
                                    for (int s = 0; s < slots.Length; s++)
                                    {
                                        if (s == SpatialProcessor.FL || s == SpatialProcessor.FR) continue;
                                        var sl = slots[s];
                                        for (int i = 0; i < B; i++) { float a = Math.Abs(sl[i]); if (a > pk) pk = a; }
                                    }
                                    if (pk > 1e-4f) surroundHold = holdBlocks;            // above -80 dBFS
                                    else if (surroundHold > 0) surroundHold--;
                                    src = surroundHold > 0 ? deviceSrc : SpatialProcessor.Source.Stereo;
                                }
                                long t0 = sw.ElapsedTicks;
                                Processor.Process(slots, src, outL, outR);
                                busy += sw.ElapsedTicks - t0;
                                ring.Write(outL, outR, B);
                                for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, B);
                                fill = 0;
                                lastBlock = sw.ElapsedTicks;
                            }
                        }
                        hr = cap.GetNextPacketSize(out packet);
                        if (hr != 0) throw new COMException("Game audio device disconnected", hr);
                    }
                    // Only real sound counts for the dropout check: when a game's stream stops, Windows still hands out
                    // a few stray silent packets, which used to look like dropouts and grow the margin for nothing.
                    if (got) { lastInputTicks = DateTime.UtcNow.Ticks; if (sound) ring.InputArrived(); }
                    // Nothing playing (loopback goes quiet): keep the test sound and reverb tails running in real time.
                    double sinceBlock = (sw.ElapsedTicks - lastBlock) * 1000.0 / Stopwatch.Frequency;
                    bool recent = DateTime.UtcNow.Ticks - lastInputTicks < 15 * TimeSpan.TicksPerSecond / 10;
                    if (!got && !recent && !Processor.TestRunning) lastBlock = sw.ElapsedTicks;   // idle: nothing to render, save CPU
                    else if (!got && sinceBlock > 25)
                    {
                        while ((sw.ElapsedTicks - lastBlock) * 1000.0 / Stopwatch.Frequency >= B * 1000.0 / SpatialProcessor.Fs)
                        {
                            for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, B);
                            fill = 0;
                            long t0 = sw.ElapsedTicks;
                            Processor.Process(slots, Processor.TestRunning ? SpatialProcessor.Source.Surround71 : src, outL, outR);
                            busy += sw.ElapsedTicks - t0;
                            ring.Write(outL, outR, B);
                            lastBlock += (long)(B * (double)Stopwatch.Frequency / SpatialProcessor.Fs);
                        }
                    }
                    long now = sw.ElapsedTicks;
                    if (now - window > Stopwatch.Frequency)
                    {
                        CpuPercent = (float)(100 * busy / (now - window));
                        busy = 0; window = now;
                        bool idle = DateTime.UtcNow.Ticks - lastInputTicks > TimeSpan.TicksPerSecond;
                        Status = idle ? "Waiting for game audio" : "Running";
                    }
                }
                client.Stop();
            }
            finally
            {
                Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
                ev.Dispose();
            }
        }

        void RenderLoop()
        {
            while (!stop)
            {
                try { RunRender(); }
                catch (Exception ex) { Status = "Headphones: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        void RunRender()
        {
            IMMDevice dev;
            var client = Open(phonesId, out dev);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, SpatialProcessor.Fs, true, 32);
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Headphones init failed (0x" + hr.ToString("X8") + ")", hr);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint bufferFrames;
                client.GetBufferSize(out bufferFrames);
                long devPeriod, minPeriod;
                client.GetDevicePeriod(out devPeriod, out minPeriod);
                Guid iid = CoreAudioConst.IID_IAudioRenderClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Headphones service failed", hr);
                var rc = (IAudioRenderClient)svc;
                IntPtr data;
                if (rc.GetBuffer(bufferFrames, out data) == 0) rc.ReleaseBuffer(bufferFrames, CoreAudioConst.BUFFERFLAGS_SILENT);
                ring.Clear();
                hr = client.Start();
                if (hr != 0) throw new COMException("Headphones start failed", hr);
                // Queue 1.5 device periods (safe) or one period + 2 ms (low latency).
                long periodFrames = devPeriod * SpatialProcessor.Fs / 10000000;
                uint target = (uint)Math.Min(bufferFrames, LowLatency ? periodFrames + 96 : periodFrames * 3 / 2);
                var tmp = new float[bufferFrames * 2];
                while (!stop)
                {
                    ev.WaitOne(50);
                    uint padding;
                    hr = client.GetCurrentPadding(out padding);
                    if (hr != 0) throw new COMException("Headphones lost", hr);
                    if (padding >= target) continue;
                    uint n = target - padding;
                    hr = rc.GetBuffer(n, out data);
                    if (hr != 0) throw new COMException("Headphones write failed", hr);
                    ring.Read(tmp, (int)n);
                    Marshal.Copy(tmp, 0, data, (int)n * 2);
                    rc.ReleaseBuffer(n, 0);
                    Underruns = ring.Underruns;
                    LatencyMs = (float)((padding + ring.Fill + SpatialProcessor.B / 2 + 480 + Processor.ExtraLatency) * 1000.0 / SpatialProcessor.Fs);
                }
                client.Stop();
            }
            finally
            {
                Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
                ev.Dispose();
            }
        }
    }

}