using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    internal static class MicPresets
    {
        public const string Extension = ".slmic";

        /// <summary>The mic's identity on any PC (see MicPreset.Key).</summary>
        public static string KeyOf(AudioDeviceInfo d) { return d == null ? null : KeyOf(d.Instance, d.Name); }

        public static string KeyOf(string instance, string name)
        {
            var m = Regex.Match(instance ?? "", @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            string hw = m.Success ? "usb:" + m.Groups[1].Value.ToUpperInvariant() + ":" + m.Groups[2].Value.ToUpperInvariant() : "name";
            return hw + "|" + (name ?? "").Trim();
        }

        /// <summary>The preset for this mic: the same USB mic and name, else the same USB mic (renamed), else the same name.</summary>
        public static MicPreset Find(AppSettings s, AudioDeviceInfo d) { return d == null ? null : Find(s, KeyOf(d)); }

        public static MicPreset Find(AppSettings s, string key)
        {
            if (key == null || s.MicPresets == null) return null;
            var exact = s.MicPresets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            string hw = key.Split('|')[0], name = key.Substring(key.IndexOf('|') + 1);
            if (hw != "name")
            {
                var sameHw = s.MicPresets.Where(p => p.Key != null && p.Key.StartsWith(hw + "|", StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameHw.Count == 1) return sameHw[0];
            }
            return s.MicPresets.FirstOrDefault(p => p.Key != null && string.Equals(p.Key.Substring(p.Key.IndexOf('|') + 1), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The current settings as this mic's preset.</summary>
        public static MicPreset Capture(AppSettings s, string device, string key, string studio)
        {
            return new MicPreset
            {
                Device = device, Key = key, Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Chain = SetupExport.Fields(s, SetupExport.MicChainFields),
                MicEq = s.MicEq != null ? s.MicEq.Clone() : null, MainMicEq = s.MainMicEq != null ? s.MainMicEq.Clone() : null,
                MainMicGainDb = s.MainMicGainDb, StudioVoice = s.StudioVoice, StudioStrength = s.StudioStrength, Studio = studio
            };
        }

        /// <summary>Keeps (adds or replaces) a preset; the one it replaces keeps its note.</summary>
        public static void Keep(AppSettings s, MicPreset p)
        {
            if (s.MicPresets == null) s.MicPresets = new List<MicPreset>();
            int i = s.MicPresets.FindIndex(x => string.Equals(x.Key, p.Key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { if (p.Note == null) p.Note = s.MicPresets[i].Note; s.MicPresets[i] = p; }
            else s.MicPresets.Add(p);
        }

        /// <summary>Puts a preset's settings in place (the engine picks them up with PushEngineParams).</summary>
        public static void Apply(AppSettings s, MicPreset p, string micId)
        {
            SetupExport.SetFields(s, p.Chain, SetupExport.MicChainFields);
            if (p.MicEq != null) { s.MicEq = p.MicEq.Clone(); s.MicEq.Repair(); }
            if (p.MainMicEq != null) { s.MainMicEq = p.MainMicEq.Clone(); s.MainMicEq.Repair(); }
            s.MainMicGainDb = Math.Max(-20, Math.Min(20, p.MainMicGainDb));
            s.StudioVoice = p.StudioVoice; s.StudioStrength = Math.Max(0, Math.Min(100, p.StudioStrength));
            if (micId != null && !string.IsNullOrEmpty(p.Studio)) s.StudioProfiles[micId] = p.Studio;
        }

        public static string ToJson(MicPreset p, string appVersion)
        {
            var f = new MicPresetFile { Format = MicPresetFile.Kind, FormatVersion = 1, AppVersion = appVersion, Created = DateTime.Now.ToString("yyyy-MM-dd"), Preset = p };
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(f);
            var leaks = SetupExport.Leaks(json);
            if (leaks.Count > 0) throw new InvalidOperationException("The preset would include something from this PC (" + leaks[0] + ") - not saved.");
            return json;
        }

        public static MicPreset Parse(string json)
        {
            MicPresetFile f;
            try { f = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<MicPresetFile>(json); }
            catch { throw new InvalidDataException("This isn't a StreamLite mic preset (or it's damaged)."); }
            if (f == null || f.Format != MicPresetFile.Kind || f.Preset == null) throw new InvalidDataException("This isn't a StreamLite mic preset.");
            if (f.FormatVersion > 1) throw new InvalidDataException("This preset is from a newer StreamLite (" + f.AppVersion + ") - update StreamLite to use it.");
            if (string.IsNullOrEmpty(f.Preset.Key)) f.Preset.Key = "name|" + (f.Preset.Device ?? "Microphone");
            return f.Preset;
        }

        /// <summary>The same settings (when it was kept and where it came from aside).</summary>
        public static bool Same(MicPreset a, MicPreset b)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Func<MicPreset, string> text = p => { string u = p.Updated, n = p.Note; p.Updated = null; p.Note = null; string t = js.Serialize(p); p.Updated = u; p.Note = n; return t; };
            return text(a) == text(b);
        }

        /// <summary>A file name for a preset ("Yeti Stereo Microphone.slmic").</summary>
        public static string FileName(MicPreset p)
        {
            string n = p.Device ?? "Microphone";
            var m = Regex.Match(n, @"\(([^)]+)\)\s*$");
            if (m.Success) n = m.Groups[1].Value;
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '-');
            return n.Trim() + Extension;
        }

        /// <summary>Is a mic with this preset's identity plugged in right now?</summary>
        public static AudioDeviceInfo PluggedIn(MicPreset p, IEnumerable<AudioDeviceInfo> mics)
        {
            return mics.FirstOrDefault(d => string.Equals(KeyOf(d), p.Key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
