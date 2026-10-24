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
        var big = new AppSettings();
        Check(HardwareTuning.Apply(big, 16, 8192, true) && big.NsEngine == NoiseEngines.DfnCpu && !big.LowVram, "16 threads, 8 GB card: DeepFilterNet on the CPU, normal models");
        var small = new AppSettings();
        HardwareTuning.Apply(small, 12, 4096, true);
        Check(small.NsEngine == NoiseEngines.DfnCpu && small.LowVram, "12 threads, 4 GB card (RTX 4060 class): DeepFilterNet, Low VRAM");
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
