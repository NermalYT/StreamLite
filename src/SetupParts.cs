using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>
    /// One piece of a setup file (4.1.3): a group of settings you can tick on or off on its own - "Noise gate",
    /// "Headphone EQ for Sennheiser MOMENTUM 5", "Camera: AI tracking", one hotkey. A piece that belongs to a device names
    /// the device (by name, never by an id from that PC) so importing can put it on any device here.
    /// </summary>
    public class SetupPart
    {
        public string Id;                         // what it is ("mic.gate", "phones.eq", "profile", "hotkey"...)
        public string Area;                       // Microphone, Headphones, Outputs, 3D audio, Game profiles, Camera, Hotkeys, StreamLite
        public string Section;                    // a group inside the area ("Mic EQ", "Headphone EQs", "AI features") or null
        public string Label;
        public string Device;                     // the device it belongs to (its name), or null
        public string DeviceKind;                 // mic, output, camera
        public Dictionary<string, object> Values = new Dictionary<string, object>();
    }

    /// <summary>
    /// Every setting as pieces (SetupPart), and putting chosen pieces back: what Export settings writes, what Import
    /// settings and Scan for settings offer, piece by piece and device by device. A genuine copy of your setup with two
    /// exceptions, both for privacy: device ids and file paths from this PC (devices go by name), and anything learned
    /// from a recording - your voiceprint ("Only my voice") and a recorded sound to remove are never in a setup file.
    /// </summary>
    internal static class SetupParts
    {
        public const string Mic = "Microphone", Phones = "Headphones", Outputs = "Outputs", ThreeD = "3D audio", Profiles = "Game profiles", Camera = "Camera", Keys = "Hotkeys", App = "StreamLite";
        public static readonly string[] Areas = { Mic, Phones, Outputs, ThreeD, Profiles, Camera, Keys, App };

        // ---- which settings each piece carries (field names of AppSettings, GameAudioSettings, CamFxSettings)
        static readonly string[] NoiseFields = { "NoiseRemoval", "NsEngine", "Strength", "AdaptSpeed", "NsSmoothing", "NsVoiceProtect", "NsRealtime" };
        static readonly string[] FilterFields = { "HighPassHz", "HighCutHz", "DeHumHz" };
        static readonly string[] GateFields = { "Gate", "GateExpander", "ExpanderRatio", "GateThreshold", "GateAttack", "GateHold", "GateRelease", "GateRange" };
        static readonly string[] CompFields = { "Compressor", "CompThreshold", "CompRatio", "CompAttack", "CompRelease", "CompMakeup" };
        static readonly string[] DeEssFields = { "DeEsser", "DeEssFreq", "DeEssAmount" };
        static readonly string[] LevelFields = { "VolumeDb", "Limiter", "AutoLevel", "AutoLevelTarget", "AutoLevelMaxBoost" };
        static readonly string[] FarFields = { "FarVoice", "FarPresenceDb" };
        static readonly string[] StudioFields = { "StudioVoice", "StudioStrength" };
        static readonly string[] MicOptionFields = { "VirtualMicOn", "MakeDefaultMic", "RawMic", "ShareMic", "HideMicFeed", "MicAutoMix", "MicPresetsFollow", "MicChannel" };
        static readonly string[] Game3DSound = { "Preset", "Room", "OpenSphere", "FootstepBoost", "LoudTamer", "DynSpeed", "Width", "VirtualizeStereo", "MasterDb", "Limiter", "NaturalWhenNoGame" };
        static readonly string[] Game3DHead = { "Hrtf", "HeadSize", "CueSharpen", "FloorContrast" };
        static readonly string[] Game3DDir = { "DirEq", "DirGain", "LinkSides" };
        static readonly string[] Game3DApps = { "GamesOnly", "Also3DApps", "BypassEnabled", "BypassApps", "Mode16D", "Music16D", "Music16DSeconds" };
        static readonly string[] Game3DOn = { "Enabled", "Exclusive" };
        static readonly string[] CamImage = { "AutoLight", "FaceExposure", "Brightness", "Contrast", "Denoise", "Sharpen", "SkinSmoothing", "Saturation", "Warmth", "Hdr", "HdrLast", "Mirror", "Level", "UpsideDown", "Capture4K" };
        static readonly string[] CamTracking = { "FaceTracking", "Zoom", "TrackSpeed", "TrackMode", "Framing", "Headroom", "TrackZoneOn", "TrackZone", "PauseZones", "ManualX", "ManualY", "Presets", "Vertical" };
        static readonly string[] CamBackground = { "Background", "BlurAmount", "EdgeSharpness" };
        static readonly string[] CamGestures = { "GesturePalm", "GestureV", "GestureL", "GesturePoint", "VAction", "PenColor", "PenSize", "InkFadeSeconds" };
        static readonly string[] CamViews = { "ViewMode", "DeskQuad" };
        static readonly string[] CamPrivacy = { "AutoPrivacy", "PrivacySeconds" };
        static readonly string[] CamOn = { "Enabled", "Gpu" };
        static readonly string[] AppFields = { "StartMinimized", "CloseAction", "UpdateOnStartup", "RunAsAdmin", "AiTier", "LowVram", "AutoFormats", "HideVirtualDevices", "HotkeyToasts" };

        /// <summary>Every piece of these settings. deviceName(id) = a device's name on this PC (null = unknown); mainMicName = microphone 1's.</summary>
        public static List<SetupPart> From(AppSettings s, string mainMicName, Func<string, string> deviceName)
        {
            var l = new List<SetupPart>();
            Func<string, string, string, string[], object, SetupPart> fields = (id, area, label, names, from) =>
            {
                var p = new SetupPart { Id = id, Area = area, Label = label };
                foreach (var n in names) { var fi = from.GetType().GetField(n); if (fi != null) p.Values[n] = fi.GetValue(from); }
                l.Add(p); return p;
            };

            // Microphone: the voice chain, piece by piece, then each mic's own volume and EQ.
            fields("mic.noise", Mic, "Noise removal", NoiseFields, s).Section = "Voice chain";
            fields("mic.filters", Mic, "Low cut, high cut and hum filter", FilterFields, s).Section = "Voice chain";
            fields("mic.gate", Mic, "Noise gate", GateFields, s).Section = "Voice chain";
            fields("mic.comp", Mic, "Compressor", CompFields, s).Section = "Voice chain";
            fields("mic.deess", Mic, "De-esser", DeEssFields, s).Section = "Voice chain";
            fields("mic.level", Mic, "Volume, limiter and auto level", LevelFields, s).Section = "Voice chain";
            fields("mic.far", Mic, "Across the room", FarFields, s).Section = "Voice chain";
            fields("mic.studio", Mic, "Studio voice", StudioFields, s).Section = "Voice chain";
            var veq = new SetupPart { Id = "mic.voiceeq", Area = Mic, Section = "Mic EQ", Label = "Voice EQ (everyone's, after mixing)" };
            veq.Values["MicEq"] = s.MicEq; l.Add(veq);
            if (s.MyVoice != null || s.MyVoiceEq != null)
            {
                var mv = new SetupPart { Id = "mic.myvoice", Area = Mic, Section = "Mic EQ", Label = "My voice (your own tuning and its EQ)" };
                mv.Values["MyVoice"] = s.MyVoice; mv.Values["MyVoiceEq"] = s.MyVoiceEq; l.Add(mv);
            }
            var main = new SetupPart { Id = "mic.device", Area = Mic, Section = "Mic EQ", Label = "Its own EQ and volume", Device = mainMicName ?? "Microphone 1", DeviceKind = "mic" };
            main.Values["Eq"] = s.MainMicEq; main.Values["GainDb"] = s.MainMicGainDb; main.Values["Main"] = true;
            string studio; if (s.MicId != null && s.StudioProfiles.TryGetValue(s.MicId, out studio)) main.Values["Studio"] = studio;
            l.Add(main);
            foreach (var m in s.ExtraMics)
            {
                var p = new SetupPart { Id = "mic.device", Area = Mic, Section = "Mic EQ", Label = "Its own EQ and volume (added mic)", Device = deviceName(m.Id) ?? m.Name ?? "Microphone", DeviceKind = "mic" };
                p.Values["Eq"] = m.Eq; p.Values["GainDb"] = m.GainDb; p.Values["Main"] = false;
                if (s.StudioProfiles.TryGetValue(m.Id, out studio)) p.Values["Studio"] = studio;
                l.Add(p);
            }
            foreach (var mp in s.MicPresets)
            {
                var p = new SetupPart { Id = "mic.preset", Area = Mic, Section = "Each mic's saved settings", Label = "Saved settings for this mic", Device = mp.Device ?? "Microphone", DeviceKind = "mic" };
                p.Values["Preset"] = mp; l.Add(p);
            }
            fields("mic.options", Mic, "Virtual mic, raw mic and mixing options", MicOptionFields, s).Section = "Options";

            // Headphones: per pair, its correction EQ, its own output EQ, its personal 3D head and sync delay.
            var phones = new List<string>();
            foreach (var id in s.Profiles.Corrections.Keys.Concat(s.Game.PersonalHeads.Keys).Concat(s.OutputEq.Keys).Concat(s.OutputDelayMs.Keys).Concat(s.Game.HeadScores.Keys))
                if (!phones.Contains(id) && !s.ExtraOutputs.Any(o => o.Id == id)) phones.Add(id);
            foreach (var id in phones)
            {
                DeviceCorrection c; s.Profiles.Corrections.TryGetValue(id, out c);
                string name = deviceName(id) ?? (c != null && !string.IsNullOrEmpty(c.Name) ? "Headphones (" + c.Name + ")" : null);
                if (name == null) continue;   // nothing to match it by
                if (c != null) { var p = new SetupPart { Id = "phones.correction", Area = Phones, Section = "Headphone EQs", Label = "Correction EQ (" + (c.Name ?? "custom") + ")", Device = name, DeviceKind = "output" }; p.Values["Correction"] = c; l.Add(p); }
                EqProfile oeq; if (s.OutputEq.TryGetValue(id, out oeq) && oeq != null) { var p = new SetupPart { Id = "phones.eq", Area = Phones, Section = "Headphone EQs", Label = "Its own EQ", Device = name, DeviceKind = "output" }; p.Values["Eq"] = oeq; l.Add(p); }
                string head; s.Game.PersonalHeads.TryGetValue(id, out head);
                string scores; s.Game.HeadScores.TryGetValue(id, out scores);
                if (head != null || scores != null) { var p = new SetupPart { Id = "phones.head", Area = Phones, Section = "3D", Label = "Personal 3D head (Personalize 3D)", Device = name, DeviceKind = "output" }; p.Values["Head"] = head; p.Values["Scores"] = scores; l.Add(p); }
                int ms; if (s.OutputDelayMs.TryGetValue(id, out ms) && ms > 0) { var p = new SetupPart { Id = "phones.delay", Area = Phones, Section = "Sync", Label = "Sync delay (" + ms + " ms)", Device = name, DeviceKind = "output" }; p.Values["DelayMs"] = ms; l.Add(p); }
            }
            // The per-game headphone EQs (each game profile's EQ and apps), and the rules that pick them.
            foreach (var gp in s.Profiles.Profiles)
            {
                var p = new SetupPart { Id = "profile", Area = Phones, Section = "Headphone EQs", Label = gp.Name + (gp.Builtin ? "" : " (yours)") + " - EQ and apps" };
                p.Values["Profile"] = CleanProfile(gp); l.Add(p);
            }
            var rules = new SetupPart { Id = "profiles.rules", Area = Profiles, Label = "Auto EQ: switching, presets together, default, priority, same loudness" };
            rules.Values["AutoSwitch"] = s.Profiles.AutoSwitch; rules.Values["Together"] = s.Profiles.Together; rules.Values["DefaultProfile"] = s.Profiles.DefaultProfile;
            rules.Values["Priority"] = s.Profiles.Priority; rules.Values["MatchLoudness"] = s.Profiles.MatchLoudness;
            l.Add(rules);

            // Added outputs: volume, EQ and sync delay each.
            foreach (var o in s.ExtraOutputs)
            {
                var p = new SetupPart { Id = "out.device", Area = Outputs, Label = "Volume, EQ and sync delay", Device = deviceName(o.Id) ?? o.Name ?? "Output", DeviceKind = "output" };
                p.Values["GainDb"] = o.GainDb; p.Values["Eq"] = o.Eq; int ms; s.OutputDelayMs.TryGetValue(o.Id, out ms); p.Values["DelayMs"] = ms; l.Add(p);
            }

            // 3D audio.
            fields("3d.on", ThreeD, "3D audio on or off", Game3DOn, s.Game);
            fields("3d.sound", ThreeD, "Preset, room, footsteps and loudness", Game3DSound, s.Game);
            var head3d = fields("3d.head", ThreeD, "Head model, head size, cue sharpening, floor contrast", Game3DHead, s.Game);
            if ((string)head3d.Values["Hrtf"] == HrtfSet.Custom) head3d.Values.Remove("Hrtf");   // a file on this PC
            fields("3d.direq", ThreeD, "Directional EQ", Game3DDir, s.Game);
            fields("3d.apps", ThreeD, "Which apps play in 3D, 16D audio", Game3DApps, s.Game);

            // Camera: each camera's own picture settings (base), then StreamLite Camera's image and AI features.
            foreach (var kv in s.Cameras)
            {
                string name = deviceName(kv.Key); if (name == null || kv.Value == null) continue;
                var p = new SetupPart { Id = "cam.base", Area = Camera, Section = "Base camera settings", Label = "Brightness, exposure, focus, white balance... (the camera's own)", Device = name, DeviceKind = "camera" };
                p.Values["Profile"] = kv.Value; l.Add(p);
            }
            fields("cam.on", Camera, "StreamLite Camera on, graphics card", CamOn, s.CamFx).Section = "StreamLite Camera";
            fields("cam.image", Camera, "Image: light, color, sharpness, HDR, mirror, level", CamImage, s.CamFx).Section = "StreamLite Camera";
            fields("cam.tracking", Camera, "AI tracking, framing, zoom, zones and saved views", CamTracking, s.CamFx).Section = "AI features";
            var bg = fields("cam.background", Camera, "Background (blur, green screen...)", CamBackground, s.CamFx);
            bg.Section = "AI features";
            string bgKind = bg.Values["Background"] as string;
            if (bgKind == "image" || bgKind == "video") bg.Values["Background"] = "blur";   // the picture / video is a file on this PC
            fields("cam.gestures", Camera, "Hand signs and air drawing", CamGestures, s.CamFx).Section = "AI features";
            fields("cam.views", Camera, "Whiteboard and desk view", CamViews, s.CamFx).Section = "AI features";
            fields("cam.privacy", Camera, "Auto privacy", CamPrivacy, s.CamFx).Section = "AI features";

            // Hotkeys, one by one.
            for (int i = 0; i < s.Hotkeys.Count; i++)
            {
                var h = s.Hotkeys[i];
                var act = HotkeyActions.Find(h.Action);
                var p = new SetupPart { Id = "hotkey", Area = Keys, Label = HotkeyManager.Describe(h) + " - " + (act != null ? act.Label : h.Action) + (string.IsNullOrEmpty(h.Arg) ? "" : " (" + h.Arg + ")") };
                p.Values["Hotkey"] = new HotkeyBinding { Action = h.Action, Arg = h.Arg, Key = h.Key, Mods = h.Mods, Mouse = h.Mouse };
                l.Add(p);
            }
            fields("app", App, "Start, close, updates, AI quality, hotkey notes", AppFields, s);
            return l;
        }

        /// <summary>A profile without anything machine-specific (the headphone correction underneath goes with the headphones).</summary>
        static GameProfile CleanProfile(GameProfile p)
        {
            var eq = p.Eq != null ? p.Eq.Clone() : new EqProfile();
            eq.Extra.Clear(); eq.ExtraName = "";
            return new GameProfile
            {
                Name = p.Name, Exes = p.Exes.Select(GameProfiles.NormalizeExe).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RemovedExes = (p.RemovedExes ?? new List<string>()).Select(GameProfiles.NormalizeExe).Where(x => x != null).ToList(),
                Eq = eq, ThreeD = p.ThreeD, Tips = p.Builtin ? "" : p.Tips, Basis = p.Builtin ? "" : p.Basis, Builtin = p.Builtin, Version = p.Version, EqSignature = p.EqSignature,
                Cinematic = p.Cinematic, WhilePlaying = p.WhilePlaying, ChatApp = p.ChatApp
            };
        }

        // ---- putting pieces back

        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>A value from a file as the field's own type (lists, EQs and arrays included).</summary>
        static object As(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum) return Enum.Parse(t, v.ToString());
            if (v is IConvertible && (t.IsPrimitive || t == typeof(string) || t == typeof(decimal))) return Convert.ChangeType(v, t, System.Globalization.CultureInfo.InvariantCulture);
            return Js.ConvertToType(v, t);
        }

        static T Get<T>(SetupPart p, string key) where T : class { object v; return p.Values.TryGetValue(key, out v) && v != null ? (T)As(v, typeof(T)) : null; }
        static int GetInt(SetupPart p, string key, int dflt) { object v; return p.Values.TryGetValue(key, out v) && v != null ? (int)As(v, typeof(int)) : dflt; }

        static void SetFields(object o, SetupPart p)
        {
            foreach (var kv in p.Values)
            {
                var fi = o.GetType().GetField(kv.Key);
                if (fi == null || fi.IsInitOnly || kv.Value == null) continue;
                try { fi.SetValue(o, As(kv.Value, fi.FieldType)); }
                catch { }   // a value from a newer / damaged file: keep yours
            }
        }

        /// <summary>
        /// Puts the chosen pieces into the settings. target(part) = the device here a device piece goes to (null = leave it
        /// out); mainMicId = microphone 1 here; deviceName(id) = a device's name here.
        /// </summary>
        public static void Apply(AppSettings s, IEnumerable<SetupPart> parts, Func<SetupPart, string> target, string mainMicId, Func<string, string> deviceName)
        {
            foreach (var p in parts)
            {
                string dev = p.DeviceKind != null ? target(p) : null;
                if (p.DeviceKind != null && dev == null) continue;
                switch (p.Id)
                {
                    case "mic.noise": case "mic.filters": case "mic.gate": case "mic.comp": case "mic.deess": case "mic.level": case "mic.far": case "mic.studio": case "mic.options": case "app":
                        SetFields(s, p); break;
                    case "mic.voiceeq": { var e = Get<EqProfile>(p, "MicEq"); if (e != null) s.MicEq = e.Clone(); break; }
                    case "mic.myvoice": { s.MyVoice = Get<Dictionary<string, object>>(p, "MyVoice"); var e = Get<EqProfile>(p, "MyVoiceEq"); s.MyVoiceEq = e != null ? e.Clone() : null; break; }
                    case "mic.device":
                    {
                        var e = Get<EqProfile>(p, "Eq"); int gain = Math.Max(-20, Math.Min(20, GetInt(p, "GainDb", 0)));
                        if (dev == mainMicId) { if (e != null) s.MainMicEq = e.Clone(); s.MainMicGainDb = gain; }
                        else
                        {
                            var m = s.ExtraMics.FirstOrDefault(x => x.Id == dev);
                            if (m == null) { m = new ExtraMicSetting { Id = dev, Name = deviceName(dev) ?? p.Device }; s.ExtraMics.Add(m); }
                            if (e != null) m.Eq = e.Clone(); m.GainDb = gain;
                        }
                        var st = Get<string>(p, "Studio"); if (st != null) s.StudioProfiles[dev] = st;
                        break;
                    }
                    case "mic.preset":
                    {
                        var mp = Get<MicPreset>(p, "Preset"); if (mp == null) break;
                        string name = deviceName(dev) ?? mp.Device;
                        mp.Device = name; mp.Note = "Imported";
                        s.MicPresets.RemoveAll(x => x.Key == mp.Key || x.Device == name);
                        s.MicPresets.Add(mp);
                        break;
                    }
                    case "phones.correction":
                    {
                        var c = Get<DeviceCorrection>(p, "Correction"); if (c == null) break;
                        s.Profiles.Corrections[dev] = new DeviceCorrection { Name = c.Name, Filters = (c.Filters ?? new List<EqFilter>()).Select(x => x.Copy()).ToList(), On = c.On };
                        break;
                    }
                    case "phones.eq": { var e = Get<EqProfile>(p, "Eq"); if (e != null) s.OutputEq[dev] = e.Clone(); break; }
                    case "phones.head":
                    {
                        var h = Get<string>(p, "Head"); if (h != null) s.Game.PersonalHeads[dev] = h;
                        var sc = Get<string>(p, "Scores"); if (sc != null) s.Game.HeadScores[dev] = sc;
                        break;
                    }
                    case "phones.delay": s.OutputDelayMs[dev] = Math.Max(0, Math.Min(900, GetInt(p, "DelayMs", 0))); break;
                    case "out.device":
                    {
                        if (dev == s.Game.HeadphonesId) break;
                        var o = s.ExtraOutputs.FirstOrDefault(x => x.Id == dev);
                        if (o == null) { o = new ExtraOutputSetting { Id = dev, Name = deviceName(dev) ?? p.Device }; s.ExtraOutputs.Add(o); }
                        o.GainDb = Math.Max(-20, Math.Min(20, GetInt(p, "GainDb", 0)));
                        var e = Get<EqProfile>(p, "Eq"); if (e != null) o.Eq = e.Clone();
                        int ms = GetInt(p, "DelayMs", 0); if (ms > 0) s.OutputDelayMs[dev] = Math.Min(900, ms); else s.OutputDelayMs.Remove(dev);
                        break;
                    }
                    case "profile": ApplyProfile(s, Get<GameProfile>(p, "Profile")); break;
                    case "profiles.rules":
                    {
                        object v;
                        if (p.Values.TryGetValue("AutoSwitch", out v) && v is bool) s.Profiles.AutoSwitch = (bool)v;
                        if (p.Values.TryGetValue("Together", out v) && v is bool) s.Profiles.Together = (bool)v;
                        if (p.Values.TryGetValue("MatchLoudness", out v) && v is bool) s.Profiles.MatchLoudness = (bool)v;
                        var d = Get<string>(p, "DefaultProfile"); if (d != null && s.Profiles.Profiles.Any(x => x.Name == d)) s.Profiles.DefaultProfile = d;
                        var pri = Get<List<string>>(p, "Priority"); if (pri != null) s.Profiles.Priority = pri.Where(n => !string.IsNullOrEmpty(n)).ToList();
                        break;
                    }
                    case "3d.on": case "3d.sound": case "3d.head": case "3d.direq": case "3d.apps": SetFields(s.Game, p); s.Game.Repair(); break;
                    case "cam.base": { var cp = Get<CameraProfile>(p, "Profile"); if (cp != null) s.Cameras[dev] = cp; break; }
                    case "cam.on": case "cam.image": case "cam.tracking": case "cam.background": case "cam.gestures": case "cam.views": case "cam.privacy": SetFields(s.CamFx, p); s.CamFx.Repair(); break;
                    case "hotkey":
                    {
                        var h = Get<HotkeyBinding>(p, "Hotkey"); if (h == null || string.IsNullOrEmpty(h.Action)) break;
                        // The same key or mouse button does one thing: the file's binding replaces yours.
                        s.Hotkeys.RemoveAll(x => x.Key == h.Key && x.Mods == h.Mods && x.Mouse == h.Mouse);
                        s.Hotkeys.Add(h);
                        break;
                    }
                }
            }
            foreach (var m in s.ExtraMics) m.Eq.Repair();
            foreach (var o in s.ExtraOutputs) o.Eq.Repair();
            s.MainMicEq.Repair(); s.MicEq.Repair();
            s.NsEngine = NoiseEngines.Normalize(s.NsEngine);
        }

        /// <summary>A game profile from a file: one with the same name takes its EQ and apps; an app belongs to one profile, the file's choice wins.</summary>
        static void ApplyProfile(AppSettings s, GameProfile fp)
        {
            if (fp == null || string.IsNullOrEmpty(fp.Name)) return;
            var ps = s.Profiles;
            var exes = (fp.Exes ?? new List<string>()).Select(GameProfiles.NormalizeExe).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var other in ps.Profiles.Where(x => x.Name != fp.Name)) other.Exes.RemoveAll(x => exes.Contains(x, StringComparer.OrdinalIgnoreCase));
            var have = ps.Profiles.FirstOrDefault(x => x.Name == fp.Name);
            if (have == null) { have = new GameProfile { Name = fp.Name, Builtin = fp.Builtin, Version = fp.Version }; ps.Profiles.Add(have); }
            have.Exes = exes;
            have.RemovedExes = (fp.RemovedExes ?? new List<string>()).ToList();
            if (fp.Eq != null) { var eq = fp.Eq.Clone(); eq.Extra.Clear(); have.Eq = eq; }
            have.ThreeD = fp.ThreeD; have.Cinematic = fp.Cinematic; have.WhilePlaying = fp.WhilePlaying; have.ChatApp = fp.ChatApp;
            if (!have.Builtin) { have.Tips = fp.Tips ?? ""; have.Basis = fp.Basis ?? ""; }
            if (fp.EqSignature != null) have.EqSignature = fp.EqSignature;
            GameProfiles.Merge(ps, null);
        }

        // ---- older files (before 4.1.3: whole sections, no pieces)

        /// <summary>A 4.1.2-or-older setup file as pieces (what it had, in the new shape).</summary>
        public static List<SetupPart> FromOldFile(SetupFile f)
        {
            var s = AppSettings.Defaults();
            var plan = new ImportPlan();
            string mainMic = "old-mic-1";
            var names = new Dictionary<string, string>();
            for (int i = 1; f.Mics != null && i < f.Mics.Count; i++) { plan.MicMap[i] = "old-mic-" + (i + 1); names["old-mic-" + (i + 1)] = f.Mics[i].Name; }
            for (int i = 0; f.Outputs != null && i < f.Outputs.Count; i++) { plan.OutputMap[i] = "old-out-" + i; names["old-out-" + i] = f.Outputs[i].Name; }
            for (int i = 0; f.Headphones != null && i < f.Headphones.Count; i++) { plan.HeadphoneMap[i] = "old-phones-" + i; names["old-phones-" + i] = f.Headphones[i].Name; }
            names[mainMic] = f.Mics != null && f.Mics.Count > 0 ? f.Mics[0].Name : "Microphone 1";
            s.MicId = mainMic;
            SetupExport.Apply(s, f, plan, mainMic, null, id => { string n; return names.TryGetValue(id, out n) ? n : null; });
            var parts = From(s, names[mainMic], id => { string n; return id != null && names.TryGetValue(id, out n) ? n : null; });
            // Only what the old file had.
            var keep = new HashSet<string>();
            if (f.MicChain != null) foreach (var id in new[] { "mic.noise", "mic.filters", "mic.gate", "mic.comp", "mic.deess", "mic.level", "mic.far", "mic.voiceeq", "mic.myvoice", "mic.device" }) keep.Add(id);
            if (f.Mics != null && f.Mics.Count > 1) keep.Add("mic.device");
            if (f.Outputs != null) keep.Add("out.device");
            if (f.Headphones != null) { keep.Add("phones.correction"); keep.Add("phones.head"); keep.Add("phones.delay"); }
            if (f.Game3D != null) { keep.Add("3d.sound"); keep.Add("3d.head"); keep.Add("3d.direq"); }
            if (f.Profiles != null) { keep.Add("profile"); keep.Add("profiles.rules"); keep.Add("3d.apps"); }
            if (f.Hotkeys != null) keep.Add("hotkey");
            if (f.Camera != null) { keep.Add("cam.image"); keep.Add("cam.tracking"); keep.Add("cam.background"); }
            return parts.Where(p => keep.Contains(p.Id)).ToList();
        }

        /// <summary>The pieces of a setup file, whichever version wrote it.</summary>
        public static List<SetupPart> Of(SetupFile f) { return f.Parts != null && f.FormatVersion >= 2 ? f.Parts : FromOldFile(f); }

        /// <summary>A settings file StreamLite kept (settings.json, a backup) as pieces - for Scan for settings.</summary>
        public static List<SetupPart> OfSettingsFile(string path, Func<string, string> deviceNameHere)
        {
            var s = AppSettings.ReadFile(path);
            if (s == null) return null;
            // Devices by the names this PC knows, else by the names the file kept.
            var kept = new Dictionary<string, string>();
            foreach (var m in s.ExtraMics) if (m.Id != null && m.Name != null) kept[m.Id] = m.Name;
            foreach (var o in s.ExtraOutputs) if (o.Id != null && o.Name != null) kept[o.Id] = o.Name;
            Func<string, string> name = id => { if (id == null) return null; string n = deviceNameHere(id); if (n != null) return n; return kept.TryGetValue(id, out n) ? n : null; };
            var parts = From(s, name(s.MicId), name);
            return parts;
        }
    }
}
