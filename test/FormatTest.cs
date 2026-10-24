using System;
using StreamLite.Interop;

// Dry run of the best-working-format choice for every active device (changes nothing): current format, the format
// StreamLite would pick, and a live check that the device really streams at its current rate.
class FormatTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow))
            {
                DeviceFormat cur;
                if (!DeviceFormats.Get(d.Id, out cur)) { Console.WriteLine(d.Name + ": no format"); continue; }
                bool virt = AudioDevices.LooksVirtual(d);
                var best = virt && flow == EDataFlow.Capture ? DeviceFormats.Best(d.Id, 2, 3) : DeviceFormats.Best(d.Id, cur.Channels, cur.Mask);
                Console.WriteLine(flow + " | " + d.Name + (virt ? " [StreamLite]" : DeviceFormats.IsBluetooth(d) ? " [Bluetooth]" : ""));
                Console.WriteLine("    now  " + cur + "   best working  " + (best != null ? best.Value.ToString() : "-"));
                Check(best != null && best.Value.Rate == 48000, "a 48 kHz native format exists");
                Check(DeviceFormats.Supported(d.Id, cur), "current format reported as native");
                string why = DeviceFormats.Verify(d.Id, flow == EDataFlow.Capture, cur.Rate, 400);
                Check(why == null, "a real stream runs at " + cur.Rate + " Hz" + (why != null ? " (" + why + ")" : ""));
                int wrong = cur.Rate == 44100 ? 48000 : 44100;
                string neg = DeviceFormats.Verify(d.Id, flow == EDataFlow.Capture, wrong, 200);
                Check(neg != null, "a wrong rate is caught (" + (neg ?? "not caught") + ")");
            }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
