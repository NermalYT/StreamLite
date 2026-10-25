using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>Something worth knowing that happened (Health page): when, how serious, and what it means in plain words.</summary>
    internal sealed class HealthEvent
    {
        public DateTime Time;
        public bool Warning;
        public string Text;
    }

    /// <summary>
    /// Watches the audio paths once a second (Health page): latency, dropouts, buffer changes, CPU / RAM / video memory,
    /// the noise engine in use - and writes a plain-language note whenever StreamLite falls back to a slower or less
    /// capable path, or recovers.
    /// </summary>
    internal sealed class HealthMonitor
    {
        readonly AppCore core;
        readonly List<HealthEvent> events = new List<HealthEvent>();
        public readonly DateTime Started = DateTime.Now;
        public volatile float CpuPercent;
        public volatile int RamMb, VramMb = -1;
        TimeSpan lastCpu; DateTime lastCpuAt;
        int lastMicDrops, lastGameDrops, lastMicMargin, lastGameMargin, gpuTick;
        string lastMicMode, lastEngine, lastDirect, lastGameError, lastCam, lastFallback, lastSpatial;
        readonly Dictionary<string, string> lastDevice = new Dictionary<string, string>();
        int highCpuSeconds;
        bool primed;

        public HealthMonitor(AppCore core) { this.core = core; }

        public List<HealthEvent> Events { get { lock (events) return events.ToList(); } }

        void Note(bool warning, string text)
        {
            lock (events)
            {
                events.Add(new HealthEvent { Time = DateTime.Now, Warning = warning, Text = text });
                if (events.Count > 200) events.RemoveAt(0);
            }
        }

        /// <summary>Once a second (UI thread).</summary>
        public void Sample()
        {
            var e = core.Engine; var g = core.Game; var S = core.S;
            // StreamLite's own CPU (all cores = 100%) and memory.
            try
            {
                var p = Process.GetCurrentProcess();
                var now = DateTime.UtcNow; var cpu = p.TotalProcessorTime;
                if (lastCpuAt != default(DateTime))
                    CpuPercent = (float)((cpu - lastCpu).TotalMilliseconds / (now - lastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100);
                lastCpu = cpu; lastCpuAt = now;
                RamMb = (int)(p.WorkingSet64 / (1024 * 1024));
            }
            catch { }
            if (gpuTick++ % 5 == 0) ThreadPool.QueueUserWorkItem(_ => VramMb = ReadVram());

            if (!primed)
            {
                // The first look: what's there already isn't news.
                primed = true;
                lastMicDrops = e.Dropouts; lastMicMargin = e.MarginFrames; lastGameDrops = g.Dropouts; lastGameMargin = g.LearnedMargin;
            }
            if (S.VirtualMicOn && e.Running)
            {
                // Mic path.
                string mode = e.ExclusiveCapture ? "exclusive" : "shared";
                if (mode != lastMicMode && lastMicMode != null || (lastMicMode == null && mode == "shared" && e.ExclusiveError != null))
                {
                    if (mode == "shared") Note(true, "Your mic is in shared mode: " + (e.ExclusiveError ?? "exclusive mode isn't available") + ". That's Windows' 10 ms packets - about 10 ms more delay. StreamLite tries the faster mode again when the audio restarts (or when the mic is plugged back in).");
                    else Note(false, "Your mic is back in exclusive mode (its smallest packets - the lowest delay).");
                }
                lastMicMode = mode;
                string direct = e.DirectError;
                if (direct != lastDirect && direct != null)
                    Note(true, "Writing your voice straight into the virtual cable failed (" + direct + "). StreamLite uses Windows' audio path for it instead: a few ms more, and Discord's screen share might pick up your voice.");
                lastDirect = direct;
                int drops = e.Dropouts, margin = e.MarginFrames;
                if (drops > lastMicDrops)
                    Note(true, "Mic dropout" + (drops - lastMicDrops > 1 ? "s" : "") + " (" + drops + " since start): a packet from the mic came late, so the virtual mic had a tiny gap. " +
                        (margin > lastMicMargin ? "StreamLite keeps " + (margin / 48.0).ToString("0.0") + " ms in reserve now (was " + (lastMicMargin / 48.0).ToString("0.0") + ") so it doesn't happen again." : "") +
                        " Often caused by a busy PC (a game loading, an update installing) or a USB hub.");
                lastMicDrops = drops; lastMicMargin = margin;
                // Noise engine: what you picked vs what runs.
                var d = e.Suppressor.DfnEngine;
                string running = !S.NoiseRemoval ? "off" : d != null && d.Ready && d.Error == null ? (d.Gpu ? "DeepFilterNet (GPU)" : "DeepFilterNet (CPU)") : S.NsEngine == NoiseEngines.Classic ? "Classic" : "RNNoise";
                if (running != lastEngine && lastEngine != null)
                {
                    if (NoiseEngines.IsDfn(S.NsEngine) && running == "RNNoise" && !e.Suppressor.DfnWanted)
                        Note(false, "DeepFilterNet is resting while no app records your mic (it frees the CPU / graphics memory). RNNoise keeps the meters going; DeepFilterNet is back the moment Discord, OBS or a game starts recording.");
                    else Note(false, "Noise removal now runs on " + running + ".");
                }
                lastEngine = running;
                string fb = e.Suppressor.FallbackNote;
                if (fb != null && fb != lastFallback) Note(true, fb);
                lastFallback = fb;
            }
            // Game 3D / outputs.
            if (g.Running)
            {
                int drops = g.Dropouts, margin = g.LearnedMargin;
                if (drops > lastGameDrops)
                    Note(true, "Game sound dropout (" + drops + " since start): the game device's packets came late. " + (margin > lastGameMargin ? "StreamLite keeps " + (margin / 48.0).ToString("0.0") + " ms in reserve now." : ""));
                lastGameDrops = drops; lastGameMargin = margin;
                foreach (var o in g.ExtraOutputs)
                {
                    string st = o.Playing ? "playing" : o.Status;
                    string was;
                    if (lastDevice.TryGetValue("out" + o.Id, out was) && was != st)
                    {
                        var set = S.ExtraOutputs.FirstOrDefault(x => x.Id == o.Id);
                        Note(!o.Playing, "Output " + (set != null ? set.Name : "") + ": " + (o.Playing ? "playing again." : st + "."));
                    }
                    lastDevice["out" + o.Id] = st;
                }
            }
            foreach (var m in e.Mixer.Mics)
            {
                string st = m.Running ? "running" : m.Status, was;
                if (lastDevice.TryGetValue("mic" + m.Id, out was) && was != st)
                {
                    var set = S.ExtraMics.FirstOrDefault(x => x.Id == m.Id);
                    Note(!m.Running, "Microphone " + (set != null ? set.Name : "") + ": " + (m.Running ? "back in the mix." : st + " - the other mics keep working."));
                }
                lastDevice["mic" + m.Id] = st;
            }
            if (core.GameError != lastGameError && core.GameError != null) Note(true, "3D audio: " + core.GameError);
            lastGameError = core.GameError;
            string spatial = core.SpatialWarning;
            if (spatial != lastSpatial && (spatial != null || lastSpatial != null)) Note(spatial != null, spatial ?? "Windows spatial sound is off again - games hand StreamLite their 7.1 mix untouched.");
            lastSpatial = spatial;
            string cam = core.CamFx != null ? core.CamFx.Error : null;
            if (cam != lastCam && cam != null) Note(true, "Camera: " + cam);
            lastCam = cam;
            highCpuSeconds = CpuPercent > 20 ? highCpuSeconds + 1 : 0;
            if (highCpuSeconds == 30) Note(true, "StreamLite has used over 20% of your CPU for 30 seconds. On a slower PC: RNNoise instead of DeepFilterNet (CPU), or DeepFilterNet on the graphics card, lowers it a lot.");
        }

        /// <summary>Dedicated video memory used by StreamLite (MB), from Windows' GPU counters; -1 = unknown.</summary>
        static int ReadVram()
        {
            try
            {
                var cat = new PerformanceCounterCategory("GPU Process Memory");
                string tag = "pid_" + Process.GetCurrentProcess().Id + "_";
                long ded = 0;
                foreach (var inst in cat.GetInstanceNames().Where(i => i.StartsWith(tag)))
                    using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true)) ded += c.RawValue;
                return (int)(ded / (1024 * 1024));
            }
            catch { return -1; }
        }
    }
}
