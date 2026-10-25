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
    /// Short gaps on one audio path (a packet later than the buffer could bridge - bridged with a soft fade). Every one
    /// is counted and logged; one is only worth a warning when they keep coming without a reason: a game loading or
    /// a PC busy for a moment explains a gap, and a one-off is no problem.
    /// </summary>
    internal sealed class GapWatch
    {
        /// <summary>Unexplained gaps within 10 minutes before the Health page warns (and then at most every 30 minutes).</summary>
        public const int WarnCount = 5;
        static readonly TimeSpan Window = TimeSpan.FromMinutes(10), WarnEvery = TimeSpan.FromMinutes(30);
        readonly List<DateTime> unexplained = new List<DateTime>();
        DateTime lastWarned = DateTime.MinValue;
        public int Total, Explained;
        public DateTime Last;
        public string LastWhy;

        /// <summary>count new gaps; why = what explains them (null = nothing known). True = warn now.</summary>
        public bool Add(int count, DateTime now, string why)
        {
            Total += count; Last = now; LastWhy = why;
            if (why != null) { Explained += count; return false; }
            for (int i = 0; i < count; i++) unexplained.Add(now);
            unexplained.RemoveAll(t => now - t > Window);
            if (unexplained.Count < WarnCount || now - lastWarned < WarnEvery) return false;
            lastWarned = now;
            return true;
        }

        /// <summary>Gaps keep coming right now (the Health page marks the row).</summary>
        public bool Troubled(DateTime now) { return unexplained.Count(t => now - t <= Window) >= WarnCount; }
        public int Recent(DateTime now) { return unexplained.Count(t => now - t <= Window); }

        /// <summary>"7 short gaps since start, all bridged - last 08:12 (Rainbow Six Siege was loading)".</summary>
        public string Describe()
        {
            if (Total == 0) return "none since start";
            return Total + " short gap" + (Total > 1 ? "s" : "") + " since start, bridged" + (Explained > 0 ? " (" + Explained + " while the PC was busy or a game loading)" : "") +
                " - last " + Last.ToString("t") + (LastWhy != null ? ": " + LastWhy : "");
        }
    }

    /// <summary>
    /// Watches the audio paths once a second (Health page): latency, dropouts, buffer changes, CPU / RAM / video memory,
    /// the noise engine in use - and writes a plain-language note whenever something needs your attention: a fallback
    /// to a slower or less capable path, a device gone for good, gaps that keep coming. Routine things (a game loading,
    /// a Bluetooth blip, DeepFilterNet resting between calls) only go to health.log next to the settings.
    /// </summary>
    internal sealed class HealthMonitor
    {
        readonly AppCore core;
        readonly List<HealthEvent> events = new List<HealthEvent>();
        public readonly DateTime Started = DateTime.Now;
        public volatile float CpuPercent;
        public volatile int RamMb, VramMb = -1;
        /// <summary>The whole PC's CPU use (%), highest of the last 5 seconds.</summary>
        public volatile float SystemCpu;
        public readonly GapWatch MicGaps = new GapWatch(), GameGaps = new GapWatch();
        TimeSpan lastCpu; DateTime lastCpuAt;
        int lastMicDrops, lastGameDrops, lastCatchUps, gpuTick;
        string lastMicMode, lastEngine, lastDirect, lastExclusive, lastGameError, lastCam, lastFallback, lastSpatial, lastPlainCam;
        readonly Dictionary<string, string> lastDevice = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> downSince = new Dictionary<string, DateTime>();
        readonly HashSet<string> downNoted = new HashSet<string>();
        DateTime gameErrorSince, camErrorSince;
        int highCpuSeconds;
        bool primed, lastWidened;
        readonly float[] sysCpu = new float[5]; int sysPos; long sysIdle, sysTotal;

        public HealthMonitor(AppCore core)
        {
            this.core = core;
            try
            {
                // Keep health.log small: the newest ~200 KB.
                var f = new System.IO.FileInfo(LogPath);
                if (f.Exists && f.Length > 512 * 1024)
                {
                    var text = System.IO.File.ReadAllText(LogPath);
                    System.IO.File.WriteAllText(LogPath, text.Substring(text.Length - 200 * 1024));
                }
            }
            catch { }
            Log("StreamLite " + Program.Version.ToString(3) + " started");
        }

        public static string LogPath { get { return System.IO.Path.Combine(AppSettings.Folder, "health.log"); } }

        public List<HealthEvent> Events { get { lock (events) return events.ToList(); } }

        /// <summary>A note on the Health page (and in health.log).</summary>
        void Note(bool warning, string text)
        {
            lock (events)
            {
                events.Add(new HealthEvent { Time = DateTime.Now, Warning = warning, Text = text });
                if (events.Count > 200) events.RemoveAt(0);
            }
            Log((warning ? "! " : "") + text);
        }

        /// <summary>Only in health.log: routine things worth knowing when troubleshooting.</summary>
        static void Log(string text)
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.Folder);
                System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n");
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        void SampleSystemCpu()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return;
            long total = kernel + user;   // kernel time includes idle
            if (sysTotal != 0 && total > sysTotal) sysCpu[sysPos++ % sysCpu.Length] = (float)(100.0 * (1 - (double)(idle - sysIdle) / (total - sysTotal)));
            sysIdle = idle; sysTotal = total;
            SystemCpu = sysCpu.Max();
        }

        string loadingGame; DateTime loadingChecked;

        /// <summary>A game (from your game profiles) that started in the last 3 minutes - it's probably loading. Checked at most every 10 s.</summary>
        string LoadingGame()
        {
            if (DateTime.Now - loadingChecked < TimeSpan.FromSeconds(10)) return loadingGame;
            loadingChecked = DateTime.Now; loadingGame = null;
            try
            {
                var games = core.S.Profiles.Profiles.Where(p => !p.WhilePlaying && !p.ChatApp).SelectMany(p => p.Exes.Select(x => new { x, p.Name }))
                    .GroupBy(a => a.x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                foreach (var pr in Process.GetProcesses())
                {
                    try
                    {
                        string name;
                        if (loadingGame == null && games.TryGetValue(pr.ProcessName, out name) && DateTime.Now - pr.StartTime < TimeSpan.FromMinutes(3)) loadingGame = name;
                    }
                    catch { }
                    finally { pr.Dispose(); }
                }
            }
            catch { }
            return loadingGame;
        }

        readonly int[] gcSeen = new int[3], gcPrev = new int[3];

        /// <summary>For the log: CPU, memory collections in the last second (they pause every thread briefly), time since start.</summary>
        string Context()
        {
            return " [CPU " + SystemCpu.ToString("0") + "%, GC +" + (gcSeen[0] - gcPrev[0]) + "/+" + (gcSeen[1] - gcPrev[1]) + "/+" + (gcSeen[2] - gcPrev[2]) +
                ", " + (int)(DateTime.Now - Started).TotalSeconds + " s after start]";
        }

        /// <summary>What explains a gap: a game loading, the PC busy, the game's own sound pausing; null = nothing known.</summary>
        string GapCause(double gapMs, bool gameSound)
        {
            string game = LoadingGame();
            if (game != null) return game + " was loading";
            if (SystemCpu >= 85) return "your PC was busy (CPU " + SystemCpu.ToString("0") + "%)";
            if (gameSound && gapMs >= 8) return "the game's own sound paused for " + gapMs.ToString("0") + " ms";
            return null;
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
            SampleSystemCpu();
            for (int k = 0; k < 3; k++) { gcPrev[k] = gcSeen[k]; gcSeen[k] = GC.CollectionCount(k); }
            var now2 = DateTime.Now;

            if (!primed)
            {
                // The first look: what's there already isn't news.
                primed = true;
                lastMicDrops = e.Dropouts; lastGameDrops = g.Dropouts;
            }
            // Direct to headphones that couldn't (once per reason): everything keeps playing the normal way.
            string excl = S.Game.Exclusive && g.Running ? g.ExclusiveError : null;
            if (excl != lastExclusive && excl != null)
                Note(false, "Direct to headphones isn't possible right now: " + excl + ". Your sound plays the normal way (about 10 ms more delay); StreamLite tries again when 3D audio restarts or you plug in other headphones.");
            lastExclusive = excl;
            if (S.VirtualMicOn && e.Running)
            {
                // Mic path.
                string mode = e.ExclusiveCapture ? "exclusive" : "shared";
                if (mode != lastMicMode && lastMicMode != null || (lastMicMode == null && mode == "shared" && e.ExclusiveError != null))
                {
                    if (mode == "shared") Note(false, "Your mic runs in shared mode: " + (e.ExclusiveError ?? "exclusive mode isn't available") + ". That's Windows' 10 ms packets - about 10 ms more delay, nothing breaks. StreamLite tries the faster mode again when the audio restarts (or when the mic is plugged back in).");
                    else Note(false, "Your mic is back in exclusive mode (its smallest packets - the lowest delay).");
                }
                lastMicMode = mode;
                string direct = e.DirectError;
                if (direct != lastDirect && direct != null)
                    Note(true, "Writing your voice straight into the virtual cable failed (" + direct + "). StreamLite uses Windows' audio path for it instead: a few ms more, and Discord's screen share might pick up your voice.");
                lastDirect = direct;
                int drops = e.Dropouts;
                if (drops > lastMicDrops)
                {
                    double gapMs = e.LastGapMs;
                    string why = GapCause(gapMs, false);
                    Log("Mic: " + (drops - lastMicDrops) + " short gap" + (drops - lastMicDrops > 1 ? "s" : "") + " (" + gapMs.ToString("0.0") + " ms, bridged with a fade)" + (why != null ? " - " + why : "") +
                        "; " + (e.MarginFrames / 48.0).ToString("0.0") + " ms in reserve now; longest between mic packets " + e.WorstPacketIntervalMs.ToString("0.0") +
                        " ms (" + e.PacketMs.ToString("0") + " ms normal), longest processing " + e.WorstProcessingMs.ToString("0.00") + " ms, packets picked up before a late signal " + e.EarlyPackets + Context());
                    if (MicGaps.Add(drops - lastMicDrops, now2, why))
                        Note(true, "Your mic keeps having short gaps: " + MicGaps.Recent(now2) + " in the last 10 minutes (each bridged with a soft fade, but listeners may hear a tiny skip). " +
                            "StreamLite now keeps " + (e.MarginFrames / 48.0).ToString("0.0") + " ms in reserve. Common causes: a USB hub or a front-panel port (plug the mic straight into the PC), " +
                            "USB power saving, or a driver that's busy (graphics or Wi-Fi driver updates help).");
                }
                lastMicDrops = drops;
                if (e.CableCatchUps > lastCatchUps) Log("Mic: the virtual cable caught up with the writer (" + e.CableCatchUps + "x since start) - it keeps 0.5 ms more lead for a while");
                lastCatchUps = e.CableCatchUps;
                // Noise engine: what you picked vs what runs. DeepFilterNet resting between calls is routine (log only).
                var d = e.Suppressor.DfnEngine;
                string running = !S.NoiseRemoval ? "off" : d != null && d.Ready && d.Error == null ? (d.Gpu ? "DeepFilterNet (GPU)" : "DeepFilterNet (CPU)") : S.NsEngine == NoiseEngines.Classic ? "Classic" : "RNNoise";
                if (running != lastEngine && lastEngine != null)
                    Log(NoiseEngines.IsDfn(S.NsEngine) && running == "RNNoise" && !e.Suppressor.DfnWanted ? "Noise removal: DeepFilterNet rests (no app records the mic)" : "Noise removal now runs on " + running);
                lastEngine = running;
                bool widened = d != null && d.Widened;
                if (widened && !lastWidened) Log("DeepFilterNet: frames came back late (a busy graphics card or CPU) - it keeps one frame more (+10 ms) for this session");
                lastWidened = widened;
                string fb = e.Suppressor.FallbackNote;
                if (fb != null && fb != lastFallback) Note(true, fb);
                lastFallback = fb;
            }
            // Game 3D / outputs.
            if (g.Running)
            {
                int drops = g.Dropouts;
                if (drops > lastGameDrops)
                {
                    double gapMs = g.LastGapMs;
                    string why = GapCause(gapMs, true);
                    Log("Game sound: " + (drops - lastGameDrops) + " short gap" + (drops - lastGameDrops > 1 ? "s" : "") + " (" + gapMs.ToString("0.0") + " ms)" + (why != null ? " - " + why : "") + "; " + (g.LearnedMargin / 48.0).ToString("0.0") + " ms in reserve now" + Context());
                    if (GameGaps.Add(drops - lastGameDrops, now2, why))
                        Note(true, "Game sound keeps having short gaps on the way to your headphones: " + GameGaps.Recent(now2) + " in the last 10 minutes. StreamLite now keeps " + (g.LearnedMargin / 48.0).ToString("0.0") +
                            " ms in reserve. If it continues: Bluetooth headphones and USB hubs are the usual cause - a wired or direct USB connection fixes it.");
                }
                lastGameDrops = drops;
                foreach (var o in g.ExtraOutputs)
                {
                    var set = S.ExtraOutputs.FirstOrDefault(x => x.Id == o.Id);
                    Device("out" + o.Id, "Output " + (set != null ? set.Name : ""), o.Playing, o.Playing ? "playing" : o.Status, "playing again", now2);
                }
            }
            foreach (var m in e.Mixer.Mics)
            {
                var set = S.ExtraMics.FirstOrDefault(x => x.Id == m.Id);
                Device("mic" + m.Id, "Microphone " + (set != null ? set.Name : ""), m.Running, m.Running ? "running" : m.Status + " - the other mics keep working", "back in the mix", now2);
            }
            // Errors that clear by themselves within seconds (a device switching, 3D restarting) aren't news.
            string ge = core.GameError;
            if (ge == null) { gameErrorSince = DateTime.MinValue; lastGameError = null; }
            else
            {
                if (gameErrorSince == DateTime.MinValue) { gameErrorSince = now2; Log("3D audio: " + ge); }
                if (now2 - gameErrorSince >= TimeSpan.FromSeconds(8) && ge != lastGameError) { Note(true, "3D audio: " + ge); lastGameError = ge; }
            }
            string spatial = core.SpatialWarning;
            if (spatial != lastSpatial && (spatial != null || lastSpatial != null)) Note(spatial != null, spatial ?? "Windows spatial sound is off again - games hand StreamLite their 7.1 mix untouched.");
            lastSpatial = spatial;
            string cam = core.CamFx != null ? core.CamFx.Error : null;
            if (cam == null) { camErrorSince = DateTime.MinValue; lastCam = null; }
            else
            {
                if (camErrorSince == DateTime.MinValue) { camErrorSince = now2; Log("Camera: " + cam); }
                if (now2 - camErrorSince >= TimeSpan.FromSeconds(8) && cam != lastCam) { Note(true, "Camera: " + cam); lastCam = cam; }
            }
            string plainCam = core.CamFx != null ? core.CamFx.PlainWhy : null;
            if (plainCam != lastPlainCam && plainCam != null) Note(true, "StreamLite Camera plays without effects: " + plainCam + ". A smooth picture instead of a choppy one; switch \"Use the graphics card\" on (Camera page) to get them back.");
            lastPlainCam = plainCam;
            highCpuSeconds = CpuPercent > 20 ? highCpuSeconds + 1 : 0;
            if (highCpuSeconds == 30) Note(true, "StreamLite has used over 20% of your CPU for 30 seconds. On a slower PC: RNNoise instead of DeepFilterNet (CPU), or DeepFilterNet on the graphics card, lowers it a lot.");
        }

        /// <summary>
        /// An added mic or output: gone for more than 10 seconds is worth a note (and then its return); a blip that's
        /// back within 10 s (Bluetooth reconnecting, a USB device re-enumerating) only goes to the log.
        /// </summary>
        void Device(string key, string name, bool ok, string state, string backText, DateTime now)
        {
            string was;
            bool changed = lastDevice.TryGetValue(key, out was) && was != state;
            lastDevice[key] = state;
            if (changed) Log(name + ": " + state);
            if (!ok)
            {
                if (!downSince.ContainsKey(key)) downSince[key] = now;
                if (!downNoted.Contains(key) && now - downSince[key] >= TimeSpan.FromSeconds(10)) { downNoted.Add(key); Note(true, name + ": " + state + "."); }
            }
            else
            {
                downSince.Remove(key);
                if (downNoted.Remove(key)) Note(false, name + ": " + backText + ".");
            }
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
