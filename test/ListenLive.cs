// The listening EQ live, without a sound anyone can hear: a child copy of this program plays "thin music" (bass 9 dB
// down) at -100 dBFS into StreamLite - Out like an app would, and the real AppListener - Windows' per-app capture of that
// process, AppSpectrum, AppBalance - listens to it as a profile's app. Checks that it heard the app (seconds), that the
// EQ it makes lifts the bass, and that a profile that isn't listened to is left alone.
// Usage: ListenLive [seconds]     (child mode: ListenLive play <seconds>)
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

class ListenLive
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "play") { Play(int.Parse(args[1])); return; }
        int secs = args.Length > 0 ? int.Parse(args[0]) : 25;
        AppSpectrum.Silent = 1e-7;   // the test sound is at -100 dBFS
        var ps = new ProfileSettings();
        ps.Profiles.Add(new GameProfile { Name = "Test music", Exes = { "ListenLive" }, WhilePlaying = true, Eq = new EqProfile() });
        var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "play " + (secs + 25)) { UseShellExecute = false, CreateNoWindow = true });
        // The capture itself first: what Windows hands over of the app (frames, loudest sample).
        Thread.Sleep(1500);
        using (var tap = AppTap.Open(child.Id))
        {
            var b = new float[96000]; long frames = 0; double peak = 0;
            var per = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 30; i++) { Thread.Sleep(100); int n = tap.Read(b); per.Add(n); frames += n; for (int k = 0; k < 2 * n; k++) peak = Math.Max(peak, Math.Abs(b[k])); }
            Console.WriteLine("  reads: " + string.Join(" ", per));
            Check(frames > 100000 && peak > 1e-6, "per-app capture: " + frames + " frames in 3 s, loudest " + (peak > 0 ? (20 * Math.Log10(peak)).ToString("0") + " dBFS" : "silence"));
        }
        var l = new AppListener(ps);
        var heardAt = DateTime.Now.AddSeconds(secs + 3);
        string hearing = "";
        while (DateTime.Now < heardAt) { Thread.Sleep(500); if (l.Hearing != "") hearing = l.Hearing; }
        var got = l.TakeHeard();
        Check(hearing.Contains("ListenLive (Test music)"), "it listened to the app of the profile (\"" + hearing + "\")");
        Tuple<double[], double> h;
        if (!got.TryGetValue("Test music", out h)) { Check(false, "something heard for the profile"); }
        else
        {
            Check(h.Item2 > secs * 0.6, "heard " + h.Item2.ToString("0") + " s of the app's sound in " + secs + " s");
            var fit = AppBalance.Fit(h.Item1, 3600, "music", 1);
            double bass = Biquad.ResponseDb(fit, 80, 48000);
            Check(bass >= 1.5, "the thin app gets more bass: " + AppBalance.Describe(fit) + " (80 Hz " + bass.ToString("+0.0;-0.0") + " dB)");
        }

        // A profile with listening off: no ear on its app.
        ps.Profiles[0].Listen = false;
        Thread.Sleep(4000);
        Check(l.Hearing == "", "listening off for the profile: it stops listening (\"" + l.Hearing + "\")");

        // An app in no profile: it plays under the fallback profile, which learns from it.
        ps.Profiles[0].Exes.Clear();
        ps.Profiles.Add(new GameProfile { Name = "Everything else", Eq = new EqProfile() });
        ps.DefaultProfile = "Everything else";
        Thread.Sleep(4000);
        Check(l.Hearing.Contains("ListenLive (Everything else)"), "an app in no profile: heard for the fallback profile (\"" + l.Hearing + "\")");
        l.Dispose();
        try { if (!child.HasExited) child.Kill(); } catch { }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    // The app: "thin music" (bass 9 dB down below 200 Hz), stereo, at -100 dBFS - far below hearing - into StreamLite - Out.
    static void Play(int secs)
    {
        Func<double, double> shape = f => (f < 50 ? -6 * Math.Log(50 / f, 2) : f <= 500 ? 0 : -4.5 * Math.Log(f / 500, 2)) - (f < 200 ? 9 : 0);
        int n = 1 << 18; var rnd = new Random(7);
        var re = new float[n]; var im = new float[n];
        for (int k = 1; k < n / 2; k++)
        {
            double f = k * 48000.0 / n, amp = Math.Sqrt(Math.Pow(10, shape(Math.Max(10, f)) / 10) / f), ph = rnd.NextDouble() * 2 * Math.PI;
            re[k] = (float)(amp * Math.Cos(ph)); im[k] = (float)(amp * Math.Sin(ph)); re[n - k] = re[k]; im[n - k] = -im[k];
        }
        new Fft(n).Transform(re, im, true);
        double rms = Math.Sqrt(re.Select(x => (double)x * x).Average()), g = 1e-5 / rms;   // -100 dBFS
        for (int i = 0; i < n; i++) re[i] = (float)(re[i] * g);

        string id = AudioDevices.List(EDataFlow.Render).First(d => d.Name.StartsWith("StreamLite - Out")).Id;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var ac = (IAudioClient)o;
        IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
        var ev = new AutoResetEvent(false);
        if (ac.Initialize(0, CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 0, 0, fmt, IntPtr.Zero) != 0) return;
        ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
        uint size; ac.GetBufferSize(out size);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        var tmp = new float[size * 2]; int pos = 0;
        ac.Start();
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            ev.WaitOne(200);
            uint pad; ac.GetCurrentPadding(out pad);
            int free = (int)(size - pad);
            if (free <= 0) continue;
            IntPtr buf; if (rc.GetBuffer((uint)free, out buf) != 0) continue;
            for (int i = 0; i < free; i++) { tmp[2 * i] = tmp[2 * i + 1] = re[pos]; pos = (pos + 1) % n; }
            System.Runtime.InteropServices.Marshal.Copy(tmp, 0, buf, free * 2);
            rc.ReleaseBuffer((uint)free, 0);
        }
        ac.Stop();
    }
}
