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

        static IntPtr AllocFormat()
        {
            var wf = new WaveFormatEx { wFormatTag = 3, nChannels = 1, nSamplesPerSec = SampleRate, nAvgBytesPerSec = SampleRate * 4, nBlockAlign = 4, wBitsPerSample = 32, cbSize = 0 };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatEx)));
            Marshal.StructureToPtr(wf, p, false);
            return p;
        }

        static IAudioClient OpenClient(string id, out IMMDevice device)
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
            int fails = 0;
            while (!stopFlag)
            {
                int t0 = Environment.TickCount;
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
                Biquad hpf = null; float hpfHz = -1;
                float limGain = 1f;
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
                        Fx.Clip.Feed(buf, n);

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
                ring.Clear();
                DirectOutput = true; PrivateOutput = true;
                if (Status == "Running" || Status.StartsWith("Running (")) Status = "Running";
                bool ok = ks.Run((dst, n) => { if (stopFlag) ks.Stop = true; ring.Read(dst, n); }, 8);
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
                    ring.Read(mono, (int)frames);
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
                    ring.Read(tmp, (int)avail);
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
