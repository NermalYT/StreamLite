using System;
using System.Diagnostics;
using System.Linq;
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
        /// <summary>
        /// What the game sends, per 7.1 slot (WAVE order): the loudest level in the last ~3 seconds (dBFS). Shows
        /// whether a game really fills the surround channels or plays stereo only.
        /// </summary>
        public readonly float[] ChannelDb = Enumerable.Repeat(-100f, SpatialProcessor.Slots).ToArray();
        public volatile float CpuPercent, LatencyMs;
        public volatile int Underruns;
        /// <summary>
        /// Plain: no 3D - surround is folded down to stereo and stereo plays as it is (3D audio off, but StreamLite
        /// still plays StreamLite - Out on several outputs).
        /// </summary>
        public volatile bool Plain;

        /// <summary>
        /// One more output playing the same sound (a second pair of headphones, speakers...). Every device has its own
        /// clock, so each one gets its own drift-corrected ring and render thread, plus its own volume and EQ.
        /// </summary>
        internal sealed class OutputSink
        {
            public readonly string Id;
            public volatile float GainDb;
            public readonly EqChain EqL = new EqChain(), EqR = new EqChain();
            public readonly StereoRing Ring;
            public volatile string Status = "Starting";
            public volatile bool Playing, StopFlag;
            /// <summary>Held back by this much to play in sync with slower outputs (Bluetooth...), frames.</summary>
            public volatile int DelayFrames;
            public Thread Thread;
            public int Dropouts { get { return Ring.Underruns; } }
            public OutputSink(string id, float gainDb, int startMargin) { Id = id; GainDb = gainDb; Ring = new StereoRing(48, startMargin); }
        }
        volatile OutputSink[] extras = new OutputSink[0];
        readonly object extrasGate = new object();
        public OutputSink[] ExtraOutputs { get { return extras; } }

        /// <summary>The extra outputs to play on (id -> volume dB). Running ones keep running; removed ones stop.</summary>
        public void SetOutputs(System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, float>> wanted)
        {
            lock (extrasGate)
            {
                var old = extras;
                var next = new System.Collections.Generic.List<OutputSink>();
                foreach (var w in wanted)
                {
                    if (w.Key == phonesId) continue;
                    OutputSink have = null;
                    foreach (var o in old) if (o.Id == w.Key) have = o;
                    if (have == null) { have = new OutputSink(w.Key, w.Value, StartMargin); if (Running) StartSink(have); }
                    else have.GainDb = w.Value;
                    if (!next.Contains(have)) next.Add(have);
                }
                extras = next.ToArray();
                foreach (var o in old) if (!next.Contains(o)) StopSink(o);
            }
        }

        void StartSink(OutputSink o)
        {
            o.StopFlag = false;
            o.Thread = new Thread(() => SinkLoop(o)) { IsBackground = true, Name = "Extra output", Priority = ThreadPriority.Highest };
            o.Thread.Start();
        }

        static void StopSink(OutputSink o)
        {
            o.StopFlag = true;
            var t = o.Thread; if (t != null) t.Join(1500);
            o.Thread = null; o.Playing = false; o.Status = "Stopped";
        }

        void SinkLoop(OutputSink o)
        {
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                while (!stop && !o.StopFlag)
                {
                    try { RunRender(o.Id, o.Ring, o); }
                    catch (Exception ex) { o.Status = ex.Message; o.Playing = false; }
                    for (int i = 0; i < 10 && !stop && !o.StopFlag; i++) Thread.Sleep(100);
                }
            }
            finally { AudioEngine.EndProAudio(mm); o.Playing = false; }
        }

        /// <summary>Output 1 (the headphones) held back to play in sync with slower outputs, frames.</summary>
        public volatile int MainDelayFrames;

        /// <summary>
        /// Sync test: a click on one output only (0 = output 1, 1.. = the extra outputs), through its real path (ring,
        /// delay, device). ClickTicks = when it went into that output's ring (Stopwatch ticks).
        /// </summary>
        public void Click(int output) { keepAliveUntil = Environment.TickCount + 2500; clickFor = output; }
        /// <summary>Keeps the outputs running (even without game sound) for the next 2.5 s, so a click isn't the first sound.</summary>
        public void KeepAwake() { keepAliveUntil = Environment.TickCount + 2500; }
        volatile int keepAliveUntil = Environment.TickCount;
        /// <summary>Click(AllOutputs): the same click on every output at once (listening for sync by ear).</summary>
        public const int AllOutputs = 100;
        public volatile int ClicksPlayed;
        public long ClickTicks;
        volatile int clickFor = -1;
        float[] clickL = new float[0], clickR = new float[0];
        /// <summary>The sync test's click: 1.5 ms of noise burst, falling off quickly - easy to time, not loud for long.</summary>
        internal static readonly float[] ClickShape = MakeClick();
        static float[] MakeClick()
        {
            var c = new float[480]; var rnd = new Random(7);
            for (int i = 0; i < c.Length; i++) c[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.35 * Math.Exp(-i / 48.0));
            return c;
        }

        /// <summary>One processed block to every output.</summary>
        void WriteOut(float[] l, float[] r, int n)
        {
            int target = clickFor;
            if (target >= 0)
            {
                clickFor = -1;
                if (clickL.Length < n) { clickL = new float[n]; clickR = new float[n]; }
                Array.Copy(l, clickL, n); Array.Copy(r, clickR, n);
                for (int i = 0; i < Math.Min(n, ClickShape.Length); i++) { clickL[i] += ClickShape[i]; clickR[i] += ClickShape[i]; }
                ClickTicks = Stopwatch.GetTimestamp();
                ClicksPlayed++;
            }
            bool all = target == AllOutputs;
            if (target == 0 || all) ring.Write(clickL, clickR, n); else ring.Write(l, r, n);
            var ex = extras;
            for (int k = 0; k < ex.Length; k++) { if (target == k + 1 || all) ex[k].Ring.Write(clickL, clickR, n); else ex[k].Ring.Write(l, r, n); }
        }

        int lastSoundTick;

        void InputArrived()
        {
            // Sound again after a second or more of silence: Windows hands out the first packets of a stream unevenly -
            // bridged as always, but not counted as gaps (and no margin for them).
            int now = Environment.TickCount;
            bool resumed = now - lastSoundTick > 1000;
            lastSoundTick = now;
            if (resumed) ring.Grace(1000);
            ring.InputArrived();
            var ex = extras;
            for (int k = 0; k < ex.Length; k++) { if (resumed) ex[k].Ring.Grace(1000); ex[k].Ring.InputArrived(); }
        }

        float plainLim = 1;
        /// <summary>Plain mode: surround folded down to stereo (stereo stays exactly as it is), with a peak limiter.</summary>
        void Fold(float[][] slots, float[] l, float[] r, int n)
        {
            const float c = 0.7071f;
            var fl = slots[SpatialProcessor.FL]; var fr = slots[SpatialProcessor.FR]; var fc = slots[SpatialProcessor.FC]; var lfe = slots[SpatialProcessor.LFE];
            var bl = slots[SpatialProcessor.BL]; var br = slots[SpatialProcessor.BR]; var sl = slots[SpatialProcessor.SL]; var sr = slots[SpatialProcessor.SR];
            for (int i = 0; i < n; i++)
            {
                float a = fl[i] + c * (fc[i] + sl[i] + bl[i]) + 0.5f * lfe[i];
                float b = fr[i] + c * (fc[i] + sr[i] + br[i]) + 0.5f * lfe[i];
                float pk = Math.Max(Math.Abs(a), Math.Abs(b)) * plainLim;
                if (pk > 0.98f) plainLim *= 0.98f / pk; else plainLim = Math.Min(1f, plainLim + 0.00002f);
                l[i] = a * plainLim; r[i] = b * plainLim;
            }
        }

        string sinkId, phonesId;
        Thread capThread, renThread;
        volatile bool stop;
        StereoRing ring;
        /// <summary>Times game sound ran dry on the way to the headphones while the game was still playing (status.txt).</summary>
        public int Dropouts { get { var r = ring; return r != null ? r.Underruns : 0; } }
        /// <summary>Margin to start with (learned in an earlier run, so it isn't re-learned with dropouts each start).</summary>
        public int StartMargin;
        public int LearnedMargin { get { var r = ring; return r != null ? r.Margin : 0; } }
        /// <summary>What's worth remembering for the next start: the floor set by recurring gaps (not a one-off's cover).</summary>
        public int LearnedFloor { get { var r = ring; return r != null ? r.Least : 0; } }
        /// <summary>How long the last gap lasted (ms): long = the game itself stalled (loading), not the way to your ears.</summary>
        public double LastGapMs { get { var r = ring; return r != null ? r.LastGapMs : 0; } }
        /// <summary>Seconds 3D audio has run without a dropout (since it started or since the last one).</summary>
        public int CalmSeconds
        {
            get
            {
                var r = ring; if (r == null) return 0;
                int last = r.LastUnderrunTick, from = last != 0 && last - startTick > 0 ? last : startTick;
                return (Environment.TickCount - from) / 1000;
            }
        }
        int startTick;
        long lastInputTicks;

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        public void Start(string sink, string headphones)
        {
            Stop();
            sinkId = sink; phonesId = headphones;
            if (string.IsNullOrEmpty(phonesId)) { Status = "Choose your headphones."; return; }
            stop = false; Running = true;
            // Always the smallest buffers: the ring learns any extra margin this PC needs (and keeps it across starts).
            ring = new StereoRing(48, StartMargin);
            startTick = Environment.TickCount;
            timeBeginPeriod(1);
            capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "3D audio in", Priority = ThreadPriority.Highest };
            renThread = new Thread(RenderLoop) { IsBackground = true, Name = "3D audio out", Priority = ThreadPriority.Highest };
            capThread.Start(); renThread.Start();
            foreach (var o in extras) { o.Ring.Clear(); StartSink(o); }
        }

        public void Stop()
        {
            if (!Running) return;
            stop = true;
            if (capThread != null) capThread.Join(1500);
            if (renThread != null) renThread.Join(1500);
            capThread = renThread = null;
            foreach (var o in extras) StopSink(o);
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
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                if (string.IsNullOrEmpty(sinkId)) { RunIdle(); return; }
                while (!stop)
                {
                    try { RunCapture(); }
                    catch (Exception ex) { Status = "Game audio device: " + ex.Message; }
                    for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
                }
            }
            finally { AudioEngine.EndProAudio(mm); }
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
                    WriteOut(outL, outR, B);
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
                    // Rendering on our own (test sound / sync clicks, no game sound): wake often and keep the outputs fed
                    // block by block - 20 ms gaps would let their 13 ms buffers run dry.
                    bool selfPaced = (Processor.TestRunning || Environment.TickCount - keepAliveUntil < 0) && DateTime.UtcNow.Ticks - lastInputTicks > 15 * TimeSpan.TicksPerSecond / 10;
                    if (selfPaced) Thread.Sleep(2); else if (events) ev.WaitOne(20); else Thread.Sleep(2);
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
                                for (int s = 0; s < slots.Length; s++)
                                {
                                    float m = 0; var sl = slots[s];
                                    for (int i = 0; i < B; i++) { float a = Math.Abs(sl[i]); if (a > m) m = a; }
                                    float db = m > 1e-6f ? (float)(20 * Math.Log10(m)) : -100f;
                                    ChannelDb[s] = db > ChannelDb[s] ? db : Math.Max(-100f, ChannelDb[s] - 0.1f);   // ~19 dB/s release
                                }
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
                                if (Plain && !Processor.TestRunning) Fold(slots, outL, outR, B);
                                else Processor.Process(slots, src, outL, outR);
                                busy += sw.ElapsedTicks - t0;
                                WriteOut(outL, outR, B);
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
                    if (got) { lastInputTicks = DateTime.UtcNow.Ticks; if (sound) InputArrived(); }
                    // Nothing playing (loopback goes quiet): keep the test sound and reverb tails running in real time.
                    double sinceBlock = (sw.ElapsedTicks - lastBlock) * 1000.0 / Stopwatch.Frequency;
                    bool recent = DateTime.UtcNow.Ticks - lastInputTicks < 15 * TimeSpan.TicksPerSecond / 10;
                    bool clicks = Environment.TickCount - keepAliveUntil < 0;   // the sync test plays into silence too
                    if (!got && !recent && !Processor.TestRunning && !clicks) lastBlock = sw.ElapsedTicks;   // idle: nothing to render, save CPU
                    else if (!got && (selfPaced ? sinceBlock >= B * 1000.0 / SpatialProcessor.Fs : sinceBlock > 25))
                    {
                        while ((sw.ElapsedTicks - lastBlock) * 1000.0 / Stopwatch.Frequency >= B * 1000.0 / SpatialProcessor.Fs)
                        {
                            for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, B);
                            fill = 0;
                            long t0 = sw.ElapsedTicks;
                            if (Plain && !Processor.TestRunning) Fold(slots, outL, outR, B);
                            else Processor.Process(slots, Processor.TestRunning ? SpatialProcessor.Source.Surround71 : src, outL, outR);
                            busy += sw.ElapsedTicks - t0;
                            WriteOut(outL, outR, B);
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
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                while (!stop)
                {
                    try { RunRender(phonesId, ring, null); }
                    catch (Exception ex) { Status = "Headphones: " + ex.Message; }
                    for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
                }
            }
            finally { AudioEngine.EndProAudio(mm); }
        }

        /// <summary>Plays a ring on an output (sink = null: the main headphones; else an extra output with its volume and EQ).</summary>
        void RunRender(string id, StereoRing ring, OutputSink sink)
        {
            if (sink != null)
            {
                int state = AudioDevices.State(id);
                if (state == 2) throw new InvalidOperationException("turned off in Windows (Sound settings > Playback)");
                if (state >= 4) throw new InvalidOperationException("unplugged - waiting");
            }
            IMMDevice dev;
            var client = Open(id, out dev);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, SpatialProcessor.Fs, true, 32);
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException((sink != null ? "Could not open it" : "Headphones init failed") + " (0x" + hr.ToString("X8") + ")", hr);
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
                // Queue one device period + 2 ms: the least Windows' mixer can take without running dry.
                long periodFrames = devPeriod * SpatialProcessor.Fs / 10000000;
                uint target = (uint)Math.Min(bufferFrames, periodFrames + 96);
                var tmp = new float[bufferFrames * 2];
                float[] chL = null, chR = null;
                var delay = new DelayLine();
                if (sink != null) { chL = new float[bufferFrames]; chR = new float[bufferFrames]; sink.Status = "Playing"; sink.Playing = true; ring.Grace(2000); }
                while (!stop && (sink == null || !sink.StopFlag))
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
                    delay.Process(tmp, (int)n, sink != null ? sink.DelayFrames : MainDelayFrames);
                    if (sink != null)
                    {
                        // This output's own volume and EQ.
                        float g = (float)Math.Pow(10, sink.GainDb / 20);
                        for (int i = 0; i < n; i++) { chL[i] = tmp[2 * i] * g; chR[i] = tmp[2 * i + 1] * g; }
                        sink.EqL.Process(chL, (int)n); sink.EqR.Process(chR, (int)n);
                        for (int i = 0; i < n; i++)
                        {
                            float a = chL[i], b = chR[i];
                            tmp[2 * i] = a > 1f ? 1f : a < -1f ? -1f : a; tmp[2 * i + 1] = b > 1f ? 1f : b < -1f ? -1f : b;
                        }
                    }
                    Marshal.Copy(tmp, 0, data, (int)n * 2);
                    rc.ReleaseBuffer(n, 0);
                    if (sink != null) continue;
                    Underruns = ring.Underruns;
                    LatencyMs = (float)((padding + ring.Fill + SpatialProcessor.B / 2 + 480 + Processor.ExtraLatency + MainDelayFrames) * 1000.0 / SpatialProcessor.Fs);
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

    /// <summary>Holds a stereo stream back by a set number of frames (up to 1 s), for outputs in sync.</summary>
    internal sealed class DelayLine
    {
        const int Max = 48000;
        readonly float[] buf = new float[Max * 2];
        int pos, current = -1;

        /// <summary>Interleaved stereo in place. A new delay starts from silence (only when you change it).</summary>
        public void Process(float[] inter, int frames, int delay)
        {
            delay = Math.Max(0, Math.Min(Max - 1, delay));
            if (delay != current) { Array.Clear(buf, 0, buf.Length); current = delay; }
            if (delay == 0) return;
            for (int i = 0; i < frames; i++)
            {
                int w = pos * 2, rd = ((pos - delay + Max) % Max) * 2;
                float l = inter[2 * i], r = inter[2 * i + 1];
                buf[w] = l; buf[w + 1] = r;
                inter[2 * i] = buf[rd]; inter[2 * i + 1] = buf[rd + 1];
                pos = (pos + 1) % Max;
            }
        }
    }
}
