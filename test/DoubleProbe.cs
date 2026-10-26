using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

// Is an app heard twice with Direct to headphones? Records the app's own sound (per-app capture, what the mixer adds)
// and the game cable "StreamLite - Out" (what 3D audio plays) at the same time, and looks for the same sound in both.
// Usage: DoubleProbe <process name> [seconds]
class DoubleProbe
{
    static float[] Mono(List<float> st) { var m = new float[st.Count / 2]; for (int i = 0; i < m.Length; i++) m[i] = (st[2 * i] + st[2 * i + 1]) / 2; return m; }

    static void Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "DiscordPTB";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 15;
        var pids = new HashSet<int>(Process.GetProcessesByName(name).Select(p => p.Id));
        int root = AppMixer.Roots(pids, ProcessLoopback.Parents()).First();
        var app = ProcessLoopback.Open(root);
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        app.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
        object s1; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; app.GetService(ref ci, out s1); var appCap = (IAudioCaptureClient)s1;

        var cable = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith("StreamLite - Out"));
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDevice(cable.Id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o); var cc = (IAudioClient)o;
        IntPtr mf; cc.GetMixFormat(out mf); int ch = Marshal.ReadInt16(mf, 2);
        cc.Initialize(0, 0x00020000, 1000000, 0, mf, IntPtr.Zero);
        object s2; cc.GetService(ref ci, out s2); var cabCap = (IAudioCaptureClient)s2;
        var a = new List<float>(); var b = new List<float>(); var tmp = new float[96000 * 8];
        app.Start(); cc.Start();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(5);
            foreach (var pair in new[] { new { c = appCap, l = a, n = 2 }, new { c = cabCap, l = b, n = ch } })
            {
                uint p; pair.c.GetNextPacketSize(out p);
                while (p > 0)
                {
                    IntPtr d; uint fr, fl; ulong x1, x2; pair.c.GetBuffer(out d, out fr, out fl, out x1, out x2);
                    if ((fl & 2) != 0) for (int i = 0; i < fr * 2; i++) pair.l.Add(0);
                    else { Marshal.Copy(d, tmp, 0, (int)fr * pair.n); for (int i = 0; i < fr; i++) { pair.l.Add(tmp[i * pair.n]); pair.l.Add(tmp[i * pair.n + 1]); } }
                    pair.c.ReleaseBuffer(fr); pair.c.GetNextPacketSize(out p);
                }
            }
        }
        app.Stop(); cc.Stop();
        var ma = Mono(a); var mb = Mono(b);
        Func<float[], double> db = x => { double e = 0; foreach (var v in x) e += v * v; return 10 * Math.Log10(e / Math.Max(1, x.Length) + 1e-20); };
        Console.WriteLine(name + " own sound " + db(ma).ToString("0.0") + " dBFS (" + (ma.Length / 48000.0).ToString("0.0") + " s), game cable " + db(mb).ToString("0.0") + " dBFS (" + (mb.Length / 48000.0).ToString("0.0") + " s, " + ch + " ch)");
        // The same sound in both: best normalised correlation over -100..+300 ms.
        int n = Math.Min(ma.Length, mb.Length) - 15000; double best = 0; int at = 0;
        for (int lag = -4800; lag <= 14400; lag += 24)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 5000; i < n; i += 4) { int j = i + lag; if (j < 0 || j >= mb.Length) continue; xy += ma[i] * mb[j]; xx += ma[i] * ma[i]; yy += mb[j] * mb[j]; }
            double c = xy / Math.Sqrt(xx * yy + 1e-30); if (Math.Abs(c) > Math.Abs(best)) { best = c; at = lag; }
        }
        Console.WriteLine("same sound in both: correlation " + best.ToString("0.00") + " at " + (at / 48.0).ToString("0") + " ms (above ~0.3 = the app plays into 3D audio too)");
    }
}
