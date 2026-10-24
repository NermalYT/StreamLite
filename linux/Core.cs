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
        readonly Timer watch;
        readonly object gate = new object();
        readonly Dictionary<string, int> lastHeard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string appliedKey;

        public Core()
        {
            GameProfiles.Merge(S.Profiles, null);
            watch = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Checks that the audio system has what StreamLite needs. null = fine.</summary>
        public static string CheckSystem()
        {
            if (!PulseStream.Available) return "libpulse isn't installed. Install it (Debian/Ubuntu: libpulse0, Fedora: pulseaudio-libs, Arch: libpulse) and start StreamLite again.";
            if (!Pactl.Available) return "pactl isn't available or no sound server is running. Install pulseaudio-utils (with PipeWire also pipewire-pulse) and start StreamLite again.";
            return null;
        }

        public string RealOutput { get { return LinuxAudio.PickOutput(S.OutputDeviceId); } }

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
                cfg.FootstepBoost = 0; cfg.CueSharpen = 0; cfg.LoudTamer = 0;
                foreach (var d in SpatialProcessor.Dirs) { cfg.DirEq[d].Enabled = false; cfg.DirGainDb[d] = 0; }
            }
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
                    target = GameProfiles.Pick(S.Profiles.Profiles, x => running.Contains(x), hit =>
                    {
                        int now = Environment.TickCount;
                        if (streams().Any(si => !si.Corked && Bare(si.Binary ?? "").Equals(hit, StringComparison.OrdinalIgnoreCase))) lastHeard[hit] = now;
                        int t; return lastHeard.TryGetValue(hit, out t) && now - t <= 15000;
                    }, out exe);
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
            var eq = p.Eq.Clone();
            eq.Extra.Clear();
            var corr = S.Profiles.ActiveCorrection(RealOutput);   // these headphones' own correction
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

        public void Dispose()
        {
            watch.Dispose();
            Mic.Stop(); Eq.Stop(); Game.Stop();
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
