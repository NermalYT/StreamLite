using System;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>
    /// Picks the AI settings that fit this PC, once (new installs, and once for everyone when the tuning changes):
    /// the noise remover by CPU, Low VRAM by graphics card. Anything you change afterwards stays as you set it.
    /// </summary>
    internal static class HardwareTuning
    {
        /// <summary>Bumps when the rules change, so every install is tuned once more.</summary>
        public const int Version = 1;

        /// <summary>
        /// DeepFilterNet on the CPU needs about 20% of one fast core (with realtime noise removal RNNoise steers next to
        /// it); from 12 threads that stays under 6% of the CPU even on a budget CPU with cores 2.5x slower, so it's the
        /// default there (it only runs while an app records your mic). Smaller CPUs keep RNNoise, which is much lighter.
        /// </summary>
        public const int DfnMinThreads = 12;
        /// <summary>Graphics cards with up to 4 GB (4.5 GB to allow for reporting) get the small Low VRAM models.</summary>
        public const long LowVramMaxMb = 4608;

        /// <summary>What the last tuning found and chose (status.txt, Copy details).</summary>
        public static string Summary = "";

        public static bool Apply(AppSettings s)
        {
            return Apply(s, Environment.ProcessorCount, GpuMemoryMb(), DeepFilter.Installed);
        }

        /// <summary>The rules, testable: returns true when it changed something (settings need saving).</summary>
        public static bool Apply(AppSettings s, int threads, long vramMb, bool dfnInstalled)
        {
            string found = threads + " CPU threads, " + (vramMb > 0 ? vramMb + " MB graphics memory" : "no graphics card memory found");
            if (s.AutoTuned >= Version) { Summary = found + " (tuned before; your settings kept)"; return false; }
            var chose = new System.Collections.Generic.List<string>();
            // Only the old default moves up: someone who picked Classic or DeepFilterNet keeps it.
            // Not on a new install (4.1.1): there the AI quality tier picks the remover - DeepFilterNet on the CPU set here
            // first made the tier think it was chosen by hand and put every new install on Full CPU.
            if (s.NsEngine == NoiseEngines.Ai && dfnInstalled && threads >= DfnMinThreads && Array.IndexOf(AiTiers.All, s.AiTier) >= 0)
            {
                s.NsEngine = NoiseEngines.DfnCpu;
                chose.Add("DeepFilterNet on the CPU");
            }
            if (vramMb > 0 && vramMb <= LowVramMaxMb && !s.LowVram)
            {
                s.LowVram = true;
                chose.Add("Low VRAM");
            }
            s.AutoTuned = Version;
            Summary = found + " -> " + (chose.Count > 0 ? string.Join(", ", chose) : "settings kept");
            return true;
        }

        /// <summary>The largest dedicated video memory of the graphics cards in this PC (MB), or 0 when unknown.</summary>
        public static long GpuMemoryMb()
        {
#if LINUX
            return Linux.LinuxGpu.Ready ? Linux.LinuxGpu.VramMb : 0;   // an NVIDIA card with its CUDA download
#else
            long best = 0;
            try
            {
                // Display adapters' class key: the driver writes the real size there (WMI's AdapterRAM stops at 4 GB).
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (var name in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = cls.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                object v = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                                long bytes = 0;
                                if (v is long) bytes = (long)v;
                                else if (v is int) bytes = (uint)(int)v;
                                else if (v is byte[] && ((byte[])v).Length >= 4) { var b = (byte[])v; bytes = b.Length >= 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToUInt32(b, 0); }
                                best = Math.Max(best, bytes / (1024 * 1024));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return best;
#endif
        }
    }
}
