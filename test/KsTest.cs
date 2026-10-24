using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

// Direct kernel-streaming write into the mic cable: a -66 dBFS 1 kHz tone (inaudible) goes into the VB-Cable driver
// and is recorded back from "StreamLite - In" (shared capture). Passes if the tone arrives.
class KsTest
{
    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static void Main(string[] args)
    {
        foreach (var p in KsRender.RenderFilters()) Console.WriteLine("filter: " + p);
        string path = KsRender.RenderFilters().FirstOrDefault(p => p.IndexOf("vbaudiovac", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("_out", StringComparison.OrdinalIgnoreCase) >= 0);
        if (path == null) { Console.WriteLine("no VB-Cable render filter"); return; }
        var ks = new KsRender();
        if (!ks.Open(path, 48000, 20)) { Console.WriteLine("open failed: " + ks.Error); return; }
        Console.WriteLine("opened pin: " + ks.Rate + " Hz " + ks.Bits + "-bit " + (ks.Float ? "float" : "int"));
        double phase = 0; float amp = (float)Math.Pow(10, -66 / 20.0);
        var th = new Thread(() =>
        {
            bool ok = ks.Run((dst, n) => { for (int i = 0; i < n; i++) { dst[i] = (float)(amp * Math.Sin(phase)); phase += 2 * Math.PI * 1000 / 48000; } }, 10);
            if (!ok) Console.WriteLine("run stopped: " + ks.Error);
        });
        th.Start();
        Thread.Sleep(500);

        // Record the capture side for 2 s.
        var cap = AudioDevices.List(EDataFlow.Capture).FirstOrDefault(d => d.Name.StartsWith("StreamLite - In"));
        var ac = Open(cap.Id);
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4);
        ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero);
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cc = (IAudioCaptureClient)svc;
        var samples = new List<float>(); var buf = new float[rate * ch];
        ac.Start();
        var end = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < end)
        {
            Thread.Sleep(10);
            uint pk; cc.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cc.GetBuffer(out data, out frames, out fl, out a, out b);
                int count = (int)frames * ch;
                if ((fl & 2) == 0 && count <= buf.Length) { Marshal.Copy(data, buf, 0, count); for (int i = 0; i < frames; i++) samples.Add(buf[i * ch]); }
                else for (int i = 0; i < frames; i++) samples.Add(0);
                cc.ReleaseBuffer(frames); cc.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        ks.Stop = true; th.Join(2000); ks.Dispose();
        // Goertzel at 1 kHz vs total.
        double s1 = 0, s2 = 0, coeff = 2 * Math.Cos(2 * Math.PI * 1000 / rate), tot = 0;
        foreach (var v in samples) { double s0 = v + coeff * s1 - s2; s2 = s1; s1 = s0; tot += v * v; }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        double toneRms = Math.Sqrt(2 * power) / samples.Count;
        Console.WriteLine("captured " + samples.Count + " samples at " + rate + " Hz; 1 kHz tone level " + (20 * Math.Log10(toneRms + 1e-12)).ToString("0.0") + " dBFS (sent -66), total " + (10 * Math.Log10(tot / Math.Max(1, samples.Count) + 1e-12)).ToString("0.0") + " dBFS; frames written " + ks.FramesWritten);
        // Continuity: 10 ms blocks after the first 100 ms; a block more than 6 dB under the median is a gap.
        int blk = rate / 100; var lv = new List<double>();
        for (int b0 = blk * 10; b0 + blk <= samples.Count; b0 += blk) { double e = 0; for (int i = b0; i < b0 + blk; i++) e += samples[i] * samples[i]; lv.Add(Math.Sqrt(e / blk)); }
        var sorted = lv.OrderBy(x => x).ToList(); double med = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;
        int gaps = lv.Count(x => x < med * 0.5);
        Console.WriteLine("continuity: " + lv.Count + " blocks of 10 ms, " + gaps + " more than 6 dB below the median");
        Console.WriteLine(toneRms > Math.Pow(10, -75 / 20.0) ? "TONE ARRIVED" : "NO TONE");
    }
}
