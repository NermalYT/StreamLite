using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>The voice effects in order: de-hum, low/high cut, noise removal, gate, studio voice, EQ, de-esser, compressor, auto level, volume, limiter.</summary>
    internal sealed class VoiceChain
    {
        const int Fs = AudioEngine.SampleRate;
        /// <summary>Picks the input(s) of a multi-channel mic before anything else.</summary>
        public readonly InputChannels Channels = new InputChannels();
        /// <summary>Is the mic clipping before StreamLite gets it?</summary>
        public readonly ClipWatch Clip = new ClipWatch();
        public readonly NoiseSuppressor Suppressor = new NoiseSuppressor();
        public readonly NoiseGate Gate = new NoiseGate(Fs);
        public readonly StudioVoice Studio = new StudioVoice();
        public readonly EqChain Eq = new EqChain();
        public readonly AutoLevel Leveler = new AutoLevel(Fs);
        /// <summary>"Only my voice": someone else's voice turned down (after noise removal).</summary>
        public readonly VoiceLock Lock = new VoiceLock();
        /// <summary>Across the room: the treble distance takes off, back (a gentle shelf over 3.5 kHz).</summary>
        public volatile float PresenceDb;
        Biquad presence; float presenceAt = -1;
        public readonly DeHum Hum = new DeHum();
        public readonly HighCut Hiss = new HighCut();
        public readonly Compressor Comp = new Compressor(Fs);
        public readonly DeEsser Ess = new DeEsser(Fs);
        public volatile float HighPassHz = 80f;
        public volatile float VolumeDb = 0f;
        public volatile bool Limiter = true;
        Biquad hpf; float hpfHz = -1, limGain = 1f;

        /// <summary>What the chain adds to the mic's delay: the noise remover's own path and studio voice.</summary>
        public float DelayMs { get { return Suppressor.DelayMs + (Studio.Active ? StudioVoice.DelayMs : 0); } }

        /// <summary>The presence shelf on a block (live engine and test clip alike).</summary>
        public void Presence(float[] buf, int n)
        {
            float db = PresenceDb;
            if (db != presenceAt) { presenceAt = db; presence = Math.Abs(db) > 0.05f ? Biquad.Create(new EqFilter { Type = "HSC", Freq = 3500, Gain = db, Q = 0.7 }, Fs) : null; }
            var p = presence;
            if (p != null) for (int i = 0; i < n; i++) buf[i] = p.Process(buf[i]);
        }

        /// <summary>Copies every voice setting into this chain (the live mic and the test clip use the same settings).</summary>
        public void Configure(AppSettings S)
        {
            Channels.Mode = string.IsNullOrEmpty(S.MicChannel) ? InputChannels.Auto : S.MicChannel;
            Suppressor.Enabled = S.NoiseRemoval;
            Suppressor.UseAi = S.NsEngine != NoiseEngines.Classic;
            Suppressor.Dfn = S.NsEngine == NoiseEngines.DfnGpu ? 2 : S.NsEngine == NoiseEngines.DfnCpu ? 1 : 0;
            Suppressor.Realtime = S.NsRealtime;
            Suppressor.LowVram = S.LowVram;
            Suppressor.Strength = S.Strength / 100f;
            Suppressor.AdaptSpeed = S.AdaptSpeed / 100f;
            Suppressor.Smoothing = S.NsSmoothing / 100f;
            Suppressor.VoiceProtect = S.NsVoiceProtect / 100f;
            Suppressor.Print.Enabled = S.NoisePrintOn;
            Suppressor.Print.Strength = S.NoisePrintStrength / 100f;
            Studio.Enabled = S.StudioVoice;
            Studio.Strength = S.StudioStrength / 100f;
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
            // Across the room the voice reaches the mic about 20 dB quieter: room for that much more level.
            Leveler.MaxBoostDb = S.FarVoice ? Math.Max(S.AutoLevelMaxBoost, 40) : S.AutoLevelMaxBoost;
            Leveler.Average = S.FarVoice;
            Lock.Enabled = S.VoiceLockOn;
            PresenceDb = S.FarVoice ? S.FarPresenceDb : 0;
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
            if (!bypass) Lock.Process(buf, n);
            Studio.Bypass = bypass;
            Studio.Process(buf, n);
            if (!bypass) Gate.Process(buf, n);
            if (!bypass)
            {
                Presence(buf, n);
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
        public VoiceTest() { Chain.Suppressor.Synchronous = true; Load(); }

        /// <summary>
        /// Compare: the test clip uses this noise engine (off, ai, classic, dfn-cpu, dfn-gpu) instead of yours - your
        /// live mic doesn't change until you pick "Use this one". null = your settings.
        /// </summary>
        public volatile string EngineOverride;
        /// <summary>Plays the processed clip at the original's loudness, so you compare the sound, not the volume.</summary>
        public volatile bool MatchLoudness = true;
        /// <summary>The gain loudness matching applies right now (dB), for the window.</summary>
        public volatile float MatchDb;
        double clipLoud, outLoud, matchGain = 1;
        AppSettings lastSettings;

        /// <summary>The test chain gets your settings (and the compare engine, when one is picked).</summary>
        public void Configure(AppSettings S)
        {
            lastSettings = S;
            Chain.Configure(S);
            string o = EngineOverride;
            if (o == null) return;
            Chain.Suppressor.Enabled = o != "off";
            if (o == "off") return;
            Chain.Suppressor.UseAi = o != NoiseEngines.Classic;
            Chain.Suppressor.Dfn = o == NoiseEngines.DfnGpu ? 2 : o == NoiseEngines.DfnCpu ? 1 : 0;
        }

        /// <summary>
        /// Loudness matching for one processed block: the processed sound at the original recording's speech loudness
        /// (the gain eases in over ~2 s of speech). The original (A/B) plays untouched.
        /// </summary>
        internal void Match(float[] block, int n)
        {
            double p = 0; for (int i = 0; i < n; i++) p += block[i] * block[i];
            p /= n;
            if (!Original && p > 1e-5) outLoud = outLoud == 0 ? p : outLoud * 0.995 + p * 0.005;
            double target = MatchLoudness && !Original && outLoud > 0 && clipLoud > 0 ? Math.Sqrt(clipLoud / outLoud) : 1;
            target = Math.Max(0.18, Math.Min(5.6, target));   // +-15 dB
            matchGain += (target - matchGain) * 0.02;
            MatchDb = (float)(20 * Math.Log10(matchGain));
            if (Math.Abs(matchGain - 1) > 1e-4) for (int i = 0; i < n; i++) { float v = (float)(block[i] * matchGain); block[i] = v > 1f ? 1f : v < -1f ? -1f : v; }
        }

        /// <summary>Picks the compare engine (null = your settings) and applies it right away.</summary>
        public void Compare(string engine) { EngineOverride = engine; if (lastSettings != null) Configure(lastSettings); }

        /// <summary>Loudness of speech in a buffer: mean power of the 10 ms blocks above -50 dBFS (silence doesn't count).</summary>
        internal static double SpeechPower(float[] x, int len)
        {
            double sum = 0; int n = 0;
            for (int b = 0; b + 480 <= len; b += 480)
            {
                double p = 0; for (int i = b; i < b + 480; i++) p += x[i] * x[i];
                p /= 480;
                if (p > 1e-5) { sum += p; n++; }
            }
            return n > 0 ? sum / n : 0;
        }

        /// <summary>The recording is kept on this PC only (next to the settings) so you can compare again later.</summary>
        static string SamplePath { get { return Path.Combine(AppSettings.Folder, "voice-sample.wav"); } }

        void Load()
        {
            try
            {
                if (!File.Exists(SamplePath)) return;
                var bytes = File.ReadAllBytes(SamplePath);
                int n = (bytes.Length - 44) / 4;
                if (n < Fs / 2 || n > Fs * MaxSeconds) return;
                var c = new float[n];
                Buffer.BlockCopy(bytes, 44, c, 0, n * 4);
                clip = c; clipLoud = SpeechPower(c, n);
            }
            catch { }
        }

        void Save()
        {
            try
            {
                var c = clip; if (c == null) return;
                Directory.CreateDirectory(AppSettings.Folder);
                using (var w = new BinaryWriter(File.Create(SamplePath)))
                {
                    int bytes = c.Length * 4;
                    w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVEfmt ".ToCharArray());
                    w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(Fs); w.Write(Fs * 4); w.Write((short)4); w.Write((short)32);
                    w.Write("data".ToCharArray()); w.Write(bytes);
                    var buf = new byte[bytes]; Buffer.BlockCopy(c, 0, buf, 0, bytes); w.Write(buf);
                }
            }
            catch { }
        }

        /// <summary>Forgets the recording (and deletes the saved copy).</summary>
        public void DeleteSample()
        {
            StopPlaying(); clip = null;
            try { if (File.Exists(SamplePath)) File.Delete(SamplePath); } catch { }
        }
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
            clip = c; clipLoud = SpeechPower(c, n); outLoud = 0; matchGain = 1;
            if (n >= Fs / 2) Save();
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

        /// <summary>What Studio voice learned about the live mic (the clip was recorded through it): used as is.</summary>
        public Func<string> StudioProfile;

        public void Play(string deviceId)
        {
            StopPlaying();
            if (!HasClip || string.IsNullOrEmpty(deviceId)) return;
            var sp = StudioProfile; if (sp != null) Chain.Studio.LoadProfile(sp());
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

        /// <summary>The next 10 ms of the clip through the chain (blocks of 10 ms, so effects behave exactly as on the live mic).</summary>
        void NextBlock(float[] block)
        {
            var c = clip;
            for (int i = 0; i < 480; i++) { block[i] = c[playPos]; if (++playPos >= c.Length) playPos = 0; }
            Chain.Process(block, 480, Original);
            Match(block, 480);
        }

        void Run(string id)
        {
            // 3D audio holds the headphones exclusively (Direct to headphones): play through its stream instead.
            var direct = HeadphonesDirect.Open(id);
            if (direct != null) { RunDirect(direct); return; }
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
                    while (queued < want)
                    {
                        NextBlock(block);
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

        void RunDirect(StereoRing ring)
        {
            try
            {
                var block = new float[480];
                while (!stopPlay)
                {
                    if (!HeadphonesDirect.WaitRoom(ring, () => stopPlay))
                    {
                        if (stopPlay) break;
                        throw new InvalidOperationException("The headphones stopped (3D audio let them go) - press Play again");
                    }
                    NextBlock(block);
                    ring.Write(block, block, 480); ring.InputArrived();
                    float pk = 0; for (int i = 0; i < 480; i++) pk = Math.Max(pk, Math.Abs(block[i]));
                    Level = (float)(20 * Math.Log10(Math.Max(pk, 1e-6f)));
                }
            }
            finally
            {
                HeadphonesDirect.Close(ring);
                Level = -100f;
                Chain.Suppressor.ReleaseEngine();
            }
        }
    }
#endif
}
