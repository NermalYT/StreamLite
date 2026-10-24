using System;
using System.Linq;
using System.Threading;
using StreamLite.Interop;
using StreamLite.Linux;

namespace StreamLite.Audio
{
    /// <summary>
    /// The clean microphone on Linux: records your mic, runs the voice chain (noise removal, gate, auto level...) and
    /// writes it to "StreamLite Mic Feed", which apps see as the "StreamLite Mic" input. Mic and output run on their
    /// own clocks, so a drift-corrected buffer sits between them (same as on Windows).
    /// </summary>
    internal sealed class AudioEngine
    {
        public const int SampleRate = 48000;
        public readonly VoiceChain Chain = new VoiceChain();
        public volatile string Status = "Stopped";
        public volatile float InLevelDb = -100f, OutLevelDb = -100f;
        /// <summary>Channels the microphone delivers (1 = mono).</summary>
        public volatile int InputChannelCount = 1;
        public int Dropouts { get { var r = ring; return r != null ? r.Underruns : 0; } }

        Thread capture, render;
        volatile bool stop;
        SampleRing ring;
        string mic;

        public bool Running { get { return capture != null; } }
        /// <summary>The microphone (PulseAudio source) it listens to.</summary>
        public string Source { get { return mic; } }

        public void Start(string micSource)
        {
            Stop();
            mic = micSource;
            if (mic == null) { Status = "No microphone found"; return; }
            stop = false;
            ring = new SampleRing(SampleRate, SampleRate);
            capture = new Thread(CaptureLoop) { IsBackground = true, Name = "Mic in", Priority = ThreadPriority.Highest };
            render = new Thread(RenderLoop) { IsBackground = true, Name = "Mic out", Priority = ThreadPriority.Highest };
            capture.Start(); render.Start();
        }

        public void Stop()
        {
            stop = true;
            if (capture != null) capture.Join(2000);
            if (render != null) render.Join(2000);
            capture = render = null;
            Status = "Stopped";
            InLevelDb = OutLevelDb = -100f;
        }

        static float PeakDb(float[] b, int n) { float p = 0; for (int i = 0; i < n; i++) { float a = Math.Abs(b[i]); if (a > p) p = a; } return (float)(20 * Math.Log10(Math.Max(p, 1e-6f))); }

        void CaptureLoop()
        {
            var buf = new float[480];
            while (!stop)
            {
                try
                {
                    // Every channel in the mic's own layout, so Chain.Channels can use just the inputs with your voice
                    // (an interface's empty input, an out-of-phase laptop channel); otherwise the server would mix them.
                    PulseDevice d = null;
                    try { d = Pactl.AllSources().FirstOrDefault(x => x.Name == mic); } catch { }
                    int ch = d != null && d.Channels >= 2 && d.Channels <= 32 && d.Map != null && d.Map.Length == d.Channels ? d.Channels : 1;
                    var inter = new float[480 * ch];
                    using (var rec = ch > 1 ? PulseStream.Record(mic, ch, "StreamLite: your microphone", 10, d.Map) : PulseStream.Record(mic, 1, "StreamLite: your microphone", 10))
                    {
                        Status = "Running";
                        InputChannelCount = ch;
                        while (!stop)
                        {
                            if (ch == 1) rec.Read(buf, 480);
                            else { rec.Read(inter, 480); Chain.Channels.Process(inter, 480, ch, buf); }
                            Chain.Clip.Feed(buf, 480);
                            InLevelDb = PeakDb(buf, 480);
                            Chain.Process(buf, 480, false);
                            OutLevelDb = PeakDb(buf, 480);
                            ring.Write(buf, 480);
                        }
                    }
                }
                catch (Exception ex) { Status = "Microphone: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        void RenderLoop()
        {
            var buf = new float[240];
            while (!stop)
            {
                try
                {
                    using (var play = PulseStream.Play(LinuxAudio.MicFeed, 1, "StreamLite: clean mic", 20))
                    {
                        ring.Clear();
                        while (!stop) { ring.Read(buf, 240); play.Write(buf, 240); }
                    }
                }
                catch (Exception ex) { if (Status == "Running") Status = "Output: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }
    }
}

namespace StreamLite.Linux
{
    using StreamLite.Audio;

    /// <summary>
    /// Reads a StreamLite output device (its monitor), processes it and plays it on your real headphones, with a
    /// drift-corrected buffer between the two clocks. Used for the EQ path and the 3D path.
    /// </summary>
    internal abstract class OutputPath
    {
        protected const int B = SpatialProcessor.B;
        public volatile string Status = "Off";
        public volatile float LevelDb = -100f;
        public int Dropouts { get { var r = ring; return r != null ? r.Underruns : 0; } }
        Thread capture, render;
        volatile bool stop;
        StereoRing ring;
        string source, output;
        readonly int channels;
        readonly int[] map;
        readonly string name;

        protected OutputPath(int channels, int[] map, string name) { this.channels = channels; this.map = map; this.name = name; }

        public bool Running { get { return capture != null; } }
        public string Output { get { return output; } }
        /// <summary>Play silence from the next block on (the output vanished: the server may move this stream onto StreamLite's own device).</summary>
        public volatile bool Hold;

        public void Start(string monitorOf, string headphones)
        {
            Stop();
            Hold = false;
            source = monitorOf + ".monitor"; output = headphones;
            if (output == null) { Status = "No headphones / speakers found"; return; }
            stop = false;
            ring = new StereoRing(144);
            capture = new Thread(CaptureLoop) { IsBackground = true, Name = name + " in", Priority = ThreadPriority.Highest };
            render = new Thread(RenderLoop) { IsBackground = true, Name = name + " out", Priority = ThreadPriority.Highest };
            capture.Start(); render.Start();
        }

        public void Stop()
        {
            stop = true;
            if (capture != null) capture.Join(2000);
            if (render != null) render.Join(2000);
            capture = render = null;
            Status = "Off"; LevelDb = -100f;
        }

        /// <summary>Turns one block of input (interleaved, `channels` wide) into stereo.</summary>
        protected abstract void Process(float[] interleaved, bool silent, float[] l, float[] r);

        void CaptureLoop()
        {
            var inter = new float[B * channels];
            var l = new float[B]; var r = new float[B];
            int quietBlocks = 0;
            while (!stop)
            {
                try
                {
                    using (var rec = PulseStream.Record(source, channels, "StreamLite: " + name, 10, map))
                    {
                        Status = "Running";
                        while (!stop)
                        {
                            rec.Read(inter, B);
                            float pk = 0; for (int i = 0; i < inter.Length; i++) { float a = Math.Abs(inter[i]); if (a > pk) pk = a; }
                            quietBlocks = pk < 1e-6f ? quietBlocks + 1 : 0;
                            bool silent = quietBlocks > 280;          // ~1.5 s of digital silence: idle
                            Process(inter, silent, l, r);
                            float po = 0; for (int i = 0; i < B; i++) po = Math.Max(po, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                            LevelDb = (float)(20 * Math.Log10(Math.Max(po, 1e-6f)));
                            ring.Write(l, r, B);
                            ring.InputArrived();
                        }
                    }
                }
                catch (Exception ex) { Status = name + ": " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }

        void RenderLoop()
        {
            var buf = new float[240 * 2];
            while (!stop)
            {
                try
                {
                    using (var play = PulseStream.Play(output, 2, "StreamLite: " + name, 20))
                    {
                        ring.Clear();
                        while (!stop) { ring.Read(buf, 240); if (Hold) Array.Clear(buf, 0, buf.Length); play.Write(buf, 240); }
                    }
                }
                catch (Exception ex) { if (Status == "Running") Status = "Headphones: " + ex.Message; }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }
    }

    /// <summary>Everything not played through 3D: headphone correction + the profile EQ (game, music, chat...).</summary>
    internal sealed class EqPath : OutputPath
    {
        readonly EqChain eqL = new EqChain(), eqR = new EqChain();
        volatile EqProfile pending;

        public EqPath() : base(2, null, "headphone EQ") { }

        public void SetEq(EqProfile p) { pending = p; }

        protected override void Process(float[] inter, bool silent, float[] l, float[] r)
        {
            var p = pending;
            if (p != null) { pending = null; eqL.Update(p, 48000); eqR.Update(p, 48000); }
            for (int i = 0; i < B; i++) { l[i] = inter[2 * i]; r[i] = inter[2 * i + 1]; }
            if (silent) return;
            eqL.Process(l, B); eqR.Process(r, B);
        }
    }

    /// <summary>Games: 7.1 in, 3D for headphones out (SADIE II HRTFs, true 360), then the same headphone EQ.</summary>
    internal sealed class GamePath : OutputPath
    {
        public readonly SpatialProcessor Processor = new SpatialProcessor();
        readonly EqChain eqL = new EqChain(), eqR = new EqChain();
        volatile EqProfile pending;
        readonly float[][] slots = new float[SpatialProcessor.Slots][];
        int surroundHold;

        public GamePath() : base(8, PulseStream.Map71, "3D game audio")
        {
            for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
        }

        public void SetEq(EqProfile p) { pending = p; }

        protected override void Process(float[] inter, bool silent, float[] l, float[] r)
        {
            var p = pending;
            if (p != null) { pending = null; eqL.Update(p, 48000); eqR.Update(p, 48000); }
            if (silent && !Processor.TestRunning) { Array.Clear(l, 0, B); Array.Clear(r, 0, B); return; }
            // Channel order of the 7.1 device: FL FR FC LFE rear-L rear-R side-L side-R = the processor's slots.
            float pk = 0;
            for (int i = 0; i < B; i++)
                for (int c = 0; c < 8; c++)
                {
                    float v = inter[i * 8 + c];
                    slots[c][i] = v;
                    if (c >= 2) { float a = Math.Abs(v); if (a > pk) pk = a; }
                }
            // Browsers and stereo games only fill front left/right: treat as stereo until a surround channel sounds.
            if (pk > 1e-4f) surroundHold = 20 * SpatialProcessor.Fs / B; else if (surroundHold > 0) surroundHold--;
            var src = surroundHold > 0 ? SpatialProcessor.Source.Surround71 : SpatialProcessor.Source.Stereo;
            Processor.Process(slots, Processor.TestRunning ? SpatialProcessor.Source.Surround71 : src, l, r);
            eqL.Process(l, B); eqR.Process(r, B);
        }
    }
}
