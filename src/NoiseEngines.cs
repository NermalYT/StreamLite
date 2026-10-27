using System.Collections.Generic;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>The noise-removal engines the mic can use, as stored in AppSettings.NsEngine.</summary>
    internal static class NoiseEngines
    {
        public const string Ai = "ai", Classic = "classic", DfnCpu = "dfn-cpu", DfnGpu = "dfn-gpu";

        public static bool IsDfn(string e) { return e == DfnCpu || e == DfnGpu; }

        /// <summary>Voice styles treat every neural engine alike: a style never switches you off DeepFilterNet.</summary>
        public static string Family(string e) { return e == Classic ? Classic : Ai; }

        public static string Normalize(string e)
        {
            if (e == Classic || e == DfnCpu || e == DfnGpu) return e;
            return Ai;
        }

        /// <summary>Engines in menu order (DeepFilterNet only when its files are installed).</summary>
        public static List<string> Available()
        {
            var l = new List<string> { Ai, Classic };
            if (DeepFilter.Installed) { l.Add(DfnCpu); l.Add(DfnGpu); }
            return l;
        }

        public static string Label(string e, bool longForm)
        {
            switch (e)
            {
                case Classic: return longForm ? "Classic (steady noise only)" : "Classic (steady noise)";
                case DfnCpu: return longForm ? "DeepFilterNet 3 - studio quality (CPU)" : "DeepFilterNet (CPU)";
                case DfnGpu: return longForm ? "DeepFilterNet 3 - studio quality (GPU)" : "DeepFilterNet (GPU)";
                default: return longForm ? "AI voice isolation - RNNoise (light)" : "AI voice isolation";
            }
        }

        public const string Help =
            "AI voice isolation is built in: with noise removal on, StreamLite always runs the best noise remover your AI quality (Settings) allows - nothing to pick.\n" +
            "  - DeepFilterNet 3 (graphics card, or the CPU with Full CPU): a 48 kHz network, the kind behind 'studio voice' tools. Keeps your voice clean and natural in fans, wind, keyboards, TV and room noise. About 50 ms.\n" +
            "  - RNNoise: a much lighter network, used with Full CPU on CPUs with fewer than 12 threads. 20 ms.\n" +
            "A TV, kids or music that it lets through: Tuning & EQ > Noise removal > Remove a specific sound.";
    }
}
