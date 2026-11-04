// How steady is Direct to headphones' stream on this PC? Opens the QCC dongle the way GameAudioEngine.RunRenderExclusive does and
// feeds it silence (only zeros are ever written), timing every step: 30 s alone, then 30 s with a thread allocating like the
// app's busiest screens (garbage collections stop every managed thread). Not part of all.ps1: it takes the headphones for a minute.
// Usage: run.ps1 ExclusiveTimingProbe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

class ExclusiveTimingProbe
{
    const string Only = "QCC Dongle";   // never any other output: exclusive mode goes around Windows' mute
    const int Fs = 48000, PhaseSeconds = 30;
    static volatile bool loadOn, stopLoad;
    static long allocated;

    class Node { public Node Next; public int V; }

    static void Load()
    {
        // A heap with some weight, and old objects pointing at new ones (what a young collection has to look through).
        var old = new Node[1000000];
        for (int i = 0; i < old.Length; i++) old[i] = new Node { V = i };
        var keep = new byte[256][];
        for (int i = 0; i < keep.Length; i++) keep[i] = new byte[1 << 20];
        var rnd = new Random(7);
        while (!stopLoad)
        {
            if (!loadOn) { Thread.Sleep(20); continue; }
            for (int i = 0; i < 2000; i++) old[rnd.Next(old.Length)].Next = new Node { V = i };
            var b = new byte[64 * 1024]; b[0] = 1;
            Interlocked.Add(ref allocated, b.Length);
        }
        GC.KeepAlive(keep);
    }

    static void Report(string label, double[] t, int n, double periodMs, int gcs, int errors)
    {
        var s = new double[n]; Array.Copy(t, s, n); Array.Sort(s);
        Console.WriteLine("{0}: {1} steps, median {2:0.00} ms, 99.9% {3:0.00} ms, longest {4:0.0} ms; over 1.5 steps: {5}, over 2: {6}, over 3: {7}, over 5: {8}; garbage collections {9}, stream errors {10}",
            label, n, s[n / 2], s[(int)(n * 0.999)], s[n - 1], s.Count(x => x > periodMs * 1.5), s.Count(x => x > periodMs * 2), s.Count(x => x > periodMs * 3), s.Count(x => x > periodMs * 5), gcs, errors);
    }

    [STAThread]
    static void Main()
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;   // as the app runs
        var target = AudioDevices.List(EDataFlow.Render).Where(d => (d.Name ?? "").IndexOf(Only, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (target.Count != 1) { Console.WriteLine("Refusing: wanted exactly one output named " + Only + ", found " + target.Count); return; }
        Console.WriteLine("Output: " + target[0].Name);
        var run = new Thread(() => Stream(target[0].Id)) { Priority = ThreadPriority.Highest, Name = "Probe out" };
        var load = new Thread(Load) { IsBackground = true, Name = "Allocating" };
        load.Start(); run.Start(); run.Join();
        stopLoad = true;
    }

    static void Stream(string id)
    {
        IntPtr mm = AudioEngine.ProAudio();
        IMMDevice dev = null; IAudioClient client = null; IntPtr fmt = IntPtr.Zero; object svc = null;
        var ev = new AutoResetEvent(false);
        try
        {
            int hr = -1; string used = null;
            int[][] tries = { new[] { 32, 1, 32 }, new[] { 32, 0, 24 }, new[] { 32, 0, 32 }, new[] { 16, 0, 16 } };   // container, float, valid bits
            long def = 0, min = 0;
            foreach (var tr in tries)
            {
                if (client != null) { Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev); client = null; }
                client = AudioEngine.OpenClient(id, out dev);
                client.GetDevicePeriod(out def, out min);
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                fmt = AudioDevices.MakeFormat(2, 3, Fs, tr[1] == 1, tr[0]);
                Marshal.WriteInt16(fmt, 18, (short)tr[2]);
                hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, min, min, fmt, IntPtr.Zero);
                if (hr == unchecked((int)0x88890019))   // AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
                {
                    uint fr; client.GetBufferSize(out fr);
                    long aligned = (long)(10000000.0 * fr / Fs + 0.5);
                    Marshal.ReleaseComObject(client); Marshal.ReleaseComObject(dev);
                    client = AudioEngine.OpenClient(id, out dev);
                    hr = client.Initialize(1, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK, aligned, aligned, fmt, IntPtr.Zero);
                }
                if (hr == 0) { used = tr[0] + "-bit " + (tr[1] == 1 ? "float" : "integer") + " (" + tr[2] + " valid)"; break; }
                if (hr == unchecked((int)0x8889000E) || hr == unchecked((int)0x8889000A)) break;
            }
            if (hr != 0) { Console.WriteLine("Exclusive mode unavailable: 0x" + hr.ToString("X8")); return; }
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            uint frames; client.GetBufferSize(out frames);
            Guid iid = CoreAudioConst.IID_IAudioRenderClient;
            hr = client.GetService(ref iid, out svc);
            if (hr != 0) { Console.WriteLine("No render service: 0x" + hr.ToString("X8")); return; }
            var rc = (IAudioRenderClient)svc;
            int n = (int)frames, bytes = n * Marshal.ReadInt16(fmt, 12);
            double periodMs = n * 1000.0 / Fs;
            Console.WriteLine("Format " + used + ", device default period " + (def / 10000.0).ToString("0.0") + " ms, minimum " + (min / 10000.0).ToString("0.0") + " ms, steps of " + periodMs.ToString("0.0") + " ms (" + n + " frames)");
            var zeros = new byte[bytes];
            IntPtr data;
            if (rc.GetBuffer(frames, out data) == 0) { Marshal.Copy(zeros, 0, data, bytes); rc.ReleaseBuffer(frames, 0); }
            hr = client.Start();
            if (hr != 0) { Console.WriteLine("Start failed: 0x" + hr.ToString("X8")); return; }
            var t = new double[PhaseSeconds * 2 * 1000];
            for (int phase = 0; phase < 2; phase++)
            {
                loadOn = phase == 1;
                int count = 0, errors = 0, gc0 = GC.CollectionCount(0);
                long start = Stopwatch.GetTimestamp(), last = start, end = start + PhaseSeconds * Stopwatch.Frequency;
                while (last < end && count < t.Length)
                {
                    if (!ev.WaitOne(1000)) { errors++; last = Stopwatch.GetTimestamp(); continue; }
                    long now = Stopwatch.GetTimestamp();
                    t[count++] = (now - last) * 1000.0 / Stopwatch.Frequency;
                    last = now;
                    hr = rc.GetBuffer(frames, out data);
                    if (hr != 0) { errors++; continue; }
                    Marshal.Copy(zeros, 0, data, bytes);
                    rc.ReleaseBuffer(frames, 0);
                }
                Report(phase == 0 ? "Stream alone          " : "With collections      ", t, Math.Max(1, count), periodMs, GC.CollectionCount(0) - gc0, errors);
            }
            loadOn = false;
            client.Stop();
            Console.WriteLine("Allocated " + (Interlocked.Read(ref allocated) >> 20) + " MB in the second part. ALL DONE");
        }
        catch (Exception ex) { Console.WriteLine("Failed: " + ex.Message); }
        finally
        {
            if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            if (svc != null) Marshal.ReleaseComObject(svc);
            if (client != null) Marshal.ReleaseComObject(client);
            if (dev != null) Marshal.ReleaseComObject(dev);
            AudioEngine.EndProAudio(mm);
        }
    }
}
