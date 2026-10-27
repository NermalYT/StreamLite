using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>Owns the settings, the engines and StreamLite's virtual devices on Linux. The window talks to this.</summary>
    internal sealed class Core : IDisposable
    {
        public readonly AppSettings S = AppSettings.Load();
        public readonly AudioEngine Mic = new AudioEngine();
        public readonly EqPath Eq = new EqPath();
        public readonly GamePath Game = new GamePath();
        public string ActiveProfile = GameProfiles.Movies, DetectedExe;
        public string Error;
        public event Action Changed;
        readonly Timer watch, follow;
        readonly object gate = new object();
        /// <summary>StreamLite switched devices by itself (what it now uses), for a notification.</summary>
        public event Action<string> Followed;
        readonly Dictionary<string, int> lastHeard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string appliedKey;

        public Core()
        {
            if (HardwareTuning.Apply(S)) S.Save();   // AI settings that fit this PC, once
            // 3.2: realtime noise removal sounded clearly worse on real voices (DNSMOS 2.3 vs 3.0 with DeepFilterNet):
            // back to the noise remover's own path once. LastVersion remembers that (and which version ran last).
            Version last;
            if (S.NsRealtime && (S.LastVersion == null || !Version.TryParse(S.LastVersion, out last) || last < new Version(3, 2, 0))) S.NsRealtime = false;
            if (S.LastVersion != Program.Version) { S.LastVersion = Program.Version; S.Save(); }
            // Voice isolation is built in: the best noise remover this CPU can run (Linux has no graphics-card AI).
            if (AiTiers.Ensure(S, Environment.ProcessorCount, 0, DeepFilter.Installed)) S.Save();
            GameProfiles.Merge(S.Profiles, null);
            watch = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            follow = new Timer(_ => FollowDevices(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Checks that the audio system has what StreamLite needs. null = fine.</summary>
        public static string CheckSystem()
        {
            if (!PulseStream.Available) return "libpulse isn't installed. Install it (Debian/Ubuntu: libpulse0, Fedora: pulseaudio-libs, Arch: libpulse) and start StreamLite again.";
            if (!Pactl.Available) return "pactl isn't available or no sound server is running. Install pulseaudio-utils (with PipeWire also pipewire-pulse) and start StreamLite again.";
            return null;
        }

        public string RealOutput { get { return LinuxAudio.PickOutput(S.OutputDeviceId); } }

        /// <summary>In a call (an app records StreamLite Mic) or playing (game sound in 3D): not a good moment to restart.</summary>
        public bool Busy
        {
            get
            {
                try { if (S.VirtualMicOn && Pactl.Recording(LinuxAudio.MicSource) > 0) return true; } catch { }
                return S.Game.Enabled && Game.Running && Game.LevelDb > -70f;
            }
        }

        public void Start()
        {
            Error = CheckSystem();
            if (Error != null) return;
            lock (gate)
            {
                try { LinuxAudio.CleanStale(); } catch { }
                // Remember the user's own default output before StreamLite points it at its devices.
                string def = Pactl.DefaultSink();
                if (def != null && !LinuxAudio.IsOurs(def)) { S.PreviousDefaultSink = def; if (S.OutputDeviceId == null) S.OutputDeviceId = def; }
                ApplyMic(); ApplyOutputs();
            }
            watch.Change(500, 3000);
            follow.Change(1000, 2000);
            // Device added or removed, default changed: check right away (the timer above is only the fallback).
            try
            {
                events = Pactl.Subscribe(line =>
                {
                    // The output StreamLite plays to is gone: stop playing at once, before the server "rescues" the
                    // stream onto StreamLite's own device (a feedback loop). Picking the next output follows.
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"'remove' on sink #(\d+)");
                    if (trace) Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " event: " + line + " (output #" + outIndex + ")");
                    if (m.Success && int.Parse(m.Groups[1].Value) == outIndex)
                    {
                        Eq.Hold = true; Game.Hold = true;    // silence within one block, before any loop can form
                        if (trace) Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " held");
                    }
                    if (m.Success && int.Parse(m.Groups[1].Value) == outIndex)
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (disposed) return;
                            // Both at once: the 3D stream is the one that would loop through StreamLite's game device.
                            var t1 = new Thread(() => Game.Stop()); t1.Start();
                            Eq.Stop(); t1.Join();
                            if (trace) Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " stopped");
                            follow.Change(0, 2000);
                        });
                    else if (line.Contains("on sink #") || line.Contains("on source #") || line.Contains("on server") || line.Contains("on card #"))
                        follow.Change(60, 2000);
                });
            }
            catch { }
        }

        System.Diagnostics.Process events;
        volatile int outIndex = -1;   // the real output's index, to spot its removal in the event stream
        static readonly bool trace = Environment.GetEnvironmentVariable("STREAMLITE_TRACE") == "1";

        int following;

        /// <summary>
        /// Headsets and mics come and go. When the output StreamLite plays to vanishes, the sound server "rescues" its
        /// stream onto the default output - StreamLite's own device, a feedback loop - so the paths move to a real
        /// output right away. When a real output becomes the default (picked in the sound settings), StreamLite plays
        /// there. When the mic is unplugged, the clean mic uses another one, and goes back when yours returns.
        /// </summary>
        void FollowDevices()
        {
            if (disposed || Error != null || Interlocked.Exchange(ref following, 1) == 1) return;
            try
            {
                if (S.HeadphoneEq || S.Game.Enabled)
                {
                    var all = Pactl.AllSinks();
                    var ours = new HashSet<string>(all.Where(d => LinuxAudio.IsOurs(d.Name) && d.Name != LinuxAudio.MicFeed).Select(d => d.Index.ToString()));
                    bool looped = Pactl.SinkInputs().Any(i => i.AppName == "StreamLite" && i.Sink != null && ours.Contains(i.Sink));
                    string def = Pactl.DefaultSink();
                    bool picked = def != null && !LinuxAudio.IsOurs(def) && def != S.OutputDeviceId && all.Any(d => d.Name == def);
                    bool gone = (Eq.Output != null && !all.Any(d => d.Name == Eq.Output))
                             || (Game.Output != null && !all.Any(d => d.Name == Game.Output))
                             || (S.HeadphoneEq && !Eq.Running) || (S.Game.Enabled && !Game.Running);
                    // Your own headphones are back (after a fallback while they were unplugged).
                    bool back = S.OutputDeviceId != null && all.Any(d => d.Name == S.OutputDeviceId)
                             && ((S.HeadphoneEq && Eq.Output != S.OutputDeviceId) || (S.Game.Enabled && Game.Output != S.OutputDeviceId));
                    if (picked || gone || looped || back)
                    {
                        if (picked) S.OutputDeviceId = def;
                        lock (gate) { Eq.Stop(); Game.Stop(); }
                        ApplyOutputs();
                        var now = all.FirstOrDefault(d => d.Name == RealOutput);
                        if (now != null && Followed != null) Followed("Sound now plays on " + now.Description);
                    }
                }
                if (S.VirtualMicOn && Mic.Running)
                {
                    string want = LinuxAudio.PickInput(S.MicId);
                    if (want != null && want != Mic.Source)
                    {
                        lock (gate) { Mic.Stop(); Mic.Start(want); }
                        var d = Pactl.Sources().FirstOrDefault(x => x.Name == want);
                        if (Followed != null) Followed("Clean mic now uses " + (d != null ? d.Description : want));
                        if (Changed != null) Changed();
                    }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref following, 0); }
        }

        public void ApplyMic()
        {
            lock (gate)
            {
                Mic.Chain.Configure(S);
                if (!S.VirtualMicOn) { Mic.Stop(); LinuxAudio.Remove("mic"); if (Changed != null) Changed(); return; }
                try { LinuxAudio.CreateMic(); } catch (Exception ex) { Mic.Status = "Couldn't create StreamLite Mic: " + ex.Message; return; }
                string mic = LinuxAudio.PickInput(S.MicId);
                if (S.MicId == null) S.MicId = mic;
                if (!Mic.Running) Mic.Start(mic);
                if (S.MakeDefaultMic) try { Pactl.SetDefaultSource(LinuxAudio.MicSource); } catch { }
            }
            if (Changed != null) Changed();
        }

        /// <summary>Restarts the mic with a different input.</summary>
        public void SetMic(string source) { S.MicId = source; Mic.Stop(); ApplyMic(); S.Save(); }

        /// <summary>Headphone EQ path and 3D game path, and where the default output points.</summary>
        public void ApplyOutputs()
        {
            if (disposed) return;
            lock (gate)
            {
                string phones = RealOutput;
                try
                {
                    if (S.HeadphoneEq) { LinuxAudio.CreateHeadphones(); if (!Eq.Running || Eq.Output != phones) Eq.Start(LinuxAudio.Headphones, phones); }
                    else { Eq.Stop(); }
                    if (S.Game.Enabled) { LinuxAudio.CreateGame(); if (!Game.Running || Game.Output != phones) Game.Start(LinuxAudio.Game, phones); }
                    else { Game.Stop(); }
                    PushGameParams();
                    var o = Pactl.AllSinks().FirstOrDefault(d => d.Name == phones);
                    outIndex = o != null ? o.Index : -1;
                    // Default output: games device while 3D is on, else the EQ device, else your headphones.
                    string target = S.Game.Enabled ? LinuxAudio.Game : S.HeadphoneEq ? LinuxAudio.Headphones : phones;
                    if (target != null && Pactl.DefaultSink() != target) Pactl.SetDefaultSink(target);
                    if (!S.HeadphoneEq) LinuxAudio.Remove("hp");
                    if (!S.Game.Enabled) LinuxAudio.Remove("game");
                    appliedKey = null;
                }
                catch (Exception ex) { Error = ex.Message; }
            }
            S.Save();
            if (Changed != null) Changed();
        }

        public void SetOutput(string sink) { S.OutputDeviceId = sink; ApplyOutputs(); }

        public void PushGameParams()
        {
            var cfg = S.Game.ToConfig();
            var p = S.Profiles.Profiles.FirstOrDefault(x => x.Name == ActiveProfile);
            if (S.Game.NaturalWhenNoGame && p != null && p.Cinematic && !Game.Processor.TestRunning)
            {
                cfg.FootstepBoost = 0; cfg.CueSharpen = 0; cfg.LoudTamer = 0; cfg.Demuffle = 0;
                foreach (var d in SpatialProcessor.Dirs) { cfg.DirEq[d].Enabled = false; cfg.DirGainDb[d] = 0; }
            }
            else GameProfiles.Blend3D(cfg, p);   // 3D boosts and the profile EQ don't stack
            Game.Processor.Configure(cfg);
        }

        // ---------------------------------------------------------------- profiles (game / music / chat)

        /// <summary>Running programs: process names, and Windows games under Wine / Proton by their .exe name.</summary>
        static HashSet<string> RunningPrograms()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                string pid = Path.GetFileName(dir);
                if (pid.Length == 0 || !char.IsDigit(pid[0])) continue;
                try
                {
                    string comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    if (comm.Length > 0) set.Add(Bare(comm));
                    string cmd = File.ReadAllText(Path.Combine(dir, "cmdline"));
                    int z = cmd.IndexOf('\0'); if (z >= 0) cmd = cmd.Substring(0, z);
                    int slash = Math.Max(cmd.LastIndexOf('/'), cmd.LastIndexOf('\\'));
                    string exe = slash >= 0 ? cmd.Substring(slash + 1) : cmd;
                    if (exe.Length > 0) set.Add(Bare(exe));
                }
                catch { }
            }
            return set;
        }

        static string Bare(string exe) { return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe; }

        void Tick()
        {
            try
            {
                List<Pactl.SinkInput> inputs = null;
                Func<List<Pactl.SinkInput>> streams = () => inputs ?? (inputs = Pactl.SinkInputs());
                GameProfile target = null; string exe = null;
                if (S.Profiles.AutoSwitch)
                {
                    var running = RunningPrograms();
                    var d = GameProfiles.Decide(S.Profiles, x => running.Contains(x), hit =>
                    {
                        int now = Environment.TickCount;
                        if (streams().Any(si => !si.Corked && Bare(si.Binary ?? "").Equals(hit, StringComparison.OrdinalIgnoreCase))) lastHeard[hit] = now;
                        int t; return lastHeard.TryGetValue(hit, out t) && now - t <= 15000;
                    });
                    target = d.Profile; exe = d.Exe;
                }
                else target = S.Profiles.Profiles.FirstOrDefault(p => p.Name == S.Profiles.ManualProfile);
                if (target == null) target = S.Profiles.Profiles.FirstOrDefault(p => p.Name == S.Profiles.DefaultProfile) ?? S.Profiles.Profiles.FirstOrDefault(p => p.Name == GameProfiles.Movies);
                if (target == null) return;
                DetectedExe = exe;
                var corr = S.Profiles.ActiveCorrection(RealOutput);
                string key = target.Name + "|" + (corr != null ? corr.Name : "") + "|" + RealOutput;
                if (key != appliedKey) { appliedKey = key; ApplyProfile(target); }

                // While 3D is on: chat, music, browsers and games with their own HRTF go straight to the EQ path.
                if (S.Game.Enabled && Game.Running)
                {
                    string straight = S.HeadphoneEq ? LinuxAudio.Headphones : RealOutput;
                    var apps = (S.Game.BypassApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
                    apps.AddRange(GameProfiles.ChatExes); apps.Add("spotify");
                    if (target.ThreeD == "game" && target.Exes.Count > 0 && !target.ChatApp && !target.WhilePlaying) apps.AddRange(target.Exes);
                    if (straight != null) LinuxAudio.MoveApps(apps, straight);
                }
            }
            catch { }
        }

        void ApplyProfile(GameProfile p)
        {
            ActiveProfile = p.Name;
            var corr = S.Profiles.ActiveCorrection(RealOutput);   // these headphones' own correction
            GameProfiles.MatchLoudness(S.Profiles, corr);          // every profile equally loud on these headphones
            var eq = p.Eq.Clone();
            eq.Extra.Clear();
            if (corr != null)
            {
                foreach (var f in corr.Filters) eq.Extra.Add(f.Copy());
                eq.ExtraName = corr.Name;
            }
            eq.AutoPreamp = true;
            Eq.SetEq(eq); Game.SetEq(eq);
            PushGameParams();
            if (Changed != null) Changed();
        }

        public void Reapply() { appliedKey = null; Tick(); }

        volatile bool disposed;

        public void Dispose()
        {
            disposed = true;
            try { if (events != null && !events.HasExited) events.Kill(); } catch { }
            watch.Dispose(); follow.Dispose();
            // Let a device check that's already running finish first (it could re-create a device after we remove it).
            for (int i = 0; i < 50 && Volatile.Read(ref following) == 1; i++) Thread.Sleep(50);
            lock (gate) { Mic.Stop(); Eq.Stop(); Game.Stop(); }
            try
            {
                // Put the default output back on your headphones before the virtual devices disappear.
                string back = LinuxAudio.PickOutput(S.PreviousDefaultSink ?? S.OutputDeviceId);
                if (back != null) Pactl.SetDefaultSink(back);
                LinuxAudio.RemoveAll();
            }
            catch { }
            S.Save();
        }
    }
}
