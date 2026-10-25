using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// Microphone -> effects -> virtual cable (and optional monitor). Runs WASAPI shared mode at
    /// 48 kHz mono float and lets Windows convert to/from each device's native format.
    /// </summary>
    internal sealed class AudioEngine
    {
        public const int SampleRate = 48000;

        /// <summary>The live mic's effects (the voice test runs its own copy with the same settings).</summary>
        public readonly VoiceChain Fx = new VoiceChain();
        /// <summary>Extra microphones mixed into this one (Microphone page > More microphones).</summary>
        public readonly MicMixer Mixer = new MicMixer();
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
        public readonly SpectrumAnalyzer CleanSpectrum = new SpectrumAnalyzer(SampleRate);   // after noise removal + gate
        public readonly SpectrumAnalyzer NoiseSpectrum = new SpectrumAnalyzer(SampleRate);   // estimated background noise
        public readonly LevelHistory History = new LevelHistory();
        /// <summary>A/B compare: send the mic without noise removal, gate, EQ or auto level.</summary>
        public volatile bool Bypass;

        public float HighPassHz { get { return Fx.HighPassHz; } }
        public float VolumeDb { get { return Fx.VolumeDb; } }
        public bool Limiter { get { return Fx.Limiter; } }
        public volatile float InputLevelDb = -100f;
        public volatile float OutputLevelDb = -100f;
        public volatile string Status = "Stopped";
        /// <summary>Channels the microphone delivers (1 = mono).</summary>
        public volatile int InputChannelCount = 1;
        public volatile bool Running;

        string captureId, renderId, monitorId;
        Thread captureThread, renderThread, monitorThread;
        SampleRing outRing, monitorRing;
        /// <summary>Times the virtual mic ran dry while the microphone was still delivering (status.txt).</summary>
        public int Dropouts { get { var r = outRing; return r != null ? r.Underruns : 0; } }
        volatile bool stopFlag;

        public AudioEngine() { Suppressor.RawSpectrum = RawSpectrum; Suppressor.NoiseSpectrum = NoiseSpectrum; }

        public void Start(string capture, string render, string monitor)
        {
            Stop();
            captureId = capture; renderId = render; monitorId = monitor;
            exclusiveFailedFor = null; ExclusiveError = null;
            PacketMs = CaptureWaitMs = BufferMs = CableLeadMs = 0;
            if (string.IsNullOrEmpty(captureId)) { Status = "No microphone selected"; return; }
            stopFlag = false;
            Running = true;
            outRing = new SampleRing(SampleRate, SampleRate * 60 / 1000);
            monitorRing = new SampleRing(SampleRate, SampleRate * 60 / 1000);
            captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Mic capture", Priority = ThreadPriority.Highest };
            captureThread.Start();
            if (!string.IsNullOrEmpty(renderId))
            {
                renderThread = new Thread(() => RenderLoop(renderId, outRing)) { IsBackground = true, Name = "Virtual mic out", Priority = ThreadPriority.Highest };
                renderThread.Start();
            }
            if (!string.IsNullOrEmpty(monitorId))
            {
                monitorThread = new Thread(() => RenderLoop(monitorId, monitorRing)) { IsBackground = true, Name = "Monitor out", Priority = ThreadPriority.AboveNormal };
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

        internal static IntPtr AllocFormat()
        {
            var wf = new WaveFormatEx { wFormatTag = 3, nChannels = 1, nSamplesPerSec = SampleRate, nAvgBytesPerSec = SampleRate * 4, nBlockAlign = 4, wBitsPerSample = 32, cbSize = 0 };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatEx)));
            Marshal.StructureToPtr(wf, p, false);
            return p;
        }

        internal static IAudioClient OpenClient(string id, out IMMDevice device)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                int hr = en.GetDevice(id, out device);
                if (hr != 0 || device == null) throw new InvalidOperationException("Audio device not found (it may be unplugged).");
                int state;
                if (device.GetState(out state) == 0 && state >= 4)
                {
                    Marshal.ReleaseComObject(device); device = null;
                    throw new InvalidOperationException("unplugged - waiting for it to come back");
                }
                object o;
                Guid iid = CoreAudioConst.IID_IAudioClient;
                hr = device.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
                if (hr != 0) throw new COMException("Could not open audio device", hr);
                return (IAudioClient)o;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        void CaptureLoop()
        {
            IntPtr mm = ProAudio();
            try { CaptureLoopInner(); }
            finally { EndProAudio(mm); }
        }

        void CaptureLoopInner()
        {
            int fails = 0;
            while (!stopFlag)
            {
                int t0 = Environment.TickCount;
                // Low latency first: the mic in exclusive mode. If it can't (another app has it, exclusive mode is off,
                // no 48 kHz mode), shared mode - and exclusive isn't tried again on this mic until the audio restarts.
                if (LowLatency && exclusiveFailedFor != captureId)
                {
                    bool streaming = false;
                    try { RunCaptureExclusive(ref streaming); }
                    catch (Exception ex)
                    {
                        if (!streaming) { ExclusiveError = ex.Message; exclusiveFailedFor = captureId; continue; }
                        Status = "Microphone: " + ex.Message;
                    }
                    if (stopFlag) break;
                    if (Environment.TickCount - t0 > 5000) fails = 0;
                    for (int i = 0, pause = ++fails <= 3 ? 3 : 20; i < pause && !stopFlag; i++) Thread.Sleep(100);
                    continue;
                }
                try { RunCapture(); }
                catch (Exception ex) { Status = "Microphone: " + ex.Message; }
                // After running fine (format change, driver reset) reconnect quickly; keep retrying an unplugged mic slower.
                if (Environment.TickCount - t0 > 5000) fails = 0;
                int waits = ++fails <= 3 ? 3 : 20;
                for (int i = 0; i < waits && !stopFlag; i++) Thread.Sleep(100);
            }
        }

        void RunCapture()
        {
            IMMDevice dev;
            var client = OpenClient(captureId, out dev);
            // All of the mic's channels (Windows still converts the rate), so Fx.Channels can pick the ones with your voice.
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(captureId, out ch, out mask, out rate) || ch < 2 || ch > 32) { ch = 1; mask = 0; }
            IntPtr fmt = ch > 1 ? AudioDevices.MakeFormat(ch, mask, SampleRate, true, 32) : AllocFormat();
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                if (hr != 0 && ch > 1)
                {
                    // A driver that won't hand out its channels: let Windows mix them down as before.
                    Marshal.FreeHGlobal(fmt); Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                    client = OpenClient(captureId, out dev);
                    fmt = AllocFormat(); ch = 1;
                    hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                }
                if (hr != 0) throw new COMException(DeviceErrors.Describe(hr, true), hr);
                InputChannelCount = ch;
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Microphone service failed", hr);
                var cap = (IAudioCaptureClient)svc;
                hr = client.Start();
                if (hr != 0) throw new COMException("Microphone start failed", hr);
                Status = "Running";

                var buf = new float[SampleRate];
                var inter = ch > 1 ? new float[SampleRate * ch] : null;
                bool sized = false;
                while (!stopFlag)
                {
                    ev.WaitOne(200);
                    uint packet;
                    hr = cap.GetNextPacketSize(out packet);
                    if (hr != 0) throw new COMException("Microphone disconnected", hr);
                    while (packet > 0 && !stopFlag)
                    {
                        IntPtr data; uint frames, bflags; ulong p1, p2;
                        hr = cap.GetBuffer(out data, out frames, out bflags, out p1, out p2);
                        if (hr != 0) throw new COMException("Microphone read failed", hr);
                        int n = (int)Math.Min(frames, (uint)buf.Length);
                        if ((bflags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0) Array.Clear(buf, 0, n);
                        else if (inter == null) Marshal.Copy(data, buf, 0, n);
                        else { Marshal.Copy(data, inter, 0, n * ch); Fx.Channels.Process(inter, n, ch, buf); }
                        cap.ReleaseBuffer(frames);
                        if (!sized) { sized = true; PacketMs = n * 1000f / SampleRate; outRing.SetPacket(Math.Max(n, 480)); outRing.Grace(5000); }
                        ProcessPacket(buf, n, (bflags & CoreAudioConst.BUFFERFLAGS_TIMESTAMP_ERROR) == 0 ? p2 : 0);

                        hr = cap.GetNextPacketSize(out packet);
                        if (hr != 0) throw new COMException("Microphone disconnected", hr);
                    }
                }
                client.Stop();
            }
            finally
            {
                InputLevelDb = OutputLevelDb = -100f;
                Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
                ev.Dispose();
            }
        }

        const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A), AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x8889000E);

        /// <summary>
        /// Low latency: the mic in exclusive mode at its smallest period (e.g. 3 ms instead of Windows' 10 ms shared
        /// packets), in its own format (no Windows mixer or converter in between). Throws before streaming when the mic
        /// can't (another app has it, exclusive mode is off for it, or it has no 48 kHz mode) - the caller falls back to
        /// shared mode then.
        /// </summary>
        void RunCaptureExclusive(ref bool streaming)
        {
            IMMDevice dev = null;
            IAudioClient client = null;
            IntPtr fmt = IntPtr.Zero;
            object svc = null;
            var ev = new AutoResetEvent(false);
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(captureId, out ch, out mask, out rate) || ch < 1 || ch > 32) { ch = 2; mask = 3; }
            int bits = 0, valid = 0; bool flt = false;
            try
            {
                int hr = -1;
                int[][] tries = { new[] { 32, 1, 32 }, new[] { 32, 0, 24 }, new[] { 24, 0, 24 }, new[] { 16, 0, 16 }, new[] { 32, 0, 32 } };   // container, float, valid bits
                foreach (var tr in tries)
                {
                    if (client != null) { Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev); }
                    client = OpenClient(captureId, out dev);
                    long def, min;
                    client.GetDevicePeriod(out def, out min);
                    if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                    fmt = AudioDevices.MakeFormat(ch, mask, SampleRate, tr[1] == 1, tr[0]);
                    Marshal.WriteInt16(fmt, 18, (short)tr[2]);
                    hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, min, min, fmt, IntPtr.Zero);
                    if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
                    {
                        uint fr; client.GetBufferSize(out fr);
                        long aligned = (long)(10000000.0 * fr / SampleRate + 0.5);
                        Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                        client = OpenClient(captureId, out dev);
                        hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, aligned, aligned, fmt, IntPtr.Zero);
                    }
                    if (hr == 0) { bits = tr[0]; flt = tr[1] == 1; valid = tr[2]; break; }
                    if (hr == AUDCLNT_E_DEVICE_IN_USE || hr == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED) break;   // no format will help
                }
                if (hr != 0)
                    throw new COMException(hr == AUDCLNT_E_DEVICE_IN_USE ? "another app is using the mic" : hr == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED ? "exclusive mode is turned off for this mic in Windows" : "no 48 kHz exclusive mode (0x" + hr.ToString("X8") + ")", hr);
                InputChannelCount = ch;
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Microphone service failed", hr);
                var cap = (IAudioCaptureClient)svc;
                hr = client.Start();
                if (hr != 0) throw new COMException("Microphone start failed", hr);
                streaming = true;
                ExclusiveCapture = true;
                Status = "Running";

                int bps = bits / 8;
                var raw = new byte[SampleRate * ch * bps / 10];
                var inter = new float[SampleRate * ch / 10];
                var buf = new float[SampleRate / 10];
                bool sized = false;
                int silentWaits = 0;
                while (!stopFlag)
                {
                    if (!ev.WaitOne(200)) { if (++silentWaits >= 5) throw new COMException("Microphone stopped delivering", unchecked((int)0x88890004)); continue; }
                    silentWaits = 0;
                    uint packet;
                    hr = cap.GetNextPacketSize(out packet);
                    if (hr != 0) throw new COMException("Microphone disconnected", hr);
                    while (packet > 0 && !stopFlag)
                    {
                        IntPtr data; uint frames, bflags; ulong p1, p2;
                        hr = cap.GetBuffer(out data, out frames, out bflags, out p1, out p2);
                        if (hr != 0) throw new COMException("Microphone read failed", hr);
                        int n = (int)Math.Min(frames, (uint)buf.Length);
                        if ((bflags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0) Array.Clear(inter, 0, n * ch);
                        else { Marshal.Copy(data, raw, 0, n * ch * bps); Decode(raw, n * ch, bps, flt, inter); }
                        cap.ReleaseBuffer(frames);
                        if (ch == 1) Array.Copy(inter, buf, n); else Fx.Channels.Process(inter, n, ch, buf);
                        if (!sized) { sized = true; PacketMs = n * 1000f / SampleRate; outRing.SetPacket(n, true); outRing.Grace(5000); }   // the app may still be loading (noise model, camera)
                        ProcessPacket(buf, n, (bflags & CoreAudioConst.BUFFERFLAGS_TIMESTAMP_ERROR) == 0 ? p2 : 0);
                        hr = cap.GetNextPacketSize(out packet);
                        if (hr != 0) throw new COMException("Microphone disconnected", hr);
                    }
                }
                client.Stop();
            }
            finally
            {
                ExclusiveCapture = false;
                InputLevelDb = OutputLevelDb = -100f;
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                if (client != null) Marshal.ReleaseComObject(client);
                if (dev != null) Marshal.ReleaseComObject(dev);
                ev.Dispose();
            }
        }

        /// <summary>Device samples (16/24/32-bit integer or 32-bit float, little-endian) to floats.</summary>
        internal static void Decode(byte[] raw, int count, int bytesPerSample, bool isFloat, float[] dst)
        {
            if (isFloat) { Buffer.BlockCopy(raw, 0, dst, 0, count * 4); return; }
            for (int i = 0, o = 0; i < count; i++, o += bytesPerSample)
            {
                if (bytesPerSample == 2) dst[i] = (short)(raw[o] | raw[o + 1] << 8) / 32768f;
                else if (bytesPerSample == 3) dst[i] = ((raw[o] << 8 | raw[o + 1] << 16 | raw[o + 2] << 24) >> 8) / 8388608f;
                else dst[i] = (raw[o] | raw[o + 1] << 8 | raw[o + 2] << 16 | raw[o + 3] << 24) / 2147483648f;
            }
        }

        // ------------------------------------------------------------------ the mic chain, one packet at a time

        Biquad hpf; float hpfHz = -1, limGain = 1f;

        /// <summary>Effects on one mic packet, then into the cable's ring. qpc = when its first sample was recorded
        /// (100 ns units of the performance counter, 0 = unknown), for the latency readout.</summary>
        void ProcessPacket(float[] buf, int n, ulong qpc)
        {
            Fx.Clip.Feed(buf, n);
            Mixer.Mix(buf, n);   // more microphones (two people, one PC): mixed in before the whole voice chain

            // Input meter (raw mic).
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
            float gateInDb = LevelHistory.RmsDb(buf, n);
            if (!bypass) Gate.Process(buf, n);
            float cleanDb = LevelHistory.RmsDb(buf, n);
            CleanSpectrum.PushSamples(buf, n);
            if (!bypass)
            {
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
                    if (a > 0.89f) limGain *= 0.89f / a;           // instant attack
                    else limGain = Math.Min(1f, limGain + 0.00002f); // ~1 s release
                    x *= limGain;
                }
                if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
                buf[i] = x;
                float ax = Math.Abs(x); if (ax > outPk) outPk = ax;
            }
            OutputLevelDb = Math.Max((float)(20 * Math.Log10(Math.Max(outPk, 1e-6f))), OutputLevelDb - 1.5f);
            OutSpectrum.PushSamples(buf, n);
            History.Push(rawDb, gateInDb, cleanDb, LevelHistory.RmsDb(buf, n), bypass || Gate.IsOpen);

            outRing.Write(buf, n);
            if (monitorThread != null) monitorRing.Write(buf, n);
            // Low latency: this packet goes into the cable now (one packet + 1.5 ms ahead of its play position), not
            // when the writer's timer next wakes up; the timer only covers a late packet.
            var ks = liveKs;
            if (ks != null && ExclusiveCapture) ks.KickPacket(n * 1000f / SampleRate);

            // How long this packet's samples waited (on average) between being recorded and reaching the ring.
            if (qpc != 0)
            {
                double now = System.Diagnostics.Stopwatch.GetTimestamp() * (1e7 / System.Diagnostics.Stopwatch.Frequency);
                // Drivers stamp the packet's first sample (as documented) or, some USB ones, its last: a stamp younger
                // than the packet itself can only be the last sample.
                double age = (now - qpc) / 1e4, half = n * 500.0 / SampleRate;
                double ms = age >= 1.8 * half ? age - half : age + half;
                if (ms > -5 && ms < 500) CaptureWaitMs = CaptureWaitMs == 0 ? (float)ms : CaptureWaitMs * 0.95f + (float)ms * 0.05f;
            }
        }

        // ------------------------------------------------------------------ latency

        /// <summary>Use the mic in exclusive mode at its smallest period (lowest latency) when it allows.</summary>
        public volatile bool LowLatency = true;
        /// <summary>The mic runs exclusive right now / why it couldn't (then it runs shared).</summary>
        public volatile bool ExclusiveCapture;
        public volatile string ExclusiveError;
        string exclusiveFailedFor;
        volatile KsRender liveKs;
        /// <summary>Mic to cable, ms (smoothed): packet size, average wait from recording to the ring, time in the ring,
        /// and how far ahead of the cable's play position the samples are written.</summary>
        public volatile float PacketMs, CaptureWaitMs, BufferMs, CableLeadMs;
        /// <summary>Microphone to virtual cable, ms, without the noise remover's own frame delay.</summary>
        public float LatencyMs { get { return CaptureWaitMs + BufferMs + CableLeadMs; } }
        /// <summary>Times the cable caught up with the direct writer (each one = a tiny gap, and 0.5 ms more lead).</summary>
        public volatile int CableCatchUps;

        /// <summary>Time in the ring: the average of the fill before and after a timed read (Little's law); when the
        /// cable is topped up right after each packet, a packet leaves at once and only what's left after the read waits.</summary>
        void NoteBuffer(int before, int after, bool driven)
        {
            float ms = (driven ? after : (before + after) / 2f) * 1000f / SampleRate;
            BufferMs = BufferMs == 0 ? ms : BufferMs * 0.99f + ms * 0.01f;
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref int index);
        [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr h);

        /// <summary>Windows' "Pro Audio" scheduling for this thread (what DAWs use): far fewer late wake-ups.</summary>
        internal static IntPtr ProAudio() { int i = 0; try { return AvSetMmThreadCharacteristicsW("Pro Audio", ref i); } catch { return IntPtr.Zero; } }
        internal static void EndProAudio(IntPtr h) { if (h != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(h); } catch { } }

        /// <summary>
        /// True while the virtual mic is sent in exclusive mode. Exclusive streams bypass the Windows mixer, so
        /// Discord/OBS screen-share audio capture (which records every app's shared streams) can't pick up your mic.
        /// </summary>
        public volatile bool PrivateOutput;
        public volatile bool PreferPrivateOutput = true;
        /// <summary>Kernel-streaming path of the mic cable when its playback endpoint is hidden (disabled); null = WASAPI.</summary>
        public volatile string KsFeedPath;
        /// <summary>True while the mic goes straight into the cable driver (the hidden-feed path).</summary>
        public volatile bool DirectOutput;
        public volatile string DirectError;


        void RenderLoop(string id, SampleRing ring)
        {
            IntPtr mm = ring == outRing ? ProAudio() : IntPtr.Zero;
            try { RenderLoopInner(id, ring); }
            finally { EndProAudio(mm); }
        }

        void RenderLoopInner(string id, SampleRing ring)
        {
            int ksFails = 0;
            while (!stopFlag)
            {
                string ks = KsFeedPath;
                if (ring == outRing && ks != null)
                {
                    int t0 = Environment.TickCount;
                    string err = null;
                    try { RunRenderKs(ks, ring); }
                    catch (Exception ex) { err = ex.Message; }
                    DirectOutput = false;
                    if (stopFlag) break;
                    if (err == null) { ksFails = 0; continue; }
                    // A hiccup after running fine (sleep/resume, driver restart) or a few failed opens in a row: retry.
                    if (Environment.TickCount - t0 > 10000) ksFails = 0;
                    if (++ksFails < 4) { for (int i = 0; i < 10 && !stopFlag; i++) Thread.Sleep(100); continue; }
                    DirectError = err;
                    KsFeedPath = null;   // give up on the direct path: the endpoint gets switched back on below
                }
                if (ring == outRing && PreferPrivateOutput)
                {
                    try { RunRenderExclusive(id, ring); }
                    catch (Exception ex) { PrivateOutput = false; Status = "Running (shared output: " + ex.Message + ")"; }
                    if (stopFlag) break;
                }
                PrivateOutput = false;
                try { RunRender(id, ring); }
                catch (Exception ex) { if (ring == outRing) Status = "Output: " + ex.Message; }
                for (int i = 0; i < 20 && !stopFlag; i++) Thread.Sleep(100);
            }
        }

        const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);

        /// <summary>The mic straight into the cable driver's WaveRT buffer (its Windows endpoint can stay disabled).</summary>
        void RunRenderKs(string path, SampleRing ring)
        {
            using (var ks = new KsRender())
            {
                if (!ks.Open(path, SampleRate, 20)) throw new InvalidOperationException(ks.Error);
                liveKs = ks;
                ring.Clear();
                DirectOutput = true; PrivateOutput = true;
                if (Status == "Running" || Status.StartsWith("Running (")) Status = "Running";
                bool ok = ks.Run((dst, n) =>
                {
                    if (stopFlag) ks.Stop = true;
                    int before = ring.Fill;
                    ring.Read(dst, n);
                    NoteBuffer(before, ring.Fill, ExclusiveCapture);
                    CableLeadMs = ks.AheadMs;
                    CableCatchUps = ks.CatchUps;
                }, 2);   // VB-CABLE's play position jumps up to 1.5 ms at a time, the safety timer wakes every 0.5 ms
                liveKs = null;
                PrivateOutput = false;
                if (!ok && !stopFlag) throw new InvalidOperationException(ks.Error ?? "direct output stopped");
            }
        }

        /// <summary>Exclusive-mode output to the virtual cable (16/24/32-bit, 48 kHz stereo, event driven).</summary>
        void RunRenderExclusive(string id, SampleRing ring)
        {
            // A disabled feed can't be opened: switch it back on (needs admin; harmless otherwise).
            if (AudioDevices.State(id) == 2) { AudioDevices.SetEnabled(id, true); Thread.Sleep(400); }
            IMMDevice dev = null;
            IAudioClient client = null;
            IntPtr fmt = IntPtr.Zero;
            object svc = null;
            var ev = new AutoResetEvent(false);
            int bits = 0; bool flt = false;
            try
            {
                int hr = -1;
                int[][] tries = { new[] { 32, 1, 32 }, new[] { 32, 0, 24 }, new[] { 16, 0, 16 }, new[] { 32, 0, 32 } };   // container, float, valid bits
                foreach (var tr in tries)
                {
                    if (client != null) { Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev); }
                    client = OpenClient(id, out dev);
                    long def, min;
                    client.GetDevicePeriod(out def, out min);
                    if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                    fmt = AudioDevices.MakeFormat(2, 3, SampleRate, tr[1] == 1, tr[0]);
                    Marshal.WriteInt16(fmt, 18, (short)tr[2]);
                    hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, def, def, fmt, IntPtr.Zero);
                    if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
                    {
                        // The driver wants a slightly different period: use the size it reported.
                        uint fr; client.GetBufferSize(out fr);
                        long aligned = (long)(10000000.0 * fr / SampleRate + 0.5);
                        Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                        client = OpenClient(id, out dev);
                        hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, aligned, aligned, fmt, IntPtr.Zero);
                    }
                    if (hr == 0) { bits = tr[0]; flt = tr[1] == 1; break; }
                }
                if (hr != 0) throw new COMException("exclusive mode unavailable (0x" + hr.ToString("X8") + ")", hr);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint frames;
                client.GetBufferSize(out frames);
                Guid iid = CoreAudioConst.IID_IAudioRenderClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Output service failed", hr);
                var rc = (IAudioRenderClient)svc;
                IntPtr data;
                if (rc.GetBuffer(frames, out data) == 0) rc.ReleaseBuffer(frames, CoreAudioConst.BUFFERFLAGS_SILENT);
                ring.Clear();
                hr = client.Start();
                if (hr != 0) throw new COMException("Output start failed", hr);
                PrivateOutput = true;
                if (Status == "Running" || Status.StartsWith("Running (")) Status = "Running";

                int silentWaits = 0;
                int bps = bits / 8;
                var mono = new float[frames];
                var bytes = new byte[frames * 2 * bps];
                while (!stopFlag)
                {
                    if (!ev.WaitOne(200))
                    {
                        // No buffer requests for a while: Windows stopped the stream (e.g. device disabled under us).
                        if (++silentWaits >= 5) throw new COMException("Output stream stopped", unchecked((int)0x88890004));
                        continue;
                    }
                    silentWaits = 0;
                    hr = rc.GetBuffer(frames, out data);
                    if (hr != 0) throw new COMException("Output write failed", hr);
                    int before = ring.Fill; CableLeadMs = frames * 1000f / SampleRate;   // one exclusive period queued
                    ring.Read(mono, (int)frames);
                    NoteBuffer(before, ring.Fill, false);
                    for (int i = 0; i < frames; i++)
                    {
                        float v = mono[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        int o = i * 2 * bps;
                        if (flt) { var b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, bytes, o, 4); Buffer.BlockCopy(b, 0, bytes, o + 4, 4); }
                        else if (bps == 2) { short s = (short)(v * 32767); bytes[o] = (byte)s; bytes[o + 1] = (byte)(s >> 8); bytes[o + 2] = bytes[o]; bytes[o + 3] = bytes[o + 1]; }
                        else
                        {
                            int s = (int)(v * 2147483392.0);
                            bytes[o] = (byte)s; bytes[o + 1] = (byte)(s >> 8); bytes[o + 2] = (byte)(s >> 16); bytes[o + 3] = (byte)(s >> 24);
                            Buffer.BlockCopy(bytes, o, bytes, o + 4, 4);
                        }
                    }
                    Marshal.Copy(bytes, 0, data, (int)frames * 2 * bps);
                    rc.ReleaseBuffer(frames, 0);
                }
                client.Stop();
            }

            finally
            {
                PrivateOutput = false;
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                if (client != null) Marshal.ReleaseComObject(client);
                if (dev != null) Marshal.ReleaseComObject(dev);
                ev.Dispose();
            }
        }

        void RunRender(string id, SampleRing ring)
        {
            IMMDevice dev;
            var client = OpenClient(id, out dev);
            IntPtr fmt = AllocFormat();
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 300000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Output init failed (0x" + hr.ToString("X8") + ")", hr);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint bufferFrames;
                client.GetBufferSize(out bufferFrames);
                Guid iid = CoreAudioConst.IID_IAudioRenderClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Output service failed", hr);
                var rc = (IAudioRenderClient)svc;

                IntPtr data;
                if (rc.GetBuffer(bufferFrames, out data) == 0) rc.ReleaseBuffer(bufferFrames, CoreAudioConst.BUFFERFLAGS_SILENT);
                ring.Clear();
                hr = client.Start();
                if (hr != 0) throw new COMException("Output start failed", hr);

                var tmp = new float[bufferFrames];
                while (!stopFlag)
                {
                    ev.WaitOne(200);
                    uint padding;
                    hr = client.GetCurrentPadding(out padding);
                    if (hr != 0) throw new COMException("Output device lost", hr);
                    uint avail = bufferFrames - padding;
                    if (avail == 0) continue;
                    hr = rc.GetBuffer(avail, out data);
                    if (hr != 0) throw new COMException("Output write failed", hr);
                    int before = ring.Fill;
                    if (ring == outRing) CableLeadMs = padding * 1000f / SampleRate + 10;   // queued + the shared engine's 10 ms period
                    ring.Read(tmp, (int)avail);
                    if (ring == outRing) NoteBuffer(before, ring.Fill, false);
                    Marshal.Copy(tmp, 0, data, (int)avail);
                    rc.ReleaseBuffer(avail, 0);
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
