using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// 3D game audio on Linux: the same engine as on Windows - the 7.1 game output ("StreamLite - Out") is read, made
    /// into true-360 3D for your headphones (or folded to stereo when 3D is off), and played on your headphones and every
    /// added output, each with its own volume, EQ and sync delay. Linux has no Equalizer APO, so the headphones' own EQ
    /// (correction + profile) runs here (MainEq) - also for the apps that skip 3D: they play into "StreamLite - Out (no 3D)",
    /// mixed in after the 3D.
    /// </summary>
    internal sealed class GameAudioEngine
    {
        public readonly SpatialProcessor Processor = new SpatialProcessor();
        public volatile string Status = "Off";
        public volatile bool Running;
        public volatile int InputChannels, InputMask;
        public readonly float[] ChannelDb = Enumerable.Repeat(-100f, SpatialProcessor.Slots).ToArray();
        public volatile float CpuPercent, LatencyMs;
        public volatile int Underruns;
        public volatile bool Plain;
        // Direct to headphones (exclusive) is a Windows path: PipeWire mixes at the device's own small quantum already.
        public volatile bool Exclusive, ExclusiveActive;
        public volatile string ExclusiveError = "not needed on Linux - PipeWire plays at the device's own small steps";
        public volatile float ExclusivePeriodMs;
        public volatile float RingMs, QueueMs;
        double ringAvg, queueAvg;
        float PathMs() { return RingMs + QueueMs + (float)((SpatialProcessor.B / 2 + Processor.ExtraLatency + MainDelayFrames) * 1000.0 / SpatialProcessor.Fs); }
        void Measure(int fill, double queueFrames) { ringAvg += (fill - ringAvg) * 0.002; queueAvg += (queueFrames - queueAvg) * 0.002; RingMs = (float)(ringAvg / 48); QueueMs = (float)(queueAvg / 48); }
        public readonly AppMixer Mixer = new AppMixer();
        /// <summary>The headphones' own EQ (correction + profile): always applied on Linux.</summary>
        public readonly EqChain MainEqL = new EqChain(), MainEqR = new EqChain();
        public readonly EqChain LayerL = new EqChain(), LayerR = new EqChain();

        public StereoRing OpenDirect(string deviceId, int margin) { return null; }
        public void CloseDirect(StereoRing r) { }
        public bool HoldsExclusively(string deviceId) { return false; }
        public bool WillHold(string deviceId) { return false; }

        internal sealed class OutputSink
        {
            public readonly string Id;
            public volatile float GainDb;
            public readonly EqChain EqL = new EqChain(), EqR = new EqChain();
            public readonly StereoRing Ring;
            public volatile string Status = "Starting";
            public volatile bool Playing, StopFlag;
            public volatile int DelayFrames;
            public Thread Thread;
            public int Dropouts { get { return Ring.Underruns; } }
            public OutputSink(string id, float gainDb, int startMargin) { Id = id; GainDb = gainDb; Ring = new StereoRing(48, startMargin); }
        }
        volatile OutputSink[] extras = new OutputSink[0];
        readonly object extrasGate = new object();
        public OutputSink[] ExtraOutputs { get { return extras; } }

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
            try
            {
                while (!stop && !o.StopFlag)
                {
                    try { RunRender(o.Id, o.Ring, o); }
                    catch (Exception ex) { o.Status = ex.Message; o.Playing = false; }
                    for (int i = 0; i < 10 && !stop && !o.StopFlag; i++) Thread.Sleep(100);
                }
            }
            finally { o.Playing = false; }
        }

        public volatile int MainDelayFrames;
        public void Click(int output) { keepAliveUntil = Environment.TickCount + 2500; clickFor = output; }
        public void KeepAwake() { keepAliveUntil = Environment.TickCount + 2500; }
        volatile int keepAliveUntil = Environment.TickCount;
        public const int AllOutputs = 100;
        public volatile int ClicksPlayed;
        public long ClickTicks;
        volatile int clickFor = -1;
        float[] clickL = new float[0], clickR = new float[0];
        internal static readonly float[] ClickShape = MakeClick();
        static float[] MakeClick()
        {
            var c = new float[480]; var rnd = new Random(7);
            for (int i = 0; i < c.Length; i++) c[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.35 * Math.Exp(-i / 48.0));
            return c;
        }

        void WriteOut(float[] l, float[] r, int n)
        {
            LayerL.Process(l, n); LayerR.Process(r, n);
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
            int now = Environment.TickCount;
            bool resumed = now - lastSoundTick > 1000;
            lastSoundTick = now;
            if (resumed) ring.Grace(1000);
            ring.InputArrived();
            var ex = extras;
            for (int k = 0; k < ex.Length; k++) { if (resumed) ex[k].Ring.Grace(1000); ex[k].Ring.InputArrived(); }
        }

        float plainLim = 1;
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
        Thread capThread, renThread, plainThread, keepThread;
        StereoRing plainRing;
        float[] plainBuf = new float[0];
        volatile bool stop;
        StereoRing ring;
        public int Dropouts { get { var r = ring; return r != null ? r.Underruns : 0; } }
        public int StartMargin;
        public int LearnedMargin { get { var r = ring; return r != null ? r.Margin : 0; } }
        public int LearnedFloor { get { var r = ring; return r != null ? r.Least : 0; } }
        public double LastGapMs { get { var r = ring; return r != null ? r.LastGapMs : 0; } }
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

        /// <summary>sink = StreamLite's 7.1 game output (its monitor is read); headphones = your real headphones.</summary>
        public void Start(string sink, string headphones)
        {
            Stop();
            sinkId = sink; phonesId = headphones;
            if (string.IsNullOrEmpty(phonesId)) { Status = "Choose your headphones."; return; }
            stop = false; Running = true;
            ring = new StereoRing(48, StartMargin);
            startTick = Environment.TickCount;
            capThread = new Thread(CaptureLoop) { IsBackground = true, Name = "3D audio in", Priority = ThreadPriority.Highest };
            renThread = new Thread(RenderLoop) { IsBackground = true, Name = "3D audio out", Priority = ThreadPriority.Highest };
            plainRing = new StereoRing(48, StartMargin);
            plainThread = new Thread(PlainLoop) { IsBackground = true, Name = "No-3D apps in", Priority = ThreadPriority.Highest };
            keepThread = new Thread(KeepLoop) { IsBackground = true, Name = "Apps on the headphones" };
            capThread.Start(); renThread.Start(); plainThread.Start(); keepThread.Start();
            Linux.LinuxAudio.EngineHeadphones = phonesId;
            foreach (var o in extras) { o.Ring.Clear(); StartSink(o); }
        }

        public void Stop()
        {
            if (!Running) return;
            stop = true;
            Linux.LinuxAudio.EngineHeadphones = null;
            if (capThread != null) capThread.Join(1500);
            if (renThread != null) renThread.Join(1500);
            if (plainThread != null) plainThread.Join(1500);
            if (keepThread != null) keepThread.Join(3000);
            capThread = renThread = plainThread = keepThread = null;
            foreach (var o in extras) StopSink(o);
            Running = false;
            InputChannels = 0;
            Status = "Off";
        }

        void CaptureLoop()
        {
            while (!stop)
            {
                if (string.IsNullOrEmpty(sinkId)) { RunIdle(); return; }
                try { RunCapture(); }
                catch (Exception ex) { Status = "Game audio device: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

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
                    MixPlain(outL, outR, B);
                    WriteOut(outL, outR, B);
                    next += step;
                }
            }
        }

        /// <summary>The apps kept out of 3D ("StreamLite - Out (no 3D)"), into their own ring.</summary>
        void PlainLoop()
        {
            while (!stop)
            {
                try
                {
                    const int B = SpatialProcessor.B;
                    using (var rec = PulseStream.Record(Linux.LinuxAudio.Plain + ".monitor", 2, "StreamLite: apps without 3D", 10))
                    {
                        var inter = new float[B * 2]; var l = new float[B]; var r = new float[B];
                        while (!stop)
                        {
                            rec.Read(inter, B);
                            for (int i = 0; i < B; i++) { l[i] = inter[2 * i]; r[i] = inter[2 * i + 1]; }
                            plainRing.Write(l, r, B);
                            plainRing.InputArrived();
                        }
                    }
                }
                catch { }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Apps that play straight to the headphones (set so in their own settings, or remembered by the desktop) move to
        /// "StreamLite - Out (no 3D)" every couple of seconds, so they keep the headphone EQ - Equalizer APO gives them
        /// that on Windows. StreamLite's own streams stay.
        /// </summary>
        void KeepLoop()
        {
            int me = Process.GetCurrentProcess().Id;
            while (!stop)
            {
                for (int i = 0; i < 20 && !stop; i++) Thread.Sleep(100);
                if (stop) break;
                try
                {
                    var nums = Pactl.DeviceNumbers(true);
                    foreach (var s in Pactl.Streams(true))
                    {
                        string dev; if (s.Device == null || !nums.TryGetValue(s.Device, out dev)) dev = s.Device;
                        if (dev == phonesId && s.Pid != me && s.Pid > 0) Pactl.MoveSinkInput(s.Index, Linux.LinuxAudio.Plain);
                    }
                    AppRouting.Reapply();
                }
                catch { }
            }
        }

        /// <summary>Adds the apps kept out of 3D to a finished block (after the 3D, before the EQ).</summary>
        void MixPlain(float[] l, float[] r, int n)
        {
            var pr = plainRing; if (pr == null) return;
            if (plainBuf.Length < n * 2) plainBuf = new float[n * 2];
            pr.Read(plainBuf, n);
            for (int i = 0; i < n; i++) { l[i] += plainBuf[2 * i]; r[i] += plainBuf[2 * i + 1]; }
        }

        /// <summary>The game output's monitor, 7.1 in the processor's slot order (FL FR FC LFE BL BR SL SR), one block at a time.</summary>
        void RunCapture()
        {
            const int B = SpatialProcessor.B, ch = 8;
            using (var rec = PulseStream.Record(sinkId + ".monitor", ch, "StreamLite: 3D game audio", 10, PulseStream.Map71))
            {
                InputChannels = ch; InputMask = 0x63F;
                Status = "Running";
                var inter = new float[B * ch];
                var slots = new float[SpatialProcessor.Slots][];
                for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
                var outL = new float[B]; var outR = new float[B];
                var src = SpatialProcessor.Source.Stereo;
                int surroundHold = 0, holdBlocks = 20 * SpatialProcessor.Fs / B, quiet = 0;
                var sw = Stopwatch.StartNew();
                double busy = 0; long window = sw.ElapsedTicks;
                while (!stop)
                {
                    rec.Read(inter, B);
                    float any = 0, pk = 0;
                    for (int i = 0; i < B; i++)
                        for (int c = 0; c < ch; c++)
                        {
                            float v = inter[i * ch + c];
                            slots[c][i] = v;
                            float a = Math.Abs(v); if (a > any) any = a;
                            if (c >= 2 && a > pk) pk = a;
                        }
                    for (int s = 0; s < slots.Length; s++)
                    {
                        float m = 0; var sl = slots[s];
                        for (int i = 0; i < B; i++) { float a = Math.Abs(sl[i]); if (a > m) m = a; }
                        float db = m > 1e-6f ? (float)(20 * Math.Log10(m)) : -100f;
                        ChannelDb[s] = db > ChannelDb[s] ? db : Math.Max(-100f, ChannelDb[s] - 0.1f);
                    }
                    // Browsers and stereo games only fill front left/right: stereo until a surround channel carries sound.
                    if (pk > 1e-4f) surroundHold = holdBlocks; else if (surroundHold > 0) surroundHold--;
                    src = surroundHold > 0 ? SpatialProcessor.Source.Surround71 : SpatialProcessor.Source.Stereo;
                    bool sound = any > 1e-6f;
                    quiet = sound ? 0 : quiet + 1;
                    long t0 = sw.ElapsedTicks;
                    if (Plain && !Processor.TestRunning) Fold(slots, outL, outR, B);
                    else Processor.Process(slots, Processor.TestRunning ? SpatialProcessor.Source.Surround71 : src, outL, outR);
                    busy += sw.ElapsedTicks - t0;
                    MixPlain(outL, outR, B);
                    WriteOut(outL, outR, B);
                    if (sound) { lastInputTicks = DateTime.UtcNow.Ticks; InputArrived(); }
                    long now = sw.ElapsedTicks;
                    if (now - window > Stopwatch.Frequency)
                    {
                        CpuPercent = (float)(100 * busy / (now - window));
                        busy = 0; window = now;
                        Status = DateTime.UtcNow.Ticks - lastInputTicks > TimeSpan.TicksPerSecond ? "Waiting for game audio" : "Running";
                    }
                }
            }
        }

        void RenderLoop()
        {
            while (!stop)
            {
                try { RunRender(phonesId, ring, null); }
                catch (Exception ex) { Status = "Headphones: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        /// <summary>One output: its ring, its delay, then (the headphones) their own EQ or (an added output) its volume and EQ.</summary>
        void RunRender(string id, StereoRing r, OutputSink sink)
        {
            const int n = 240;
            using (var play = PulseStream.Play(id, 2, sink == null ? "StreamLite: 3D game audio" : "StreamLite: added output", 15))
            {
                var tmp = new float[n * 2];
                var chL = new float[n]; var chR = new float[n];
                var delay = new DelayLine();
                if (sink != null) { sink.Status = "Playing"; sink.Playing = true; r.Grace(2000); }
                r.Clear();
                while (!stop && (sink == null || !sink.StopFlag))
                {
                    if (sink == null) Measure(r.Fill, play.LatencyMs * 48);
                    r.Read(tmp, n);
                    delay.Process(tmp, n, sink != null ? sink.DelayFrames : MainDelayFrames);
                    float g = sink != null ? (float)Math.Pow(10, sink.GainDb / 20) : 1f;
                    for (int i = 0; i < n; i++) { chL[i] = tmp[2 * i] * g; chR[i] = tmp[2 * i + 1] * g; }
                    if (sink != null) { sink.EqL.Process(chL, n); sink.EqR.Process(chR, n); }
                    else { MainEqL.Process(chL, n); MainEqR.Process(chR, n); }
                    for (int i = 0; i < n; i++)
                    {
                        float a = chL[i], b = chR[i];
                        tmp[2 * i] = a > 1f ? 1f : a < -1f ? -1f : a; tmp[2 * i + 1] = b > 1f ? 1f : b < -1f ? -1f : b;
                    }
                    play.Write(tmp, n);
                    if (sink != null) continue;
                    Underruns = r.Underruns;
                    LatencyMs = PathMs();
                }
            }
        }
    }

    /// <summary>Linux: StreamLite never holds the headphones exclusively, so its own sounds open them normally.</summary>
    internal static class HeadphonesDirect
    {
        public static volatile GameAudioEngine Engine;
        public const int TestMargin = StereoRing.MaxMargin;
        public static StereoRing Open(string deviceId, int margin = TestMargin, int wait = 2000) { return null; }
        public static void Close(StereoRing r) { }
        public static bool Held(string deviceId) { return false; }
        public static bool WillHold(string deviceId) { return false; }
        public static bool Wants(StereoRing r) { return r.Fill < r.Margin + 240; }
        public static bool WaitRoom(StereoRing r, Func<bool> stop) { return false; }
        public static void Play(StereoRing r, float[] stereo, Func<bool> stop) { }
    }

    /// <summary>Holds a stereo stream back by a set number of frames (up to 1 s), for outputs in sync.</summary>
    internal sealed class DelayLine
    {
        const int Max = 48000;
        readonly float[] buf = new float[Max * 2];
        int pos, current = -1;

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

    /// <summary>
    /// Linux: apps that skip 3D play into "StreamLite - Out (no 3D)" (PipeWire mixes them), so StreamLite needn't pick
    /// up and mix each app itself as it does while it holds the headphones exclusively on Windows.
    /// </summary>
    internal sealed class AppMixer
    {
        public volatile string SinkId, DeviceId, LastError;
        public volatile bool CaptureBroken;
        public Tap[] Taps { get { return new Tap[0]; } }

        internal sealed class Tap
        {
            public readonly int Pid; public readonly string Name;
            public readonly StereoRing Ring = new StereoRing(96);
            public long Frames;
            public volatile bool AlsoOnSink;
            public Tap(int pid, string name) { Pid = pid; Name = name; }
        }
        public void Start() { }
        public void Stop() { }
    }
}
