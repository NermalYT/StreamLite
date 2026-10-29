using System;
using System.Linq;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// The clean microphone on Linux: the same engine as on Windows - the same voice chain, mixer for added mics,
    /// meters, graphs, test recording, mute and push to talk - reading the mic from PipeWire / PulseAudio and writing
    /// "StreamLite Mic Feed", which apps see as the "StreamLite Mic" input. Mic and output run on their own clocks, so a
    /// drift-corrected, concealing buffer sits between them (as on Windows).
    /// </summary>
    internal sealed class AudioEngine
    {
        public const int SampleRate = 48000;
        const int Packet = 480;   // 10 ms, the size PipeWire's pulse server hands out by default

        /// <summary>The live mic's effects (the voice test runs its own copy with the same settings).</summary>
        public readonly VoiceChain Fx = new VoiceChain();
        /// <summary>Extra microphones mixed into this one (Microphone page > More microphones).</summary>
        public readonly MicMixer Mixer = new MicMixer();
        /// <summary>Tests and the output sync check: the main mic's raw samples with the time they reached StreamLite.</summary>
        public volatile Action<float[], int, long> RawTap;
        /// <summary>Setup check: the virtual mic carries a 1 kHz tone instead of your voice while set.</summary>
        public volatile bool TestTone;
        double tonePhase;
        /// <summary>The whole virtual mic muted (hotkey), and push-to-talk not held / push-to-mute held.</summary>
        public volatile bool Muted, PttClosed;
        float muteGain = 1f;
        public NoiseSuppressor Suppressor { get { return Fx.Suppressor; } }
        public NoiseGate Gate { get { return Fx.Gate; } }
        public EqChain Eq { get { return Fx.Eq; } }
        public AutoLevel Leveler { get { return Fx.Leveler; } }
        public DeHum Hum { get { return Fx.Hum; } }
        public HighCut Hiss { get { return Fx.Hiss; } }
        public Compressor Comp { get { return Fx.Comp; } }
        public DeEsser Ess { get { return Fx.Ess; } }
        /// <summary>Gets a copy of the raw mic while a test recording runs.</summary>
        public volatile VoiceTest Test;
        public readonly SpectrumAnalyzer RawSpectrum = new SpectrumAnalyzer(SampleRate);
        public readonly SpectrumAnalyzer OutSpectrum = new SpectrumAnalyzer(SampleRate);
        public readonly SpectrumAnalyzer CleanSpectrum = new SpectrumAnalyzer(SampleRate);
        public readonly SpectrumAnalyzer NoiseSpectrum = new SpectrumAnalyzer(SampleRate);
        public readonly LevelHistory History = new LevelHistory();
        /// <summary>A/B compare: send the mic without noise removal, gate, EQ or auto level.</summary>
        public volatile bool Bypass;

        public float HighPassHz { get { return Fx.HighPassHz; } }
        public float VolumeDb { get { return Fx.VolumeDb; } }
        public bool Limiter { get { return Fx.Limiter; } }
        public volatile float InputLevelDb = -100f;
        public volatile float OutputLevelDb = -100f;
        public volatile string Status = "Stopped";
        public volatile int InputChannelCount = 1;
        public volatile bool Running;

        string captureId, renderId, monitorId;
        Thread captureThread, renderThread, monitorThread;
        SampleRing outRing, monitorRing;
        public int Dropouts { get { var r = outRing; return r != null ? r.Underruns : 0; } }
        public double LastGapMs { get { var r = outRing; return r != null ? r.LastGapMs : 0; } }
        public volatile int StartMargin;
        public int LearnedFloor { get { var r = outRing; return r != null ? r.Least : 0; } }
        public int MarginFrames { get { var r = outRing; return r != null ? r.Margin : 0; } }
        volatile bool stopFlag;

        // Windows-only paths (exclusive mode, the hidden kernel-streaming feed): never on Linux, kept so the pages read the same.
        public volatile bool RawInput, LowLatency = true, ExclusiveCapture, PrivateOutput, PreferPrivateOutput, DirectOutput;
        public volatile string ExclusiveError = "PipeWire shares every device", KsFeedPath, DirectError;
        public volatile int EarlyPackets, LostPackets, CableCatchUps;
        public int LatePacketsFilled { get { var r = outRing; return r != null ? r.Concealed : 0; } }
        public double StartDriftPpm;
        public double CableDriftPpm { get { return double.NaN; } }
        public string CableFillTrace { get { return ""; } }
        public long LostFrames;
        public volatile float PacketMs, CaptureWaitMs, BufferMs, CableLeadMs;
        public float LatencyMs { get { return CaptureWaitMs + BufferMs + CableLeadMs; } }

        public AudioEngine() { Suppressor.RawSpectrum = RawSpectrum; Suppressor.NoiseSpectrum = NoiseSpectrum; }

        /// <summary>capture = your mic; render = the mic feed (StreamLite's own); monitor = where "Hear myself" plays (or null).</summary>
        public void Start(string capture, string render, string monitor)
        {
            Stop();
            captureId = capture; renderId = render; monitorId = monitor;
            PacketMs = CaptureWaitMs = BufferMs = CableLeadMs = 0;
            if (string.IsNullOrEmpty(captureId)) { Status = "No microphone selected"; return; }
            stopFlag = false;
            Running = true;
            outRing = new SampleRing(SampleRate, SampleRate * 60 / 1000) { Conceal = true };
            monitorRing = new SampleRing(SampleRate, SampleRate * 60 / 1000);
            captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Mic capture", Priority = ThreadPriority.Highest };
            captureThread.Start();
            if (!string.IsNullOrEmpty(renderId))
            {
                renderThread = new Thread(() => RenderLoop(renderId, outRing, true)) { IsBackground = true, Name = "Virtual mic out", Priority = ThreadPriority.Highest };
                renderThread.Start();
            }
            if (!string.IsNullOrEmpty(monitorId))
            {
                monitorThread = new Thread(() => RenderLoop(Linux.LinuxAudio.PlaybackFor(monitorId), monitorRing, false)) { IsBackground = true, Name = "Monitor out", Priority = ThreadPriority.AboveNormal };
                monitorThread.Start();
            }
        }

        public void Stop()
        {
            stopFlag = true;
            foreach (var t in new[] { captureThread, renderThread, monitorThread })
                if (t != null) t.Join(1500);
            captureThread = renderThread = monitorThread = null;
            Running = false;
            InputLevelDb = OutputLevelDb = -100f;
            Status = "Stopped";
        }

        void CaptureLoop()
        {
            int fails = 0;
            var buf = new float[Packet];
            while (!stopFlag)
            {
                int t0 = Environment.TickCount;
                try
                {
                    // Every channel in the mic's own layout, so the chain can use just the inputs with your voice.
                    PulseDevice d = AudioDevices.Find(captureId);
                    int ch = d != null && d.Channels >= 2 && d.Channels <= 32 && d.Map != null && d.Map.Length == d.Channels ? d.Channels : 1;
                    var inter = new float[Packet * ch];
                    using (var rec = ch > 1 ? PulseStream.Record(captureId, ch, "StreamLite: your microphone", 10, d.Map) : PulseStream.Record(captureId, 1, "StreamLite: your microphone", 10))
                    {
                        Status = "Running";
                        InputChannelCount = ch;
                        PacketMs = Packet * 1000f / SampleRate;
                        while (!stopFlag)
                        {
                            if (ch == 1) rec.Read(buf, Packet);
                            else { rec.Read(inter, Packet); Fx.Channels.Process(inter, Packet, ch, buf); }
                            CaptureWaitMs = (float)rec.LatencyMs;
                            ProcessPacket(buf, Packet);
                        }
                    }
                }
                catch (Exception ex) { Status = "Microphone: " + ex.Message; }
                if (Environment.TickCount - t0 > 5000) fails = 0;
                int waits = ++fails <= 3 ? 3 : 20;
                for (int i = 0; i < waits && !stopFlag; i++) Thread.Sleep(100);
            }
        }

        void RenderLoop(string id, SampleRing ring, bool cable)
        {
            var buf = new float[240];
            while (!stopFlag)
            {
                try
                {
                    using (var play = PulseStream.Play(id, 1, cable ? "StreamLite: clean mic" : "StreamLite: hear myself", cable ? 15 : 30))
                    {
                        ring.Clear();
                        while (!stopFlag)
                        {
                            int before = ring.Fill;
                            ring.Read(buf, 240);
                            play.Write(buf, 240);
                            if (cable) { BufferMs = BufferMs == 0 ? before * 1000f / SampleRate : BufferMs * 0.99f + before * 1000f / SampleRate * 0.01f; CableLeadMs = (float)play.LatencyMs; }
                        }
                    }
                }
                catch (Exception ex) { if (cable && Status == "Running") Status = "Output: " + ex.Message; }
                for (int i = 0; i < 10 && !stopFlag; i++) Thread.Sleep(100);
            }
        }

        // ------------------------------------------------------------------ the mic chain, one packet at a time (as on Windows)

        Biquad hpf; float hpfHz = -1, limGain = 1f;
        long lastPacketAt, winStart;
        double winInterval, winProc, prevInterval, prevProc;
        public double WorstPacketIntervalMs { get { return Math.Max(winInterval, prevInterval); } }
        public double WorstProcessingMs { get { return Math.Max(winProc, prevProc); } }

        void NoteTiming(ref double slot, double ms)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - winStart > System.Diagnostics.Stopwatch.Frequency * 10) { prevInterval = winInterval; prevProc = winProc; winInterval = winProc = 0; winStart = now; }
            if (ms > slot) slot = ms;
        }

        void ProcessPacket(float[] buf, int n)
        {
            if (TestMic.Active) TestMic.Mix(buf, n);   // live tests only (STREAMLITE_TEST_MIC)
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastPacketAt != 0) NoteTiming(ref winInterval, (t0 - lastPacketAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            lastPacketAt = t0;
            ProcessPacketInner(buf, n);
            NoteTiming(ref winProc, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        void ProcessPacketInner(float[] buf, int n)
        {
            Fx.Clip.Feed(buf, n);
            var raw = RawTap;
            if (raw != null) raw(buf, n, System.Diagnostics.Stopwatch.GetTimestamp());
            Mixer.Mix(buf, n);

            float pk = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > pk) pk = a; }
            InputLevelDb = Math.Max((float)(20 * Math.Log10(Math.Max(pk, 1e-6f))), InputLevelDb - 1.5f);

            var test = Test;
            if (test != null && test.Recording) test.Feed(buf, n);
            float rawDb = LevelHistory.RmsDb(buf, n);
            bool bypass = Bypass;
            float hz = HighPassHz;
            if (hz != hpfHz) { hpfHz = hz; hpf = hz >= 20 ? Biquad.HighPass(hz, SampleRate) : null; }
            if (!bypass)
            {
                Hum.Process(buf, n);
                if (hpf != null) for (int i = 0; i < n; i++) buf[i] = hpf.Process(buf[i]);
                Hiss.Process(buf, n);
            }

            Suppressor.Bypass = bypass;
            Suppressor.Process(buf, n);
            if (!bypass) Fx.Lock.Process(buf, n);
            Fx.Studio.Bypass = bypass;
            Fx.Studio.Process(buf, n);
            float gateInDb = LevelHistory.RmsDb(buf, n);
            if (!bypass) Gate.Process(buf, n);
            float cleanDb = LevelHistory.RmsDb(buf, n);
            CleanSpectrum.PushSamples(buf, n);
            if (!bypass)
            {
                Fx.Presence(buf, n);
                Eq.Process(buf, n);
                Ess.Process(buf, n);
                Comp.Process(buf, n);
                Leveler.Process(buf, n);
            }

            float vol = (float)Math.Pow(10, VolumeDb / 20);
            bool lim = Limiter;
            float outPk = 0;
            for (int i = 0; i < n; i++)
            {
                float x = buf[i] * vol;
                if (lim)
                {
                    float a = Math.Abs(x) * limGain;
                    if (a > 0.89f) limGain *= 0.89f / a;
                    else limGain = Math.Min(1f, limGain + 0.00002f);
                    x *= limGain;
                }
                if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
                buf[i] = x;
                float ax = Math.Abs(x); if (ax > outPk) outPk = ax;
            }
            OutputLevelDb = Math.Max((float)(20 * Math.Log10(Math.Max(outPk, 1e-6f))), OutputLevelDb - 1.5f);
            OutSpectrum.PushSamples(buf, n);
            History.Push(rawDb, gateInDb, cleanDb, LevelHistory.RmsDb(buf, n), bypass || Gate.IsOpen);

            if (TestTone) { for (int i = 0; i < n; i++) { buf[i] = 0.1f * (float)Math.Sin(tonePhase); tonePhase += 2 * Math.PI * 1000 / SampleRate; } tonePhase %= 2 * Math.PI; }
            float target = Muted || PttClosed ? 0f : 1f;
            if (target != muteGain || target == 0f)
            {
                const float step = 1f / 240f;
                for (int i = 0; i < n; i++)
                {
                    muteGain = target > muteGain ? Math.Min(target, muteGain + step) : Math.Max(target, muteGain - step);
                    buf[i] *= muteGain;
                }
                if (target == 0f) OutputLevelDb = -100f;
            }
            outRing.Write(buf, n);
            if (monitorThread != null) monitorRing.Write(buf, n);
        }

        // ------------------------------------------------------------------ helpers the Windows engine offers to others

        internal static IntPtr ProAudio() { return IntPtr.Zero; }
        internal static void EndProAudio(IntPtr h) { }
    }
}
