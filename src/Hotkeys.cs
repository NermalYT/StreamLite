using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>Something a hotkey can do. Arg choices (value, label) when it applies to one of several things.</summary>
    internal sealed class HotkeyAction
    {
        public string Id, Group, Label, Help;
        /// <summary>Push to talk / push to mute: acts while held (down and up), not as a toggle.</summary>
        public bool Hold;
        public Func<AppCore, List<KeyValuePair<string, string>>> Args;
        /// <summary>Runs it (UI thread). down = false only for Hold actions. Returns the on-screen note, or null.</summary>
        public Func<AppCore, string, bool, string> Run;
        public override string ToString() { return Group + ": " + Label; }
    }

    /// <summary>Everything a hotkey can do (Hotkeys page).</summary>
    internal static class HotkeyActions
    {
        public static readonly List<HotkeyAction> All = Build();
        public static HotkeyAction Find(string id) { return All.FirstOrDefault(a => a.Id == id); }

        static KeyValuePair<string, string> P(string v, string l) { return new KeyValuePair<string, string>(v, l); }
        static string OnOff(bool on) { return on ? "on" : "off"; }

        /// <summary>A mic setting switched on/off from a hotkey.</summary>
        static HotkeyAction Toggle(string id, string group, string label, string help, Func<AppSettings, bool> get, Action<AppSettings, bool> set, Action<AppCore> apply)
        {
            return new HotkeyAction
            {
                Id = id, Group = group, Label = label + " on / off", Help = help,
                Run = (core, arg, down) => { bool v = !get(core.S); set(core.S, v); apply(core); core.SaveSoon(); return label + " " + OnOff(v); }
            };
        }

        static List<KeyValuePair<string, string>> MicArgs(AppCore core)
        {
            var l = new List<KeyValuePair<string, string>> { P("all", "All microphones (the virtual mic)") };
            l.Add(P("1", "Microphone 1 (main)"));
            for (int i = 0; i < core.S.ExtraMics.Count; i++) l.Add(P((i + 2).ToString(), "Microphone " + (i + 2) + " - " + (core.S.ExtraMics[i].Name ?? "")));
            return l;
        }

        static List<KeyValuePair<string, string>> OutputArgs(AppCore core)
        {
            var l = new List<KeyValuePair<string, string>> { P("1", "Output 1 (your headphones)") };
            for (int i = 0; i < core.S.ExtraOutputs.Count; i++) l.Add(P((i + 2).ToString(), "Output " + (i + 2) + " - " + (core.S.ExtraOutputs[i].Name ?? "")));
            return l;
        }

        static string Cycle(IList<string> list, string current, string arg)
        {
            if (arg != "next" && list.Contains(arg)) return arg;
            int i = list.IndexOf(current);
            return list[(i + 1) % list.Count];
        }

        static List<HotkeyAction> Build()
        {
            const string Mic = "Microphone", Out = "Headphones & 3D", Cam = "Camera", App = "StreamLite";
            var l = new List<HotkeyAction>();
            l.Add(new HotkeyAction
            {
                Id = "mic.mute", Group = Mic, Label = "Mute / unmute", Args = MicArgs,
                Help = "Mutes the virtual mic (everyone), or just one microphone of your Microphones list while the others keep talking.",
                Run = (core, arg, down) =>
                {
                    if (arg == null || arg == "all") { core.Engine.Muted = !core.Engine.Muted; return core.Engine.Muted ? "Microphone muted" : "Microphone on"; }
                    int n; if (!int.TryParse(arg, out n)) return null;
                    bool m = core.ToggleMicMute(n);
                    return "Microphone " + n + (m ? " muted" : " on");
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "mic.ptt", Group = Mic, Label = "Push to talk (hold)", Hold = true,
                Help = "Your mic is silent unless you hold this key (or mouse button).",
                Run = (core, arg, down) => { core.Engine.PttClosed = !down; return null; }
            });
            l.Add(new HotkeyAction
            {
                Id = "mic.ptm", Group = Mic, Label = "Push to mute (hold)", Hold = true,
                Help = "Your mic is silent while you hold this key (cough button).",
                Run = (core, arg, down) => { core.Engine.PttClosed = down; return null; }
            });
            l.Add(Toggle("mic.virtual", Mic, "Virtual mic", "Turns the whole clean mic on or off.", s => s.VirtualMicOn, (s, v) => s.VirtualMicOn = v, c => c.RestartAudio()));
            l.Add(Toggle("mic.nr", Mic, "Noise removal", null, s => s.NoiseRemoval, (s, v) => s.NoiseRemoval = v, c => c.PushEngineParams()));
            l.Add(new HotkeyAction
            {
                Id = "mic.engine", Group = Mic, Label = "Noise remover",
                Args = core => new[] { P("next", "Next one") }.Concat(NoiseEngines.Available().Select(e => P(e, NoiseEngines.Label(e, false)))).ToList(),
                Run = (core, arg, down) =>
                {
                    var list = NoiseEngines.Available();
                    core.S.NsEngine = Cycle(list, core.S.NsEngine, arg);
                    core.S.NoiseRemoval = true;
                    core.PushEngineParams();
                    return "Noise removal: " + NoiseEngines.Label(core.S.NsEngine, false);
                }
            });
            l.Add(Toggle("mic.gate", Mic, "Noise gate", null, s => s.Gate, (s, v) => s.Gate = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.eq", Mic, "Mic equalizer", null, s => s.MicEq.Enabled, (s, v) => s.MicEq.Enabled = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.comp", Mic, "Voice compressor", null, s => s.Compressor, (s, v) => s.Compressor = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.deess", Mic, "De-esser", null, s => s.DeEsser, (s, v) => s.DeEsser = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.autolevel", Mic, "Auto level", null, s => s.AutoLevel, (s, v) => s.AutoLevel = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.automix", Mic, "Auto mix", "With several microphones.", s => s.MicAutoMix, (s, v) => s.MicAutoMix = v, c => c.PushEngineParams()));
            l.Add(Toggle("mic.monitor", Mic, "Hear myself", null, s => s.Monitor, (s, v) => s.Monitor = v, c => c.RestartAudio()));
            l.Add(new HotkeyAction
            {
                Id = "mic.style", Group = Mic, Label = "Voice style",
                Args = core => new[] { P("next", "Next style") }.Concat(VoiceStyles.All.Select(x => P(x.Name, x.Name))).ToList(),
                Run = (core, arg, down) =>
                {
                    var names = VoiceStyles.All.Select(x => x.Name).ToList();
                    string name = Cycle(names, VoiceStyles.Match(core.S), arg);
                    VoiceStyles.RememberMine(core.S);
                    VoiceStyles.Apply(core.S, name);
                    core.PushEngineParams();
                    return "Voice style: " + name;
                }
            });

            l.Add(Toggle("out.3d", Out, "Game 3D audio", null, s => s.Game.Enabled, (s, v) => s.Game.Enabled = v, c => c.RestartGameAudio()));
            l.Add(new HotkeyAction
            {
                Id = "out.preset", Group = Out, Label = "3D preset",
                Args = core => new[] { P("next", "Next preset") }.Concat(GamePresets.Names.Select(x => P(x, x))).ToList(),
                Run = (core, arg, down) =>
                {
                    string name = Cycle(GamePresets.Names, core.S.Game.Preset, arg);
                    GamePresets.Apply(core.S.Game, name);
                    core.S.Game.Preset = name;
                    core.PushGameParams();
                    return "3D preset: " + name;
                }
            });
            l.Add(Toggle("out.direct", Out, "Direct to headphones (for Discord screen shares)",
                "While on, a Discord whole-screen share sends each app's sound once - without it viewers also get StreamLite's 3D copy. Other apps can't use your headphones meanwhile. Sharing an app's window is always clean.",
                s => s.Game.Exclusive, (s, v) => { }, c => c.SetDirect(!c.S.Game.Exclusive)));
            l.Add(Toggle("out.natural", Out, "Natural sound for videos", null, s => s.Game.NaturalWhenNoGame, (s, v) => s.Game.NaturalWhenNoGame = v, c => c.PushGameParams()));
            l.Add(new HotkeyAction
            {
                Id = "out.test", Group = Out, Label = "Test footsteps",
                Run = (core, arg, down) =>
                {
                    var p = core.Game.Processor;
                    if (p.TestRunning) { p.StopTest(); core.PushGameParams(); return "Footsteps test stopped"; }
                    if (!core.Game.Running) { core.S.Game.Enabled = true; core.RestartGameAudio(); }
                    p.StartRingsTest(); core.PushGameParams();
                    return "Footsteps test: around you, above, below";
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "out.profile", Group = Out, Label = "Headphone profile",
                Args = core => new[] { P("next", "Next profile"), P("auto", GameProfiles.AutoEq) }.Concat(core.S.Profiles.Profiles.Select(x => P(x.Name, x.Name))).ToList(),
                Run = (core, arg, down) =>
                {
                    var ps = core.S.Profiles;
                    if (arg == "auto") { ps.AutoSwitch = true; ps.ManualProfile = null; }
                    else
                    {
                        string name = Cycle(ps.Profiles.Select(x => x.Name).ToList(), core.Profiles.ActiveName, arg);
                        ps.AutoSwitch = false; ps.ManualProfile = name;
                    }
                    core.SaveSoon(); core.Profiles.Reapply();
                    return "Headphones: " + (ps.AutoSwitch ? GameProfiles.AutoEq : ps.ManualProfile);
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "out.eq", Group = Out, Label = "Headphone EQ on / off",
                Run = (core, arg, down) =>
                {
                    var id = core.ProfileDeviceId(); if (id == null) return null;
                    var p = core.OutputProfile(id); p.Enabled = !p.Enabled; core.OutputEqChanged();
                    return "Headphone EQ " + OnOff(p.Enabled);
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "out.mute", Group = Out, Label = "Mute / unmute output", Args = OutputArgs,
                Help = "Windows' own mute of one of your outputs (Speakers & Headphones > Outputs).",
                Run = (core, arg, down) =>
                {
                    int n; if (!int.TryParse(arg ?? "1", out n)) n = 1;
                    string id = n == 1 ? (core.GamePhonesId ?? core.S.Game.HeadphonesId ?? core.RealDefaultOutput()) : n - 2 < core.S.ExtraOutputs.Count ? core.S.ExtraOutputs[n - 2].Id : null;
                    if (id == null) return null;
                    bool m = !(AudioDevices.GetMute(id) ?? false);
                    AudioDevices.SetMute(id, m);
                    return "Output " + n + (m ? " muted" : " on");
                }
            });

            l.Add(new HotkeyAction
            {
                Id = "out.16d", Group = Out, Label = "16D music on / off",
                Help = "Spotify and other music apps play through 3D audio, the song circling your head (needs 3D audio on).",
                Run = (core, arg, down) =>
                {
                    core.S.Game.Music16D = !core.S.Game.Music16D; core.SaveSoon(); core.RouteBypassApps(); core.PushGameParams();
                    return "16D music " + OnOff(core.S.Game.Music16D) + (core.S.Game.Music16D && !core.S.Game.Enabled ? " - turn on 3D audio to hear it" : "");
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "app.lockin", Group = Out, Label = "Lock in on / off",
                Help = "Turns every other app down (Discord, music, browser...) while the game in front stays at 100%. Press again: back to how they were.",
                Args = core => new List<KeyValuePair<string, string>> { P("50", "Other apps at 50%"), P("25", "Other apps at 25%"), P("75", "Other apps at 75%"), P("0", "Other apps muted") },
                Run = (core, arg, down) =>
                {
                    int pct; if (!int.TryParse(arg, out pct)) pct = 50;
                    return Interop.LockIn.Toggle(pct, core.Profiles != null ? core.Profiles.DetectedExe : null);
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "cam.bg", Group = Cam, Label = "Background",
                Args = core => new[] { P("next", "Next background") }.Concat(CamBackgrounds.Select(b => P(b.Key, b.Value))).ToList(),
                Run = (core, arg, down) =>
                {
                    var c = core.S.CamFx;
                    // Picture / video only once a file has been picked on the Camera page.
                    var usable = CamBackgrounds.Select(b => b.Key).Where(k => (k != "image" || !string.IsNullOrEmpty(c.BackgroundImage)) && (k != "video" || !string.IsNullOrEmpty(c.BackgroundVideo))).ToList();
                    c.Background = Cycle(usable, c.Background, arg);
                    core.SaveSoon();
                    return "Camera background: " + CamBackgrounds.First(b => b.Key == c.Background).Value;
                }
            });
            l.Add(Toggle("cam.track", Cam, "AI tracking", null, s => s.CamFx.FaceTracking, (s, v) => s.CamFx.FaceTracking = v, c => { }));
            l.Add(new HotkeyAction
            {
                Id = "cam.privacy", Group = Cam, Label = "Privacy (camera paused) on / off", Help = "Apps see a \"Camera paused\" picture instead of you.",
                Run = (core, arg, down) => { core.CamFx.Privacy = !core.CamFx.Privacy; return "Camera " + (core.CamFx.Privacy ? "paused (privacy)" : "back on"); }
            });
            l.Add(new HotkeyAction
            {
                Id = "cam.preset", Group = Cam, Label = "Camera preset",
                Args = core => Enumerable.Range(1, Camera.CameraDirector.MaxPresets).Select(i =>
                {
                    var p = i <= core.S.CamFx.Presets.Count ? core.S.CamFx.Presets[i - 1] : null;
                    return P(i.ToString(), "Preset " + i + (p != null && p.Used && !string.IsNullOrEmpty(p.Name) && p.Name != "Preset " + i ? " - " + p.Name : ""));
                }).ToList(),
                Run = (core, arg, down) =>
                {
                    int i; if (!int.TryParse(arg, out i)) i = 1;
                    var ps = core.S.CamFx.Presets;
                    if (i < 1 || i > ps.Count || !ps[i - 1].Used) return "Camera preset " + i + " isn't saved yet (Camera page)";
                    core.CamFx.Director.RecallPreset(i - 1);
                    return "Camera: " + (string.IsNullOrEmpty(ps[i - 1].Name) ? "preset " + i : ps[i - 1].Name);
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "cam.view", Group = Cam, Label = "Camera view",
                Args = core => new List<KeyValuePair<string, string>> { P("whiteboard", "Whiteboard on / off"), P("desk", "Desk view on / off"), P("group", "Group / single tracking"), P("reset", "Reset the view (whole picture)") },
                Run = (core, arg, down) =>
                {
                    var c = core.S.CamFx; var d = core.CamFx.Director;
                    switch (arg)
                    {
                        case "desk": d.SetViewMode("desk"); return "Camera: desk view " + OnOff(c.ViewMode != "desk");
                        case "group": c.TrackMode = c.TrackMode == "group" ? "single" : "group"; core.SaveSoon(); return "Camera: " + (c.TrackMode == "group" ? "group" : "single person") + " tracking";
                        case "reset": d.ResetView(); return "Camera: whole picture";
                        default: d.SetViewMode("whiteboard"); return "Camera: whiteboard " + OnOff(c.ViewMode != "whiteboard");
                    }
                }
            });
            l.Add(new HotkeyAction
            {
                Id = "cam.zoom", Group = Cam, Label = "Camera zoom",
                Args = core => new List<KeyValuePair<string, string>> { P("in", "Zoom in"), P("out", "Zoom out") },
                Run = (core, arg, down) => { core.CamFx.Director.GestureZoom(arg == "out" ? 1 / 1.25 : 1.25); return "Camera zoom " + (arg == "out" ? "out" : "in"); }
            });
            l.Add(Toggle("cam.light", Cam, "Auto light", null, s => s.CamFx.AutoLight, (s, v) => s.CamFx.AutoLight = v, c => { }));
            l.Add(Toggle("cam.mirror", Cam, "Mirror", null, s => s.CamFx.Mirror, (s, v) => s.CamFx.Mirror = v, c => { }));

            l.Add(new HotkeyAction { Id = "app.show", Group = App, Label = "Show / hide the window", Run = (core, arg, down) => { core.RequestToggleWindow(); return null; } });
            return l;
        }

        public static readonly KeyValuePair<string, string>[] CamBackgrounds =
        {
            P("off", "Off (your room)"), P("blur", "Blur"), P("green", "Green screen"), P("image", "Picture"), P("video", "Video"), P("dim", "Dim the room")
        };
    }

    /// <summary>
    /// Global hotkeys: low-level keyboard and mouse hooks on their own thread (so a busy window never delays your
    /// typing), matched against the bindings and run on the UI thread. Keys are never swallowed - games and apps still
    /// get them - except while the Hotkeys page is recording a new one.
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        readonly AppCore core;
        readonly SynchronizationContext ui;
        Thread thread;
        uint threadId;
        IntPtr kbHook, msHook;
        HookProc kbProc, msProc;   // kept alive while hooked
        readonly HashSet<int> down = new HashSet<int>();
        readonly HashSet<HotkeyBinding> held = new HashSet<HotkeyBinding>();
        volatile HotkeyBinding[] bindings = new HotkeyBinding[0];
        volatile Action<HotkeyBinding> capture;
        int captureMods; bool captureOther;
        /// <summary>A hotkey did something: the note to show (UI thread).</summary>
        public event Action<string> Fired;

        public HotkeyManager(AppCore core) : this(core, true) { }

        /// <summary>hook = false: tests feed keys through Press without touching the system.</summary>
        internal HotkeyManager(AppCore core, bool hook)
        {
            this.core = core;
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Reload();
            if (!hook) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "Hotkeys" };
            thread.Start();
        }

        /// <summary>Tests: a key (virtual-key code) or mouse button (3/4/5) going down or up.</summary>
        internal bool Press(int vk, int mouse, bool isDown) { return Input(vk, mouse, isDown); }

        /// <summary>Bindings changed (Hotkeys page): use them now. Push to talk closes the mic until held.</summary>
        public void Reload()
        {
            bindings = core.S.Hotkeys.Where(b => b.Key != 0 || b.Mouse != 0).ToArray();
            bool ptt = bindings.Any(b => b.Action == "mic.ptt");
            lock (held) if (!held.Any(h => h.Action == "mic.ptt" || h.Action == "mic.ptm")) core.Engine.PttClosed = ptt;
        }

        /// <summary>The next key / mouse button (with modifiers) goes to done instead of running anything; Esc cancels (null).</summary>
        public void Capture(Action<HotkeyBinding> done) { captureMods = 0; captureOther = false; capture = done; }
        public void CancelCapture() { capture = null; }

        public void Dispose()
        {
            if (threadId != 0) PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT
        }

        void Loop()
        {
            threadId = GetCurrentThreadId();
            kbProc = KeyboardHook; msProc = MouseHook;
            IntPtr mod = GetModuleHandle(null);
            kbHook = SetWindowsHookEx(13, kbProc, mod, 0);
            msHook = SetWindowsHookEx(14, msProc, mod, 0);
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
            if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) UnhookWindowsHookEx(msHook);
        }

        public const int ModCtrl = 1, ModAlt = 2, ModShift = 4, ModWin = 8;

        static int ModOf(int vk)
        {
            switch (vk)
            {
                case 0x10: case 0xA2: case 0xA3: case 0x11: return vk == 0x10 ? ModShift : ModCtrl;
                case 0xA0: case 0xA1: return ModShift;
                case 0x12: case 0xA4: case 0xA5: return ModAlt;
                case 0x5B: case 0x5C: return ModWin;
            }
            return 0;
        }

        int Mods(int except)
        {
            int m = 0;
            foreach (var k in down) if (k != except) m |= ModOf(k);
            return m;
        }

        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32(), vk = Marshal.ReadInt32(lParam);
                bool isDown = msg == 0x100 || msg == 0x104, isUp = msg == 0x101 || msg == 0x105;
                if (isDown && Input(vk, 0, true)) return (IntPtr)1;
                if (isUp && Input(vk, 0, false)) return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32(), button = 0; bool isDown = false;
                if (msg == 0x207 || msg == 0x208) { button = 3; isDown = msg == 0x207; }
                else if (msg == 0x20B || msg == 0x20C) { int x = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF; button = x == 1 ? 4 : x == 2 ? 5 : 0; isDown = msg == 0x20B; }
                if (button != 0 && Input(0, button, isDown)) return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        /// <summary>One key or mouse button went down/up. Returns true to swallow it (only while recording).</summary>
        bool Input(int vk, int mouse, bool isDown)
        {
            int id = vk != 0 ? vk : -mouse;
            bool repeat = isDown && !down.Add(id);
            if (!isDown) down.Remove(id);
            var cap = capture;
            if (cap != null)
            {
                bool modifier = vk != 0 && ModOf(vk) != 0;
                if (isDown && !repeat)
                {
                    if (modifier) captureMods |= ModOf(vk);
                    else
                    {
                        captureOther = true; capture = null;
                        var b = vk == 0x1B && Mods(vk) == 0 ? null : new HotkeyBinding { Key = vk, Mouse = mouse, Mods = Mods(id) };
                        ui.Post(_ => cap(b), null);
                    }
                    return true;
                }
                if (!isDown && modifier && !captureOther && vk != 0)
                {
                    // Only a modifier was pressed and released: that key alone (e.g. push to talk on Left Ctrl).
                    capture = null;
                    var b = new HotkeyBinding { Key = vk, Mods = 0 };
                    ui.Post(_ => cap(b), null);
                    return true;
                }
                return !isDown;
            }
            if (isDown)
            {
                if (repeat) return false;
                int mods = Mods(id);
                foreach (var b in bindings)
                {
                    if (b.Key != vk || b.Mouse != mouse) continue;
                    var a = HotkeyActions.Find(b.Action); if (a == null) continue;
                    // Hold actions work with other keys down too (you're running and talking at once).
                    if (a.Hold ? (b.Mods & ~mods) != 0 : b.Mods != mods) continue;
                    if (a.Hold) lock (held) held.Add(b);
                    Run(a, b, true);
                }
            }
            else
            {
                List<HotkeyBinding> released;
                lock (held) { released = held.Where(b => b.Key == vk && b.Mouse == mouse).ToList(); foreach (var b in released) held.Remove(b); }
                foreach (var b in released) Run(HotkeyActions.Find(b.Action), b, false);
            }
            return false;
        }

        void Run(HotkeyAction a, HotkeyBinding b, bool isDown)
        {
            if (a == null) return;
            // Push to talk changes the mic right here (no wait for the window's thread).
            if (a.Hold) { a.Run(core, b.Arg, isDown); return; }
            ui.Post(_ =>
            {
                string note = null;
                try { note = a.Run(core, b.Arg, isDown); } catch (Exception ex) { note = a.Label + ": " + ex.Message; }
                if (note != null && Fired != null) Fired(note);
            }, null);
        }

        /// <summary>"Ctrl + Shift + M", "Mouse back button".</summary>
        public static string Describe(HotkeyBinding b)
        {
            if (b == null || (b.Key == 0 && b.Mouse == 0)) return "Set key...";
            var parts = new List<string>();
            if ((b.Mods & ModCtrl) != 0) parts.Add("Ctrl");
            if ((b.Mods & ModAlt) != 0) parts.Add("Alt");
            if ((b.Mods & ModShift) != 0) parts.Add("Shift");
            if ((b.Mods & ModWin) != 0) parts.Add("Win");
            if (b.Mouse != 0) parts.Add(b.Mouse == 3 ? "Middle mouse" : b.Mouse == 4 ? "Mouse back" : "Mouse forward");
            else parts.Add(KeyName(b.Key));
            return string.Join(" + ", parts);
        }

        static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0xA0: return "Left Shift"; case 0xA1: return "Right Shift";
                case 0xA2: return "Left Ctrl"; case 0xA3: return "Right Ctrl";
                case 0xA4: return "Left Alt"; case 0xA5: return "Right Alt";
                case 0x5B: case 0x5C: return "Win";
                case 0x14: return "Caps Lock"; case 0x20: return "Space"; case 0x0D: return "Enter"; case 0x09: return "Tab";
                case 0xC0: return "`"; case 0xBD: return "-"; case 0xBB: return "="; case 0xDB: return "["; case 0xDD: return "]";
                case 0xDC: return "\\"; case 0xBA: return ";"; case 0xDE: return "'"; case 0xBC: return ","; case 0xBE: return "."; case 0xBF: return "/";
                case 0x13: return "Pause"; case 0x91: return "Scroll Lock"; case 0x2D: return "Insert"; case 0x2E: return "Delete";
                case 0x24: return "Home"; case 0x23: return "End"; case 0x21: return "Page Up"; case 0x22: return "Page Down";
                case 0xAD: return "Mute key"; case 0xAE: return "Volume down key"; case 0xAF: return "Volume up key";
                case 0xB3: return "Play/Pause key"; case 0xB0: return "Next track key"; case 0xB1: return "Previous track key";
            }
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            var k = (Keys)vk;
            string s = k.ToString();
            if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s.Substring(1);
            return s;
        }

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }
}
