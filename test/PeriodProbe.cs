// What every active audio device can do for latency: the shared engine's default / smallest period (IAudioClient3) and
// the exclusive-mode minimum period. Read-only. Usage: PeriodProbe
using System;
using System.Runtime.InteropServices;
using StreamLite;
using StreamLite.Interop;

class PeriodProbe
{
    static void Main()
    {
        foreach (var flow in new[] { EDataFlow.Capture, EDataFlow.Render })
            foreach (var d in AudioDevices.List(flow))
            {
                string line = (flow == EDataFlow.Capture ? "in  " : "out ") + d.Name.PadRight(48);
                try
                {
                    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                    IMMDevice dev; en.GetDevice(d.Id, out dev);
                    object o; Guid iid = typeof(IAudioClient3).GUID;
                    int hr = dev.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out o);
                    if (hr != 0) { Console.WriteLine(line + "no IAudioClient3 (" + hr.ToString("X") + ")"); continue; }
                    var c = (IAudioClient3)o;
                    IntPtr mix; c.GetMixFormat(out mix);
                    int rate = Marshal.ReadInt32(mix, 4);
                    uint def, fund, min, max;
                    hr = c.GetSharedModeEnginePeriod(mix, out def, out fund, out min, out max);
                    long exDef, exMin; c.GetDevicePeriod(out exDef, out exMin);
                    Console.WriteLine(line + string.Format("{0} Hz  shared {1:0.00} ms (min {2:0.00}, step {3:0.00})  exclusive min {4:0.00} ms",
                        rate, def * 1000.0 / rate, min * 1000.0 / rate, fund * 1000.0 / rate, exMin / 10000.0) + (hr != 0 ? " hr " + hr.ToString("X") : ""));
                    Marshal.FreeCoTaskMem(mix);
                    Marshal.ReleaseComObject(c); Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(en);
                }
                catch (Exception ex) { Console.WriteLine(line + ex.Message); }
            }
    }
}
