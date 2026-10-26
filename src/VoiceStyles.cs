using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>
    /// One-click voice styles: each sets the whole mic chain (noise removal, gate, low cut, EQ, compressor, de-esser,
    /// auto level) at once. Your volume is never touched. Picking a style while your own tuning is active saves that
    /// tuning first, so "My settings" brings it back.
    /// </summary>
    internal static class VoiceStyles
    {
        public const string Mine = "My settings";

        public sealed class Style
        {
            public string Name, Description;
            public Action<AppSettings> Set;
        }

        // Everything a style decides (besides the mic EQ).
        static readonly string[] Fields =
        {
            "NoiseRemoval", "Strength", "NsSmoothing", "NsVoiceProtect", "HighPassHz", "HighCutHz", "DeHumHz",
            "Gate", "GateExpander", "GateThreshold", "ExpanderRatio", "GateRange", "GateAttack", "GateHold", "GateRelease",
            "Compressor", "CompThreshold", "CompRatio", "CompAttack", "CompRelease", "CompMakeup",
            "DeEsser", "DeEssFreq", "DeEssAmount", "AutoLevel", "AutoLevelTarget", "AutoLevelMaxBoost"
        };

        /// <summary>
        /// Shared starting point: noise removal, smooth expander, gentle compression, auto level. The noise remover itself
        /// is never a style's choice - it's built in (AI quality decides it).
        /// </summary>
        static void Base(AppSettings s)
        {
            s.NoiseRemoval = true; s.Strength = 60; s.NsSmoothing = 50; s.NsVoiceProtect = 60;
            s.HighPassHz = 80; s.HighCutHz = 0; s.DeHumHz = 0;
            s.Gate = true; s.GateExpander = true; s.GateThreshold = -55; s.ExpanderRatio = 20; s.GateRange = -30;
            s.GateAttack = 2; s.GateHold = 200; s.GateRelease = 150;
            s.Compressor = true; s.CompThreshold = -24; s.CompRatio = 20; s.CompAttack = 10; s.CompRelease = 160; s.CompMakeup = 0;
            s.DeEsser = false; s.DeEssFreq = 6500; s.DeEssAmount = 5;
            s.AutoLevel = true; s.AutoLevelTarget = -20; s.AutoLevelMaxBoost = 15;
        }

        // 10-band graphic EQ: 31 62 125 250 500 1k 2k 4k 8k 16k Hz.
        static void Eq(AppSettings s, params double[] bands)
        {
            s.MicEq.Mode = "graphic"; s.MicEq.Enabled = true; s.MicEq.AutoPreamp = true;
            s.MicEq.Bands = bands;
        }

        public static readonly List<Style> All = new List<Style>
        {
            new Style { Name = "Natural", Description = "Clean but still sounds like you. Gentle noise removal and leveling - the best starting point.",
                Set = s => { Base(s); Eq(s, -2, -1, 0, -0.5, 0, 0, 1, 1.5, 1, 0); } },
            new Style { Name = "Clear for chat", Description = "Made to be understood in Discord and games: less boom, more presence, steady level, soft \"s\" sounds.",
                Set = s => { Base(s); s.Strength = 70; s.HighPassHz = 100; s.GateThreshold = -50; s.ExpanderRatio = 25; s.GateRange = -40;
                    s.CompThreshold = -26; s.CompRatio = 30; s.CompAttack = 6; s.CompRelease = 120; s.DeEsser = true; s.DeEssAmount = 6;
                    s.AutoLevelTarget = -18; s.AutoLevelMaxBoost = 20; Eq(s, -6, -4, -1, -1, 0, 0, 2, 3, 2, 0); } },
            new Style { Name = "Broadcast", Description = "Rich, close and polished like a streamer or radio voice: warm low end, firm compression, smooth highs.",
                Set = s => { Base(s); s.HighPassHz = 60; s.GateThreshold = -52; s.ExpanderRatio = 30; s.GateRange = -50;
                    s.CompThreshold = -26; s.CompRatio = 35; s.CompAttack = 5; s.CompRelease = 100; s.DeEsser = true; s.DeEssFreq = 7000; s.DeEssAmount = 5;
                    s.AutoLevelTarget = -18; Eq(s, -3, 1, 2.5, 0.5, -1.5, -0.5, 1, 2, 1, 0); } },
            new Style { Name = "Noisy room", Description = "Fans, keyboard, people or TV around you: strongest noise removal and near-silence between words (smoothly - quiet syllables aren't cut).",
                Set = s => { Base(s); s.Strength = 90; s.NsSmoothing = 60; s.NsVoiceProtect = 50; s.HighPassHz = 100;
                    s.GateThreshold = -58; s.ExpanderRatio = 30; s.GateRange = -50; s.GateHold = 250;
                    s.CompThreshold = -26; s.CompRatio = 25; s.DeEsser = true; s.AutoLevelMaxBoost = 20; Eq(s, -6, -4, -1, -1, 0, 0, 2, 3, 2, 0); } },
            new Style { Name = "Late night", Description = "For talking quietly: lifts a soft voice to a normal level without lifting the room, and keeps breaths and \"s\" sounds down.",
                Set = s => { Base(s); s.Strength = 70; s.NsVoiceProtect = 80; s.GateThreshold = -62; s.GateRange = -30;
                    s.CompThreshold = -34; s.CompRatio = 30; s.CompRelease = 200; s.DeEsser = true; s.DeEssAmount = 7;
                    s.AutoLevelTarget = -20; s.AutoLevelMaxBoost = 25; Eq(s, 0, 1, 1, 0, -1, 0, 1, 1, 0, -1); } },
            new Style { Name = "Deep & warm", Description = "Fuller and lower: more chest and body, softer top end. Nice for chill calls and voice-overs.",
                Set = s => { Base(s); s.HighPassHz = 60; s.DeEsser = true; s.DeEssFreq = 6000; s.DeEssAmount = 5; Eq(s, -1, 2, 3, 1, -1, -1, 0, 1, 0, -1); } },
            new Style { Name = "Raw", Description = "Just AI noise removal and a low cut - no gate, EQ, compression or leveling. Your mic exactly as it is, minus the noise.",
                Set = s => { Base(s); s.Strength = 50; s.Gate = false; s.Compressor = false; s.AutoLevel = false; s.HighPassHz = 60;
                    Eq(s, new double[10]); s.MicEq.Enabled = false; } },
        };

        static FieldInfo F(string name) { return typeof(AppSettings).GetField(name); }

        static string EqPrint(EqProfile e)
        {
            if (e == null || !e.Enabled) return "eq off";
            var inv = CultureInfo.InvariantCulture;
            return e.Mode == "graphic"
                ? "g:" + string.Join(",", (e.Bands ?? new double[0]).Select(b => b.ToString("0.##", inv)))
                : (e.Mode ?? "") + ":" + string.Join(";", e.AllFilters().Select(f => f.Type + f.Freq.ToString("0", inv) + "/" + f.Gain.ToString("0.#", inv) + "/" + f.Q.ToString("0.##", inv)));
        }

        /// <summary>The settings a style controls, as one comparable string.</summary>
        public static string Fingerprint(AppSettings s)
        {
            return string.Join("|", Fields.Select(n => n == "NsEngine" ? NoiseEngines.Family(s.NsEngine) : Convert.ToString(F(n).GetValue(s), CultureInfo.InvariantCulture))) + "|" + EqPrint(s.MicEq);
        }

        /// <summary>The style the current settings match exactly, or null (your own tuning).</summary>
        public static string Match(AppSettings s)
        {
            string fp = Fingerprint(s);
            foreach (var st in All)
            {
                var t = new AppSettings();
                st.Set(t);
                if (Fingerprint(t) == fp) return st.Name;
            }
            return null;
        }

        public static Style Find(string name) { return All.FirstOrDefault(x => x.Name == name); }

        /// <summary>Applies a style; your own tuning is saved first so it can be brought back.</summary>
        public static void Apply(AppSettings s, string name)
        {
            var st = Find(name);
            if (st == null) return;
            if (Match(s) == null) SaveMine(s);
            string engine = s.NsEngine;
            st.Set(s);
            if (s.NsEngine == NoiseEngines.Ai && NoiseEngines.IsDfn(engine)) s.NsEngine = engine;   // keep your neural engine
        }

        public static bool HasMine(AppSettings s) { return s.MyVoice != null && s.MyVoice.Count > 0; }

        /// <summary>Saves your own tuning (if that's what is active) before something replaces it, so "My settings" can bring it back.</summary>
        public static void RememberMine(AppSettings s) { if (Match(s) == null) SaveMine(s); }

        static void SaveMine(AppSettings s)
        {
            s.MyVoice = new Dictionary<string, object>();
            foreach (var n in Fields) s.MyVoice[n] = F(n).GetValue(s);
            s.MyVoiceEq = s.MicEq.Clone();
        }

        /// <summary>Brings back the tuning you had before trying styles.</summary>
        public static bool RestoreMine(AppSettings s)
        {
            if (!HasMine(s)) return false;
            foreach (var kv in s.MyVoice)
            {
                var f = F(kv.Key);
                if (f == null || kv.Value == null) continue;
                try { f.SetValue(s, Convert.ChangeType(kv.Value, f.FieldType, CultureInfo.InvariantCulture)); } catch { }
            }
            if (s.MyVoiceEq != null) { s.MicEq = s.MyVoiceEq.Clone(); s.MicEq.Repair(); }
            return true;
        }
    }
}
