using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// One more microphone mixed into the virtual mic (two people, two mics, one PC). It records in shared mode on its
    /// own thread, so other apps can still use it, into a drift-corrected ring: every mic has its own clock, and the
    /// ring reads it at the main mic's pace without clicks or slowly growing delay. Unplug it and it waits for it.
    /// </summary>
    internal sealed class ExtraMic
    {
        public readonly string Id;
        public volatile float GainDb;
        public volatile float LevelDb = -100f;      // raw input, for the meter
        public volatile float Share = 0;            // automix: how much of the mix this mic gets right now (0..1)
        public volatile string Status = "Starting";
        public volatile bool Running;
        /// <summary>This mic muted on its own (hotkey).</summary>
        public volatile bool Muted;
        internal float MuteGain = 1f;
        /// <summary>This mic's own EQ (before it's mixed; the voice chain's EQ then applies to everyone).</summary>
        public readonly EqChain Eq = new EqChain();
        public int Dropouts { get { return ring.Underruns; } }
        readonly SampleRing ring = new SampleRing(AudioEngine.SampleRate, 0);
        readonly InputChannels channels = new InputChannels();
        volatile bool stop;
        Thread thread;

        public ExtraMic(string id, float gainDb) { Id = id; GainDb = gainDb; ring.SetPacket(480); }

        public void Start()
        {
            stop = false;
            thread = new Thread(Loop) { IsBackground = true, Name = "Extra mic", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            var t = thread; if (t != null) t.Join(1500);
            Running = false; LevelDb = -100f; Status = "Stopped";
        }

        /// <summary>n samples at the main mic's pace (silence until it has started).</summary>
        public void Read(float[] dst, int n) { ring.Read(dst, n); }
        /// <summary>Recorded samples in (the capture thread; tests feed it directly).</summary>
        internal void Feed(float[] src, int n) { ring.Write(src, n); }
        internal int Fill { get { return ring.Fill; } }
        internal int Margin { get { return ring.Margin; } }

        void Loop()
        {
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                int fails = 0;
                while (!stop)
                {
                    int t0 = Environment.TickCount;
                    try { Run(); }
                    catch (Exception ex) { Status = ex.Message; }
                    Running = false; LevelDb = -100f;
                    if (Environment.TickCount - t0 > 5000) fails = 0;
                    for (int i = 0, waits = ++fails <= 3 ? 3 : 20; i < waits && !stop; i++) Thread.Sleep(100);
                }
            }
            finally { AudioEngine.EndProAudio(mm); }
        }

        void Run()
        {
            int state = AudioDevices.State(Id);
            if (state == 2) throw new InvalidOperationException("turned off in Windows (Sound settings > Recording)");
            if (state >= 4) throw new InvalidOperationException("unplugged - waiting");
            IMMDevice dev;
            var client = AudioEngine.OpenClient(Id, out dev);
            bool raw = RawCapture.Request(client, Id);   // its raw sound too (none of Windows' or its maker's effects)
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(Id, out ch, out mask, out rate) || ch < 2 || ch > 32) { ch = 1; mask = 0; }
            IntPtr fmt = ch > 1 ? AudioDevices.MakeFormat(ch, mask, AudioEngine.SampleRate, true, 32) : AudioEngine.AllocFormat();
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                if (hr != 0 && raw)
                {
                    Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                    client = AudioEngine.OpenClient(Id, out dev);
                    hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
                }
                if (hr != 0) throw new COMException(DeviceErrors.Describe(hr, true), hr);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) throw new COMException("Microphone service failed", hr);
                var cap = (IAudioCaptureClient)svc;
                hr = client.Start();
                if (hr != 0) throw new COMException("Microphone start failed", hr);
                Status = "Running"; Running = true;
                ring.Grace(2000);
                var inter = new float[AudioEngine.SampleRate * ch];
                var buf = new float[AudioEngine.SampleRate];
                int quiet = 0;
                while (!stop)
                {
                    if (!ev.WaitOne(200)) { if (++quiet >= 10) throw new COMException("stopped delivering - reconnecting", unchecked((int)0x88890004)); continue; }
                    quiet = 0;
                    uint packet;
                    hr = cap.GetNextPacketSize(out packet);
                    if (hr != 0) throw new COMException("unplugged - waiting for it to come back", hr);
                    while (packet > 0 && !stop)
                    {
                        IntPtr data; uint frames, bflags; ulong p1, p2;
                        hr = cap.GetBuffer(out data, out frames, out bflags, out p1, out p2);
                        if (hr != 0) throw new COMException("unplugged - waiting for it to come back", hr);
                        int n = (int)Math.Min(frames, (uint)buf.Length);
                        if ((bflags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0) Array.Clear(buf, 0, n);
                        else { Marshal.Copy(data, inter, 0, n * ch); channels.Process(inter, n, ch, buf); }
                        Eq.Process(buf, n);
                        cap.ReleaseBuffer(frames);
                        float pk = 0;
                        for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > pk) pk = a; }
                        LevelDb = Math.Max((float)(20 * Math.Log10(Math.Max(pk, 1e-6f))), LevelDb - 1.5f);
                        Feed(buf, n);
                        hr = cap.GetNextPacketSize(out packet);
                        if (hr != 0) throw new COMException("unplugged - waiting for it to come back", hr);
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
    }

    /// <summary>
    /// Mixes the extra microphones into the main one, before noise removal and the rest of the voice chain.
    /// Auto mix (on by default) shares the gain by who is talking - each mic gets its share of the total level - so the
    /// person speaking comes through on their own mic while the other mic (which hears them too, from a desk away) is
    /// turned down: no hollow double voice, and the background noise of N open mics doesn't add up.
    /// </summary>
    internal sealed class MicMixer
    {
        volatile ExtraMic[] mics = new ExtraMic[0];
        public volatile bool AutoMix = true;
        public volatile float MainGainDb;
        /// <summary>Microphone 1's own EQ (before mixing).</summary>
        public readonly EqChain MainEq = new EqChain();
        /// <summary>Main mic's share of the mix right now (automix), for the meter.</summary>
        public volatile float MainShare = 1;
        float[] tmp = new float[0];
        float[] env = new float[0], gain = new float[0], own = new float[0], raw = new float[0], trim = new float[0], held = new float[0];
        float[][] hist; int histPos;
        readonly object gate = new object();

        /// <summary>Device id of microphone 1 (for the measured bleed between mics).</summary>
        public volatile string MainId;
        /// <summary>
        /// Measured by the calibration: how much of each person's voice reaches the other mics, as a power ratio,
        /// from mic id -> to mic id (0.06 = the other mic hears you 12 dB quieter). Auto mix subtracts that expected
        /// bleed, so a mic gets the mix for its own person's voice only: one voice on two mics doesn't double, and two
        /// people talking at once both stay in.
        /// </summary>
        public volatile Dictionary<string, Dictionary<string, float>> Bleed;
        /// <summary>Calibration: each mic's level in every packet (power, before its volume), main mic first.</summary>
        public volatile Action<float[]> PowerTap;
        float[,] bleedM; ExtraMic[] bleedFor; Dictionary<string, Dictionary<string, float>> bleedSrc; string bleedMain;

        public ExtraMic[] Mics { get { return mics; } }
        /// <summary>Tests: mix these (not started) mics.</summary>
        internal void Use(params ExtraMic[] list) { mics = list; }

        /// <summary>The extra mics to use (id -> gain dB). Mics already running keep running; removed ones stop.</summary>
        public void Set(IList<KeyValuePair<string, float>> wanted)
        {
            lock (gate)
            {
                var old = mics;
                var next = new List<ExtraMic>();
                foreach (var w in wanted)
                {
                    var have = old.FirstOrDefault(m => m.Id == w.Key);
                    if (have == null) { have = new ExtraMic(w.Key, w.Value); have.Start(); }
                    else have.GainDb = w.Value;
                    if (!next.Contains(have)) next.Add(have);
                }
                mics = next.ToArray();
                foreach (var m in old) if (!next.Contains(m)) m.Stop();
            }
        }

        public void StopAll() { Set(new KeyValuePair<string, float>[0]); }

        /// <summary>Mixes the extra mics into buf (the main mic, n samples) in place.</summary>
        public void Mix(float[] buf, int n)
        {
            var list = mics;
            int k = list.Length;
            MainEq.Process(buf, n);
            if (k == 0) { MainShare = 1; Fade(ref mainMute, MainMuted, buf, 0, n); return; }
            int total = k + 1;
            if (tmp.Length < total * n) tmp = new float[total * n];
            if (env.Length != total) { env = new float[total]; gain = new float[total]; for (int i = 0; i < total; i++) gain[i] = 1f / total; }
            // Channel 0 = main mic, 1.. = extra mics, each with its trim.
            if (raw.Length != total) { raw = new float[total]; trim = new float[total]; own = new float[total]; held = new float[total]; }
            float g0 = (float)Math.Pow(10, MainGainDb / 20);
            trim[0] = g0;
            raw[0] = Power(buf, 0, n);
            for (int i = 0; i < n; i++) tmp[i] = buf[i] * g0;
            Fade(ref mainMute, MainMuted, tmp, 0, n);
            for (int m = 0; m < k; m++)
            {
                var mic = list[m];
                ReadInto(mic, tmp, (m + 1) * n, n);
                raw[m + 1] = Power(tmp, (m + 1) * n, n);
                float g = (float)Math.Pow(10, mic.GainDb / 20);
                trim[m + 1] = g;
                if (g != 1f) for (int i = 0; i < n; i++) tmp[(m + 1) * n + i] *= g;
                Fade(ref mic.MuteGain, mic.Muted, tmp, (m + 1) * n, n);
            }
            var tap = PowerTap;
            if (tap != null) tap((float[])raw.Clone());

            // Level of each mic in this packet; fast up (~5 ms), slower down (~150 ms) so a word's tail isn't cut.
            float dt = n / (float)AudioEngine.SampleRate;
            float up = 1f - (float)Math.Exp(-dt / 0.005), down = 1f - (float)Math.Exp(-dt / 0.15);
            float sum = 0;
            for (int c = 0; c < total; c++)
            {
                double p = 0;
                for (int i = 0; i < n; i++) { float v = tmp[c * n + i]; p += v * v; }
                float pw = (float)(p / n) + 1e-12f;
                env[c] += (pw > env[c] ? up : down) * (pw - env[c]);
                sum += env[c];
            }
            bool auto = AutoMix;
            // Calibrated: take out of each mic the part of its level that is the other people's voices.
            var bm = auto ? BleedMatrix(list) : null;
            if (bm != null)
            {
                // Each mic's level over the last ~40 ms: an added mic reaches the mix 10-20 ms after mic 1 (its sync
                // buffer and the distance between desks), so the copy of a word on the other mic outlasts the word.
                int h = Math.Max(1, Math.Min(32, (int)Math.Ceiling(0.04 * AudioEngine.SampleRate / n)));
                if (hist == null || hist.Length != total || hist[0].Length != h) { hist = new float[total][]; for (int c = 0; c < total; c++) hist[c] = new float[h]; histPos = 0; }
                histPos = (histPos + 1) % h;
                for (int c = 0; c < total; c++) { hist[c][histPos] = env[c]; float mx = 0; foreach (var v in hist[c]) if (v > mx) mx = v; held[c] = mx; }
                sum = 0;
                for (int c = 0; c < total; c++)
                {
                    float o = env[c];
                    for (int j = 0; j < total; j++)
                        if (j != c && bm[j, c] > 0) { float t = trim[c] / trim[j]; o -= bm[j, c] * t * t * held[j]; }
                    own[c] = Math.Max(o, 0.02f * env[c]);
                    sum += own[c];
                }
            }
            Array.Clear(buf, 0, n);
            for (int c = 0; c < total; c++)
            {
                float target = auto ? (bm != null ? own[c] : env[c]) / sum : 1f;
                float from = gain[c], step = (target - from) / n;
                for (int i = 0; i < n; i++) buf[i] += tmp[c * n + i] * (from + step * (i + 1));
                gain[c] = target;
                if (c == 0) MainShare = auto ? target : 1; else list[c - 1].Share = auto ? target : 1;
            }
        }

        static float Power(float[] x, int off, int n)
        {
            double p = 0;
            for (int i = 0; i < n; i++) { float v = x[off + i]; p += v * v; }
            return (float)(p / n);
        }

        /// <summary>The measured bleed as [from channel, to channel] (0 = main mic), or null when not calibrated.</summary>
        float[,] BleedMatrix(ExtraMic[] list)
        {
            var b = Bleed; string main = MainId;
            if (b == null || b.Count == 0) return null;
            if (ReferenceEquals(list, bleedFor) && ReferenceEquals(b, bleedSrc) && main == bleedMain) return bleedM;
            int total = list.Length + 1;
            var ids = new string[total];
            ids[0] = main; for (int i = 0; i < list.Length; i++) ids[i + 1] = list[i].Id;
            var m = new float[total, total]; bool any = false;
            for (int f = 0; f < total; f++)
            {
                Dictionary<string, float> to;
                if (ids[f] == null || !b.TryGetValue(ids[f], out to)) continue;
                for (int t = 0; t < total; t++) { float r; if (t != f && ids[t] != null && to.TryGetValue(ids[t], out r)) { m[f, t] = r; any = true; } }
            }
            bleedM = any ? m : null; bleedFor = list; bleedSrc = b; bleedMain = main;
            return bleedM;
        }

        /// <summary>Microphone 1 muted on its own (hotkey); the others keep talking.</summary>
        public volatile bool MainMuted;
        float mainMute = 1f;

        /// <summary>A mute that fades over ~5 ms instead of clicking.</summary>
        static void Fade(ref float g, bool muted, float[] x, int off, int n)
        {
            float target = muted ? 0f : 1f;
            if (target == g && target == 1f) return;
            const float step = 1f / 240f;
            for (int i = 0; i < n; i++)
            {
                g = target > g ? Math.Min(target, g + step) : Math.Max(target, g - step);
                x[off + i] *= g;
            }
        }

        float[] one = new float[0];
        void ReadInto(ExtraMic mic, float[] dst, int offset, int n)
        {
            if (one.Length < n) one = new float[n];
            mic.Read(one, n);
            Array.Copy(one, 0, dst, offset, n);
        }
    }
}
