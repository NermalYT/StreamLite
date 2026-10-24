using System.Collections.Generic;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>Directional EQ starting points. Bands: 31 62 125 250 500 1k 2k 4k 8k 16k Hz.</summary>
    internal static class GamePresets
    {
        public static readonly string[] Names = { "Footsteps (competitive)", "Front/back clarity", "Behind me boost", "Flat (no EQ)", "Cinematic" };

        public static string Describe(string name)
        {
            switch (name)
            {
                case "Footsteps (competitive)": return "Cuts rumble and explosion bass, lifts the 1-5 kHz range where footsteps, reloads and gear sounds live, and gives sounds behind you a small extra push.";
                case "Front/back clarity": return "Uses Blauert's 'directional bands': the ear hears 400 Hz and 4 kHz as 'in front' and ~1 kHz and 10-12 kHz as 'behind'. Emphasizing each band in its direction makes front/back easier to tell apart.";
                case "Behind me boost": return "Sounds behind you are louder (+4 dB) and brighter, so someone sneaking up from behind stands out.";
                case "Cinematic": return "Fuller bass, wider room. For story games and movies, not for competitive play.";
                default: return "No directional EQ. Pure virtual surround.";
            }
        }

        static void Set(GameAudioSettings g, string dir, double gain, params double[] bands)
        {
            var p = g.DirEq[dir];
            p.Mode = "graphic"; p.Enabled = true; p.AutoPreamp = false;
            p.Bands = bands.Length == 10 ? (double[])bands.Clone() : new double[10];
            g.DirGain[dir] = gain;
        }

        public static void Apply(GameAudioSettings g, string name)
        {
            g.Repair();
            var flat = new double[10];
            foreach (var d in SpatialProcessor.Dirs) Set(g, d, 0, flat);
            switch (name)
            {
                case "Footsteps (competitive)":
                    double[] steps = { -5, -4, -2, 0, 0, 1, 3, 4, 2, 0 };
                    foreach (var d in new[] { "front", "center", "left", "right" }) Set(g, d, 0, steps);
                    Set(g, "behind", 2, -5, -4, -2, 0, 0, 2, 4, 4, 3, 1);
                    Set(g, "bass", -6, flat);
                    g.FootstepBoost = 55; g.LoudTamer = 45; g.CueSharpen = 35; g.Room = 15;
                    break;
                case "Front/back clarity":
                    Set(g, "front", 0, 0, 0, 0, 1, 3, -1, 0, 3, 0, 0);
                    Set(g, "center", 0, 0, 0, 0, 1, 3, -1, 0, 3, 0, 0);
                    Set(g, "behind", 1, 0, 0, 0, 0, -1, 4, 1, -2, 3, 4);
                    g.CueSharpen = 45; g.Room = 15;
                    break;
                case "Behind me boost":
                    Set(g, "behind", 4, -3, -2, 0, 0, 0, 1, 3, 3, 3, 1);
                    Set(g, "front", -1, flat);
                    g.CueSharpen = 35;
                    break;
                case "Cinematic":
                    Set(g, "bass", 3, flat);
                    Set(g, "front", 0, 2, 2, 1, 0, 0, 0, 0, 0, 1, 1);
                    g.FootstepBoost = 0; g.LoudTamer = 20; g.CueSharpen = 0; g.Room = 60;
                    break;
            }
            g.Preset = name;
        }
    }
}
