using System;

namespace StreamLite
{
    /// <summary>
    /// AI quality (Settings): one choice for where StreamLite's AI runs and how big its models are. Voice isolation is
    /// built in - there's no engine to pick: each tier runs the best noise remover it can, and the AI camera's quality
    /// goes up with it.
    ///   Low VRAM  - graphics card, small models: DeepFilterNet (16-bit), camera effects in 16-bit at 1080p. Under 250 MB.
    ///   Normal    - graphics card, the full models: DeepFilterNet, camera effects at 1080p or from 4K.
    ///   High VRAM - graphics card, the camera's cut-out worked out at full HD instead of a quarter of it: sharpest
    ///               hair and edges.
    ///   Full CPU  - no graphics card: DeepFilterNet on the CPU (RNNoise on CPUs too small for it), camera effects on
    ///               the CPU (slowest; StreamLite plays the camera without effects when they can't keep up).
    /// </summary>
    internal static class AiTiers
    {
        public const string Low = "low", Normal = "normal", High = "high", Cpu = "cpu";
        public static readonly string[] All = { Low, Normal, High, Cpu };

        public static string Label(string t)
        {
            return t == Low ? "Low VRAM" : t == High ? "High VRAM" : t == Cpu ? "Full CPU" : "Normal";
        }

        /// <summary>Video memory all of StreamLite's AI uses together (measured with test/VramProbe.cs: Low 209 MB, Normal 429 MB
        /// or 485 MB from a 4K camera, High 861 / 886 MB).</summary>
        public static string Budget(string t)
        {
            return t == Low ? "about 210 MB of video memory" : t == High ? "about 860-890 MB of video memory" : t == Cpu ? "no video memory (slowest)" : "about 430-490 MB of video memory";
        }

        public static string Describe(string t)
        {
            switch (t)
            {
                case Low: return "Small models on the graphics card. Voice isolation: DeepFilterNet 3 (16-bit). Camera: effects in 16-bit at 1080p. For graphics cards with 4 GB or less.";
                case High: return "The best quality. Voice isolation: DeepFilterNet 3. Camera: the cut-out (blur, green screen, backgrounds) worked out at full HD - four times the pixels: cleaner hair and edges, fewer halos (about 20 ms per frame on the graphics card instead of 11).";
                case Cpu: return "Nothing on the graphics card. Voice isolation: DeepFilterNet 3 on the CPU (RNNoise on CPUs with fewer than " + HardwareTuning.DfnMinThreads + " threads). Camera effects on the CPU are slow: the camera plays without them when they can't keep up.";
                default: return "The full models on the graphics card. Voice isolation: DeepFilterNet 3. Camera: effects at 1080p (from 4K when your camera has it).";
            }
        }

        /// <summary>What fits this PC: no graphics card memory found = Full CPU, up to 4 GB = Low, 8 GB and more = High.</summary>
        public static string Recommended(long vramMb)
        {
            return vramMb <= 0 ? Cpu : vramMb <= HardwareTuning.LowVramMaxMb ? Low : vramMb >= 7680 ? High : Normal;
        }

        /// <summary>The noise remover a tier runs (the best this PC can, on the CPU or the graphics card).</summary>
        public static string Engine(string t, int threads, bool dfnInstalled)
        {
            if (!dfnInstalled) return NoiseEngines.Ai;
            if (t == Cpu) return threads >= HardwareTuning.DfnMinThreads ? NoiseEngines.DfnCpu : NoiseEngines.Ai;
#if LINUX
            return threads >= HardwareTuning.DfnMinThreads ? NoiseEngines.DfnCpu : NoiseEngines.Ai;   // no graphics-card AI on Linux
#else
            return NoiseEngines.DfnGpu;
#endif
        }

        /// <summary>Sets a tier and everything it decides.</summary>
        public static void Apply(AppSettings s, string tier, int threads, bool dfnInstalled)
        {
            if (Array.IndexOf(All, tier) < 0) tier = Normal;
            s.AiTier = tier;
            s.LowVram = tier == Low;
            s.CamFx.Gpu = tier != Cpu;
            s.NsEngine = Engine(tier, threads, dfnInstalled);
        }

        /// <summary>
        /// Every start: an install from before tiers gets one (Low VRAM stays Low, DeepFilterNet on the CPU stays on the
        /// CPU, everyone else what fits the PC), and the tier is applied again (the engine follows it). True = changed.
        /// </summary>
        public static bool Ensure(AppSettings s, int threads, long vramMb, bool dfnInstalled)
        {
            string before = s.AiTier + "|" + s.LowVram + "|" + s.CamFx.Gpu + "|" + s.NsEngine;
            string tier = s.AiTier;
            if (Array.IndexOf(All, tier) < 0)
                tier = s.LowVram ? Low : s.NsEngine == NoiseEngines.DfnCpu || !s.CamFx.Gpu ? Cpu : Recommended(vramMb);
            Apply(s, tier, threads, dfnInstalled);
            return before != s.AiTier + "|" + s.LowVram + "|" + s.CamFx.Gpu + "|" + s.NsEngine;
        }
    }
}
