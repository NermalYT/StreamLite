using System;
using System.Collections.Generic;
using System.IO;
using StreamLite.Audio;
#if !LINUX
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
#endif

namespace StreamLite
{
    public class CameraPropSetting { public int Value; public bool Auto; }

    /// <summary>"StreamLite Camera": the AI virtual camera (face tracking, background, image quality).</summary>
    public class CamFxSettings
    {
        public bool Enabled = true;               // the virtual camera exists while StreamLite runs
        public string Source;                     // physical camera (device path); null = the Camera page's camera
        public bool Capture4K = true;             // use the camera's 4K mode when it has one: lossless zoom, cleaner image
        public bool FaceTracking;                 // auto-frame: follows your face
        public int Zoom = 140;                    // 100-250 %: how close face tracking frames you
        public int TrackSpeed = 40;               // 0-100: how quickly the framing follows
        public string Background = "off";         // off, blur, image, video, green, dim
        public int BlurAmount = 100;              // 0-100
        public string BackgroundImage, BackgroundVideo;
        public int EdgeSharpness = 30;            // 0-100: softer or tighter cut-out edges
        public bool AutoLight = true;             // auto exposure / low-light lift (tone curve)
        public int Brightness = 0, Contrast = 0;  // -50..50
        public int Denoise = 40, Sharpen = 30, SkinSmoothing = 0;   // 0-100
        public int Saturation = 105, Warmth = 0;  // 0-200 %, -50..50
        public bool Mirror;
        public bool Gpu = true;                   // DirectML on the graphics card (CPU fallback is slow at 1080p)

        public void Repair()
        {
            Zoom = Math.Max(100, Math.Min(250, Zoom)); TrackSpeed = Math.Max(0, Math.Min(100, TrackSpeed));
            if (Array.IndexOf(new[] { "off", "blur", "image", "video", "green", "dim" }, Background) < 0) Background = "off";
            BlurAmount = Math.Max(0, Math.Min(100, BlurAmount)); EdgeSharpness = Math.Max(0, Math.Min(100, EdgeSharpness));
            Brightness = Math.Max(-50, Math.Min(50, Brightness)); Contrast = Math.Max(-50, Math.Min(50, Contrast));
            Denoise = Math.Max(0, Math.Min(100, Denoise)); Sharpen = Math.Max(0, Math.Min(100, Sharpen)); SkinSmoothing = Math.Max(0, Math.Min(100, SkinSmoothing));
            Saturation = Math.Max(0, Math.Min(200, Saturation)); Warmth = Math.Max(-50, Math.Min(50, Warmth));
        }
    }

    /// <summary>A microphone mixed into the virtual mic besides the main one (name kept for when it's unplugged).</summary>
    public class ExtraMicSetting
    {
        public string Id, Name;
        public int GainDb;
        public EqProfile Eq = new EqProfile();   // this mic's own EQ, before mixing
    }

    /// <summary>Calibration: how much of the person on one mic another mic hears (power ratio; 0.06 = 12 dB quieter).</summary>
    public class MicBleedEntry
    {
        public string From, To;
        public double Ratio;
    }

    /// <summary>A hotkey (Hotkeys page): a key or mouse button with modifiers, and what it does.</summary>
    public class HotkeyBinding
    {
        public string Action, Arg;
        public int Key;            // virtual-key code (0 = a mouse button instead)
        public int Mods;           // 1 Ctrl, 2 Alt, 4 Shift, 8 Win
        public int Mouse;          // 3 middle, 4 back (XButton1), 5 forward (XButton2)
    }

    /// <summary>An output that plays everything too, besides the main headphones (Speakers &amp; Headphones > Outputs).</summary>
    public class ExtraOutputSetting
    {
        public string Id, Name;
        public int GainDb;
        public EqProfile Eq = new EqProfile();   // applied by StreamLite on this output's stream
    }

    public class CameraProfile
    {
        public bool Lock;
        public string PreviewMode;
        public Dictionary<string, CameraPropSetting> Values = new Dictionary<string, CameraPropSetting>();
        public Dictionary<string, CameraPropSetting> SavedLook = new Dictionary<string, CameraPropSetting>();
    }

    /// <summary>Game 3D audio (virtual surround for headphones).</summary>
    public class GameAudioSettings
    {
        public bool Enabled;
        public string SinkId, HeadphonesId, PreviousDefault;
        public string Hrtf = HrtfSet.Ku100;
        public string CustomHrir;
        public int Room = 20;                     // % of natural studio room
        public int FrontAngle = 30, SideAngle = 90, RearAngle = 145, Elevation = 0;   // old speaker layout (3D is always true 360 now; kept so old files load)
        public Dictionary<string, EqProfile> DirEq = new Dictionary<string, EqProfile>();
        public Dictionary<string, double> DirGain = new Dictionary<string, double>();
        public bool LinkSides = true;
        public int FootstepBoost = 50, LoudTamer = 40, DynSpeed = 50;
        public int CueSharpen = 30;
        public int Width = 100;
        public bool VirtualizeStereo;
        public bool Render360 = true;             // old switches: 3D is always true 360° now (kept so old files load)
        public int Focus360 = 100;
        public int MasterDb = 0;
        public bool Limiter = true;
        public bool LowLatency;                   // old switch: the 3D path always runs at the lowest delay this PC keeps up with
        public int LearnedMargin;                 // 3D buffer safety margin learned on this PC (frames), kept across restarts
        public bool BypassEnabled = true;
        public string BypassApps = "spotify, discord, discordptb, chrome, msedge, firefox, vivaldi";
        public string Preset = "Footsteps (competitive)";
        public bool NaturalWhenNoGame = true;     // movies/videos/story games: no footstep tricks, just clean 3D
        public Dictionary<string, string> PersonalHeads = new Dictionary<string, string>();   // headphones id -> "ku100|35" from the listening test

        public void Repair()
        {
            if (DirEq == null) DirEq = new Dictionary<string, EqProfile>();
            if (DirGain == null) DirGain = new Dictionary<string, double>();
            if (PersonalHeads == null) PersonalHeads = new Dictionary<string, string>();
            foreach (var d in SpatialProcessor.Dirs)
            {
                EqProfile p;
                if (!DirEq.TryGetValue(d, out p) || p == null) { p = new EqProfile { AutoPreamp = false }; DirEq[d] = p; }
                p.Repair();
                p.AutoPreamp = false;
                if (!DirGain.ContainsKey(d)) DirGain[d] = 0;
            }
            if (Hrtf != HrtfSet.Kemar && Hrtf != HrtfSet.Custom) Hrtf = HrtfSet.Ku100;
            if (Hrtf == HrtfSet.Custom && (string.IsNullOrEmpty(CustomHrir) || !File.Exists(CustomHrir))) Hrtf = HrtfSet.Ku100;
        }

        internal SpatialConfig ToConfig()
        {
            var c = new SpatialConfig
            {
                HrtfKind = Hrtf, CustomPath = CustomHrir, Room = Room / 100.0,
                // Games pan by the standard 7.1 layout and true 360 undoes exactly that panning, so the angles are fixed.
                FrontAngle = 30, SideAngle = 90, RearAngle = 145, Elevation = 0,
                FootstepBoost = FootstepBoost / 100.0, LoudTamer = LoudTamer / 100.0, DynSpeed = DynSpeed / 100.0,
                CueSharpen = CueSharpen / 100.0, Width = Width / 100.0, VirtualizeStereo = VirtualizeStereo,
                Render360 = true, Focus360 = 1.0,
                MasterDb = MasterDb, Limiter = Limiter
            };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = DirEq[d].Clone(); c.DirGainDb[d] = DirGain[d]; }
            return c;
        }
    }

    public class AppSettings
    {
        // Microphone
        public string MicId;
        public string MicChannel = "auto";       // which input(s) of a multi-channel mic: auto, mix, 1, 2, ...
        public List<ExtraMicSetting> ExtraMics = new List<ExtraMicSetting>();   // more microphones mixed into the virtual mic
        public bool MicAutoMix = true;           // each mic turned up while its person talks (no double voice from the other mic)
        public int MainMicGainDb;                // trim of the main mic against the extra ones
        public EqProfile MainMicEq = new EqProfile();   // microphone 1's own EQ, before mixing (MicEq = everyone's, after)
        public List<ExtraOutputSetting> ExtraOutputs = new List<ExtraOutputSetting>();   // more outputs playing everything
        public Dictionary<string, int> OutputDelayMs = new Dictionary<string, int>();   // per output device: held back to play in sync (several outputs)
        public List<MicBleedEntry> MicBleed = new List<MicBleedEntry>();   // measured by the multi-mic calibration (smart auto mix)
        public string MicCalibrated;             // when (for the Microphones list)
        public List<HotkeyBinding> Hotkeys = new List<HotkeyBinding>();
        public bool HotkeyToasts = true;         // a small note on screen when a hotkey does something
        public string LastVersion;               // the version that last ran (after an update: a quick setup check)
        public int MicLearnedMargin;             // the low-latency mic path's buffer floor learned on this PC (frames), for MicLearnedFor
        public string MicLearnedFor;             // the mic it was learned on
        public string VirtualOutId;
        public bool VirtualMicOn = true;
        public bool NoiseRemoval = true;
        public int Strength = 70;
        public int AdaptSpeed = 50;
        public int HighPassHz = 80;
        public bool Gate = false;
        public int GateThreshold = -50;
        public int GateAttack = 2;
        public int GateHold = 200;
        public int GateRelease = 150;
        public int GateRange = -80;
        public int VolumeDb = 0;
        public bool Limiter = true;
        public bool AutoLevel = true;
        public int AutoLevelTarget = -20;
        public int AutoLevelMaxBoost = 20;
        // Noise removal fine-tuning (v2 engine)
        public int NsSmoothing = 50;
        public int NsVoiceProtect = 60;
        public int HighCutHz = 0;            // 0 = off
        public int DeHumHz = 0;              // 0 = off, 50, 60
        // Gate / expander
        public bool GateExpander = true;
        public int ExpanderRatio = 25;       // x10 -> 2.5:1
        // Voice tools
        public bool Compressor = true;
        public int CompThreshold = -26;
        public int CompRatio = 25;           // x10 -> 2.5:1
        public int CompAttack = 8;
        public int CompRelease = 150;
        public int CompMakeup = 0;
        public bool DeEsser;
        public int DeEssFreq = 6500;
        public int DeEssAmount = 6;
        public string NsEngine = "ai";        // ai = RNNoise neural voice isolation, classic = statistical suppressor
        public int NsEngineVersion;          // bumps when the noise engine changes, to reset old tuning once
        public bool LowVram;                 // Low VRAM: small half-precision AI models on the graphics card (all AI < 250 MB)
        public int AutoTuned;                // HardwareTuning.Version this install was last tuned for (AI settings picked by hardware)
        public bool LowLatencyMic = true;    // old switch (2.4.0): the mic is now always exclusive at its smallest period, shared by itself when needed
        public bool MakeDefaultMic;
        public string PreviousDefaultMic;
        public bool Monitor;
        public string MonitorId;
        public EqProfile MicEq = new EqProfile();
        public Dictionary<string, object> MyVoice;   // your own tuning, saved when you first try a voice style
        public EqProfile MyVoiceEq;
        public GameAudioSettings Game = new GameAudioSettings();
        public ProfileSettings Profiles = new ProfileSettings();

        // Speakers / headphones
        public string OutputDeviceId;
        public Dictionary<string, EqProfile> OutputEq = new Dictionary<string, EqProfile>();

        // Camera
        public string LastCamera;
        public Dictionary<string, CameraProfile> Cameras = new Dictionary<string, CameraProfile>();
        public CamFxSettings CamFx = new CamFxSettings();

        // App
        public bool StartMinimized = true;
        public bool CloseToTray = true;          // legacy, replaced by CloseAction
        public string CloseAction = "ask";       // ask | background | exit
        public bool RunAsAdmin;
        public bool UpdateOnStartup = true;      // check GitHub when StreamLite starts and install a newer version
        public bool TrayHintShown;
        public int[] WindowBounds;               // x, y, width, height of the main window
        public bool HideMicFeed = true;          // feed the mic cable directly (kernel streaming) so "StreamLite - Mic Feed" isn't an output anywhere
        public bool HideVirtualDevices = true;   // disable the unused virtual cable endpoints so device lists stay short
        public bool TidyPending;                 // changed without admin rights: apply at the next admin start
        public List<string> RoutedGames = new List<string>();   // games already sent straight to the headphones once
        public bool AutoFormats = true;          // give real devices their best working format (48 kHz, highest native bit depth) once
        public List<string> FormatsDone = new List<string>();   // device IDs already given it (later manual changes are respected)
#if LINUX
        public bool HeadphoneEq = true;          // Linux: everything plays through "StreamLite Headphones (EQ)" (correction + profiles)
        public string PreviousDefaultSink;       // Linux: the output that was the default before StreamLite changed it
#endif

        /// <summary>%APPDATA%\StreamLite (STREAMLITE_DATA overrides it, so tests never touch the real settings).</summary>
        public static string Folder
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("STREAMLITE_DATA");
                if (!string.IsNullOrEmpty(over)) return over;
                // DoNotVerify: on a new Linux account ~/.config may not exist yet, and the default returns "" for a missing
                // folder (settings would then land in whatever folder StreamLite was started from).
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "StreamLite");
            }
        }

        static string FilePath { get { return Path.Combine(Folder, "settings.json"); } }
#if LINUX
        static readonly System.Text.Json.JsonSerializerOptions Json = new System.Text.Json.JsonSerializerOptions
        {
            IncludeFields = true, WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
#endif

        /// <summary>
        /// The settings file, or - if it can't be read (damaged, half-written, edited by hand) - the last copy that
        /// could, instead of silently starting from defaults. A damaged file is kept as settings.broken-*.json.
        /// </summary>
        public static AppSettings Load()
        {
            string good = FilePath + ".lastgood";
            if (File.Exists(FilePath))
            {
                var s = Read(FilePath);
                if (s != null)
                {
                    try { File.Copy(FilePath, good, true); } catch { }   // the last file known to load
                    return s;
                }
                try { File.Copy(FilePath, Path.Combine(Folder, "settings.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true); } catch { }
                var g = File.Exists(good) ? Read(good) : null;
                if (g != null) return g;
            }
            var d = new AppSettings();
            d.Normalize();
            return d;
        }

        static AppSettings Read(string path)
        {
            try
            {
#if LINUX
                var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
#else
                var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(path));
#endif
                if (s != null) s.Normalize();
                return s;
            }
            catch { return null; }
        }

        void Normalize()
        {
            if (MicEq == null) MicEq = new EqProfile();
            MicEq = MicEq.Clone();
            if (NsEngineVersion < 2)
            {
                // Values tuned for the old noise engine sound wrong on the new one: start from its defaults once.
                Strength = 50; AdaptSpeed = 30; NsSmoothing = 50; NsVoiceProtect = 60;
                NsEngineVersion = 2;
            }
            if (NsEngineVersion < 3)
            {
                // AI voice isolation replaces the statistical suppressor as the default.
                NsEngine = "ai"; Strength = 70; NsSmoothing = 50; NsVoiceProtect = 60;
                NsEngineVersion = 3;
            }
            NsEngine = NoiseEngines.Normalize(NsEngine);
            if (Game == null) Game = new GameAudioSettings();
            if (Profiles == null) Profiles = new ProfileSettings();
            if (Game.DirEq == null || Game.DirEq.Count == 0) { Game.Repair(); GamePresets.Apply(Game, Game.Preset ?? "Footsteps (competitive)"); }
            Game.Repair();
            if (OutputEq == null) OutputEq = new Dictionary<string, EqProfile>();
            if (Cameras == null) Cameras = new Dictionary<string, CameraProfile>();
            if (ExtraMics == null) ExtraMics = new List<ExtraMicSetting>();
            ExtraMics.RemoveAll(m => m == null || string.IsNullOrEmpty(m.Id));
            foreach (var m in ExtraMics) { m.GainDb = Math.Max(-20, Math.Min(20, m.GainDb)); if (m.Eq == null) m.Eq = new EqProfile(); m.Eq.Repair(); }
            MainMicGainDb = Math.Max(-20, Math.Min(20, MainMicGainDb));
            if (MainMicEq == null) MainMicEq = new EqProfile();
            MainMicEq.Repair();
            if (OutputDelayMs == null) OutputDelayMs = new Dictionary<string, int>();
            foreach (var k in new List<string>(OutputDelayMs.Keys)) OutputDelayMs[k] = Math.Max(0, Math.Min(900, OutputDelayMs[k]));
            if (MicBleed == null) MicBleed = new List<MicBleedEntry>();
            MicBleed.RemoveAll(b => b == null || string.IsNullOrEmpty(b.From) || string.IsNullOrEmpty(b.To) || !(b.Ratio >= 0 && b.Ratio <= 1));
            if (Hotkeys == null) Hotkeys = new List<HotkeyBinding>();
            Hotkeys.RemoveAll(h => h == null || string.IsNullOrEmpty(h.Action));
            if (ExtraOutputs == null) ExtraOutputs = new List<ExtraOutputSetting>();
            ExtraOutputs.RemoveAll(o => o == null || string.IsNullOrEmpty(o.Id));
            foreach (var o in ExtraOutputs) { o.GainDb = Math.Max(-20, Math.Min(20, o.GainDb)); if (o.Eq == null) o.Eq = new EqProfile(); o.Eq.Repair(); }
            if (CamFx == null) CamFx = new CamFxSettings();
            CamFx.Repair();
            if (RoutedGames == null) RoutedGames = new List<string>();
            if (FormatsDone == null) FormatsDone = new List<string>();
            foreach (var c in Cameras.Values)
            {
                if (c.Values == null) c.Values = new Dictionary<string, CameraPropSetting>();
                if (c.SavedLook == null) c.SavedLook = new Dictionary<string, CameraPropSetting>();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
#if LINUX
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(this, Json));
#else
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
#endif
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch { }
        }
    }

#if !LINUX
    /// <summary>Launch-at-login via the per-user Run key, which is what Task Manager's Startup apps tab lists.</summary>
    internal static class StartupRegistration
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "StreamLite";

        public static bool IsEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (k == null || k.GetValue(ValueName) == null) return false;
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    var b = k != null ? k.GetValue(ValueName) as byte[] : null;
                    return b == null || b.Length == 0 || (b[0] & 1) == 0;
                }
            }
        }

        public static void Set(bool enable)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --minimized");
                else k.DeleteValue(ValueName, false);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                if (enable) k.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                else k.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Keeps the registered path current if the app was moved.</summary>
        public static void Refresh()
        {
            if (IsEnabled) Set(true);
        }
    }
#endif
}
