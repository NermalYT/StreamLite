// The AI settings picked by hardware (HardwareTuning): DeepFilterNet on the CPU for 8+ threads, Low VRAM for graphics
// cards up to 4 GB, a choice you made is never overridden, and it runs once. Also prints what this PC reports.
// Usage: run.ps1 HardwareTuningTest
using System;
using StreamLite;

class HardwareTuningTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        Console.WriteLine("  this PC: " + Environment.ProcessorCount + " threads, " + HardwareTuning.GpuMemoryMb() + " MB graphics memory");
        // A new install as AppCore starts it: the tuning, then the AI quality tier (4.1.1: a graphics card gets used).
        var big = new AppSettings();
        Check(HardwareTuning.Apply(big, 16, 8192, true) && big.NsEngine == NoiseEngines.Ai, "16 threads, 8 GB card, new install: the engine is left to the AI quality tier");
        AiTiers.Ensure(big, 16, 8192, true);
        Check(big.AiTier == AiTiers.High && big.NsEngine == NoiseEngines.DfnGpu && big.CamFx.Gpu, "...which puts it on the graphics card: High VRAM, DeepFilterNet (" + big.AiTier + ", " + big.NsEngine + ")");
        var big16 = new AppSettings(); HardwareTuning.Apply(big16, 32, 16303, true); AiTiers.Ensure(big16, 32, 16303, true);
        Check(big16.AiTier == AiTiers.High && big16.NsEngine == NoiseEngines.DfnGpu, "this PC's kind (32 threads, 16 GB): High VRAM, never Full CPU (4.1.0's bug)");
        var small = new AppSettings();
        HardwareTuning.Apply(small, 12, 4096, true); AiTiers.Ensure(small, 12, 4096, true);
        Check(small.NsEngine == NoiseEngines.DfnGpu && small.LowVram && small.AiTier == AiTiers.Low, "12 threads, 4 GB card (RTX 4060 class): DeepFilterNet, Low VRAM");
        var oldCpu = new AppSettings { AiTier = AiTiers.Normal };   // a tier already there: the old move to DeepFilterNet on the CPU still happens
        HardwareTuning.Apply(oldCpu, 16, 8192, true);
        Check(oldCpu.NsEngine == NoiseEngines.DfnCpu, "settings with a tier: DeepFilterNet on the CPU as before");
        var weak = new AppSettings();
        HardwareTuning.Apply(weak, 4, 2048, true);
        Check(weak.NsEngine == NoiseEngines.Ai && weak.LowVram, "4 threads, 2 GB: RNNoise stays (light), Low VRAM");
        var noDfn = new AppSettings();
        HardwareTuning.Apply(noDfn, 16, 0, false);
        Check(noDfn.NsEngine == NoiseEngines.Ai && !noDfn.LowVram, "DeepFilterNet not installed, no card found: nothing changes");
        var classic = new AppSettings { NsEngine = NoiseEngines.Classic };
        HardwareTuning.Apply(classic, 16, 8192, true);
        Check(classic.NsEngine == NoiseEngines.Classic, "a picked engine is kept");
        var once = new AppSettings();
        HardwareTuning.Apply(once, 16, 8192, true);
        once.NsEngine = NoiseEngines.Ai;   // you switch back to RNNoise afterwards
        Check(!HardwareTuning.Apply(once, 16, 8192, true) && once.NsEngine == NoiseEngines.Ai, "runs once: your change afterwards stays");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
