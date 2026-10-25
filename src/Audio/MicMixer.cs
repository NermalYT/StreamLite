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
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(Id, out ch, out mask, out rate) || ch < 2 || ch > 32) { ch = 1; mask = 0; }
            IntPtr fmt = ch > 1 ? AudioDevices.MakeFormat(ch, mask, AudioEngine.SampleRate, true, 32) : AudioEngine.AllocFormat();
            object svc = null;
            var ev = new AutoResetEvent(false);
            try
            {
                int flags = CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(0, flags, 200000, 0, fmt, IntPtr.Zero);
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
        float[] env = new float[0], gain = new float[0];
        readonly object gate = new object();

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
            if (k == 0) { MainShare = 1; return; }
            int total = k + 1;
            if (tmp.Length < total * n) tmp = new float[total * n];
            if (env.Length != total) { env = new float[total]; gain = new float[total]; for (int i = 0; i < total; i++) gain[i] = 1f / total; }
            // Channel 0 = main mic, 1.. = extra mics, each with its trim.
            float g0 = (float)Math.Pow(10, MainGainDb / 20);
            for (int i = 0; i < n; i++) tmp[i] = buf[i] * g0;
            for (int m = 0; m < k; m++)
            {
                var mic = list[m];
                ReadInto(mic, tmp, (m + 1) * n, n);
                float g = (float)Math.Pow(10, mic.GainDb / 20);
                if (g != 1f) for (int i = 0; i < n; i++) tmp[(m + 1) * n + i] *= g;
            }

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
            Array.Clear(buf, 0, n);
            for (int c = 0; c < total; c++)
            {
                float target = auto ? env[c] / sum : 1f;
                float from = gain[c], step = (target - from) / n;
                for (int i = 0; i < n; i++) buf[i] += tmp[c * n + i] * (from + step * (i + 1));
                gain[c] = target;
                if (c == 0) MainShare = auto ? target : 1; else list[c - 1].Share = auto ? target : 1;
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
