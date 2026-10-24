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
        readonly Timer eqTimer = new Timer { Interval = 350 };
        readonly Timer routeTimer = new Timer { Interval = 4000 };
        public string RoutingError;
        public string OutputEqError;

        /// <param name="lookupOnly">Only for reading devices and settings (the uninstaller): no periodic device
        /// checks (they would rename the cables back while it restores them) and no virtual camera.</param>
        public AppCore(bool lookupOnly = false)
        {
            Cameras = new CameraService(S);
            CamFx = lookupOnly ? null : new Camera.CameraFx(S);
            Engine.Test = VoiceTest;
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); S.Save(); };
            eqTimer.Tick += (s, e) => { eqTimer.Stop(); OutputEqError = WriteOutputEq(false); };
            routeTimer.Tick += (s, e) =>
            {
                FollowMic(); FollowHeadphones(); RouteBypassApps(); UpdateMicListeners(); CheckFormats();
                if (formatsChanged) { formatsChanged = false; SaveSoon(); }
                // Remember the 3D buffer margin this PC needs, so the next start doesn't re-learn it with dropouts.
                // (The peak: during long calm play the margin shrinks again, and the next start should begin safe.)
                int learned = Game.Running ? Game.LearnedMargin : 0;
                if (learned > S.Game.LearnedMargin) { S.Game.LearnedMargin = learned; SaveSoon(); }
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
            if (!NoiseEngines.IsDfn(S.NsEngine)) { Engine.Suppressor.DfnWanted = true; return; }
            var cap = PairedCapture(ActiveOutId);
            micCapId = cap != null ? cap.Id : null;
            bool listening = cap != null && Interop.AudioSessions.AnyoneRecording(cap.Id);
            if (listening || Engine.Fx == null || Environment.TickCount - dfnPreviewTick < 20000) { idleTicks = 0; Engine.Suppressor.DfnWanted = true; }
            else if (++idleTicks >= 15) Engine.Suppressor.DfnWanted = false;   // 15 x 4 s
            WriteStatus(listening);
        }

        /// <summary>status.txt next to the settings: what the engines are doing right now (for troubleshooting).</summary>
        string lastStatus; int lastStatusTick;

        void WriteStatus(bool listening)
        {
            try
            {
                var d = Engine.Suppressor.DfnEngine;
                string body =
                    "mic: " + Engine.Status + "\r\n" +
                    "mic input: " + Engine.InputChannelCount + " ch" + (Engine.InputChannelCount > 1 ? " - " + Engine.Fx.Channels.Describe() : "") +
                        (DeviceErrors.MicBlocked() ? " - BLOCKED by Windows privacy settings" : "") + "\r\n" +
                    "mic output: " + (Engine.DirectOutput ? "direct to cable (feed hidden)" : Engine.PrivateOutput ? "exclusive" : "shared") +
                        (Engine.DirectError != null ? " - direct path failed: " + Engine.DirectError : "") + ", dropouts " + Engine.Dropouts + "\r\n" +
                    "mic feed endpoint state: " + (ActiveOutId != null ? AudioDevices.State(ActiveOutId).ToString() : "-") + "\r\n" +
                    "app recording the mic: " + listening + "\r\n" +
                    "noise engine: " + S.NsEngine + (d != null ? (d.Ready ? " loaded in " + d.LoadMs + " ms (" + (d.Gpu ? "GPU" : "CPU") + ", " + d.AvgMs.ToString("0.0") + " ms/frame, late " + d.Late + ")" : " loading") : " not loaded") + "\r\n" +
                    "3D audio: " + (Game.Running ? Game.Status + " - " + Game.Processor.Current + (Game.Processor.Rendering360 ? " (true 360)" : "") + ", dropouts " + Game.Dropouts + " -> " + GamePhonesName : "off") + (GameError != null ? " - " + GameError : "") + "\r\n" +
                    "3D tuning: " + GameTuning + "\r\n" +
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
            VoiceTest.Chain.Configure(S);   // the test clip always sounds like the live mic
            SaveSoon();
            if (ParamsChanged != null) ParamsChanged(this, EventArgs.Empty);
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
            string sink = GameSinkId ?? S.Game.SinkId;
            return Outputs().Where(d => d.Id == ActiveOutId || (AudioDevices.LooksVirtual(d) && d.Id != sink)).ToList();
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
            var outs = Outputs();
            return outs.FirstOrDefault(d => d.Name.StartsWith(MicFeedName, StringComparison.OrdinalIgnoreCase))
                ?? outs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? outs.FirstOrDefault(AudioDevices.LooksVirtual);
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

            if (!S.VirtualMicOn) { Engine.Stop(); RestoreDefaultMic(); ActiveMicId = ActiveOutId = null; return; }

            var mics = Mics();
            var outs = Outputs();
            var outDev = outs.FirstOrDefault(d => d.Id == S.VirtualOutId) ?? FindVirtualOutput();
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
            Engine.Start(ActiveMicId, ActiveOutId, monitor);
            if (S.MakeDefaultMic) ApplyDefaultMic(); else RestoreDefaultMic();
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
            if (!S.Game.Enabled || GameSinkId == null || GamePhonesId == null) return;
            try
            {
                var outs = Outputs();
                string cur = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
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
                if (S.Game.Enabled && Game.Running && GameSinkId != null && GamePhonesId != null)
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
            if (!S.Game.Enabled) { Game.Stop(); RestoreGameDefault(); return; }
            if (phones == null) { Game.Stop(); GameError = "No headphones found."; return; }
            if (sink == null)
            {
                // Test sound still works; games need the virtual device.
                GameError = "No virtual 7.1 device yet - install the free VB-Audio Hi-Fi Cable (button above). The test sound works already.";
                Game.LowLatency = S.Game.LowLatency;
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
            // Voice/communications apps stay on the real headphones.
            if (AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Communications) == sink.Id) AudioDevices.SetDefaultRole(phones.Id, ERole.Communications);
            // Remember them as your headphones - unless yours are only unplugged right now (this is a stand-in until they're back).
            int prefState = S.Game.HeadphonesId != null && S.Game.HeadphonesId != phones.Id ? AudioDevices.State(S.Game.HeadphonesId) : 0;
            if (prefState != 4 && prefState != 8) S.Game.HeadphonesId = phones.Id;
            // Gaming headsets whose driver shows up as 7.1 run their own virtual surround on what StreamLite sends.
            int pch, pmask, prate;
            if (AudioDevices.MixFormat(phones.Id, out pch, out pmask, out prate) && pch > 2)
                GameError = phones.Name + " is set up as " + (pch == 8 ? "7.1" : pch == 6 ? "5.1" : pch + " channels") + ". StreamLite sends it 3D stereo: if it's a gaming headset, turn off its own virtual surround in its app (G HUB, NGENUITY, Synapse, iCUE, SteelSeries GG...) so games aren't made 3D twice; a sound card can stay as it is.";
            SaveSoon();
            TidyDevices();
            Game.LowLatency = S.Game.LowLatency;
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
            }
            catch { }
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
