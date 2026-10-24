using System;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>The voice effects in order: de-hum, low/high cut, noise removal, gate, EQ, de-esser, compressor, auto level, volume, limiter.</summary>
    internal sealed class VoiceChain
    {
        const int Fs = AudioEngine.SampleRate;
        /// <summary>Picks the input(s) of a multi-channel mic before anything else.</summary>
        public readonly InputChannels Channels = new InputChannels();
        public readonly NoiseSuppressor Suppressor = new NoiseSuppressor();
        public readonly NoiseGate Gate = new NoiseGate(Fs);
        public readonly EqChain Eq = new EqChain();
        public readonly AutoLevel Leveler = new AutoLevel(Fs);
        public readonly DeHum Hum = new DeHum();
        public readonly HighCut Hiss = new HighCut();
        public readonly Compressor Comp = new Compressor(Fs);
        public readonly DeEsser Ess = new DeEsser(Fs);
        public volatile float HighPassHz = 80f;
        public volatile float VolumeDb = 0f;
        public volatile bool Limiter = true;
        Biquad hpf; float hpfHz = -1, limGain = 1f;

        /// <summary>Copies every voice setting into this chain (the live mic and the test clip use the same settings).</summary>
        public void Configure(AppSettings S)
        {
            Channels.Mode = string.IsNullOrEmpty(S.MicChannel) ? InputChannels.Auto : S.MicChannel;
            Suppressor.Enabled = S.NoiseRemoval;
            Suppressor.UseAi = S.NsEngine != NoiseEngines.Classic;
            Suppressor.Dfn = S.NsEngine == NoiseEngines.DfnGpu ? 2 : S.NsEngine == NoiseEngines.DfnCpu ? 1 : 0;
            Suppressor.Strength = S.Strength / 100f;
            Suppressor.AdaptSpeed = S.AdaptSpeed / 100f;
            Suppressor.Smoothing = S.NsSmoothing / 100f;
            Suppressor.VoiceProtect = S.NsVoiceProtect / 100f;
            HighPassHz = S.HighPassHz;
            Hiss.Hz = S.HighCutHz;
            Hum.Hz = S.DeHumHz;
            Gate.Enabled = S.Gate;
            Gate.Expander = S.GateExpander;
            Gate.ExpanderRatio = S.ExpanderRatio / 10f;
            Gate.ThresholdDb = S.GateThreshold;
            Gate.AttackMs = S.GateAttack;
            Gate.HoldMs = S.GateHold;
            Gate.ReleaseMs = S.GateRelease;
            Gate.RangeDb = S.GateRange;
            Comp.Enabled = S.Compressor;
            Comp.ThresholdDb = S.CompThreshold;
            Comp.Ratio = S.CompRatio / 10f;
            Comp.AttackMs = S.CompAttack;
            Comp.ReleaseMs = S.CompRelease;
            Comp.MakeupDb = S.CompMakeup;
            Ess.Enabled = S.DeEsser;
            Ess.FreqHz = S.DeEssFreq;
            Ess.AmountDb = S.DeEssAmount;
            VolumeDb = S.VolumeDb;
            Limiter = S.Limiter;
            Leveler.Enabled = S.AutoLevel;
            Leveler.TargetDb = S.AutoLevelTarget;
            Leveler.MaxBoostDb = S.AutoLevelMaxBoost;
            Eq.Update(S.MicEq, Fs);
        }

        /// <summary>Runs the whole chain on a block (used for the test clip; the live engine runs the same steps with meters in between).</summary>
        public void Process(float[] buf, int n, bool bypass)
        {
            float hz = HighPassHz;
            if (hz != hpfHz) { hpfHz = hz; hpf = hz >= 20 ? Biquad.HighPass(hz, Fs) : null; }
            if (!bypass)
            {
                Hum.Process(buf, n);
                if (hpf != null) for (int i = 0; i < n; i++) buf[i] = hpf.Process(buf[i]);
                Hiss.Process(buf, n);
            }
            Suppressor.Bypass = bypass;
            Suppressor.Process(buf, n);
            if (!bypass)
            {
                Gate.Process(buf, n);
                Eq.Process(buf, n);
                Ess.Process(buf, n);
                Comp.Process(buf, n);
                Leveler.Process(buf, n);
            }
            float vol = bypass ? 1f : (float)Math.Pow(10, VolumeDb / 20);
            bool lim = Limiter;
            for (int i = 0; i < n; i++)
            {
                float x = buf[i] * vol;
                if (lim)
                {
                    float a = Math.Abs(x) * limGain;
                    if (a > 0.89f) limGain *= 0.89f / a; else limGain = Math.Min(1f, limGain + 0.00002f);
                    x *= limGain;
                }
                buf[i] = x > 1f ? 1f : x < -1f ? -1f : x;
            }
        }
    }

#if !LINUX
    /// <summary>
    /// Record a few seconds of your raw mic, then loop it through a copy of the voice chain on your headphones.
    /// Settings changes are heard immediately (the chain keeps adapting, just like live), and "original" plays it untouched.
    /// </summary>
    internal sealed class VoiceTest
    {
        const int Fs = AudioEngine.SampleRate;
        public const int MaxSeconds = 15;
        public readonly VoiceChain Chain = new VoiceChain();
        public VoiceTest() { Chain.Suppressor.Synchronous = true; }
        float[] recBuf, clip;
        volatile int recPos;
        int playPos;
        Thread player;
        volatile bool stopPlay;
        public volatile bool Original;
        public volatile string Error;
        public float Level = -100f;

        public bool Recording { get { return recBuf != null; } }
        public bool Playing { get { return player != null && player.IsAlive; } }
        public bool HasClip { get { return clip != null && clip.Length > Fs / 2; } }
        public double RecordedSeconds { get { var b = recBuf; return b != null ? recPos / (double)Fs : clip != null ? clip.Length / (double)Fs : 0; } }
        public double ClipSeconds { get { return clip != null ? clip.Length / (double)Fs : 0; } }
        public double PlaySeconds { get { return playPos / (double)Fs; } }

        public void StartRecording() { StopPlaying(); recPos = 0; recBuf = new float[Fs * MaxSeconds]; }

        public void StopRecording()
        {
            var b = recBuf;
            if (b == null) return;
            recBuf = null;
            int n = Math.Min(recPos, b.Length);
            var c = new float[n];
            Array.Copy(b, c, n);
            clip = c;
        }

        /// <summary>Called by the mic thread with raw audio (before any effects).</summary>
        public void Feed(float[] raw, int n)
        {
            var b = recBuf;
            if (b == null) return;
            int k = Math.Min(n, b.Length - recPos);
            if (k > 0) { Array.Copy(raw, 0, b, recPos, k); recPos += k; }
            if (recPos >= b.Length) StopRecording();
        }

        public void Play(string deviceId)
        {
            StopPlaying();
            if (!HasClip || string.IsNullOrEmpty(deviceId)) return;
            Error = null;
            stopPlay = false;
            playPos = 0;
            player = new Thread(() =>
            {
                try { Run(deviceId); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "Voice test", Priority = ThreadPriority.AboveNormal };
            player.Start();
        }

        public void StopPlaying()
        {
            stopPlay = true;
            var t = player;
            if (t != null) t.Join(1000);
            player = null;
        }

        void Run(string id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object o = null, svc = null;
            IntPtr fmt = AudioDevices.MakeFormat(1, 4, Fs, true, 32);
            var ev = new AutoResetEvent(false);
            try
            {
                if (en.GetDevice(id, out dev) != 0 || dev == null) throw new InvalidOperationException("Headphones not found");
                Guid iid = CoreAudioConst.IID_IAudioClient;
                if (dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o) != 0) throw new InvalidOperationException("Couldn't open the headphones");
                var client = (IAudioClient)o;
                int hr = client.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 400000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Couldn't start playback (0x" + hr.ToString("X8") + ")", hr);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint frames; client.GetBufferSize(out frames);
                Guid ir = CoreAudioConst.IID_IAudioRenderClient;
                client.GetService(ref ir, out svc);
                var rc = (IAudioRenderClient)svc;
                client.Start();
                var block = new float[480];
                var queue = new float[frames + 480];
                int queued = 0;
                while (!stopPlay)
                {
                    ev.WaitOne(100);
                    uint pad; if (client.GetCurrentPadding(out pad) != 0) throw new InvalidOperationException("Headphones disconnected");
                    int want = (int)(frames - pad);
                    // Process in 10 ms blocks so effects behave exactly as on the live mic.
                    while (queued < want)
                    {
                        var c = clip;
                        for (int i = 0; i < 480; i++) { block[i] = c[playPos]; if (++playPos >= c.Length) playPos = 0; }
                        Chain.Process(block, 480, Original);
                        Array.Copy(block, 0, queue, queued, 480);
                        queued += 480;
                    }
                    if (want <= 0) continue;
                    IntPtr data;
                    if (rc.GetBuffer((uint)want, out data) != 0) continue;
                    Marshal.Copy(queue, 0, data, want);
                    rc.ReleaseBuffer((uint)want, 0);
                    float pk = 0; for (int i = 0; i < want; i++) pk = Math.Max(pk, Math.Abs(queue[i]));
                    Level = (float)(20 * Math.Log10(Math.Max(pk, 1e-6f)));
                    Array.Copy(queue, want, queue, 0, queued - want);
                    queued -= want;
                }
                client.Stop();
            }
            finally
            {
                Level = -100f;
                Chain.Suppressor.ReleaseEngine();   // don't keep a second DeepFilterNet loaded after the test
                Marshal.FreeHGlobal(fmt);
                if (svc != null) Marshal.ReleaseComObject(svc);
                if (o != null) Marshal.ReleaseComObject(o);
                if (dev != null) Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
                ev.Dispose();
            }
        }
    }
#endif
}
