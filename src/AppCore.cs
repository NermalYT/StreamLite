using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Camera;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>Owns settings, the audio engine and camera enforcement; the UI talks to this.</summary>
    internal sealed class AppCore : IDisposable
    {
        public readonly AppSettings S = AppSettings.Load();
        public readonly AudioEngine Engine = new AudioEngine();
        public readonly VoiceTest VoiceTest = new VoiceTest();
        public readonly GameAudioEngine Game = new GameAudioEngine();

        public string GameSinkId, GamePhonesId, GameError, GamePhonesName;
        public readonly ProfileManager Profiles;
        public readonly CameraService Cameras;
        public readonly Camera.CameraFx CamFx;
        public string ActiveMicId, ActiveOutId;
        readonly Timer saveTimer = new Timer { Interval = 800 };
        readonly Timer healthTimer = new Timer { Interval = 1000 };
        /// <summary>Health page: latency, dropouts, fallbacks and resources, watched once a second.</summary>
        public readonly HealthMonitor Health;
        readonly Timer eqTimer = new Timer { Interval = 350 };
        readonly Timer routeTimer = new Timer { Interval = 4000 };
        public string RoutingError;
        public string OutputEqError;

        /// <param name="lookupOnly">Only for reading devices and settings (the uninstaller): no periodic device
        /// checks (they would rename the cables back while it restores them) and no virtual camera.</param>
        public AppCore(bool lookupOnly = false)
        {
            if (!lookupOnly && HardwareTuning.Apply(S)) S.Save();   // AI settings that fit this PC, once
            // AI quality: a tier for installs from before tiers, and the engine always following it (no engine to pick).
            if (!lookupOnly && AiTiers.Ensure(S, Environment.ProcessorCount, HardwareTuning.GpuMemoryMb(), DeepFilter.Installed)) S.Save();
            // Before 3.0.1 every dropout - a game loading too - raised the 3D buffer for good, and it was remembered
            // (often 10+ ms of extra game delay). Learn it again under the new rules (only recurring gaps count).
            Version last;
            if (!lookupOnly && S.Game.LearnedMargin > 0 && (S.LastVersion == null || !Version.TryParse(S.LastVersion, out last) || last < new Version(3, 0, 1)))
                S.Game.LearnedMargin = 0;
            // 3.1.1: a late mic packet is filled in instead of being a gap, so the mic's buffer no longer needs the
            // floor late packets used to set (about 4 ms of delay on the test PC). Start again from the small base.
            if (!lookupOnly && S.MicLearnedMargin > 0 && (S.LastVersion == null || !Version.TryParse(S.LastVersion, out last) || last < new Version(3, 1, 1)))
                S.MicLearnedMargin = 0;
            // Realtime noise removal (a 2.7 ms filter steered by late gains) sounds clearly worse on real voices (DNSMOS
            // overall 2.3 vs 3.0 with DeepFilterNet), and while you talk it's the light RNNoise that steers - a fan comes
            // through every word. 3.3: gone - the noise remover always runs its own path.
            if (!lookupOnly) S.NsRealtime = false;
            Health = new HealthMonitor(this);
            if (!lookupOnly) { healthTimer.Tick += (s, e) => { try { Health.Sample(); } catch { } try { WatchMic(); } catch { } KeepCameraChanges(); }; healthTimer.Start(); }
            Cameras = new CameraService(S);
            CamFx = lookupOnly ? null : new Camera.CameraFx(S);
            Engine.Test = VoiceTest;
            if (!lookupOnly) try { LoadNoisePrint(); } catch { }
            if (!lookupOnly) try { LoadVoiceLock(); } catch { }
            VoiceTest.StudioProfile = () => Engine.Fx.Studio.SaveProfile();
            // StreamLite's own test sounds reach the headphones through 3D audio's stream while it holds them exclusively.
            if (!lookupOnly) HeadphonesDirect.Engine = Game;
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); S.Save(); };
            eqTimer.Tick += (s, e) => { eqTimer.Stop(); OutputEqError = WriteOutputEqAsking(); };
            routeTimer.Tick += (s, e) =>
            {
                FollowMic(); FollowHeadphones(); RouteBypassApps(); UpdateMicListeners(); CheckFormats();
                if (formatsChanged) { formatsChanged = false; SaveSoon(); }
                // Remember the 3D buffer margin this PC needs, so the next start doesn't re-learn it with dropouts:
                // the floor that recurring gaps set (a one-off hiccup's temporary cover isn't kept), less once
                // 10 minutes without a gap have proven a smaller one.
                // The same for the mic's low-latency path, per mic: the floor its recurring gaps set (it relaxes by
                // itself after calm periods, and this follows it down).
                if (Engine.Running && Engine.ExclusiveCapture && ActiveMicId != null)
                {
                    int mic = Engine.LearnedFloor, keep = mic > 40 ? mic : 0;
                    if (S.MicLearnedFor != ActiveMicId || Math.Abs(keep - S.MicLearnedMargin) >= 12) { S.MicLearnedMargin = keep; S.MicLearnedFor = ActiveMicId; Engine.StartMargin = keep; SaveSoon(); }
                    // And its clock against the cable's (the direct cable path learns it; the next start begins there).
                    double drift = Engine.CableDriftPpm;
                    if (!double.IsNaN(drift) && Math.Abs(drift - S.MicDriftPpm) >= 20) { S.MicDriftPpm = drift; Engine.StartDriftPpm = drift; SaveSoon(); }
                }
                int learned = Game.Running ? Game.LearnedFloor : 0;
                if (learned > S.Game.LearnedMargin) { S.Game.LearnedMargin = learned; SaveSoon(); }
                else if (Game.Running && learned > 0 && learned <= S.Game.LearnedMargin - 24 && Game.CalmSeconds >= 600) { S.Game.LearnedMargin = learned; SaveSoon(); }
            };

            Profiles = new ProfileManager(this);
            string phones = ProfileDeviceId();
            EqProfile mine = null;
            if (phones != null && S.OutputEq.TryGetValue(phones, out mine) && mine != null) mine.Repair();
            GameProfiles.Merge(S.Profiles, S.Profiles.Profiles.Count == 0 ? mine : null);
            // While the noise model sleeps, look for a new listener every second (not every 4 s) so a call starts on
            // the good model sooner. Only the session list of one device is read - no device enumeration.
            wakeTimer.Tick += (s, e) =>
            {
                string id = micCapId;
                if (id == null || Engine.Suppressor.DfnWanted || !NoiseEngines.IsDfn(S.NsEngine)) return;
                if (Interop.AudioSessions.AnyoneRecording(id)) { idleTicks = 0; Engine.Suppressor.DfnWanted = true; }
            };
            if (!lookupOnly) { routeTimer.Start(); wakeTimer.Start(); }
        }

        readonly Timer wakeTimer = new Timer { Interval = 1000 };
        volatile string micCapId;

        public void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        /// <summary>The camera changed its own settings (a gesture, a preset, dragging the view): saved.</summary>
        void KeepCameraChanges()
        {
            var d = CamFx != null ? CamFx.Director : null;
            if (d != null && d.SettingsChanged) { d.SettingsChanged = false; SaveSoon(); }
        }

        // ---- Studio voice: what it learned about each mic, kept so it's ready at the next start

        string studioFor; int studioTicks;

        /// <summary>Keeps what Studio voice learned about the mic in use (settings), when it changed.</summary>
        public void KeepStudioProfile()
        {
            if (studioFor == null) return;
            var p = Engine.Fx.Studio.SaveProfile();
            string old;
            if (p == null || (S.StudioProfiles.TryGetValue(studioFor, out old) && old == p)) return;
            S.StudioProfiles[studioFor] = p;
            SaveSoon();
        }

        // ---- each mic's own settings (MicPresets): kept when you switch away, loaded when you switch to it

        string presetFor;

        /// <summary>Every microphone Windows knows, plugged in or not (for presets).</summary>
        public List<AudioDeviceInfo> AllMics() { return AudioDevices.List(EDataFlow.Capture, 1 | 2 | 4 | 8); }

        void SwitchMicPreset(string micId)
        {
            if (micId == presetFor) return;
            bool first = presetFor == null;
            if (!first) KeepMicPreset();
            presetFor = micId;
            // At start the settings are the ones this mic had when StreamLite closed: nothing to load.
            if (first || !S.MicPresetsFollow || micId == null) return;
            var d = AllMics().FirstOrDefault(x => x.Id == micId);
            var p = MicPresets.Find(S, d);
            if (p == null) return;
            MicPresets.Apply(S, p, micId);
            PushEngineParams();
            try { HealthMonitor.LogLine("Mic: " + (d != null ? d.Name : "the mic") + " - its own settings loaded (" + (p.Note ?? "kept " + p.Updated) + ")"); } catch { }
        }

        /// <summary>The settings in use, kept as the active mic's preset.</summary>
        public void KeepMicPreset()
        {
            if (presetFor == null) return;
            KeepStudioProfile();
            var d = AllMics().FirstOrDefault(x => x.Id == presetFor);
            if (d == null) return;
            string studio; S.StudioProfiles.TryGetValue(presetFor, out studio);
            var now = MicPresets.Capture(S, d.Name, MicPresets.KeyOf(d), studio);
            var old = S.MicPresets.FirstOrDefault(x => string.Equals(x.Key, now.Key, StringComparison.OrdinalIgnoreCase));
            if (old != null && MicPresets.Same(old, now)) return;   // nothing changed: not saved again
            MicPresets.Keep(S, now);
            SaveSoon();
        }

        /// <summary>A preset put on a mic (imported, or assigned on the presets dialog): used at once when that mic is in use.</summary>
        public void UseMicPresetNow(MicPreset p)
        {
            var d = AllMics().FirstOrDefault(x => x.Id == ActiveMicId);
            if (d == null || !string.Equals(MicPresets.KeyOf(d), p.Key, StringComparison.OrdinalIgnoreCase)) return;
            MicPresets.Apply(S, p, ActiveMicId);
            studioFor = null; SwitchStudioProfile(ActiveMicId);   // Studio voice reloads what it learned about the mic
            PushEngineParams();
        }

        void SwitchStudioProfile(string micId)
        {
            if (micId == studioFor) return;
            KeepStudioProfile();
            studioFor = micId;
            string p = null;
            if (micId != null) S.StudioProfiles.TryGetValue(micId, out p);
            Engine.Fx.Studio.LoadProfile(p);
        }

        /// <summary>Forget what Studio voice learned about the mic in use and learn it again (you moved it, changed rooms).</summary>
        public void RelearnStudioVoice()
        {
            if (studioFor != null) S.StudioProfiles.Remove(studioFor);
            Engine.Fx.Studio.LoadProfile(null);
            SaveSoon();
        }

        int idleTicks;
        volatile int dfnPreviewTick = Environment.TickCount - 60000;

        /// <summary>The mic tuning is on screen: load the noise model now (it otherwise waits for an app to record).</summary>
        public void KeepDfnLoaded()
        {
            dfnPreviewTick = Environment.TickCount;
            if (NoiseEngines.IsDfn(S.NsEngine)) Engine.Suppressor.DfnWanted = true;
        }
        /// <summary>
        /// Keeps the heavy noise model loaded only while an app records "StreamLite - In" (Discord in a call, OBS...).
        /// It unloads after ~1 minute with no listener, so an idle StreamLite holds no video memory.
        /// </summary>
        void UpdateMicListeners()
        {
            var cap = PairedCapture(ActiveOutId);
            micCapId = cap != null ? cap.Id : null;
            bool listening = cap != null && Interop.AudioSessions.AnyoneRecording(cap.Id);
            MicInUse = listening;
            if (++studioTicks % 15 == 0) { KeepStudioProfile(); if (S.MicPresetsFollow) KeepMicPreset(); }   // every minute
            if (!NoiseEngines.IsDfn(S.NsEngine)) Engine.Suppressor.DfnWanted = true;
            else if (listening || Engine.Fx == null || Environment.TickCount - dfnPreviewTick < 20000) { idleTicks = 0; Engine.Suppressor.DfnWanted = true; }
            else if (++idleTicks >= 15) Engine.Suppressor.DfnWanted = false;   // 15 x 4 s
            WriteStatus(listening);
        }

        /// <summary>An app records the clean mic right now (a call, a stream...).</summary>
        public volatile bool MicInUse;

        /// <summary>In a call or playing (game sound flowing through 3D audio): not a good moment to restart StreamLite.</summary>
        public bool Busy { get { return MicInUse || ((S.Game.Enabled || MultiOut) && Game.Running && Game.Status == "Running"); } }

        /// <summary>status.txt next to the settings: what the engines are doing right now (for troubleshooting).</summary>
        string lastStatus; int lastStatusTick;

        void WriteStatus(bool listening)
        {
            try
            {
                var d = Engine.Suppressor.DfnEngine;
                string body =
                    "mic: " + Engine.Status + (Engine.Muted ? " (muted by hotkey)" : "") + (Engine.PttClosed ? " (push to talk: not held)" : "") + (Engine.Mixer.MainMuted ? " (microphone 1 muted)" : "") + "\r\n" +
                    "hotkeys: " + S.Hotkeys.Count(h => h.Key != 0 || h.Mouse != 0) + " bound\r\n" +
                    "resources: CPU " + Health.CpuPercent.ToString("0.0") + "%, RAM " + Health.RamMb + " MB, video memory " + (Health.VramMb >= 0 ? Health.VramMb + " MB" : "unknown") + "; health notes " + Health.Events.Count + "\r\n" +
                    "mic input: " + Engine.InputChannelCount + " ch" + (Engine.InputChannelCount > 1 ? " - " + Engine.Fx.Channels.Describe() : "") +
                        (DeviceErrors.MicBlocked() ? " - BLOCKED by Windows privacy settings" : "") + "\r\n" +
                    "mic latency: " + LatencyText() + "\r\n" +
                    "studio voice: " + Engine.Fx.Studio.Describe() + (S.StudioVoice ? ", strength " + S.StudioStrength + "%, " + Engine.Fx.Studio.LearnedSeconds.ToString("0") + " s of talking learned, pops caught " + Engine.Fx.Studio.Pops : "") + "\r\n" +
                    "mic levels (last seconds): " + Engine.History.Summary() + "\r\n" +
                    "mic timing: longest between packets " + Engine.WorstPacketIntervalMs.ToString("0.0") + " ms, longest processing " + Engine.WorstProcessingMs.ToString("0.00") + " ms (last 10-20 s), " + Engine.EarlyPackets + " packets picked up before a late signal, " + Engine.LatePacketsFilled + " late ones filled in, " + Engine.LostPackets + " lost by the mic's driver (filled in, " + (Engine.LostFrames / 48.0).ToString("0") + " ms)\r\n" +
                "mic cable fill-ins: " + Engine.CableFillTrace + "\r\n" +
                    "more mics: " + (Engine.Mixer.Mics.Length == 0 ? "none" : string.Join("; ", Engine.Mixer.Mics.Select(m =>
                        { var set = S.ExtraMics.FirstOrDefault(x => x.Id == m.Id); return (set != null ? set.Name : m.Id) + " " + (m.Running ? "running" : m.Status) + ", dropouts " + m.Dropouts; })) +
                        (S.MicAutoMix ? " (auto mix)" : "")) + "\r\n" +
                    "mic output: " + (Engine.DirectOutput ? "direct to cable (feed hidden)" : Engine.PrivateOutput ? "exclusive" : "shared") +
                        (Engine.DirectError != null ? " - direct path failed: " + Engine.DirectError : "") + ", dropouts " + Engine.Dropouts + "\r\n" +
                    "mic feed endpoint state: " + (ActiveOutId != null ? AudioDevices.State(ActiveOutId).ToString() : "-") + "\r\n" +
                    "app recording the mic: " + listening + "\r\n" +
                    "noise engine: " + S.NsEngine + (d != null ? (d.Ready ? " loaded in " + d.LoadMs + " ms (" + (d.Gpu ? (d.LowVram ? "GPU, low VRAM" : "GPU") : "CPU") + ", " + d.AvgMs.ToString("0.0") + " ms/frame, late " + d.Late + ")" : " loading") : " not loaded") +
                        (Engine.Suppressor.FallbackNote != null ? " - " + Engine.Suppressor.FallbackNote : "") + "\r\n" +
                    "hardware tuning: " + HardwareTuning.Summary + "\r\n" +
                    "3D audio: " + (Game.Running ? Game.Status + " - " + Game.Processor.Current + (Game.Processor.Rendering360 ? " (true 360)" : "") + ", delay " + Game.LatencyMs.ToString("0") + " ms, CPU " + Game.CpuPercent.ToString("0.0") + "%, dropouts " + Game.Dropouts + " -> " + GamePhonesName : "off") + (GameError != null ? " - " + GameError : "") + "\r\n" +
                    "3D tuning: " + GameTuning + "\r\n" +
                "direct to headphones: " + DirectStatus() + "\r\n" +
                "3D path measured: buffer " + Game.RingMs.ToString("0.0") + " ms + output queue " + Game.QueueMs.ToString("0.0") + " ms (+ " + (SpatialProcessor.B / 48.0).ToString("0.#") + " ms blocks, renderer " + (Game.Processor.ExtraLatency / 48.0).ToString("0.0") + " ms)\r\n" +
                    "headphone profile: " + (Profiles != null ? Profiles.ActiveName + " - " + Profiles.Why + (Profiles.Pending != null ? " (switching to " + Profiles.Pending + ")" : "") +
                        (Profiles.Find(Profiles.ActiveName) != null ? ", match " + Profiles.Find(Profiles.ActiveName).Eq.MatchDb.ToString("0.0") + " dB" : "") : "-") + "\r\n" +
                    "more outputs: " + (Game.ExtraOutputs.Length == 0 ? "none" : string.Join("; ", Game.ExtraOutputs.Select(o =>
                        { var set = S.ExtraOutputs.FirstOrDefault(x => x.Id == o.Id); return (set != null ? set.Name : o.Id) + " " + (o.Playing ? "playing" : o.Status) + ", dropouts " + o.Dropouts; })) +
                        (Game.Plain ? " (plain stereo)" : " (3D)")) + "\r\n" +
                    "camera: " + CamFx.Status + "\r\n" +
                    "device formats: " + FormatSummary + "\r\n";
                // Only when something changed (and once a minute as a sign of life), not every 4 seconds.
                if (body == lastStatus && Environment.TickCount - lastStatusTick < 60000) return;
                lastStatus = body; lastStatusTick = Environment.TickCount;
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppSettings.Folder, "status.txt"), "time: " + DateTime.Now.ToString("s") + "\r\n" + body);
            }
            catch { }
        }

        int formatBusy;
        volatile bool formatsChanged;
        readonly Dictionary<string, DeviceFormat> micCableBest = new Dictionary<string, DeviceFormat>();   // only touched by the format thread
        readonly Dictionary<string, int> formatRefused = new Dictionary<string, int>();   // device -> when it refused (retry after 2 min, not every tick)
        bool Refused(string id) { int t; return formatRefused.TryGetValue(id, out t) && Environment.TickCount - t < 120000; }
        /// <summary>What the last format check changed (or "as set" when nothing needed changing).</summary>
        public volatile string FormatSummary = "not checked yet";

        /// <summary>Windows spatial sound switched on where it would fight StreamLite's own 3D (null = fine).</summary>
        public volatile string SpatialWarning;

        public string SpatialCheck(string sink, string phones)
        {
            if (!S.Game.Enabled && !MultiOut) return null;
            if (sink != null && AudioDevices.SpatialObjects(sink) > 0)
                return "Windows spatial sound (Windows Sonic / Dolby Atmos / DTS) is on for \"" + GameOutName + "\": games then send it audio objects that Windows mixes down before StreamLite gets them. Switch it off: Sound settings > " + GameOutName + " > Spatial sound: Off.";
            if (phones != null && S.Game.Enabled && AudioDevices.SpatialObjects(phones) > 0)
                return "Windows spatial sound is on for your headphones too, so StreamLite's 3D gets made 3D a second time. Switch it off for them: Sound settings > your headphones > Spatial sound: Off.";
            return null;
        }

        /// <summary>
        /// StreamLite's own devices always run what its engines use: StreamLite - In stereo 48 kHz, StreamLite - Out 7.1
        /// 48 kHz. (16 channels or 384 kHz there only add resampling CPU, and apps that downmix the 14 empty channels
        /// hear you quieter.) Real devices get their best working format once, when StreamLite first sees them - a later
        /// manual change in Windows is respected. Runs off the UI thread.
        /// </summary>
        public void CheckFormats()
        {
            if (System.Threading.Interlocked.Exchange(ref formatBusy, 1) == 1) return;
            string micOut = ActiveOutId, sink = GameSinkId;
            bool auto = S.AutoFormats;
            var startList = S.FormatsDone;
            var done = new HashSet<string>(startList);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { SpatialWarning = SpatialCheck(sink, GamePhonesId); } catch { }
                try
                {
                    var log = new List<string>();
                    var cap = PairedCapture(micOut);
                    DeviceFormat cur;
                    if (cap != null)
                    {
                        DeviceFormat want;
                        if (!micCableBest.TryGetValue(cap.Id, out want))
                        {
                            var b = DeviceFormats.Best(cap.Id, 2, 3);
                            if (b != null) micCableBest[cap.Id] = want = b.Value;
                        }
                        if (want.Rate > 0 && !Refused(cap.Id) && (!DeviceFormats.Get(cap.Id, out cur) || !cur.SameAs(want)))
                        {
                            string r = DeviceFormats.Apply(cap.Id, true, want, false);
                            if (r != null) log.Add(MicName + ": " + r);
                            if (r != null && r.StartsWith("kept")) formatRefused[cap.Id] = Environment.TickCount;
                        }
                    }
                    int ch, mask, rate;
                    if (sink != null && !Refused(sink) && (!AudioDevices.MixFormat(sink, out ch, out mask, out rate) || ch != 8 || rate != 48000))
                    {
                        string err = AudioDevices.SetSurround71(sink);
                        log.Add(GameOutName + ": " + (err ?? "8 ch (7.1), 48000 Hz"));
                        if (err != null) formatRefused[sink] = Environment.TickCount;
                    }
                    if (auto)
                    {
                        int before = done.Count;
                        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                            foreach (var d in AudioDevices.List(flow))
                            {
                                if (AudioDevices.LooksVirtual(d) || done.Contains(d.Id) || DeviceFormats.IsBluetooth(d)) continue;
                                if (!DeviceFormats.Get(d.Id, out cur)) continue;   // not ready yet (just plugged in): next tick
                                done.Add(d.Id);
                                var best = DeviceFormats.Best(d.Id, cur.Channels, cur.Mask);
                                string r = best != null ? DeviceFormats.Apply(d.Id, flow == EDataFlow.Capture, best.Value, true) : null;
                                log.Add(d.Name + ": " + (r ?? "already " + cur));
                            }
                        // (Not if the list was reset meanwhile - turning the option on - so that every device gets checked again.)
                        if (done.Count != before && ReferenceEquals(S.FormatsDone, startList)) { S.FormatsDone = done.ToList(); formatsChanged = true; }
                    }
                    // A format change makes Windows rebuild the cable's endpoints, which restores the driver's names
                    // ("VB-Audio Virtual Cable" in Discord) - put StreamLite's back whenever that happened.
                    if (Elevation.IsElevated && ((cap != null && AudioDevices.AdapterName(cap.Id) != MicAdapterName)
                        || (sink != null && AudioDevices.AdapterName(sink) != GameAdapterName)))
                        TidyDevices();
                    if (log.Count > 0) FormatSummary = DateTime.Now.ToString("HH:mm") + " " + string.Join("; ", log);
                    else if (FormatSummary == "not checked yet") FormatSummary = "as set";
                }
                catch (Exception ex) { FormatSummary = "check failed: " + ex.Message; }
                finally { formatBusy = 0; }
            });
        }

        /// <summary>Gives every real device its best working format again (after turning the option on).</summary>
        public void RecheckFormats()
        {
            S.FormatsDone = new List<string>();
            SaveSoon();
            CheckFormats();
        }

        /// <summary>Raised after any voice setting changes (the UI updates the voice style shown).</summary>
        public event EventHandler ParamsChanged;

        /// <summary>A game using its own HRTF was sent straight to the headphones for the first time (profile name).</summary>
        public event EventHandler<string> GameRoutedFirstTime;

        // ---- "Record a sound to remove"

        static string NoisePrintFile { get { return System.IO.Path.Combine(AppSettings.Folder, "noise-print.bin"); } }
        static string VoicePrintFile { get { return System.IO.Path.Combine(AppSettings.Folder, "your-voice.bin"); } }

        /// <summary>The recorded sound and your learned voice, as saved last time (the voice keeps being learned while you talk).</summary>
        void LoadNoisePrint()
        {
            var live = Engine.Suppressor.Print;
            live.VoicePath = VoicePrintFile;
            live.LoadVoice();
            var w = NoisePrint.LoadDict(NoisePrintFile, NoisePrint.NoiseAtoms);
            if (w == null) { if (S.NoisePrintOn) { S.NoisePrintOn = false; S.NoisePrintAbout = null; } return; }
            live.Use(w);
            VoiceTest.Chain.Suppressor.Print.Use(w);
        }

        /// <summary>Starts recording the sound to remove (stay quiet while it plays); RecordingNoisePrint follows it.</summary>
        public NoisePrintRecording StartNoisePrint()
        {
            var r = new NoisePrintRecording();
            Engine.Suppressor.Recording = r;
            return r;
        }

        /// <summary>
        /// The recording is full: learns the sound in the background (a few seconds) and keeps it - or says the noise
        /// remover already takes it all out. done runs on a worker thread: pass the result to ApplyNoisePrint on the UI thread.
        /// </summary>
        public void FinishNoisePrint(NoisePrintRecording r, Action<NoisePrint.Learned> done)
        {
            Engine.Suppressor.Recording = null;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                NoisePrint.Learned l;
                try { l = NoisePrint.Learn(r.After, r.After.Length, r.Raw); }
                catch (Exception ex) { l = new NoisePrint.Learned { AlreadyGone = true, Character = "couldn't learn it: " + ex.Message }; }
                if (!l.AlreadyGone && l.W != null)
                {
                    try { NoisePrint.SaveDict(NoisePrintFile, l.W, NoisePrint.NoiseAtoms); } catch { }
                    Engine.Suppressor.Print.Use(l.W);
                    VoiceTest.Chain.Suppressor.Print.Use(l.W);
                }
                done(l);
            });
        }

        /// <summary>UI thread: a learned sound is switched on (and described in the settings).</summary>
        public void ApplyNoisePrint(NoisePrint.Learned l)
        {
            if (l.AlreadyGone || l.W == null) return;
            S.NoisePrintOn = true;
            S.NoisePrintAbout = l.Character + ", recorded " + DateTime.Now.ToString("MMM d, HH:mm");
            PushEngineParams();
        }

        public void CancelNoisePrint() { Engine.Suppressor.Recording = null; }

        /// <summary>Forgets the recorded sound (your learned voice stays).</summary>
        // ---- "Only my voice"

        static string LockYouFile { get { return System.IO.Path.Combine(AppSettings.Folder, "voice-lock-you.bin"); } }
        static string LockThemFile { get { return System.IO.Path.Combine(AppSettings.Folder, "voice-lock-them.bin"); } }
        public VoiceLock Lock { get { return Engine.Fx.Lock; } }
        /// <summary>How the saved voiceprints were made: seconds of voice and how alike its pieces were (0 = none).</summary>
        public double LockYouSeconds, LockYouConsistency, LockThemSeconds;

        void LoadVoiceLock()
        {
            double c, sec;
            float p10, p25; List<float[]> also;
            var you = VoiceLock.Load(LockYouFile, out c, out sec, out p10, out p25, out also);
            Lock.SetPrint(you); Lock.SetAlsoYou(also); Lock.Tune(p10, p25); LockYouSeconds = you != null ? sec : 0; LockYouConsistency = c;
            var them = VoiceLock.Load(LockThemFile, out c, out sec);
            Lock.SetBlockPrint(them); LockThemSeconds = them != null ? sec : 0;
            if (you == null && S.VoiceLockOn) S.VoiceLockOn = false;
        }

        /// <summary>Starts collecting your voice (or theirs) from the live mic, after noise removal.</summary>
        public void StartLearnVoice() { Lock.StartLearning(); }
        public void CancelLearnVoice() { Lock.CancelLearning(); }

        /// <summary>
        /// The voice collected becomes a voiceprint (yours, or theirs to keep out), saved next to the settings. done runs
        /// on a worker thread with the voiceprint (null: too little voice or no model), its seconds and consistency.
        /// </summary>
        public void FinishLearnVoice(bool them, Action<float[], double, double> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                double cons = 0, secs = 0; float[] p = null; float p10 = float.NaN, p25 = float.NaN;
                try { p = Lock.FinishLearning(out cons, out secs, out p10, out p25); } catch (Exception ex) { Lock.Error = ex.Message; }
                if (p != null)
                {
                    try { VoiceLock.Save(them ? LockThemFile : LockYouFile, p, cons, secs, them ? float.NaN : p10, them ? float.NaN : p25); } catch { }
                    if (them) { Lock.SetBlockPrint(p); LockThemSeconds = secs; } else { Lock.SetPrint(p); Lock.Tune(p10, p25); LockYouSeconds = secs; LockYouConsistency = cons; }
                    try { HealthMonitor.LogLine("Only my voice: " + (them ? "their" : "your") + " voice learned from " + secs.ToString("0") + " s (pieces agree " + cons.ToString("0.00") + ")"); } catch { }
                }
                done(p, secs, cons);
            });
        }

        /// <summary>
        /// The voice setup's recordings become your settings (VoiceSetup.Apply) and your voiceprints (saved, used at once,
        /// thresholds made for your voice). Runs the model: call it off the UI thread.
        /// </summary>
        public VoiceSetup.Result ApplyVoiceSetup(VoiceSetup.Step[] steps)
        {
            var r = VoiceSetup.Apply(steps, S, VoiceLock.ModelInstalled ? Lock : null);
            if (r.Print != null)
            {
                try { VoiceLock.Save(LockYouFile, r.Print, r.Consistency, r.PrintSeconds, r.P10, r.P25, r.AlsoYou); } catch { }
                Lock.SetPrint(r.Print); Lock.SetAlsoYou(r.AlsoYou); Lock.Tune(r.P10, r.P25);
                LockYouSeconds = r.PrintSeconds; LockYouConsistency = r.Consistency;
            }
            S.VoiceSetupDone = true;
            setupBefore = null;
            r.Lines.Add("Studio voice learned your mic again from these recordings.");
            try { HealthMonitor.LogLine("Voice setup: " + string.Join(" ", r.Lines)); } catch { }
            PushEngineParams();
            KeepMicPreset();
            SaveSoon();
            return r;
        }

        MicPreset setupBefore; bool setupPrintOn, setupLockOn, setupMicWasOff;

        /// <summary>The voice setup closed: a virtual mic it switched on for the recordings goes back off.</summary>
        public void EndVoiceSetup()
        {
            if (!setupMicWasOff) return;
            setupMicWasOff = false;
            S.VirtualMicOn = false; RestartAudio(); SaveSoon();
        }

        /// <summary>
        /// The voice setup starts from StreamLite's defaults (Remove a specific sound and Only my voice off, so nothing
        /// colours the recordings) and Studio voice learns your mic again from them. What you had is kept until the setup
        /// finishes: CancelVoiceSetup puts it back.
        /// </summary>
        public void BeginVoiceSetup()
        {
            if (setupBefore == null)
            {
                KeepStudioProfile();
                var d = AllMics().FirstOrDefault(x => x.Id == ActiveMicId);
                string studio = null; if (ActiveMicId != null) S.StudioProfiles.TryGetValue(ActiveMicId, out studio);
                setupBefore = MicPresets.Capture(S, d != null ? d.Name : "mic", d != null ? MicPresets.KeyOf(d) : "voice setup", studio);
                setupPrintOn = S.NoisePrintOn; setupLockOn = S.VoiceLockOn;
            }
            ResetVoiceSettings();
            RelearnStudioVoice();
            // The recordings come straight from the mic, so it has to be open: with the virtual mic off, it's switched on
            // for the setup (EndVoiceSetup switches it back off). Mute and push to talk don't matter - they act later.
            if (!S.VirtualMicOn) { setupMicWasOff = true; S.VirtualMicOn = true; RestartAudio(); }
        }

        /// <summary>The setup was left before it finished: your settings as they were.</summary>
        public void CancelVoiceSetup()
        {
            var b = setupBefore; setupBefore = null;
            if (b == null) return;
            MicPresets.Apply(S, b, ActiveMicId);
            S.NoisePrintOn = setupPrintOn; S.VoiceLockOn = setupLockOn;
            studioFor = null; SwitchStudioProfile(ActiveMicId);
            PushEngineParams(); KeepMicPreset(); SaveSoon();
        }

        /// <summary>The voice setup is offered once (after installing or updating to 3.5.2), when the mic runs.</summary>
        public bool VoiceSetupDue { get { return !S.VoiceSetupDone && S.VirtualMicOn && Engine.Status == "Running"; } }

        /// <summary>Forgets your voiceprint and theirs, and switches Only my voice off.</summary>
        public void ForgetVoiceLock()
        {
            foreach (var f in new[] { LockYouFile, LockThemFile }) try { if (System.IO.File.Exists(f)) System.IO.File.Delete(f); } catch { }
            Lock.SetPrint(null); Lock.SetBlockPrint(null); LockYouSeconds = LockThemSeconds = 0;
            S.VoiceLockOn = false; PushEngineParams(); SaveSoon();
        }

        public void ForgetNoisePrint()
        {
            Engine.Suppressor.Print.Forget(); VoiceTest.Chain.Suppressor.Print.Forget();
            try { if (System.IO.File.Exists(NoisePrintFile)) System.IO.File.Delete(NoisePrintFile); } catch { }
            S.NoisePrintOn = false; S.NoisePrintAbout = null;
            PushEngineParams();
        }

        public void PushEngineParams()
        {
            Engine.Fx.Configure(S);
            Engine.Mixer.AutoMix = S.MicAutoMix;
            Engine.Mixer.MainGainDb = S.MainMicGainDb;
            PushMicDeviceParams();
            VoiceTest.Configure(S);   // the test clip always sounds like the live mic (or the engine being compared)
            SaveSoon();
            if (ParamsChanged != null) ParamsChanged(this, EventArgs.Empty);
        }

        /// <summary>"3.1 ms mic to cable (capture 1.2 + buffer 1.6 + cable 0.3, exclusive 3 ms packets) + 10 ms noise removal".</summary>
        public string LatencyText()
        {
            var e = Engine;
            if (!e.Running || e.PacketMs <= 0) return "not running";
            string ai = Engine.Suppressor.DelayMs > 0 && S.NoiseRemoval ? " + " + Engine.Suppressor.DelayMs.ToString("0") + " ms noise removal" : "";
            return e.LatencyMs.ToString("0.0") + " ms mic to cable (capture " + e.CaptureWaitMs.ToString("0.0") + " + buffer " + e.BufferMs.ToString("0.0") +
                " + cable " + e.CableLeadMs.ToString("0.0") + "; " + (e.ExclusiveCapture ? "exclusive" : "shared") + " " + e.PacketMs.ToString("0.#") + " ms packets" +
                (e.CableCatchUps > 0 ? ", " + e.CableCatchUps + " catch-ups" : "") + ")" + ai +
                (!e.ExclusiveCapture && e.ExclusiveError != null ? " - low latency unavailable: " + e.ExclusiveError : "");
        }

        /// <summary>AI quality (Settings): DeepFilterNet and the AI camera reload where and as big as the tier says.</summary>
        public void SetAiTier(string tier)
        {
            if (S.AiTier == tier) return;
            AiTiers.Apply(S, tier, Environment.ProcessorCount, DeepFilter.Installed);
            PushEngineParams();
            CamFx.Restart();
            SaveSoon();
        }

        /// <summary>The noise remover running right now, in words (the pages show it; there's no engine to pick).</summary>
        public string VoiceIsolationText()
        {
            if (!S.NoiseRemoval) return "Off";
            var d = Engine.Suppressor.DfnEngine;
            string e = S.NsEngine == NoiseEngines.DfnGpu ? "DeepFilterNet 3 on the graphics card" + (S.LowVram ? " (small model)" : "") : S.NsEngine == NoiseEngines.DfnCpu ? "DeepFilterNet 3 on the CPU" : "RNNoise (light)";
            if (Engine.Suppressor.FallbackNote != null) e += " - " + Engine.Suppressor.FallbackNote;
            return e;
        }
        public List<AudioDeviceInfo> Mics() { return AudioDevices.List(EDataFlow.Capture); }
        /// <summary>Playback devices, plus StreamLite's own mic feed while it's hidden (disabled) from everyone else.</summary>
        public List<AudioDeviceInfo> Outputs()
        {
            var l = AudioDevices.List(EDataFlow.Render);
            if (!string.IsNullOrEmpty(S.VirtualOutId) && !l.Any(d => d.Id == S.VirtualOutId))
            {
                var hidden = AudioDevices.List(EDataFlow.Render, 2).FirstOrDefault(d => d.Id == S.VirtualOutId);
                if (hidden != null) l.Add(hidden);
            }
            return l;
        }

        public const string MicAdapterName = "StreamLite Mic", GameAdapterName = "StreamLite Game Audio";

        /// <summary>
        /// Hides "StreamLite - Mic Feed" from every app's output list: the endpoint is switched off and the mic is
        /// written straight into the cable driver (kernel streaming). Falls back to the visible endpoint if that fails.
        /// </summary>
        void ConfigureFeedPath(AudioDeviceInfo feed)
        {
            Engine.KsFeedPath = null;
            if (feed == null) return;
            string ks = AudioDevices.KsFilterPath(feed.Id);
            // Switching the endpoint off needs admin rights, writing to the driver doesn't: without admin rights an
            // endpoint that is already off is still fed directly (it can't be switched back on to use it either).
            bool hide = S.HideMicFeed && ks != null && Engine.DirectError == null && (Elevation.IsElevated || AudioDevices.State(feed.Id) == 2);
            if (hide)
            {
                Engine.KsFeedPath = ks;
                if (AudioDevices.State(feed.Id) == 1) AudioDevices.SetEnabled(feed.Id, false);
            }
            else if (AudioDevices.State(feed.Id) == 2 && Elevation.IsElevated) { AudioDevices.SetEnabled(feed.Id, true); System.Threading.Thread.Sleep(400); }
        }

        /// <summary>
        /// The mic to listen to when none was picked: Windows' default mic (a software mic like NVIDIA Broadcast too), the
        /// one that was the default before StreamLite took its place, or the most mic-like input - never a cable, Stereo Mix
        /// or a line input (3.5.0 could pick one of those, and StreamLite heard nothing).
        /// </summary>
        AudioDeviceInfo BestMic(List<AudioDeviceInfo> mics, AudioDeviceInfo paired)
        {
            Func<AudioDeviceInfo, bool> ok = d => (paired == null || d.Id != paired.Id) && !AudioDevices.IsCable(d);
            string def = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
            return mics.FirstOrDefault(d => d.Id == def && ok(d))
                ?? mics.FirstOrDefault(d => d.Id == S.PreviousDefaultMic && ok(d))
                ?? mics.Where(ok).OrderBy(MicRank).FirstOrDefault();
        }

        internal static int MicRank(AudioDeviceInfo d)
        {
            string n = (d.Name + " " + d.Adapter).ToLowerInvariant();
            if (n.Contains("stereo mix") || n.Contains("what u hear") || n.Contains("wave out") || n.Contains("loopback")) return 3;
            if (n.Contains("line in") || n.Contains("line-in") || n.Contains("aux")) return 2;
            if (n.Contains("mic") || n.Contains("headset") || n.Contains("array")) return 0;
            return 1;
        }

        /// <summary>
        /// No cable stays a Windows default where it doesn't belong: the mic default is StreamLite - In (when you chose
        /// that) or a real mic - never another cable end (3.5.2's uninstaller could leave "Hi-Fi Cable Output" there, so
        /// every app set to Default heard nothing); playback is never the mic feed or another maker's cable (the game
        /// device as default is StreamLite's 3D audio and stays).
        /// </summary>
        void FixCableDefaults()
        {
            var cap = PairedCapture(ActiveOutId);
            var mics = Mics();
            var real = mics.FirstOrDefault(d => d.Id == ActiveMicId && !AudioDevices.IsCable(d)) ?? mics.FirstOrDefault(d => d.Id == S.PreviousDefaultMic && !AudioDevices.IsCable(d)) ?? BestMic(mics, cap);
            string wantMic = S.MakeDefaultMic && cap != null ? cap.Id : real != null ? real.Id : null;
            if (wantMic != null)
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                {
                    string cur = AudioDevices.GetDefaultId(EDataFlow.Capture, role);
                    var d = mics.FirstOrDefault(x => x.Id == cur);
                    if (d != null && cur != wantMic && AudioDevices.IsCable(d) && (cap == null || cur != cap.Id || !S.MakeDefaultMic))
                    {
                        AudioDevices.SetDefaultRole(wantMic, role);
                        try { HealthMonitor.LogLine("Default mic (" + role + ") was the cable \"" + d.Name + "\" - set back to " + mics.Where(x => x.Id == wantMic).Select(x => x.Name).FirstOrDefault()); } catch { }
                    }
                }
            var outs = Outputs();
            var realOut = outs.FirstOrDefault(d => d.Id == S.Game.HeadphonesId && !AudioDevices.IsCable(d)) ?? outs.FirstOrDefault(d => d.Id == S.Game.PreviousDefault && !AudioDevices.IsCable(d)) ?? outs.FirstOrDefault(d => !AudioDevices.IsCable(d));
            if (realOut != null)
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                {
                    string cur = AudioDevices.GetDefaultId(EDataFlow.Render, role);
                    var d = outs.FirstOrDefault(x => x.Id == cur);
                    if (d != null && AudioDevices.IsCable(d) && !IsGameCable(d)) AudioDevices.SetDefaultRole(realOut.Id, role);
                }
        }

        bool feedHelpAsked;

        /// <summary>
        /// The mic feed is switched off (hidden) and its direct path just failed, without admin rights to switch it back
        /// on: asks Windows once (--show-feed), then the mic runs through the feed as a normal device.
        /// </summary>
        void WatchFeed()
        {
            if (feedHelpAsked || Elevation.IsElevated || Engine.DirectError == null || ActiveOutId == null || AudioDevices.State(ActiveOutId) != 2) return;
            feedHelpAsked = true;
            var ui = System.Threading.SynchronizationContext.Current;
            new System.Threading.Thread(() =>
            {
                try { HealthMonitor.LogLine("Mic: the direct path into the cable failed while its device is hidden - asking for permission to switch it back on"); } catch { }
                DriverInstaller.RunElevated(System.Windows.Forms.Application.ExecutablePath, "--show-feed");
                if (ui != null) ui.Post(_ => RestartAudio(), null);
            }) { IsBackground = true, Name = "show mic feed" }.Start();
        }

        /// <summary>Seconds the mic has delivered only silence while running (the mic page says why that may be).</summary>
        public int MicSilentSeconds;
        /// <summary>Only my voice turned down (nearly) everything it heard in the last minute: likely someone else's voiceprint.</summary>
        public bool LockBlocksAll;
        long lockLooksAt, lockBlocksAt; int lockTicks;

        void WatchMic()
        {
            MicSilentSeconds = S.VirtualMicOn && Engine.Status == "Running" && Engine.InputLevelDb < -90f ? MicSilentSeconds + 1 : 0;
            WatchFeed();
            if (++lockTicks < 60) return;
            lockTicks = 0;
            long looks = Lock.Looks, blocks = Lock.Blocks;
            long dl = looks - lockLooksAt, db = blocks - lockBlocksAt;
            lockLooksAt = looks; lockBlocksAt = blocks;
            LockBlocksAll = S.VoiceLockOn && dl >= 30 && db >= dl * 0.97;
        }

        /// <summary>
        /// Every voice setting back to how StreamLite starts out: noise removal and its tuning, gate, EQs, compressor,
        /// de-esser, levels, Studio voice, Across the room, Remove a specific sound and Only my voice switched off, all
        /// inputs of the mic, sharing it. Kept: which mics, the AI quality (noise engine), and what was recorded or learned
        /// (sounds, voiceprints) - those can be switched back on.
        /// </summary>
        public void ResetVoiceSettings()
        {
            var d = AppSettings.Defaults();
            var fields = SetupExport.MicChainFields.Where(f => f != "NsEngine").ToArray();
            SetupExport.SetFields(S, SetupExport.Fields(d, fields), fields);
            S.MicEq = d.MicEq; S.MainMicEq = d.MainMicEq; S.MainMicGainDb = d.MainMicGainDb;
            S.StudioVoice = d.StudioVoice; S.StudioStrength = d.StudioStrength;
            S.NoisePrintOn = d.NoisePrintOn; S.NoisePrintStrength = d.NoisePrintStrength;
            S.VoiceLockOn = d.VoiceLockOn; S.MicChannel = d.MicChannel; S.NsRealtime = d.NsRealtime;
            foreach (var m in S.ExtraMics) { m.GainDb = 0; m.Eq = new EqProfile(); m.Eq.Repair(); }
            bool share = S.ShareMic != d.ShareMic;
            S.ShareMic = d.ShareMic;
            try { HealthMonitor.LogLine("Mic: every voice setting reset to StreamLite's defaults"); } catch { }
            if (share) RestartAudio(); else PushEngineParams();
            KeepMicPreset();
            SaveSoon();
        }

        /// <summary>Real inputs for the mic chain - never StreamLite's own output (that would feed itself back).</summary>
        public List<AudioDeviceInfo> MicChoices()
        {
            var own = PairedCapture(ActiveOutId);
            return Mics().Where(d => own == null || d.Id != own.Id || d.Id == ActiveMicId).ToList();
        }

        /// <summary>Where the clean mic may go: virtual cables, never real speakers or the 3D game device.</summary>
        public List<AudioDeviceInfo> MicCableChoices()
        {
            return Outputs().Where(d => !IsGameCable(d) && (d.Id == ActiveOutId || AudioDevices.LooksVirtual(d))).ToList();
        }

        public AudioDeviceInfo PairedCapture(string renderId)
        {
            if (renderId == null) return null;
            var render = Outputs().FirstOrDefault(d => d.Id == renderId);
            if (render == null || !AudioDevices.LooksVirtual(render)) return null;
            return Mics().FirstOrDefault(d => AudioDevices.LooksVirtual(d) && AudioDevices.SameDevice(d, render));
        }

        public AudioDeviceInfo FindVirtualOutput()
        {
            // The hidden feed is a disabled endpoint: look there too (a moment after a restart it can be either).
            var outs = Outputs().Concat(AudioDevices.List(EDataFlow.Render, 2)).Where(d => !IsGameCable(d)).ToList();
            return outs.FirstOrDefault(d => d.Name.StartsWith(MicFeedName, StringComparison.OrdinalIgnoreCase))
                ?? outs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 && d.Name.IndexOf("16ch", StringComparison.OrdinalIgnoreCase) < 0)
                ?? outs.FirstOrDefault(d => AudioDevices.LooksVirtual(d) && AudioDevices.State(d.Id) == 1);
        }

        /// <summary>
        /// The 3D game device (VB-Audio Hi-Fi Cable, "StreamLite - Out"): never the mic's output, or the mic would go
        /// into the game sound (and, held exclusively, block games from playing at all).
        /// </summary>
        public bool IsGameCable(AudioDeviceInfo d)
        {
            if (d == null) return false;
            if (!string.IsNullOrEmpty(S.Game.SinkId) && d.Id == S.Game.SinkId) return true;
            if (GameSinkId != null && d.Id == GameSinkId) return true;
            return d.Name.StartsWith(GameOutName, StringComparison.OrdinalIgnoreCase) || d.Name.IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0
                || (d.Adapter ?? "").IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0 || (d.Adapter ?? "").IndexOf("StreamLite Game Audio", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void RestartAudio()
        {
            PushEngineParams();
            // The mic feed must be enabled for the virtual mic to work (it may have been disabled in Sound settings) -
            // unless it's deliberately hidden and fed directly.
            if (!S.HideMicFeed && !string.IsNullOrEmpty(S.VirtualOutId) && AudioDevices.State(S.VirtualOutId) == 2 && Elevation.IsElevated)
            {
                AudioDevices.SetEnabled(S.VirtualOutId, true);
                System.Threading.Thread.Sleep(400);
            }

            if (!S.VirtualMicOn) { Engine.Stop(); Engine.Mixer.StopAll(); RestoreDefaultMic(); ActiveMicId = ActiveOutId = null; return; }

            var mics = Mics();
            var outs = Outputs();
            var outDev = outs.FirstOrDefault(d => d.Id == S.VirtualOutId && !IsGameCable(d)) ?? FindVirtualOutput();
            ActiveOutId = outDev != null ? outDev.Id : null;
            var paired = PairedCapture(ActiveOutId);
            var mic = mics.FirstOrDefault(d => d.Id == S.MicId && (paired == null || d.Id != paired.Id)) ?? BestMic(mics, paired);
            // The chosen mic is unplugged and nothing else is on: keep it, the engine reconnects when it comes back.
            if (mic == null && !string.IsNullOrEmpty(S.MicId) && (paired == null || S.MicId != paired.Id) && AudioDevices.State(S.MicId) >= 4)
                ActiveMicId = S.MicId;
            else ActiveMicId = mic != null ? mic.Id : null;

            string monitor = null;
            if (S.Monitor)
            {
                var m = outs.FirstOrDefault(d => d.Id == S.MonitorId);
                monitor = m != null ? m.Id : RealDefaultOutput();
                if (monitor == ActiveOutId) monitor = null;
            }
            if (S.TidyPending && Elevation.IsElevated && !S.HideVirtualDevices) { UntidyDevices(); S.TidyPending = false; SaveSoon(); }
            TidyDevices();
            if (S.TidyPending && Elevation.IsElevated) { S.TidyPending = false; SaveSoon(); }
            if (ActiveOutId != null) S.VirtualOutId = ActiveOutId;
            ConfigureFeedPath(outDev);
            // Shared unless you let StreamLite hold the mic alone (exclusive at its smallest period; shared by itself when that can't work).
            Engine.LowLatency = !S.ShareMic;
            Engine.StartMargin = S.MicLearnedFor == ActiveMicId ? S.MicLearnedMargin : 0;   // what this mic needed last time
            Engine.StartDriftPpm = S.MicLearnedFor == ActiveMicId ? S.MicDriftPpm : 0;
            SwitchMicPreset(ActiveMicId);
            SwitchStudioProfile(ActiveMicId);
            Engine.Start(ActiveMicId, ActiveOutId, monitor);
            ApplyExtraMics();
            if (S.MakeDefaultMic) ApplyDefaultMic(); else RestoreDefaultMic();
            try { FixCableDefaults(); } catch { }
        }

        /// <summary>
        /// Starts / stops the extra microphones to match S.ExtraMics (never the main mic itself or StreamLite's own
        /// virtual mic, which would feed back). Unplugged ones keep waiting and join again when plugged back in.
        /// </summary>
        public void ApplyExtraMics()
        {
            var own = PairedCapture(ActiveOutId);
            var want = new List<KeyValuePair<string, float>>();
            if (S.VirtualMicOn && S.ExtraMics != null)
                foreach (var m in S.ExtraMics)
                    if (m != null && !string.IsNullOrEmpty(m.Id) && m.Id != ActiveMicId && (own == null || m.Id != own.Id) && !want.Any(w => w.Key == m.Id))
                        want.Add(new KeyValuePair<string, float>(m.Id, m.GainDb));
            Engine.Mixer.Set(want);
            PushMicDeviceParams();
        }

        /// <summary>Added mics muted by a hotkey (by device id; not saved - every start begins unmuted).</summary>
        readonly HashSet<string> mutedMics = new HashSet<string>();

        /// <summary>Mutes / unmutes microphone n of the Microphones list (1 = the main one). Returns true when muted now.</summary>
        public bool ToggleMicMute(int n)
        {
            if (n <= 1) { Engine.Mixer.MainMuted = !Engine.Mixer.MainMuted; return Engine.Mixer.MainMuted; }
            if (n - 2 >= S.ExtraMics.Count) return false;
            string id = S.ExtraMics[n - 2].Id;
            bool muted = mutedMics.Add(id) || !mutedMics.Remove(id);
            PushMicDeviceParams();
            return muted;
        }

        public bool IsMicMuted(string id) { return mutedMics.Contains(id); }

        /// <summary>A hotkey asks to show or hide the window.</summary>
        public event EventHandler ToggleWindowRequested;
        public void RequestToggleWindow() { if (ToggleWindowRequested != null) ToggleWindowRequested(this, EventArgs.Empty); }

        /// <summary>Each mic's volume trim and own EQ into the running mixer.</summary>
        public void PushMicDeviceParams()
        {
            Engine.Mixer.MainEq.Update(S.MainMicEq, AudioEngine.SampleRate);
            Engine.Mixer.MainId = ActiveMicId;
            Dictionary<string, Dictionary<string, float>> bleed = null;
            if (S.MicBleed.Count > 0)
            {
                bleed = new Dictionary<string, Dictionary<string, float>>();
                foreach (var b in S.MicBleed)
                {
                    Dictionary<string, float> to;
                    if (!bleed.TryGetValue(b.From, out to)) bleed[b.From] = to = new Dictionary<string, float>();
                    to[b.To] = (float)b.Ratio;
                }
            }
            Engine.Mixer.Bleed = bleed;
            foreach (var m in Engine.Mixer.Mics)
            {
                var set = S.ExtraMics.FirstOrDefault(x => x.Id == m.Id);
                if (set == null) continue;
                m.Muted = mutedMics.Contains(m.Id);
                m.GainDb = set.GainDb;
                m.Eq.Update(set.Eq, AudioEngine.SampleRate);
            }
        }

        /// <summary>Device ids in the mixer's order: microphone 1, then the added ones that are running.</summary>
        public string[] MixerMicIds()
        {
            return new[] { ActiveMicId }.Concat(Engine.Mixer.Mics.Select(m => m.Id)).ToArray();
        }

        /// <summary>Uses a calibration: the suggested volumes, the measured bleed (smart auto mix) and auto mix on.</summary>
        public void ApplyMicCalibration(MicCalibration.Result r, string[] ids)
        {
            S.MicBleed.Clear();
            for (int f = 0; f < ids.Length; f++)
                for (int t = 0; t < ids.Length; t++)
                    if (f != t && ids[f] != null && ids[t] != null && r.BleedRatio[f, t] > 0)
                        S.MicBleed.Add(new MicBleedEntry { From = ids[f], To = ids[t], Ratio = r.BleedRatio[f, t] });
            S.MainMicGainDb = (int)r.GainDb[0];
            for (int c = 1; c < ids.Length; c++)
            {
                var set = S.ExtraMics.FirstOrDefault(m => m.Id == ids[c]);
                if (set != null) set.GainDb = (int)r.GainDb[c];
            }
            S.MicAutoMix = true;
            S.MicCalibrated = DateTime.Now.ToString("g");
            PushEngineParams();
        }

        public void ClearMicCalibration()
        {
            S.MicBleed.Clear(); S.MicCalibrated = null;
            PushEngineParams();
        }

        /// <summary>Adds a microphone to the mix (Microphone page > More microphones).</summary>
        public void AddExtraMic(AudioDeviceInfo d)
        {
            if (d == null || d.Id == ActiveMicId || S.ExtraMics.Any(m => m.Id == d.Id)) return;
            S.ExtraMics.Add(new ExtraMicSetting { Id = d.Id, Name = d.Name });
            SaveSoon(); ApplyExtraMics();
        }

        public void RemoveExtraMic(string id)
        {
            S.ExtraMics.RemoveAll(m => m.Id == id);
            SaveSoon(); ApplyExtraMics();
        }

        /// <summary>Mics that can still be added: real inputs not in use yet.</summary>
        public List<AudioDeviceInfo> ExtraMicChoices()
        {
            return MicChoices().Where(d => d.Id != ActiveMicId && !AudioDevices.IsCable(d) && !S.ExtraMics.Any(m => m.Id == d.Id)).ToList();
        }

        public const string MicName = "StreamLite - In", MicFeedName = "StreamLite - Mic Feed", GameOutName = "StreamLite - Out";

        /// <summary>
        /// Gives the virtual cables StreamLite names and switches off the cable endpoints nothing uses ("CABLE In 16ch",
        /// the game cable's recording side). Needs admin rights; apps keep working because they remember devices by ID.
        /// The mic feed itself has to stay on: Windows stops any stream into a disabled device.
        /// </summary>
        public void TidyDevices()
        {
            if (!Elevation.IsElevated) return;
            try
            {
                var outs = Outputs(); var ins = Mics();
                var feed = outs.FirstOrDefault(d => d.Id == ActiveOutId);
                if (feed != null)
                {
                    NameDevice(feed, MicFeedName);
                    var cap = PairedCapture(feed.Id);
                    if (cap != null) { NameDevice(cap, MicName); NameAdapter(cap, MicAdapterName); NameAdapter(feed, MicAdapterName); }
                    if (S.HideVirtualDevices)
                        foreach (var d in outs)
                            if (d.Id != feed.Id && AudioDevices.SameDevice(d, feed)) AudioDevices.SetEnabled(d.Id, false);
                }
                var sink = GameSinkId != null ? outs.FirstOrDefault(d => d.Id == GameSinkId) : null;
                if (sink != null && (feed == null || !AudioDevices.SameDevice(sink, feed)))
                {
                    NameDevice(sink, GameOutName);
                    NameAdapter(sink, GameAdapterName);
                    if (S.HideVirtualDevices)
                        foreach (var d in ins)
                            if (AudioDevices.SameDevice(d, sink)) AudioDevices.SetEnabled(d.Id, false);
                }
                if (S.HideVirtualDevices) CableNames.Tidy();   // other virtual cables nothing uses (Virtual Audio Cable...)
            }
            catch { }
        }

        /// <summary>Switches the cable endpoints TidyDevices turned off back on.</summary>
        public void UntidyDevices()
        {
            if (!Elevation.IsElevated) return;
            try { CableNames.Untidy(); } catch { }
            var adapters = new HashSet<string>();
            foreach (var id in new[] { ActiveOutId, GameSinkId })
            {
                var d = id != null ? Outputs().FirstOrDefault(o => o.Id == id) : null;
                if (d != null && AudioDevices.DeviceKey(d).Length > 0) adapters.Add(AudioDevices.DeviceKey(d));
            }
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                foreach (var d in AudioDevices.List(flow, 2))
                    if (adapters.Contains(AudioDevices.DeviceKey(d))) AudioDevices.SetEnabled(d.Id, true);
        }

        static void NameAdapter(AudioDeviceInfo d, string want)
        {
            if (AudioDevices.AdapterName(d.Id) != want) AudioDevices.RenameAdapter(d.Id, want);
        }

        static void NameDevice(AudioDeviceInfo d, string want)
        {
            if (d.Name != want && !d.Name.StartsWith(want + " (", StringComparison.Ordinal)) AudioDevices.Rename(d.Id, want);
        }

        public void ApplyDefaultMic()
        {
            var cap = PairedCapture(ActiveOutId);
            if (cap == null) return;
            string cur = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
            if (cur != cap.Id)
            {
                S.PreviousDefaultMic = cur;
                AudioDevices.SetDefault(cap.Id);
                SaveSoon();
            }
        }

        public void RestoreDefaultMic()
        {
            if (string.IsNullOrEmpty(S.PreviousDefaultMic)) return;
            var cap = PairedCapture(ActiveOutId ?? (FindVirtualOutput() != null ? FindVirtualOutput().Id : null));
            string cur = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
            if (cap != null && cur == cap.Id) AudioDevices.SetDefault(S.PreviousDefaultMic);
        }

        // ---- Output (headphone/speaker) EQ through Equalizer APO ----

        public EqProfile OutputProfile(string deviceId)
        {
            EqProfile p;
            if (!S.OutputEq.TryGetValue(deviceId, out p) || p == null)
            {
                p = new EqProfile();
                S.OutputEq[deviceId] = p;
            }
            p.Repair();
            return p;
        }

        public void OutputEqChanged() { SaveSoon(); eqTimer.Stop(); eqTimer.Start(); PushDirectEq(); }

        bool eqDeclined;

        /// <summary>
        /// Writes the EQ; when Equalizer APO's folder is locked, Windows asks for permission (once - access then stays
        /// granted) instead of telling you to run StreamLite as administrator. Not again this run after a "No".
        /// </summary>
        public string WriteOutputEqAsking()
        {
            string err = WriteOutputEq(!eqDeclined);
            if (!eqDeclined && err == EqualizerApo.NotGranted) eqDeclined = true;
            return err;
        }

        public string WriteOutputEq(bool allowElevation)
        {
            if (!EqualizerApo.IsInstalled) return "Equalizer APO is not installed.";
            var names = Outputs().ToDictionary(d => d.Id, d => d.Name);
            string text = EqualizerApo.BuildConfig(S.OutputEq, id => { string n; return names.TryGetValue(id, out n) ? n : null; });
            return EqualizerApo.Apply(text, allowElevation);
        }

        // ---- Game 3D audio ----

        /// <summary>The headphones game profiles apply to: the 3D audio headphones, else your chosen/real default output.</summary>
        public string ProfileDeviceId()
        {
            if (GamePhonesId != null) return GamePhonesId;
            var outs = Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList();
            var p = outs.FirstOrDefault(d => d.Id == S.Game.HeadphonesId) ?? outs.FirstOrDefault(d => d.Id == S.OutputDeviceId)
                ?? outs.FirstOrDefault(d => d.Id == RealDefaultOutput()) ?? outs.FirstOrDefault();
            return p != null ? p.Id : null;
        }

        /// <summary>True while 3D audio runs "natural": a film/video/story profile is active (see NaturalWhenNoGame).</summary>
        public bool NaturalSound
        {
            get
            {
                if (!S.Game.NaturalWhenNoGame || Profiles == null || Game.Processor.TestRunning) return false;   // the footstep test shows your game settings
                var p = Profiles.Find(Profiles.ActiveName);
                return p != null && p.Cinematic;
            }
        }

        /// <summary>What the 3D engine was last given (for status.txt).</summary>
        public volatile string GameTuning = "";

        /// <summary>
        /// Direct to headphones sends every game into StreamLite - Out, also one whose profile uses the game's own HRTF
        /// (CS2, Valorant...): that sound is already 3D, so it plays as it is - no StreamLite tricks on top.
        /// </summary>
        public bool GameOwnHrtf
        {
            get
            {
                if (!S.Game.Exclusive || Game.ExclusiveError != null || Profiles == null || Profiles.DetectedExe == null || Game.Processor.TestRunning) return false;
                var p = Profiles.Find(Profiles.ActiveName);
                return p != null && p.ThreeD == "game" && p.Exes.Count > 0 && !p.ChatApp && !p.WhilePlaying;
            }
        }

        /// <summary>16D music is on and the Music profile is the one playing (Spotify... with no game running).</summary>
        public bool Music16DPlaying
        {
            get { return S.Game.Music16D && Profiles != null && Profiles.ActiveName == GameProfiles.Music && !Game.Processor.TestRunning; }
        }

        /// <summary>The apps kept out of 3D: the list on the 3D Audio page, minus the music apps while 16D music is on.</summary>
        public string[] PlainApps()
        {
            if (!S.Game.BypassEnabled) return new string[0];
            var list = (S.Game.BypassApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0);
            if (S.Game.Music16D && Profiles != null)
            {
                var music = Profiles.Find(GameProfiles.Music);
                if (music != null) list = list.Where(a => !music.Exes.Any(x => string.Equals(GameProfiles.NormalizeExe(x) ?? x, GameProfiles.NormalizeExe(a) ?? a, StringComparison.OrdinalIgnoreCase)));
            }
            return list.ToArray();
        }

        /// <summary>16D music: the music apps back to Windows' default output - 3D audio's input (they were kept out).</summary>
        void RouteMusicTo3D()
        {
            if (!S.Game.Music16D || Profiles == null) return;
            var music = Profiles.Find(GameProfiles.Music);
            if (music != null && music.Exes.Count > 0) AppRouting.Route(music.Exes, null);
        }

        public void PushGameParams()
        {
            var cfg = S.Game.ToConfig();
            bool natural = NaturalSound, own = !natural && GameOwnHrtf;
            var profile = Profiles != null ? Profiles.Find(Profiles.ActiveName) : null;
            GameTuning = (natural ? "natural" : own ? "as the game mixed it (its own HRTF)" : "game settings, blended with the profile EQ") + (Profiles != null ? " (" + Profiles.ActiveName + ")" : "");
            if (natural || own)
            {
                // Videos and films: keep the 3D placement, drop the competitive tricks that colour and compress them.
                // A game with its own HRTF: nothing on top either (and no second 3D for its stereo).
                // (The -1 dBFS limiter still catches overs, so nothing can clip.)
                cfg.FootstepBoost = 0; cfg.CueSharpen = 0; cfg.LoudTamer = 0;
                // Open sphere still gives muffled footsteps their treble back in a game with its own 3D (CS2...):
                // the same lift on both ears leaves its 3D as it is. Films keep their walls.
                if (natural) cfg.Demuffle = 0;
                foreach (var d in SpatialProcessor.Dirs) { cfg.DirEq[d].Enabled = false; cfg.DirGainDb[d] = 0; }
                if (own) cfg.VirtualizeStereo = false;
            }
            else GameProfiles.Blend3D(cfg, profile);   // 3D boosts and the profile EQ don't stack
            // "16D" music: while music plays and no game runs (games win), the song circles your head.
            if (Music16DPlaying) { cfg.OrbitSeconds = Math.Max(2, S.Game.Music16DSeconds); GameTuning = "16D music (" + Profiles.ActiveName + ")"; }
            Game.Processor.Configure(cfg);
            SaveSoon();
        }

        /// <summary>The mic's virtual cable (and its other inputs, like "CABLE In 16ch") must never carry game audio.</summary>
        public bool IsMicCable(AudioDeviceInfo d)
        {
            var mic = Outputs().FirstOrDefault(o => o.Id == (ActiveOutId ?? S.VirtualOutId));
            if (mic == null) mic = FindVirtualOutput();
            return mic != null && (d.Id == mic.Id || AudioDevices.SameDevice(d, mic));
        }

        public List<AudioDeviceInfo> GameSinkCandidates()
        {
            return Outputs().Where(d => AudioDevices.LooksVirtual(d) && !IsMicCable(d)).ToList();
        }

        public AudioDeviceInfo FindGameSink()
        {
            var c = GameSinkCandidates();
            return c.FirstOrDefault(d => d.Id == S.Game.SinkId)
                ?? c.FirstOrDefault(d => d.Name.StartsWith(GameOutName, StringComparison.OrdinalIgnoreCase))
                ?? c.FirstOrDefault(d => d.Name.IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? c.FirstOrDefault();
        }

        public AudioDeviceInfo FindHeadphones(string sinkId)
        {
            var outs = Outputs().Where(d => d.Id != sinkId && !AudioDevices.LooksVirtual(d)).ToList();
            return outs.FirstOrDefault(d => d.Id == S.Game.HeadphonesId)
                ?? outs.FirstOrDefault(d => d.Id == S.Game.PreviousDefault)
                ?? outs.FirstOrDefault(d => d.Id == AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console))
                ?? outs.FirstOrDefault(d => d.Name.IndexOf("Headphones", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? outs.FirstOrDefault();
        }

        /// <summary>The clean mic now listens to another microphone (its name): yours was unplugged, or it came back.</summary>
        public event EventHandler<string> MicFollowed;

        /// <summary>
        /// Mics come and go too: when the one StreamLite listens to is unplugged and another real mic is there, the
        /// clean mic switches to it; when the mic you picked comes back, it switches back.
        /// </summary>
        void FollowMic()
        {
            if (!S.VirtualMicOn || ActiveMicId == null) return;
            try
            {
                var mics = Mics();
                var paired = PairedCapture(ActiveOutId);
                bool gone = !mics.Any(d => d.Id == ActiveMicId);
                bool chosenBack = !string.IsNullOrEmpty(S.MicId) && ActiveMicId != S.MicId && mics.Any(d => d.Id == S.MicId) && (paired == null || paired.Id != S.MicId);
                if (!chosenBack && (!gone || !mics.Any(d => !AudioDevices.IsCable(d)))) return;   // nothing to switch to: it reconnects when it's back
                string before = ActiveMicId;
                RestartAudio();
                var now = ActiveMicId != before ? mics.FirstOrDefault(d => d.Id == ActiveMicId) : null;
                if (now != null && MicFollowed != null) MicFollowed(this, now.Name);
            }
            catch { }
        }

        /// <summary>3D audio moved to other headphones (their name): a headset was plugged in, unplugged or picked in Windows.</summary>
        public event EventHandler<string> HeadphonesFollowed;

        /// <summary>Headphones got their own correction from AutoEq by their name ("device: correction").</summary>
        public event EventHandler<string> CorrectionFound;
        bool autoLooking;

        /// <summary>
        /// Every headset sounds different: the first time headphones are used without a correction, StreamLite looks
        /// their model up in AutoEq by the name Windows gives them and, when the model is certain, uses its measured
        /// correction - every game profile then sits on neutral headphones. Once per device (a correction you remove
        /// stays removed); offline it tries again next time.
        /// </summary>
        public void AutoCorrect(string devId)
        {
            var ps = S.Profiles;
            if (devId == null || autoLooking) return;
            if (ps.AutoLooked == null) ps.AutoLooked = new List<string>();
            if (ps.AutoLooked.Contains(devId) || ps.CorrectionFor(devId) != null) return;
            var d = Outputs().FirstOrDefault(o => o.Id == devId);
            if (d == null || AudioDevices.LooksVirtual(d)) return;
            autoLooking = true;
            var ui = System.Threading.SynchronizationContext.Current;
            string name = d.Name;
            new System.Threading.Thread(() =>
            {
                AutoEq.Entry e = null; List<EqFilter> filters = null; bool looked;
                try
                {
                    e = AutoEq.BestFor(AutoEq.Index(AppSettings.Folder), name);
                    if (e != null) { double pre; filters = AutoEq.Download(e, out pre); }
                    looked = true;
                }
                catch { looked = false; }   // offline: next time
                Action done = () =>
                {
                    autoLooking = false;
                    if (!looked) return;
                    ps.AutoLooked.Add(devId);
                    string what = e == null ? "no certain AutoEq match" : e.Name + " (" + e.Source + ")";
                    try { HealthMonitor.LogLine("Headphones: " + name + " - " + what); } catch { }
                    if (filters != null && ps.CorrectionFor(devId) == null)
                    {
                        ps.SetCorrection(devId, e.Name + " to neutral (AutoEq, " + e.Source + ")", filters);
                        if (Profiles != null) Profiles.Reapply();
                        if (CorrectionFound != null) CorrectionFound(this, name + ": " + e.Name);
                    }
                    SaveSoon();
                };
                if (ui != null) ui.Post(_ => done(), null); else done();
            }) { IsBackground = true, Name = "AutoEq lookup" }.Start();
        }

        /// <summary>
        /// Headsets come and go. While 3D audio is on: when a real output becomes the Windows default (you picked it in
        /// the sound flyout, or Windows did for a headset you just plugged in), 3D audio moves there; when the
        /// headphones it plays to disappear, it moves to the best one left. The game device becomes the default again.
        /// </summary>
        void FollowHeadphones()
        {
            if ((!S.Game.Enabled && !MultiOut) || GameSinkId == null || GamePhonesId == null) return;
            try
            {
                var outs = Outputs();
                string cur = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
                // One of the extra outputs picked in Windows: it's already playing - keep StreamLite - Out the default.
                if (cur != null && S.ExtraOutputs.Any(o => o.Id == cur)) { AudioDevices.SetDefaultPlayback(GameSinkId); return; }
                var picked = outs.FirstOrDefault(d => d.Id == cur && d.Id != GameSinkId && d.Id != GamePhonesId && !AudioDevices.LooksVirtual(d));
                bool gone = !outs.Any(d => d.Id == GamePhonesId);
                // Your own headphones are back (after a fallback while they were unplugged).
                bool back = S.Game.HeadphonesId != null && S.Game.HeadphonesId != GamePhonesId && outs.Any(d => d.Id == S.Game.HeadphonesId);
                if (picked == null && !gone && !back) return;
                if (picked != null) S.Game.HeadphonesId = picked.Id;
                RestartGameAudio();
                var now = outs.FirstOrDefault(d => d.Id == GamePhonesId);
                if (now != null && HeadphonesFollowed != null) HeadphonesFollowed(this, now.Name);
                if (Profiles != null) Profiles.Reapply();   // that headset's own correction
            }
            catch { }
        }

        /// <summary>The Windows default playback device - or, while 3D audio has made the game device the default, the real headphones.</summary>
        public string RealDefaultOutput()
        {
            string cur = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
            if (cur != null && GameSinkCandidates().Any(d => d.Id == cur))
            {
                var p = FindHeadphones(cur);
                if (p != null) return p.Id;
            }
            return cur;
        }

        /// <summary>
        /// While 3D audio is on: music, voice and browser apps go straight to the headphones, and so does a running game
        /// whose profile uses the game's own HRTF. Runs every few seconds, because Windows only accepts the routing
        /// once an app has opened audio; after that Windows remembers it for the app's next launches too.
        /// </summary>
        public void RouteBypassApps()
        {
            try
            {
                // Direct to headphones: nothing can play to them but StreamLite. Games and everything else play into
                // StreamLite - Out (the fast 3D path); voice chat, music and browsers play to a side output (its own sound
                // muted meanwhile), where StreamLite's mixer picks each of them up and plays them as they are. Without a
                // side output they go through StreamLite - Out too.
                if (DirectRouting)
                {
                    var plain = PlainApps();
                    // No side output when Windows can't hand StreamLite each app's sound: those apps would play to a muted device.
                    var side = Game.Mixer.CaptureBroken ? null : SideOutput();
                    if (side != null) MuteSide(side.Id); else UnmuteSide();
                    string voice = side != null ? side.Id : GameSinkId;
                    AppRouting.Route(plain, side != null ? side.Id : null);
                    RouteMusicTo3D();
                    if (AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications) != voice) AudioDevices.SetDefaultRole(voice, ERole.Communications);
                    // A game with its own 3D for headphones stays on the fast path: StreamLite - Out plays stereo as it is.
                    var gp = Profiles != null && Profiles.DetectedExe != null ? Profiles.Find(Profiles.ActiveName) : null;
                    if (gp != null && gp.Exes.Count > 0) AppRouting.Route(gp.Exes, null);
                    Game.Mixer.SinkId = GameSinkId;
                    Game.Mixer.DeviceId = side != null ? side.Id : null;
                    SideOutputName = side != null ? side.Name : null;
                    RoutingError = null;
                    return;
                }
                Game.Mixer.DeviceId = null; SideOutputName = null;
                UnmuteSide();
                // Back from Direct to headphones (switched off, or the headphones couldn't): voice apps on the headphones again.
                if (S.Game.Enabled && !MultiOut && GameSinkId != null && GamePhonesId != null && AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications) == GameSinkId)
                    AudioDevices.SetDefaultRole(GamePhonesId, ERole.Communications);
                // With several outputs nothing is sent straight to the headphones: every output hears every app.
                if (S.Game.Enabled && !MultiOut && Game.Running && GameSinkId != null && GamePhonesId != null)
                {
                    if (S.Game.BypassEnabled)
                        AppRouting.Route(PlainApps(), GamePhonesId);
                    RouteMusicTo3D();
                    var p = Profiles.DetectedExe != null ? Profiles.Find(Profiles.ActiveName) : null;
                    if (p != null && p.Exes.Count > 0)
                    {
                        int n = AppRouting.Route(p.Exes, p.ThreeD == "game" ? GamePhonesId : null);
                        // The first time: Windows only moves the game's NEXT sound session, so say so once per game.
                        string exe = Profiles.DetectedExe;
                        if (n > 0 && p.ThreeD == "game" && !p.ChatApp && !p.WhilePlaying && S.RoutedGames != null && !S.RoutedGames.Contains(exe))
                        {
                            S.RoutedGames.Add(exe); SaveSoon();
                            if (GameRoutedFirstTime != null) GameRoutedFirstTime(this, p.Name);
                        }
                    }
                }
                else AppRouting.ResetAll();
                RoutingError = null;
            }
            catch (Exception ex) { RoutingError = ex.Message; }
        }

        /// <summary>Direct to headphones is on and working (not fallen back to the normal output).</summary>
        public bool DirectRouting
        {
            get { return S.Game.Exclusive && Game.Running && Game.ExclusiveError == null && GameSinkId != null && GamePhonesId != null && (S.Game.Enabled || MultiOut); }
        }

        /// <summary>Direct to headphones in a line (status.txt, Health).</summary>
        public string DirectStatus()
        {
            if (!S.Game.Exclusive) return "off";
            if (!Game.Running) return "on (3D audio is off)";
            if (Game.ExclusiveError != null) return "not possible right now - normal output: " + Game.ExclusiveError;
            if (!Game.ExclusiveActive) return "starting";
            var taps = Game.Mixer.Taps;
            return "exclusive, " + Game.ExclusivePeriodMs.ToString("0.#") + " ms steps; " +
                (SideOutputName == null ? "no side output - voice chat and music go through 3D audio" :
                 "voice chat/music via " + SideOutputName + " (muted): " + (taps.Length == 0 ? "none playing" : string.Join(", ", taps.Select(t => t.Name + " " + (t.Frames / 48000.0).ToString("0") + " s" + (t.Ring.Underruns > 0 ? ", " + t.Ring.Underruns + " gaps" : "") + (t.AlsoOnSink ? " (also plays into 3D audio: heard there, not twice)" : ""))))) +
                (Game.Mixer.LastError != null ? " (" + Game.Mixer.LastError + ")" : "");
        }

        /// <summary>The side output in use for Direct to headphones (its name), or null.</summary>
        public volatile string SideOutputName;

        /// <summary>
        /// Where voice chat, music and browsers play during Direct to headphones: another real output of this PC
        /// (speakers, a monitor's sound, the motherboard's output) - never the headphones themselves (or another
        /// part of them), StreamLite's devices, an output you listen to, or a Bluetooth hands-free one.
        /// </summary>
        public AudioDeviceInfo SideOutput()
        {
            var phones = Outputs().FirstOrDefault(d => d.Id == GamePhonesId);
            var ok = Outputs().Where(d => d.Id != GamePhonesId && d.Id != GameSinkId && !IsMicCable(d) && !AudioDevices.LooksVirtual(d) &&
                !S.ExtraOutputs.Any(o => o.Id == d.Id) && (phones == null || !AudioDevices.SameDevice(d, phones)) &&
                d.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) < 0 && d.Name.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            var pick = ok.FirstOrDefault(d => d.Id == S.Game.SideOutputId);
            if (pick == null) pick = ok.FirstOrDefault(d => d.Name.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase)) ?? ok.FirstOrDefault();
            return pick;
        }

        /// <summary>Mutes the side output's own sound (once; what it was is remembered, also across a crash).</summary>
        void MuteSide(string id)
        {
            if (S.Game.SideMutedId == id) return;
            UnmuteSide();
            bool? was = AudioDevices.GetMute(id);
            S.Game.SideMutedId = id; S.Game.SideWasMuted = was == true;
            S.Save();   // at once: if StreamLite is stopped right after, the next start must know it muted it
            AudioDevices.SetMute(id, true);
        }

        /// <summary>Gives the side output its sound back (Direct to headphones off, fallen back, or StreamLite closing).</summary>
        public void UnmuteSide()
        {
            if (S.Game.SideMutedId == null) return;
            try { AudioDevices.SetMute(S.Game.SideMutedId, S.Game.SideWasMuted); } catch { }
            S.Game.SideMutedId = null;
            S.Save();
        }

        /// <summary>The headphones' EQ for Direct to headphones (Equalizer APO doesn't run on exclusive streams).</summary>
        public void PushDirectEq()
        {
            EqProfile p = null;
            if (GamePhonesId != null) S.OutputEq.TryGetValue(GamePhonesId, out p);
            Game.MainEqL.Update(p, SpatialProcessor.Fs); Game.MainEqR.Update(p, SpatialProcessor.Fs);
        }

        public void RestartGameAudio()
        {
            GameError = null;
            Game.Exclusive = S.Game.Exclusive;
            PushGameParams();
            var sink = FindGameSink();
            var phones = FindHeadphones(sink != null ? sink.Id : null);
            GameSinkId = sink != null ? sink.Id : null;
            GamePhonesId = phones != null ? phones.Id : null;
            GamePhonesName = phones != null ? phones.Name : null;
            if (!S.Game.Enabled && !MultiOut) { Game.Stop(); RestoreGameDefault(); UnmuteSide(); return; }
            if (phones == null) { Game.Stop(); UnmuteSide(); GameError = "No headphones found."; return; }
            // Several outputs: everything plays through StreamLite - Out on all of them (with 3D off: plain stereo).
            Game.Plain = !S.Game.Enabled;
            Game.StartMargin = S.Game.LearnedMargin;
            ApplyExtraOutputs();
            if (sink == null)
            {
                // Test sound still works; games need the virtual device.
                GameError = "No virtual 7.1 device yet - install the free VB-Audio Hi-Fi Cable (button above). The test sound works already.";
                Game.Start(null, phones.Id);
                return;
            }
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(sink.Id, out ch, out mask, out rate) || ch != 8 || rate != 48000)
            {
                string err = AudioDevices.SetSurround71(sink.Id);
                if (err != null) GameError = "Could not switch " + sink.Name + " to 7.1: " + err;
            }
            string cur = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
            if (cur != sink.Id) { S.Game.PreviousDefault = cur; AudioDevices.SetDefaultPlayback(sink.Id); }
            // Voice/communications apps stay on the real headphones - or, with several outputs, play on all of them too.
            // Direct to headphones: on the side output StreamLite's mixer plays as it is (as RouteBypassApps keeps it).
            // Setting the game cable here first made Discord follow it and stay there: its calls then played through
            // 3D and the mixer both - every voice twice, 30 ms apart.
            string comm = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications);
            var sideOut = S.Game.Exclusive && !MultiOut ? SideOutput() : null;
            string commWant = sideOut != null ? sideOut.Id : sink.Id;
            if (MultiOut || S.Game.Exclusive) { if (comm != commWant) AudioDevices.SetDefaultRole(commWant, ERole.Communications); }
            else if (comm == sink.Id) AudioDevices.SetDefaultRole(phones.Id, ERole.Communications);
            // Remember them as your headphones - unless yours are only unplugged right now (this is a stand-in until they're back).
            int prefState = S.Game.HeadphonesId != null && S.Game.HeadphonesId != phones.Id ? AudioDevices.State(S.Game.HeadphonesId) : 0;
            if (prefState != 4 && prefState != 8) S.Game.HeadphonesId = phones.Id;
            // These headphones' own head model and cue sharpening (from the listening test), when switching to them.
            if (phones.Id != personalFor) { personalFor = phones.Id; ApplyPersonal3D(phones.Id); }
            // Gaming headsets whose driver shows up as 7.1 run their own virtual surround on what StreamLite sends.
            int pch, pmask, prate;
            if (S.Game.Enabled && AudioDevices.MixFormat(phones.Id, out pch, out pmask, out prate) && pch > 2)
                GameError = phones.Name + " is set up as " + (pch == 8 ? "7.1" : pch == 6 ? "5.1" : pch + " channels") + ". StreamLite sends it 3D stereo: if it's a gaming headset, turn off its own virtual surround in its app (G HUB, NGENUITY, Synapse, iCUE, SteelSeries GG...) so games aren't made 3D twice; a sound card can stay as it is.";
            SaveSoon();
            TidyDevices();
            Game.StartMargin = S.Game.LearnedMargin;
            Game.Start(sink.Id, phones.Id);
            PushDirectEq();
            RouteBypassApps();
            // Installed or updated: ask once whether to play Direct to headphones (lower delay).
            if (!S.ExclusiveAsked && !S.Game.Exclusive && DirectQuestion != null) DirectQuestion(this, EventArgs.Empty);
        }

        /// <summary>3D audio is playing to headphones and Direct to headphones hasn't been offered yet.</summary>
        public event EventHandler DirectQuestion;
        /// <summary>Offers it now if it's due (3D audio already running when the window came up).</summary>
        public bool DirectQuestionDue { get { return !S.ExclusiveAsked && !S.Game.Exclusive && Game.Running && GamePhonesId != null; } }

        /// <summary>Direct to headphones switched on or off (Settings or the first-start question).</summary>
        public void SetDirect(bool on)
        {
            S.Game.Exclusive = on; S.ExclusiveAsked = true;
            SaveSoon();
            if (Game.Running) RestartGameAudio();
        }

        /// <summary>Puts Windows playback back on the headphones if it still points at the game device.</summary>
        public void RestoreGameDefault()
        {
            try
            {
                string cur = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
                var sinks = GameSinkCandidates();
                if (cur == null || !sinks.Any(d => d.Id == cur)) return;
                var phones = FindHeadphones(cur);
                string back = !string.IsNullOrEmpty(S.Game.PreviousDefault) && S.Game.PreviousDefault != cur ? S.Game.PreviousDefault : phones != null ? phones.Id : null;
                if (back != null) AudioDevices.SetDefaultPlayback(back);
                // Voice apps too (several outputs had them on StreamLite - Out).
                if (back != null && sinks.Any(d => d.Id == AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications))) AudioDevices.SetDefaultRole(back, ERole.Communications);
            }
            catch { }
        }

        // ---- Personal 3D: the head model and cue sharpening that work best for you, per pair of headphones

        string personalFor;

        /// <summary>The listening test's result for a pair of headphones ("ku100|35", or "ku100|35|75" with floor contrast), or null.</summary>
        public string Personal3D(string phonesId)
        {
            string v;
            return phonesId != null && S.Game.PersonalHeads.TryGetValue(phonesId, out v) ? v : null;
        }

        public void SavePersonal3D(string phonesId, string hrtf, int cue, int contrast = -1, int headSize = 100)
        {
            if (phonesId == null) return;
            S.Game.PersonalHeads[phonesId] = hrtf + "|" + cue + "|" + (contrast >= 0 ? contrast : S.Game.FloorContrast) + "|" + headSize;
            personalFor = phonesId;
            ApplyPersonal3D(phonesId);
        }

        void ApplyPersonal3D(string phonesId)
        {
            var v = Personal3D(phonesId);
            if (v == null) return;
            var p = v.Split('|');
            int cue, contrast = S.Game.FloorContrast, size = 100;
            if (p.Length < 2 || !int.TryParse(p[1], out cue) || !HrtfSet.Available(p[0])) return;
            if (p.Length >= 3 && int.TryParse(p[2], out contrast)) contrast = Math.Max(0, Math.Min(100, contrast));
            if (p.Length >= 4 && int.TryParse(p[3], out size)) size = Math.Max(80, Math.Min(120, size)); else size = 100;
            if (S.Game.Hrtf == p[0] && S.Game.CueSharpen == cue && S.Game.FloorContrast == contrast && S.Game.HeadSize == size) return;
            S.Game.Hrtf = p[0]; S.Game.CueSharpen = cue; S.Game.FloorContrast = contrast; S.Game.HeadSize = size;
            PushGameParams();
        }

        /// <summary>A head model / cue change on the 3D page also becomes these headphones' own (when they have one).</summary>
        public void RememberPersonal3D()
        {
            string id = GamePhonesId;
            if (id != null && Personal3D(id) != null && HrtfSet.Available(S.Game.Hrtf))
                S.Game.PersonalHeads[id] = S.Game.Hrtf + "|" + S.Game.CueSharpen + "|" + S.Game.FloorContrast + "|" + S.Game.HeadSize;
        }

        // ---- Outputs: 1 = your headphones, 2, 3... = more outputs playing everything too

        /// <summary>Two or more outputs: everything plays through StreamLite - Out on all of them.</summary>
        public bool MultiOut { get { return S.ExtraOutputs != null && S.ExtraOutputs.Count > 0; } }

        public void ApplyExtraOutputs()
        {
            var want = new List<KeyValuePair<string, float>>();
            foreach (var o in S.ExtraOutputs)
                if (o.Id != GamePhonesId && o.Id != GameSinkId && !want.Any(w => w.Key == o.Id)) want.Add(new KeyValuePair<string, float>(o.Id, o.GainDb));
            Game.SetOutputs(want);
            PushOutputDeviceParams();
        }

        /// <summary>How much an output is held back to play in sync with the others (ms; 0 with a single output).</summary>
        public int OutputDelay(string id)
        {
            int ms;
            return MultiOut && id != null && S.OutputDelayMs.TryGetValue(id, out ms) ? ms : 0;
        }

        public void SetOutputDelay(string id, int ms)
        {
            if (id == null) return;
            S.OutputDelayMs[id] = Math.Max(0, Math.Min(900, ms));
            PushOutputDeviceParams();
        }

        /// <summary>Each extra output's volume and own EQ into the running engine.</summary>
        public void PushOutputDeviceParams()
        {
            Game.MainDelayFrames = OutputDelay(GamePhonesId) * 48;
            foreach (var o in Game.ExtraOutputs) o.DelayFrames = OutputDelay(o.Id) * 48;
            foreach (var o in Game.ExtraOutputs)
            {
                var set = S.ExtraOutputs.FirstOrDefault(x => x.Id == o.Id);
                if (set == null) continue;
                o.GainDb = set.GainDb;
                o.EqL.Update(set.Eq, SpatialProcessor.Fs); o.EqR.Update(set.Eq, SpatialProcessor.Fs);
            }
            SaveSoon();
        }

        public void AddExtraOutput(AudioDeviceInfo d)
        {
            if (d == null || d.Id == GamePhonesId || S.ExtraOutputs.Any(o => o.Id == d.Id)) return;
            bool first = !MultiOut;
            S.ExtraOutputs.Add(new ExtraOutputSetting { Id = d.Id, Name = d.Name });
            SaveSoon();
            if (first || !Game.Running) RestartGameAudio();   // starts playing through StreamLite - Out
            else ApplyExtraOutputs();
        }

        public void RemoveExtraOutput(string id)
        {
            S.ExtraOutputs.RemoveAll(o => o.Id == id);
            SaveSoon();
            if (!MultiOut) RestartGameAudio();   // back to normal: straight to the headphones (or 3D only)
            else ApplyExtraOutputs();
        }

        /// <summary>Outputs that can still be added: real ones not in use yet.</summary>
        public List<AudioDeviceInfo> ExtraOutputChoices()
        {
            return Outputs().Where(d => !AudioDevices.LooksVirtual(d) && d.Id != GamePhonesId && !S.ExtraOutputs.Any(o => o.Id == d.Id)).ToList();
        }

        public void Dispose()
        {
            try { Interop.LockIn.Release(); } catch { }   // nobody stays turned down after StreamLite closes
            routeTimer.Stop();
            Profiles.Stop();
            VoiceTest.StopPlaying();
            try { AppRouting.ResetAll(); } catch { }
            Game.Stop();
            RestoreGameDefault();
            UnmuteSide();
            Engine.Stop();
            Engine.Mixer.StopAll();
            if (S.MakeDefaultMic) RestoreDefaultMic();
            try { CamFx.Dispose(); } catch { }
            Cameras.Dispose();
            S.Save();
        }
    }

    internal sealed class CameraService : IDisposable
    {
        readonly AppSettings s;
        readonly Dictionary<string, CameraDevice> open = new Dictionary<string, CameraDevice>();
        readonly Timer timer = new Timer { Interval = 2000 };
        int ticks;
        public List<VideoDeviceInfo> Devices = new List<VideoDeviceInfo>();
        public event EventHandler DevicesChanged;

        public CameraService(AppSettings settings)
        {
            s = settings;
            timer.Tick += (o, e) => Tick();
        }

        public void Start()
        {
            Refresh();
            EnforceAll();
            timer.Start();
        }

        public void Refresh()
        {
            List<VideoDeviceInfo> list;
            try { list = VideoDevices.List(); } catch { list = new List<VideoDeviceInfo>(); }
            bool changed = list.Count != Devices.Count || list.Where((d, i) => d.Key != Devices[i].Key).Any();
            Devices = list;
            foreach (var key in open.Keys.ToList())
                if (!list.Any(d => d.Key == key)) { open[key].Dispose(); open.Remove(key); }
            if (changed && DevicesChanged != null) DevicesChanged(this, EventArgs.Empty);
        }

        public CameraDevice Get(VideoDeviceInfo info)
        {
            CameraDevice dev;
            if (open.TryGetValue(info.Key, out dev)) return dev;
            dev = new CameraDevice(info);
            open[info.Key] = dev;
            return dev;
        }

        public CameraProfile Profile(string key)
        {
            CameraProfile p;
            if (!s.Cameras.TryGetValue(key, out p) || p == null) { p = new CameraProfile(); s.Cameras[key] = p; }
            if (p.Values == null) p.Values = new Dictionary<string, CameraPropSetting>();
            if (p.SavedLook == null) p.SavedLook = new Dictionary<string, CameraPropSetting>();
            return p;
        }

        void Tick()
        {
            if (++ticks % 5 == 0) Refresh();
            EnforceAll();
        }

        /// <summary>Re-applies saved settings to every locked camera if another app changed them.</summary>
        public void EnforceAll()
        {
            foreach (var info in Devices)
            {
                CameraProfile p;
                if (!s.Cameras.TryGetValue(info.Key, out p) || p == null || !p.Lock || p.Values.Count == 0) continue;
                try { Enforce(Get(info), p); } catch { }
            }
        }

        public static void Enforce(CameraDevice dev, CameraProfile p)
        {
            foreach (var prop in dev.Properties)
            {
                CameraPropSetting want;
                if (!p.Values.TryGetValue(prop.Key, out want)) continue;
                int cur; bool auto;
                if (!dev.Get(prop, out cur, out auto)) continue;
                if (want.Auto && prop.SupportsAuto) { if (!auto) dev.Set(prop, cur, true); }
                else if (auto || cur != want.Value) dev.Set(prop, want.Value, false);
            }
        }

        public void Dispose()
        {
            timer.Stop();
            foreach (var d in open.Values) d.Dispose();
            open.Clear();
        }
    }
}
