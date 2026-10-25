using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Can StreamLite take an output exclusively while another app plays to it (shared)? What happens to that app? And
// the smallest exclusive period. Plays silence only. Usage: ExclusiveProbe [device name start]
class ExclusiveProbe
{
    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o); return (IAudioClient)o;
    }
    static void Main(string[] args)
    {
        string want = args.Length > 0 ? args[0] : "Speakers (Realtek";
        var d = AudioDevices.List(EDataFlow.Render).First(x => x.Name.StartsWith(want));
        Console.WriteLine(d.Name);
        // "App A": a shared stream playing silence.
        var a = Open(d.Id);
        IntPtr fa = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        int hr = a.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fa, IntPtr.Zero);
        Console.WriteLine("shared init 0x" + hr.ToString("X8"));
        object sa; Guid rid = CoreAudioConst.IID_IAudioRenderClient; a.GetService(ref rid, out sa); var ra = (IAudioRenderClient)sa;
        uint size; a.GetBufferSize(out size); IntPtr data; ra.GetBuffer(size, out data); ra.ReleaseBuffer(size, CoreAudioConst.BUFFERFLAGS_SILENT);
        Console.WriteLine("shared start 0x" + a.Start().ToString("X8"));
        Thread.Sleep(300);
        // StreamLite: exclusive, smallest period, a few formats.
        var b = Open(d.Id);
        long def, min; b.GetDevicePeriod(out def, out min);
        Console.WriteLine("periods: default " + def / 10000.0 + " ms, min " + min / 10000.0 + " ms");
        int[][] tries = { new[] { 32, 1, 32 }, new[] { 32, 0, 24 }, new[] { 24, 0, 24 }, new[] { 16, 0, 16 } };
        foreach (var t in tries)
        {
            b = Open(d.Id);
            IntPtr f = AudioDevices.MakeFormat(2, 3, 48000, t[1] == 1, t[0]); Marshal.WriteInt16(f, 18, (short)t[2]);
            hr = b.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, min, min, f, IntPtr.Zero);
            Console.WriteLine("exclusive " + t[0] + "/" + t[2] + (t[1] == 1 ? " float" : " int") + " at min period: 0x" + hr.ToString("X8"));
            if (hr == 0)
            {
                var ev = new AutoResetEvent(false); b.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                uint bf; b.GetBufferSize(out bf); object sb; b.GetService(ref rid, out sb); var rb = (IAudioRenderClient)sb;
                rb.GetBuffer(bf, out data); rb.ReleaseBuffer(bf, CoreAudioConst.BUFFERFLAGS_SILENT);
                Console.WriteLine("  buffer " + bf + " frames, start 0x" + b.Start().ToString("X8"));
                int events = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 500) { if (ev.WaitOne(100)) { events++; if (rb.GetBuffer(bf, out data) == 0) rb.ReleaseBuffer(bf, CoreAudioConst.BUFFERFLAGS_SILENT); } }
                Console.WriteLine("  " + events + " periods in 0.5 s");
                uint pad; int phr = a.GetCurrentPadding(out pad);
                IntPtr d2; int ghr = ra.GetBuffer(1, out d2);
                Console.WriteLine("  the shared app meanwhile: padding 0x" + phr.ToString("X8") + ", write 0x" + ghr.ToString("X8"));
                if (ghr == 0) ra.ReleaseBuffer(1, CoreAudioConst.BUFFERFLAGS_SILENT);
                b.Stop();
                break;
            }
        }
        // After StreamLite lets go: does the shared app play again?
        Marshal.ReleaseComObject(b); Thread.Sleep(300);
        uint p2; Console.WriteLine("after release, shared app padding 0x" + a.GetCurrentPadding(out p2).ToString("X8"));
    }
}
