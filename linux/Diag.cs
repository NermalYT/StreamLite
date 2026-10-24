using System;
using System.Linq;
using StreamLite.Audio;

namespace StreamLite.Linux
{
    /// <summary>"streamlite --check-3d": runs the 3D processor with no audio devices and reports which stage misbehaves.</summary>
    internal static class Diag
    {
        public static int Check3D()
        {
            var S = new AppSettings();
            S.Game.Repair(); GamePresets.Apply(S.Game, S.Game.Preset);
            var variants = new (string, Action<SpatialConfig>)[]
            {
                ("as configured", c => { }),
                ("no room", c => c.Room = 0),
                ("no cue sharpening", c => c.CueSharpen = 0),
                ("no footstep boost / tamer", c => { c.FootstepBoost = 0; c.LoudTamer = 0; }),
                ("no directional EQ", c => { foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d].Enabled = false; c.DirGainDb[d] = 0; } }),
                ("speakers (not 360)", c => c.Render360 = false),
                ("everything off", c => { c.Room = 0; c.CueSharpen = 0; c.FootstepBoost = 0; c.LoudTamer = 0; c.Render360 = false; foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d].Enabled = false; c.DirGainDb[d] = 0; } }),
            };
            int bad = 0;
            foreach (var kind in new[] { HrtfSet.Kemar, HrtfSet.Ku100 })
            {
                var h = HrtfSet.Get(kind, null);
                int nan = 0; foreach (var a in h.L) foreach (var x in a) if (!float.IsFinite(x)) nan++;
                Console.WriteLine("  hrtf " + kind + ": " + h.Az.Length + " dirs, " + h.Taps + " taps, room " + (h.RoomL == null ? 0 : h.RoomL.Length) + ", non-finite " + nan + ", L[0][0..3] " + string.Join(",", h.L[0].Take(4)));
            }
            var cfg0 = S.Game.ToConfig();
            foreach (var f in cfg0.GetType().GetFields())
            {
                var val = f.GetValue(cfg0);
                if (val is double d && !double.IsFinite(d) || val is float fl && !float.IsFinite(fl)) Console.WriteLine("  config " + f.Name + " = " + val);
            }
            foreach (var v in variants)
            {
                var sp = new SpatialProcessor();
                var cfg = S.Game.ToConfig(); v.Item2(cfg); sp.Configure(cfg);
                sp.StartTest(true);
                var slots = new float[SpatialProcessor.Slots][]; for (int i = 0; i < slots.Length; i++) slots[i] = new float[SpatialProcessor.B];
                var l = new float[SpatialProcessor.B]; var r = new float[SpatialProcessor.B];
                int firstNan = -1; double pk = 0;
                for (int k = 0; k < 400 && firstNan < 0; k++)
                {
                    sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);
                    for (int i = 0; i < l.Length; i++) { if (!float.IsFinite(l[i]) || !float.IsFinite(r[i])) { firstNan = k; break; } pk = Math.Max(pk, Math.Abs(l[i])); }
                }
                if (firstNan >= 0) bad++;
                Console.WriteLine("  " + v.Item1.PadRight(28) + (firstNan >= 0 ? "NaN from block " + firstNan : "ok, peak " + pk.ToString("0.000")));
            }
            return bad == 0 ? 0 : 1;
        }
    }
}
