using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

// Discord's (and Zoom's) whole-screen share with sound, against the StreamLite that's running (4.1.1 live test). The
// share records every app but Discord itself (process loopback, exclude mode); here a stand-in "Discord" and a stand-in
// game play quiet test sounds into StreamLite - Out (never the speakers) while this program records the share:
//  1. the "Discord" sound (your friends' voices) must NOT be in the share - else friends hear themselves;
//  2. your mic (STREAMLITE_TEST_MIC voice) must NOT be in the share while an app records StreamLite - In - else friends
//     hear you twice (mic + share); and it must be in StreamLite - In (the mic works);
//  3. a game's clicks must be in the share exactly once - not a second time from StreamLite's own 3D copy.
// Usage: run.ps1 ShareLeakProbe            (needs StreamLite running with STREAMLITE_TEST_MIC set to %TEMP%\claude\testmic)
//        run.ps1 ShareLeakProbe play ...   (the sound player it starts)
class ShareLeakProbe
{
    const int Fs = 48000;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "play") { Play(args[1], args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[4]), double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)); return; }
        var outs = AudioDevices.List(EDataFlow.Render);
        var sink = outs.FirstOrDefault(d => d.Name.StartsWith("StreamLite - Out", StringComparison.OrdinalIgnoreCase));
        var vmic = AudioDevices.List(EDataFlow.Capture).FirstOrDefault(d => d.Name.StartsWith("StreamLite - In", StringComparison.OrdinalIgnoreCase));
        if (sink == null || vmic == null || Process.GetProcessesByName("StreamLite").Length == 0) { Console.WriteLine("needs StreamLite running with its devices"); Environment.Exit(2); }
        Console.WriteLine("share = every app but the excluded one; test sounds go into " + sink.Name);

        // 1. Friends' voices: "Discord" plays into StreamLite (as when StreamLite plays it on your headphones).
        int discord = Spawn("play \"" + sink.Id + "\" tone 1000 5000 -30");
        var share = Record(discord, 4500, null);
        double friend = Tone(share, 1000);
        Console.WriteLine(string.Format("  friends' voice (Discord's own sound) in the share: {0:0.0} dB", friend));
        Check(friend < -80, "friends don't hear themselves: Discord's sound, played on through StreamLite, isn't in the share");
        Thread.Sleep(800);

        // The same sound from an app that ISN'T excluded: it is in the share (the measurement works).
        int idle = Spawn("play \"" + sink.Id + "\" silence 0 6000 -100");
        int app = Spawn("play \"" + sink.Id + "\" tone 1000 5000 -30");
        share = Record(idle, 4500, null);
        double other = Tone(share, 1000);
        Console.WriteLine(string.Format("  the same sound from another app: {0:0.0} dB", other));
        Check(other > -45, "(reference) another app's sound is in the share");
        Thread.Sleep(1500);

        // 2. Your mic: an app records StreamLite - In (like Discord's voice chat) while the test voice talks.
        idle = Spawn("play \"" + sink.Id + "\" silence 0 12000 -100");
        string tm = Environment.GetEnvironmentVariable("STREAMLITE_TEST_MIC") ?? Path.Combine(Path.GetTempPath(), "claude", "testmic");
        float[] micRec = null;
        var recThread = new Thread(() => micRec = RecordDevice(vmic.Id, 10000)) { IsBackground = true };
        recThread.Start();
        Thread.Sleep(2500);   // noise removal wakes up for the recording app
        File.WriteAllText(Path.Combine(tm, "play.txt"), Path.Combine(tm, "step1.wav") + "|0");
        share = Record(idle, 7000, null);
        recThread.Join();
        double shareRms = Rms(share), micRms = micRec == null ? -200 : Rms(micRec.Skip(Fs * 3).Take(Fs * 6).ToList());
        Console.WriteLine(string.Format("  while you talk: share {0:0.0} dBFS, StreamLite - In {1:0.0} dBFS", shareRms, micRms));
        Check(micRms > -45, "your voice reaches StreamLite - In (friends hear you through the mic)");
        Check(shareRms < -70, "your voice is NOT in the share (friends don't hear you twice)");
        Thread.Sleep(1000);

        // 3. A game's clicks: once in the share, not twice.
        idle = Spawn("play \"" + sink.Id + "\" silence 0 7000 -100");
        Spawn("play \"" + sink.Id + "\" clicks 0 5000 -30");
        share = Record(idle, 5500, null);
        var peaks = Peaks(share);
        Console.WriteLine("  game clicks in the share: " + peaks.Count + " (" + string.Join(", ", peaks.Take(12).Select(p => (p * 1000.0 / Fs).ToString("0"))) + " ms)");
        bool doubled = peaks.Zip(peaks.Skip(1), (a, b) => b - a).Any(d => d < Fs * 0.2);
        Check(peaks.Count >= 6 && !doubled, "a game is in the share once - StreamLite's own copy isn't added (no echo)");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }

    /// <summary>Starts this program as a player outside our process tree (WMI), returns its pid.</summary>
    static int Spawn(string cmd)
    {
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        using (var cls = new ManagementClass("Win32_Process"))
        {
            var p = cls.GetMethodParameters("Create");
            p["CommandLine"] = "\"" + exe + "\" " + cmd;
            var r = cls.InvokeMethod("Create", p, null);
            return Convert.ToInt32(r["ProcessId"]);
        }
    }

    /// <summary>The share: every app's sound but the excluded process tree (Discord's whole-screen share).</summary>
    static List<float> Record(int excludePid, int ms, object unused)
    {
        var cap = ProcessLoopback.Open(excludePid, true);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
        var ev = new AutoResetEvent(false);
        int hr = cap.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
        Marshal.FreeHGlobal(fmt);
        if (hr != 0) throw new COMException("capture init", hr);
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; cap.GetService(ref iid, out svc);
        var cc = (IAudioCaptureClient)svc;
        cap.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        cap.Start();
        var samples = new List<float>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ev.WaitOne(50);
            while (true)
            {
                IntPtr data; uint frames, flags; ulong pos, qpc;
                if (cc.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0 || frames == 0) break;
                var buf = new float[frames * 2];
                if ((flags & 2) == 0) Marshal.Copy(data, buf, 0, buf.Length);
                for (int i = 0; i < frames; i++) samples.Add((buf[2 * i] + buf[2 * i + 1]) * 0.5f);
                cc.ReleaseBuffer(frames);
            }
        }
        cap.Stop(); Marshal.ReleaseComObject(cc); Marshal.ReleaseComObject(cap);
        return samples;
    }

    /// <summary>Records a capture device (StreamLite - In) like a voice-chat app.</summary>
    static float[] RecordDevice(string id, int ms)
    {
        IMMDevice dev;
        var ac = AudioEngine.OpenClient(id, out dev);
        IntPtr fmt = AudioDevices.MakeFormat(1, 4, Fs, true, 32);
        var ev = new AutoResetEvent(false);
        int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
        Marshal.FreeHGlobal(fmt);
        if (hr != 0) return null;
        object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref iid, out svc);
        var cc = (IAudioCaptureClient)svc;
        ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        ac.Start();
        var s = new List<float>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ev.WaitOne(50);
            while (true)
            {
                IntPtr data; uint frames, flags; ulong pos, qpc;
                if (cc.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0 || frames == 0) break;
                var buf = new float[frames];
                if ((flags & 2) == 0) Marshal.Copy(data, buf, 0, buf.Length);
                s.AddRange(buf);
                cc.ReleaseBuffer(frames);
            }
        }
        ac.Stop(); Marshal.ReleaseComObject(cc); Marshal.ReleaseComObject(ac);
        return s.ToArray();
    }

    static double Rms(IList<float> x)
    {
        // The loudest 100 ms block (voice comes and goes).
        double best = 1e-20;
        for (int b = 0; b + 4800 <= x.Count; b += 2400) { double p = 0; for (int i = b; i < b + 4800; i++) p += x[i] * x[i]; best = Math.Max(best, p / 4800); }
        return 10 * Math.Log10(best);
    }

    static double Tone(List<float> x, double hz)
    {
        double best = 1e-12;
        for (int start = 0; start + Fs <= x.Count; start += Fs / 4)
        {
            double w = 2 * Math.PI * hz / Fs, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < Fs; i++) { double s0 = x[start + i] + c * s1 - s2; s2 = s1; s1 = s0; }
            best = Math.Max(best, Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) * 2 / Fs);
        }
        return 20 * Math.Log10(best);
    }

    /// <summary>Click positions: samples over a quarter of the loudest, 50 ms apart at least... and echoes 5-200 ms later count.</summary>
    static List<int> Peaks(List<float> x)
    {
        float max = 0; foreach (var v in x) max = Math.Max(max, Math.Abs(v));
        var l = new List<int>();
        if (max < 1e-4) return l;
        for (int i = 0; i < x.Count; i++)
            if (Math.Abs(x[i]) > max * 0.25f && (l.Count == 0 || i - l[l.Count - 1] > Fs * 0.004)) { l.Add(i); i += (int)(Fs * 0.004); }
        return l;
    }

    /// <summary>The player: tone, clicks (one every 500 ms) or silence, shared, into a device.</summary>
    static void Play(string id, string kind, double hz, int ms, double db)
    {
        IMMDevice dev;
        var ac = AudioEngine.OpenClient(id, out dev);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
        int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero);
        Marshal.FreeHGlobal(fmt);
        if (hr != 0) return;
        var ev = new AutoResetEvent(false); ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        double amp = Math.Pow(10, db / 20.0), ph = 0, step = 2 * Math.PI * hz / Fs;
        long t = 0;
        var sw = Stopwatch.StartNew();
        ac.Start();
        while (sw.ElapsedMilliseconds < ms)
        {
            ev.WaitOne(50);
            uint pad; ac.GetCurrentPadding(out pad);
            int n = (int)(size - pad); if (n <= 0) continue;
            IntPtr data; if (rc.GetBuffer((uint)n, out data) != 0) continue;
            var f = new float[n * 2];
            for (int i = 0; i < n; i++, ph += step, t++)
            {
                double v = kind == "tone" ? amp * Math.Sin(ph) : kind == "clicks" ? (t % (Fs / 2) < 48 ? amp * (1 - (t % (Fs / 2)) / 48.0) : 0) : 0;
                f[2 * i] = f[2 * i + 1] = (float)v;
            }
            Marshal.Copy(f, 0, data, f.Length);
            rc.ReleaseBuffer((uint)n, 0);
        }
        ac.Stop();
    }
}
