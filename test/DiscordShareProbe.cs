using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

// Discord's whole-screen share hears every app but Discord itself ("process loopback", exclude mode). Built-in Direct
// to headphones (4.1) relies on two things, measured here with quiet test tones played by a separate program (not a
// child of this one - the exclude mode leaves out this program's own children):
//  1. a tone played EXCLUSIVELY to the headphones (how StreamLite plays) does NOT reach the share - so viewers never get
//     StreamLite's 3D copy;
//  2. a tone played to another output that is MUTED (the side output voice chat and music use) DOES reach it - so
//     viewers still hear music and browsers.
// The tones are -50 dBFS (barely audible). Usage: run.ps1 DiscordShareProbe            (the measurement)
//                                                  run.ps1 DiscordShareProbe play ...  (the tone player it starts)
class DiscordShareProbe
{
    const int Fs = 48000;

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "play") { Play(args[1], args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[4])); return; }
        var outs = AudioDevices.List(EDataFlow.Render);
        foreach (var d in outs) Console.WriteLine("  output: " + d.Name);
        var phones = outs.FirstOrDefault(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0) ?? outs.FirstOrDefault(d => d.Name.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase));
        var side = outs.FirstOrDefault(d => d != phones && d.Name.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase)) ?? outs.FirstOrDefault(d => d != phones);
        if (phones == null || side == null) { Console.WriteLine("needs headphones and one more output"); Environment.Exit(2); }
        Console.WriteLine("headphones: " + phones.Name + "   side output: " + side.Name);
        bool? wasMuted = AudioDevices.GetMute(side.Id);
        AudioDevices.SetMute(side.Id, true);
        int fails = 0;
        try
        {
            double ex = Measure("exclusive", phones.Id, 1000);
            double sh = Measure("shared", side.Id, 1500);
            double shPhones = Measure("shared", phones.Id, 2000);
            Console.WriteLine(string.Format("  tone exclusive on the headphones      : {0,6:0.0} dB in the share", ex));
            Console.WriteLine(string.Format("  tone on the muted side output         : {0,6:0.0} dB in the share", sh));
            Console.WriteLine(string.Format("  tone shared on the headphones (ref.)  : {0,6:0.0} dB in the share", shPhones));
            bool exclusiveHidden = ex < -90, sideHeard = sh > -65, refHeard = shPhones > -65;
            Console.WriteLine((exclusiveHidden ? "  PASS" : "  FAIL") + " an exclusive stream (StreamLite's 3D copy) is not in a whole-screen share"); if (!exclusiveHidden) fails++;
            Console.WriteLine((sideHeard ? "  PASS" : "  FAIL") + " apps on the muted side output (music, browsers) are in it"); if (!sideHeard) fails++;
            Console.WriteLine((refHeard ? "  PASS" : "  FAIL") + " (reference) a normal shared stream is in it"); if (!refHeard) fails++;
        }
        finally { AudioDevices.SetMute(side.Id, wasMuted == true); }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    /// <summary>Starts the tone player outside this program's process tree, captures the share for its 2 seconds, and returns the tone's level (dBFS).</summary>
    static double Measure(string mode, string deviceId, double hz)
    {
        var cap = ProcessLoopback.Open(Process.GetCurrentProcess().Id, true);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
        var ev = new AutoResetEvent(false);
        int hr = cap.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
        Marshal.FreeHGlobal(fmt);
        if (hr != 0) throw new COMException("capture init", hr);
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; cap.GetService(ref iid, out svc);
        var cc = (IAudioCaptureClient)svc;
        cap.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        cap.Start();
        // The player, started by WMI (its parent is WMI, not us).
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        using (var cls = new ManagementClass("Win32_Process"))
        {
            var inParams = cls.GetMethodParameters("Create");
            inParams["CommandLine"] = "\"" + exe + "\" play " + mode + " \"" + deviceId + "\" " + hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2500";
            cls.InvokeMethod("Create", inParams, null);
        }
        var sw = Stopwatch.StartNew();
        var samples = new System.Collections.Generic.List<float>();
        while (sw.ElapsedMilliseconds < 3500)
        {
            ev.WaitOne(50);
            while (true)
            {
                IntPtr data; uint frames, flags; ulong pos, qpc;
                if (cc.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0 || frames == 0) break;
                var buf = new float[frames * 2];
                if ((flags & 2) == 0) Marshal.Copy(data, buf, 0, buf.Length);
                for (int i = 0; i < frames; i++) samples.Add(buf[2 * i]);
                cc.ReleaseBuffer(frames);
            }
        }
        cap.Stop(); Marshal.ReleaseComObject(cc); Marshal.ReleaseComObject(cap);
        // The tone's level (Goertzel over the loudest second).
        double best = 1e-12;
        for (int start = 0; start + Fs <= samples.Count; start += Fs / 4)
        {
            double w = 2 * Math.PI * hz / Fs, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < Fs; i++) { double s0 = samples[start + i] + c * s1 - s2; s2 = s1; s1 = s0; }
            double p = s1 * s1 + s2 * s2 - c * s1 * s2;
            best = Math.Max(best, Math.Sqrt(p) * 2 / Fs);
        }
        Thread.Sleep(300);
        string rep = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsp-play-" + mode + ".txt");
        Console.WriteLine("  player (" + mode + "): " + (System.IO.File.Exists(rep) ? System.IO.File.ReadAllText(rep) : "no report"));
        try { System.IO.File.Delete(rep); } catch { }
        return 20 * Math.Log10(best);
    }

    /// <summary>The tone player: -50 dBFS sine, shared or exclusive, for ms milliseconds.</summary>
    static void Play(string mode, string id, double hz, int ms)
    {
        IMMDevice dev;
        var ac = AudioEngine.OpenClient(id, out dev);
        bool excl = mode == "exclusive";
        IntPtr fmt = IntPtr.Zero; int bits = 32; bool flt = true; int hr = -1;
        long def, min; ac.GetDevicePeriod(out def, out min);
        foreach (var f in excl ? new[] { new[] { 32, 1 }, new[] { 24, 0 }, new[] { 16, 0 } } : new[] { new[] { 32, 1 } })
        {
            if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            bits = f[0]; flt = f[1] == 1;
            fmt = AudioDevices.MakeFormat(2, 3, Fs, flt, bits == 24 ? 32 : bits);
            if (bits == 24) { Marshal.WriteInt16(fmt, 18, 24); }   // 24 valid bits in 32
            if (!excl) { hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero); break; }
            hr = ac.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, def, def, fmt, IntPtr.Zero);
            if (hr == 0) break;
            Marshal.ReleaseComObject(ac); ac = AudioEngine.OpenClient(id, out dev);
        }
        string report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsp-play-" + mode + ".txt");
        var ev = new AutoResetEvent(false); ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        double amp = Math.Pow(10, -50 / 20.0), ph = 0, step = 2 * Math.PI * hz / Fs;
        int bytes = bits == 16 ? 2 : 4;
        var sw = Stopwatch.StartNew();
        ac.Start();
        while (sw.ElapsedMilliseconds < ms)
        {
            ev.WaitOne(50);
            uint pad = 0; if (!excl) ac.GetCurrentPadding(out pad);
            int n = (int)(size - pad); if (n <= 0) continue;
            IntPtr data; if (rc.GetBuffer((uint)n, out data) != 0) continue;
            var raw = new byte[n * 2 * bytes];
            for (int i = 0; i < n; i++, ph += step)
            {
                double v = amp * Math.Sin(ph);
                for (int ch = 0; ch < 2; ch++)
                {
                    int o = (i * 2 + ch) * bytes;
                    if (bits == 16) BitConverter.GetBytes((short)(v * 32767)).CopyTo(raw, o);
                    else if (flt) BitConverter.GetBytes((float)v).CopyTo(raw, o);
                    else BitConverter.GetBytes((int)(v * 2147483392.0)).CopyTo(raw, o);
                }
            }
            Marshal.Copy(raw, 0, data, raw.Length);
            rc.ReleaseBuffer((uint)n, 0);
        }
        ac.Stop();
        System.IO.File.WriteAllText(report, "played " + (excl ? "exclusive " + bits + "-bit" : "shared") + ", " + sw.ElapsedMilliseconds + " ms");
    }
}
