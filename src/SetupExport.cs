using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>A microphone in a setup file: microphone 1 first, then the added ones - by name, never by device id.</summary>
    public class SetupMic { public string Name; public int GainDb; public EqProfile Eq; }

    /// <summary>An added output in a setup file (by name), with its volume, EQ and sync delay.</summary>
    public class SetupOutput { public string Name; public int GainDb; public EqProfile Eq; public int DelayMs; }

    /// <summary>Headphones in a setup file (by name): their correction, personal 3D head and sync delay.</summary>
    public class SetupHeadphones { public string Name; public DeviceCorrection Correction; public string PersonalHead; public int DelayMs; }

    /// <summary>Game profiles and app rules: which app picks which EQ, and in which order.</summary>
    public class SetupProfiles
    {
        public bool AutoSwitch = true;
        public bool Together;                   // presets together (4.0.7)
        public string DefaultProfile;
        public List<GameProfile> Profiles = new List<GameProfile>();
        public bool BypassEnabled = true;
        public string BypassApps;
        public bool GamesOnly = true;            // 4.1.2: only games in 3D, plus Also3DApps
        public string Also3DApps;
    }

    /// <summary>
    /// A StreamLite setup file (*.streamlite): mic chain, headphone EQ, game profiles, app rules, 3D sound, hotkeys
    /// and the camera look. Devices are kept by name only and files not at all - no device ids, no paths - so a file
    /// can be shared or moved to another PC; importing maps each device to one there.
    /// </summary>
    public class SetupFile
    {
        public const string Kind = "StreamLite setup";
        public string Format;           // Kind (set on export; a file without it isn't a setup)
        public int FormatVersion;
        public string AppVersion, Created;
        public Dictionary<string, object> MicChain;
        public EqProfile MicEq, MyVoiceEq;
        public Dictionary<string, object> MyVoice;
        public List<SetupMic> Mics;
        public List<SetupOutput> Outputs;
        public List<SetupHeadphones> Headphones;
        public Dictionary<string, object> Game3D;
        public Dictionary<string, EqProfile> Game3DEq;
        public Dictionary<string, double> Game3DGain;
        public SetupProfiles Profiles;
        public List<HotkeyBinding> Hotkeys;
        public Dictionary<string, object> Camera;
    }

    /// <summary>What to import and where each device of the file goes on this PC (null = leave it out).</summary>
    public class ImportPlan
    {
        public bool MicChain = true, Mics = true, Outputs = true, Headphones = true, Game3D = true, Profiles = true, Hotkeys = true, Camera = true;
        /// <summary>file.Mics index (1 = microphone 2, ...) -> input device id.</summary>
        public Dictionary<int, string> MicMap = new Dictionary<int, string>();
        public Dictionary<int, string> OutputMap = new Dictionary<int, string>();
        public Dictionary<int, string> HeadphoneMap = new Dictionary<int, string>();
    }

    /// <summary>A device on this PC (for matching by name).</summary>
    public class SetupDevice { public string Id, Name; public override string ToString() { return Name; } }

    internal static class SetupExport
    {
        public const string Extension = ".streamlite";

        internal static readonly string[] MicChainFields = {
            "NoiseRemoval", "NsEngine", "Strength", "AdaptSpeed", "NsSmoothing", "NsVoiceProtect", "HighPassHz", "HighCutHz", "DeHumHz",
            "Gate", "GateExpander", "ExpanderRatio", "GateThreshold", "GateAttack", "GateHold", "GateRelease", "GateRange",
            "Compressor", "CompThreshold", "CompRatio", "CompAttack", "CompRelease", "CompMakeup", "DeEsser", "DeEssFreq", "DeEssAmount",
            "VolumeDb", "Limiter", "AutoLevel", "AutoLevelTarget", "AutoLevelMaxBoost", "MicAutoMix", "FarVoice", "FarPresenceDb" };
        internal static readonly string[] Game3DFields = {
            "Hrtf", "Room", "OpenSphere", "LinkSides", "FootstepBoost", "LoudTamer", "DynSpeed", "CueSharpen", "FloorContrast", "Width", "VirtualizeStereo",
            "MasterDb", "Limiter", "Preset", "NaturalWhenNoGame" };
        static readonly string[] CameraFields = {
            "FaceTracking", "Zoom", "TrackSpeed", "Background", "BlurAmount", "EdgeSharpness", "AutoLight", "Brightness", "Contrast",
            "Denoise", "Sharpen", "SkinSmoothing", "Saturation", "Warmth", "Mirror" };

        /// <summary>
        /// The setup as a file. outputName(id) gives an output's name (null when Windows doesn't know it any more);
        /// mainMicName is microphone 1's.
        /// </summary>
        public static SetupFile Export(AppSettings s, string mainMicName, Func<string, string> outputName, string appVersion)
        {
            var f = new SetupFile { Format = SetupFile.Kind, FormatVersion = 1, AppVersion = appVersion, Created = DateTime.Now.ToString("yyyy-MM-dd") };
            f.MicChain = Fields(s, MicChainFields);
            f.MicEq = Clone(s.MicEq);
            f.MyVoice = s.MyVoice;
            f.MyVoiceEq = Clone(s.MyVoiceEq);
            f.Mics = new List<SetupMic> { new SetupMic { Name = mainMicName ?? "Microphone 1", GainDb = s.MainMicGainDb, Eq = Clone(s.MainMicEq) } };
            foreach (var m in s.ExtraMics) f.Mics.Add(new SetupMic { Name = m.Name ?? "Microphone", GainDb = m.GainDb, Eq = Clone(m.Eq) });
            f.Outputs = s.ExtraOutputs.Select(o => new SetupOutput { Name = o.Name ?? "Output", GainDb = o.GainDb, Eq = Clone(o.Eq), DelayMs = Delay(s, o.Id) }).ToList();

            // Headphones: every device with a correction, a personal 3D head or a sync delay of its own.
            var ids = new List<string>();
            foreach (var id in s.Profiles.Corrections.Keys.Concat(s.Game.PersonalHeads.Keys).Concat(s.OutputDelayMs.Keys))
                if (!ids.Contains(id) && !s.ExtraOutputs.Any(o => o.Id == id)) ids.Add(id);
            f.Headphones = new List<SetupHeadphones>();
            foreach (var id in ids)
            {
                DeviceCorrection c; s.Profiles.Corrections.TryGetValue(id, out c);
                string head; s.Game.PersonalHeads.TryGetValue(id, out head);
                int delay = Delay(s, id);
                if (c == null && head == null && delay == 0) continue;
                string name = outputName(id) ?? (c != null && !string.IsNullOrEmpty(c.Name) ? "Headphones (" + c.Name + ")" : null);
                if (name == null) continue;   // nothing to match it by
                f.Headphones.Add(new SetupHeadphones { Name = name, Correction = c, PersonalHead = head, DelayMs = delay });
            }

            f.Game3D = Fields(s.Game, Game3DFields);
            if ((string)f.Game3D["Hrtf"] == HrtfSet.Custom) f.Game3D.Remove("Hrtf");   // a file on this PC: not included
            f.Game3DEq = s.Game.DirEq.ToDictionary(k => k.Key, k => Clone(k.Value));
            f.Game3DGain = new Dictionary<string, double>(s.Game.DirGain);
            f.Profiles = new SetupProfiles
            {
                AutoSwitch = s.Profiles.AutoSwitch, Together = s.Profiles.Together, DefaultProfile = s.Profiles.DefaultProfile,
                BypassEnabled = s.Game.BypassEnabled, BypassApps = s.Game.BypassApps, GamesOnly = s.Game.GamesOnly, Also3DApps = s.Game.Also3DApps,
                Profiles = s.Profiles.Profiles.Select(CleanProfile).ToList()
            };
            f.Hotkeys = s.Hotkeys.Select(h => new HotkeyBinding { Action = h.Action, Arg = h.Arg, Key = h.Key, Mods = h.Mods, Mouse = h.Mouse }).ToList();
            f.Camera = Fields(s.CamFx, CameraFields);
            string bg = (string)f.Camera["Background"];
            if (bg == "image" || bg == "video") f.Camera["Background"] = "blur";   // the picture / video is a file on this PC
            return f;
        }

        static int Delay(AppSettings s, string id) { int ms; return id != null && s.OutputDelayMs.TryGetValue(id, out ms) ? ms : 0; }

        static EqProfile Clone(EqProfile p) { return p == null ? null : p.Clone(); }

        /// <summary>A profile without anything machine-specific: the headphone correction underneath is per device (exported with the headphones).</summary>
        static GameProfile CleanProfile(GameProfile p)
        {
            var eq = p.Eq != null ? p.Eq.Clone() : new EqProfile();
            eq.Extra.Clear(); eq.ExtraName = "";
            return new GameProfile
            {
                Name = p.Name, Exes = p.Exes.Select(GameProfiles.NormalizeExe).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RemovedExes = (p.RemovedExes ?? new List<string>()).Select(GameProfiles.NormalizeExe).Where(x => x != null).ToList(),
                // Built-ins' advice comes with StreamLite itself.
                Eq = eq, ThreeD = p.ThreeD, Tips = p.Builtin ? "" : p.Tips, Basis = p.Builtin ? "" : p.Basis, Builtin = p.Builtin, Version = p.Version, EqSignature = p.EqSignature,
                Cinematic = p.Cinematic, WhilePlaying = p.WhilePlaying, ChatApp = p.ChatApp
            };
        }

        internal static Dictionary<string, object> Fields(object o, string[] names)
        {
            var d = new Dictionary<string, object>();
            foreach (var n in names) { var fi = o.GetType().GetField(n); if (fi != null) d[n] = fi.GetValue(o); }
            return d;
        }

        internal static void SetFields(object o, Dictionary<string, object> d, string[] names)
        {
            if (d == null) return;
            foreach (var n in names)
            {
                object v; var fi = o.GetType().GetField(n);
                if (fi == null || !d.TryGetValue(n, out v) || v == null) continue;
                try { fi.SetValue(o, fi.FieldType.IsEnum ? Enum.Parse(fi.FieldType, v.ToString()) : Convert.ChangeType(v, fi.FieldType, System.Globalization.CultureInfo.InvariantCulture)); }
                catch { }   // a value from a newer / damaged file: keep yours
            }
        }

        public static string ToJson(SetupFile f)
        {
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(f);
            var leaks = Leaks(json);
            if (leaks.Count > 0) throw new InvalidOperationException("The setup would include something from this PC (" + leaks[0] + ") - not saved.");
            return json;
        }

        public static SetupFile Parse(string json)
        {
            SetupFile f;
            try { f = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SetupFile>(json); }
            catch { throw new InvalidDataException("This isn't a StreamLite setup file (or it's damaged)."); }
            if (f == null || f.Format != SetupFile.Kind) throw new InvalidDataException("This isn't a StreamLite setup file.");
            if (f.FormatVersion > 1) throw new InvalidDataException("This setup file is from a newer StreamLite (" + f.AppVersion + ") - update StreamLite to import it.");
            return f;
        }

        static readonly Regex[] LeakPatterns =
        {
            new Regex(@"\{0\.0\.[01]\.0+\}", RegexOptions.IgnoreCase),                        // Windows audio endpoint ids
            new Regex(@"(SWD|USB|HID|BTHENUM|MMDEVAPI)\\\\", RegexOptions.IgnoreCase),          // device instance paths
            new Regex(@"\\\\\?\\\\|#vid_|@device:", RegexOptions.IgnoreCase),                  // camera / device paths
            new Regex(@"[A-Za-z]:\\\\"),                                                       // C:\...
            new Regex(@"\\\\\\\\[A-Za-z0-9]"),                                                 // \\server\share
            new Regex(@"/(home|Users)/", RegexOptions.IgnoreCase),
        };

        /// <summary>Device ids and paths in a setup file's text (the export never writes any; the test checks that).</summary>
        public static List<string> Leaks(string json)
        {
            var l = new List<string>();
            foreach (var r in LeakPatterns) { var m = r.Match(json); if (m.Success) l.Add(m.Value); }
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3 && json.IndexOf("\\\\" + user, StringComparison.OrdinalIgnoreCase) >= 0) l.Add("your user name");
            return l;
        }

        /// <summary>The device here that a name from the file is (same name, then one name containing the other); null = none.</summary>
        public static string Match(string name, IEnumerable<SetupDevice> devices, ICollection<string> taken)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var free = devices.Where(d => taken == null || !taken.Contains(d.Id)).ToList();
            var hit = free.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? free.FirstOrDefault(d => d.Name != null && (d.Name.IndexOf(Core(name), StringComparison.OrdinalIgnoreCase) >= 0 || Core(name).IndexOf(Core(d.Name), StringComparison.OrdinalIgnoreCase) >= 0));
            return hit != null ? hit.Id : null;
        }

        /// <summary>"Headphones (QCC Dongle Pro)" -> "QCC Dongle Pro": the model in brackets is what identifies a device.</summary>
        static string Core(string name)
        {
            int a = name.IndexOf('('), b = name.LastIndexOf(')');
            string inner = a >= 0 && b > a + 1 ? name.Substring(a + 1, b - a - 1).Trim() : name.Trim();
            return inner.Length >= 3 ? inner : name.Trim();
        }

        /// <summary>A plan with every device matched by name (the preview lets you change each one).</summary>
        public static ImportPlan AutoPlan(SetupFile f, IList<SetupDevice> inputs, IList<SetupDevice> outputs, string mainMicId, string phonesId)
        {
            var p = new ImportPlan();
            var takenIn = new List<string> { mainMicId };
            for (int i = 1; f.Mics != null && i < f.Mics.Count; i++)
            {
                string id = Match(f.Mics[i].Name, inputs, takenIn);
                p.MicMap[i] = id; if (id != null) takenIn.Add(id);
            }
            var takenOut = new List<string> { phonesId };
            for (int i = 0; f.Outputs != null && i < f.Outputs.Count; i++)
            {
                string id = Match(f.Outputs[i].Name, outputs, takenOut);
                p.OutputMap[i] = id; if (id != null) takenOut.Add(id);
            }
            var takenPhones = new List<string>();
            for (int i = 0; f.Headphones != null && i < f.Headphones.Count; i++)
            {
                string id = Match(f.Headphones[i].Name, outputs, takenPhones);
                // One pair of headphones in the file and none matched: it's meant for the headphones in use here.
                if (id == null && f.Headphones.Count == 1) id = phonesId;
                p.HeadphoneMap[i] = id; if (id != null) takenPhones.Add(id);
            }
            return p;
        }

        /// <summary>One line per part of the file, for the preview.</summary>
        public static List<KeyValuePair<string, string>> Describe(SetupFile f)
        {
            var l = new List<KeyValuePair<string, string>>();
            if (f.MicChain != null)
            {
                object eng, ns; f.MicChain.TryGetValue("NsEngine", out eng); f.MicChain.TryGetValue("NoiseRemoval", out ns);
                bool on = ns is bool && (bool)ns;
                l.Add(new KeyValuePair<string, string>("mic", "Noise removal: " + (on ? NoiseEngines.Label(NoiseEngines.Normalize(eng as string), false) : "off") +
                    Flag(f.MicChain, "Compressor", ", compressor") + Flag(f.MicChain, "DeEsser", ", de-esser") + Flag(f.MicChain, "AutoLevel", ", auto level") +
                    (f.MicEq != null && f.MicEq.Enabled ? ", voice EQ" : "")));
            }
            if (f.Mics != null && f.Mics.Count > 1) l.Add(new KeyValuePair<string, string>("mics", f.Mics.Count > 2 ? (f.Mics.Count - 1) + " added microphones with their volumes and EQ" : "1 added microphone with its volume and EQ"));
            if (f.Outputs != null && f.Outputs.Count > 0) l.Add(new KeyValuePair<string, string>("outputs", f.Outputs.Count > 1 ? f.Outputs.Count + " added outputs with their volumes, EQ and sync delays" : "1 added output with its volume, EQ and sync delay"));
            if (f.Headphones != null && f.Headphones.Count > 0)
                l.Add(new KeyValuePair<string, string>("headphones", string.Join("; ", f.Headphones.Select(h => (h.Correction != null ? "correction \"" + h.Correction.Name + "\"" : "") +
                    (h.PersonalHead != null ? (h.Correction != null ? ", " : "") + "personal 3D " + h.PersonalHead.Replace("|", ", cue ") + "%" : "") +
                    (h.DelayMs > 0 ? ", " + h.DelayMs + " ms sync delay" : "")).Where(x => x.Length > 0))));
            if (f.Game3D != null) l.Add(new KeyValuePair<string, string>("3d", "Preset \"" + f.Game3D.Where(k => k.Key == "Preset").Select(k => k.Value).FirstOrDefault() + "\", head model, directional EQ, footstep boost..."));
            if (f.Profiles != null)
            {
                int own = f.Profiles.Profiles.Count(p => !p.Builtin), apps = f.Profiles.Profiles.Sum(p => p.Exes.Count);
                l.Add(new KeyValuePair<string, string>("profiles", f.Profiles.Profiles.Count + " profiles" + (own > 0 ? " (" + own + " of your own)" : "") + ", " + apps + " apps, Auto EQ " + (f.Profiles.AutoSwitch ? "on" : "off") +
                    (f.Profiles.GamesOnly ? "; only games in 3D" + (string.IsNullOrEmpty(f.Profiles.Also3DApps) ? "" : ", and " + f.Profiles.Also3DApps) :
                     f.Profiles.BypassEnabled && !string.IsNullOrEmpty(f.Profiles.BypassApps) ? "; kept out of 3D: " + f.Profiles.BypassApps : "")));
            }
            if (f.Hotkeys != null && f.Hotkeys.Count > 0) l.Add(new KeyValuePair<string, string>("hotkeys", f.Hotkeys.Count + " hotkey" + (f.Hotkeys.Count > 1 ? "s" : "")));
            if (f.Camera != null) l.Add(new KeyValuePair<string, string>("camera", "Background " + f.Camera.Where(k => k.Key == "Background").Select(k => k.Value).FirstOrDefault() + ", framing and image tuning"));
            return l;
        }

        static string Flag(Dictionary<string, object> d, string key, string text) { object v; return d.TryGetValue(key, out v) && v is bool && (bool)v ? text : ""; }

        /// <summary>
        /// Applies the chosen parts. Mics / outputs / headphones go to the devices the plan maps them to; ones mapped
        /// to nothing are left out. Profiles merge: ones with the same name take the file's EQ and apps, yours stay.
        /// </summary>
        public static void Apply(AppSettings s, SetupFile f, ImportPlan p, string mainMicId, string phonesId, Func<string, string> deviceName)
        {
            if (p.MicChain && f.MicChain != null)
            {
                SetFields(s, f.MicChain, MicChainFields);
                if (f.MicEq != null) s.MicEq = f.MicEq.Clone();
                if (f.MyVoice != null) s.MyVoice = f.MyVoice;
                if (f.MyVoiceEq != null) s.MyVoiceEq = f.MyVoiceEq.Clone();
                s.NsEngine = NoiseEngines.Normalize(s.NsEngine);
                // Microphone 1's own volume and EQ belong to your main mic, whichever it is here.
                if (f.Mics != null && f.Mics.Count > 0)
                {
                    s.MainMicGainDb = Math.Max(-20, Math.Min(20, f.Mics[0].GainDb));
                    if (f.Mics[0].Eq != null) s.MainMicEq = f.Mics[0].Eq.Clone();
                }
            }
            if (p.Mics && f.Mics != null && f.Mics.Count > 1)
            {
                var mics = new List<ExtraMicSetting>();
                for (int i = 1; i < f.Mics.Count; i++)
                {
                    string id; if (!p.MicMap.TryGetValue(i, out id) || id == null || id == mainMicId || mics.Any(m => m.Id == id)) continue;
                    mics.Add(new ExtraMicSetting { Id = id, Name = deviceName(id) ?? f.Mics[i].Name, GainDb = f.Mics[i].GainDb, Eq = f.Mics[i].Eq != null ? f.Mics[i].Eq.Clone() : new EqProfile() });
                }
                s.ExtraMics = mics;
            }
            if (p.Outputs && f.Outputs != null && f.Outputs.Count > 0)
            {
                var outs = new List<ExtraOutputSetting>();
                for (int i = 0; i < f.Outputs.Count; i++)
                {
                    string id; if (!p.OutputMap.TryGetValue(i, out id) || id == null || id == phonesId || outs.Any(o => o.Id == id)) continue;
                    outs.Add(new ExtraOutputSetting { Id = id, Name = deviceName(id) ?? f.Outputs[i].Name, GainDb = f.Outputs[i].GainDb, Eq = f.Outputs[i].Eq != null ? f.Outputs[i].Eq.Clone() : new EqProfile() });
                    if (f.Outputs[i].DelayMs > 0) s.OutputDelayMs[id] = f.Outputs[i].DelayMs; else s.OutputDelayMs.Remove(id);
                }
                s.ExtraOutputs = outs;
            }
            if (p.Headphones && f.Headphones != null)
                for (int i = 0; i < f.Headphones.Count; i++)
                {
                    string id; if (!p.HeadphoneMap.TryGetValue(i, out id) || id == null) continue;
                    var h = f.Headphones[i];
                    if (h.Correction != null) s.Profiles.Corrections[id] = new DeviceCorrection { Name = h.Correction.Name, Filters = (h.Correction.Filters ?? new List<EqFilter>()).Select(x => x.Copy()).ToList(), On = h.Correction.On };
                    if (h.PersonalHead != null) s.Game.PersonalHeads[id] = h.PersonalHead;
                    if (h.DelayMs > 0) s.OutputDelayMs[id] = h.DelayMs;
                }
            if (p.Game3D && f.Game3D != null)
            {
                SetFields(s.Game, f.Game3D, Game3DFields);
                if (f.Game3DEq != null) foreach (var k in f.Game3DEq) if (k.Value != null && s.Game.DirEq.ContainsKey(k.Key)) s.Game.DirEq[k.Key] = k.Value.Clone();
                if (f.Game3DGain != null) foreach (var k in f.Game3DGain) if (s.Game.DirGain.ContainsKey(k.Key)) s.Game.DirGain[k.Key] = Math.Max(-12, Math.Min(12, k.Value));
                s.Game.Repair();
            }
            if (p.Profiles && f.Profiles != null)
            {
                var ps = s.Profiles;
                foreach (var fp in f.Profiles.Profiles.Where(x => x != null && !string.IsNullOrEmpty(x.Name)))
                {
                    var have = ps.Profiles.FirstOrDefault(x => x.Name == fp.Name);
                    var cleanExes = fp.Exes.Select(GameProfiles.NormalizeExe).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    // An app belongs to one profile: the file's choice wins.
                    foreach (var other in ps.Profiles.Where(x => x.Name != fp.Name)) other.Exes.RemoveAll(x => cleanExes.Contains(x, StringComparer.OrdinalIgnoreCase));
                    if (have == null) { have = new GameProfile { Name = fp.Name, Builtin = fp.Builtin, Version = fp.Version }; ps.Profiles.Add(have); }
                    have.Exes = cleanExes;
                    have.RemovedExes = (fp.RemovedExes ?? new List<string>()).ToList();
                    if (fp.Eq != null) { var eq = fp.Eq.Clone(); eq.Extra.Clear(); have.Eq = eq; }
                    have.ThreeD = fp.ThreeD; have.Cinematic = fp.Cinematic; have.WhilePlaying = fp.WhilePlaying; have.ChatApp = fp.ChatApp;
                    if (!have.Builtin) { have.Tips = fp.Tips ?? ""; have.Basis = fp.Basis ?? ""; }
                    if (fp.EqSignature != null) have.EqSignature = fp.EqSignature;
                }
                ps.AutoSwitch = f.Profiles.AutoSwitch;
                ps.Together = f.Profiles.Together;
                if (!string.IsNullOrEmpty(f.Profiles.DefaultProfile) && ps.Profiles.Any(x => x.Name == f.Profiles.DefaultProfile)) ps.DefaultProfile = f.Profiles.DefaultProfile;
                s.Game.BypassEnabled = f.Profiles.BypassEnabled;
                if (f.Profiles.BypassApps != null) s.Game.BypassApps = f.Profiles.BypassApps;
                s.Game.GamesOnly = f.Profiles.GamesOnly;
                if (f.Profiles.Also3DApps != null) s.Game.Also3DApps = f.Profiles.Also3DApps;
                GameProfiles.Merge(ps, null);
            }
            if (p.Hotkeys && f.Hotkeys != null)
                s.Hotkeys = f.Hotkeys.Where(h => h != null && !string.IsNullOrEmpty(h.Action)).Select(h => new HotkeyBinding { Action = h.Action, Arg = h.Arg, Key = h.Key, Mods = h.Mods, Mouse = h.Mouse }).ToList();
            if (p.Camera && f.Camera != null) { SetFields(s.CamFx, f.Camera, CameraFields); s.CamFx.Repair(); }
            foreach (var m in s.ExtraMics) m.Eq.Repair();
            foreach (var o in s.ExtraOutputs) o.Eq.Repair();
            s.MainMicEq.Repair(); s.MicEq.Repair();
        }

        /// <summary>Your complete settings from just before the last import (Undo import puts them back exactly).</summary>
        public static string UndoPath { get { return Path.Combine(AppSettings.Folder, "settings.before-import.json"); } }

        public static void Snapshot(AppSettings s)
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(UndoPath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(s));
        }

        public static bool CanUndo { get { return File.Exists(UndoPath); } }

        /// <summary>Puts the settings from before the import back, into the same objects the app already uses.</summary>
        public static void Undo(AppSettings s)
        {
            var old = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<AppSettings>(File.ReadAllText(UndoPath));
            if (old == null) throw new InvalidDataException("The settings from before the import can't be read.");
            CopyInto(s, old);
            File.Delete(UndoPath);
        }

        /// <summary>Every field of src into dst; the nested settings objects are filled in place (other parts hold on to them).</summary>
        static void CopyInto(object dst, object src)
        {
            foreach (var fi in dst.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (fi.IsInitOnly) continue;
                object v = fi.GetValue(src), cur = fi.GetValue(dst);
                if (v != null && cur != null && (fi.FieldType == typeof(GameAudioSettings) || fi.FieldType == typeof(CamFxSettings) || fi.FieldType == typeof(ProfileSettings)))
                    CopyInto(cur, v);
                else fi.SetValue(dst, v);
            }
        }
    }
}
