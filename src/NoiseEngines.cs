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
            "AI voice isolation (RNNoise): a small neural network on the CPU (about 2% of one core). Light and quick (20 ms).\n" +
            "DeepFilterNet 3: a much stronger 48 kHz network (the model behind many 'studio voice' tools). Keeps your voice far cleaner and more natural in fans, keyboards, TV and room noise. About 30-40 ms delay.\n" +
            "  - CPU: about 13% of one core, ~35 MB RAM.\n" +
            "  - GPU: runs on your graphics card with DirectML (~150 MB of video memory, under 1 ms per 10 ms of audio); almost no CPU or RAM.\n" +
            "Classic: the older statistical remover for steady noise only.";
    }
}
