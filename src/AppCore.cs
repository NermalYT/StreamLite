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
            // Before 3.0.1 every dropout - a game loading too - raised the 3D buffer for good, and it was remembered
            // (often 10+ ms of extra game delay). Learn it again under the new rules (only recurring gaps count).
            Version last;
            if (!lookupOnly && S.Game.LearnedMargin > 0 && (S.LastVersion == null || !Version.TryParse(S.LastVersion, out last) || last < new Version(3, 0, 1)))
                S.Game.LearnedMargin = 0;
            Health = new HealthMonitor(this);
            if (!lookupOnly) { healthTimer.Tick += (s, e) => { try { Health.Sample(); } catch { } }; healthTimer.Start(); }
            Cameras = new CameraService(S);
            CamFx = lookupOnly ? null : new Camera.CameraFx(S);
            Engine.Test = VoiceTest;
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); S.Save(); };
            eqTimer.Tick += (s, e) => { eqTimer.Stop(); OutputEqError = WriteOutputEq(false); };
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
                    "mic timing: longest between packets " + Engine.WorstPacketIntervalMs.ToString("0.0") + " ms, longest processing " + Engine.WorstProcessingMs.ToString("0.00") + " ms (last 10-20 s), " + Engine.EarlyPackets + " packets picked up before a late signal\r\n" +
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

        /// <summary>Low VRAM on/off: DeepFilterNet on the GPU and the AI camera reload with their small models.</summary>
        public void SetLowVram(bool on)
        {
            if (S.LowVram == on) return;
            S.LowVram = on;
            PushEngineParams();
            CamFx.Restart();
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
            var mic = mics.FirstOrDefault(d => d.Id == S.MicId && (paired == null || d.Id != paired.Id))
                      ?? mics.FirstOrDefault(d => d.Id == AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console) && !AudioDevices.LooksVirtual(d))
                      ?? mics.FirstOrDefault(d => !AudioDevices.LooksVirtual(d));
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
            Engine.LowLatency = true;   // always: exclusive mic at its smallest period, shared mode by itself when that can't work
            Engine.StartMargin = S.MicLearnedFor == ActiveMicId ? S.MicLearnedMargin : 0;   // what this mic needed last time
            Engine.Start(ActiveMicId, ActiveOutId, monitor);
            ApplyExtraMics();
            if (S.MakeDefaultMic) ApplyDefaultMic(); else RestoreDefaultMic();
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
            return MicChoices().Where(d => d.Id != ActiveMicId && !AudioDevices.LooksVirtual(d) && !S.ExtraMics.Any(m => m.Id == d.Id)).ToList();
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
            }
            catch { }
        }

        /// <summary>Switches the cable endpoints TidyDevices turned off back on.</summary>
        public void UntidyDevices()
        {
            if (!Elevation.IsElevated) return;
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

        public void OutputEqChanged() { SaveSoon(); eqTimer.Stop(); eqTimer.Start(); }

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

        public void PushGameParams()
        {
            var cfg = S.Game.ToConfig();
            bool natural = NaturalSound;
            GameTuning = (natural ? "natural" : "game settings") + (Profiles != null ? " (" + Profiles.ActiveName + ")" : "");
            if (natural)
            {
                // Videos and films: keep the 3D placement, drop the competitive tricks that colour and compress them.
                // (The -1 dBFS limiter still catches overs, so nothing can clip.)
                cfg.FootstepBoost = 0; cfg.CueSharpen = 0; cfg.LoudTamer = 0;
                foreach (var d in SpatialProcessor.Dirs) { cfg.DirEq[d].Enabled = false; cfg.DirGainDb[d] = 0; }
            }
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
                if (!chosenBack && (!gone || !mics.Any(d => !AudioDevices.LooksVirtual(d)))) return;   // nothing to switch to: it reconnects when it's back
                string before = ActiveMicId;
                RestartAudio();
                var now = ActiveMicId != before ? mics.FirstOrDefault(d => d.Id == ActiveMicId) : null;
                if (now != null && MicFollowed != null) MicFollowed(this, now.Name);
            }
            catch { }
        }

        /// <summary>3D audio moved to other headphones (their name): a headset was plugged in, unplugged or picked in Windows.</summary>
        public event EventHandler<string> HeadphonesFollowed;

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
                // With several outputs nothing is sent straight to the headphones: every output hears every app.
                if (S.Game.Enabled && !MultiOut && Game.Running && GameSinkId != null && GamePhonesId != null)
                {
                    if (S.Game.BypassEnabled)
                        AppRouting.Route((S.Game.BypassApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries), GamePhonesId);
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

        public void RestartGameAudio()
        {
            GameError = null;
            PushGameParams();
            var sink = FindGameSink();
            var phones = FindHeadphones(sink != null ? sink.Id : null);
            GameSinkId = sink != null ? sink.Id : null;
            GamePhonesId = phones != null ? phones.Id : null;
            GamePhonesName = phones != null ? phones.Name : null;
            if (!S.Game.Enabled && !MultiOut) { Game.Stop(); RestoreGameDefault(); return; }
            if (phones == null) { Game.Stop(); GameError = "No headphones found."; return; }
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
            string comm = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications);
            if (MultiOut) { if (comm != sink.Id) AudioDevices.SetDefaultRole(sink.Id, ERole.Communications); }
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

        /// <summary>The listening test's result for a pair of headphones ("ku100|35"), or null.</summary>
        public string Personal3D(string phonesId)
        {
            string v;
            return phonesId != null && S.Game.PersonalHeads.TryGetValue(phonesId, out v) ? v : null;
        }

        public void SavePersonal3D(string phonesId, string hrtf, int cue)
        {
            if (phonesId == null) return;
            S.Game.PersonalHeads[phonesId] = hrtf + "|" + cue;
            personalFor = phonesId;
            ApplyPersonal3D(phonesId);
        }

        void ApplyPersonal3D(string phonesId)
        {
            var v = Personal3D(phonesId);
            if (v == null) return;
            var p = v.Split('|');
            int cue;
            if (p.Length != 2 || !int.TryParse(p[1], out cue) || (p[0] != HrtfSet.Ku100 && p[0] != HrtfSet.Kemar)) return;
            if (S.Game.Hrtf == p[0] && S.Game.CueSharpen == cue) return;
            S.Game.Hrtf = p[0]; S.Game.CueSharpen = cue;
            PushGameParams();
        }

        /// <summary>A head model / cue change on the 3D page also becomes these headphones' own (when they have one).</summary>
        public void RememberPersonal3D()
        {
            string id = GamePhonesId;
            if (id != null && Personal3D(id) != null && (S.Game.Hrtf == HrtfSet.Ku100 || S.Game.Hrtf == HrtfSet.Kemar))
                S.Game.PersonalHeads[id] = S.Game.Hrtf + "|" + S.Game.CueSharpen;
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
            routeTimer.Stop();
            Profiles.Stop();
            VoiceTest.StopPlaying();
            try { AppRouting.ResetAll(); } catch { }
            Game.Stop();
            RestoreGameDefault();
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
