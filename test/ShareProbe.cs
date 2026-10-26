// What a Discord screen share would send while StreamLite runs. Discord takes stream sound with Windows' per-app
// capture: a window share = that app (and what it started), a whole-screen share = every app except Discord. This
// starts a stand-in app playing a 1234 Hz tone (by default to the Windows default output; "as chrome" = a copy named
// chrome.exe, which StreamLite routes like a browser) and captures at the same time:
//   window share  - the app only
//   screen share  - everything except one other process (as Discord excludes itself)
//   StreamLite    - what StreamLite itself plays (in a screen share that's a second, processed copy of the same sound)
// Usage: ShareProbe [chrome]      (internally: ShareProbe play <seconds>)
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

class ShareProbe
{
    const double Tone = 1234; const int Fs = 48000;

    static void Play(int secs)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms); int n = Fs * secs;
        w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)Math.Round(32767 * 0.03 * Math.Sin(2 * Math.PI * Tone * i / Fs)));   // -30 dBFS
        ms.Position = 0;
        using (var p = new SoundPlayer(ms)) p.PlaySync();
    }

    sealed class Cap
    {
        public string Name; public int Pid; public bool Exclude; public string Error;
        public double ToneEnergy, AllEnergy; public long Frames;
        double cr, ci, prevR, prevI; // not used
        public void Run(int ms)
        {
            IAudioClient c = null;
            try
            {
                c = ProcessLoopback.Open(Pid, Exclude);
                IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
                int hr = c.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 2000000, 0, fmt, IntPtr.Zero);
                Marshal.FreeHGlobal(fmt);
                if (hr != 0) { Error = "init 0x" + hr.ToString("X8"); return; }
                object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                c.GetService(ref iid, out svc);
                var cap = (IAudioCaptureClient)svc;
                c.Start();
                var buf = new float[Fs * 2];
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    Thread.Sleep(5);
                    uint packet;
                    while (cap.GetNextPacketSize(out packet) == 0 && packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong p1, p2;
                        if (cap.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) break;
                        int n = (int)Math.Min(frames, (uint)Fs);
                        bool silent = (flags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0;
                        if (!silent) Marshal.Copy(data, buf, 0, n * 2);
                        for (int i = 0; i < n; i++)
                        {
                            double x = silent ? 0 : (buf[2 * i] + buf[2 * i + 1]) * 0.5;
                            double ph = 2 * Math.PI * Tone * (Frames + i) / Fs;
                            cr += x * Math.Cos(ph); ci += x * Math.Sin(ph);
                            AllEnergy += x * x;
                            if (((Frames + i) % 4800) == 4799) { ToneEnergy += cr * cr + ci * ci; cr = ci = 0; }   // 100 ms blocks
                        }
                        Frames += n;
                        cap.ReleaseBuffer(frames);
                    }
                }
                c.Stop();
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { if (c != null) Marshal.ReleaseComObject(c); }
        }
        // Tone level in dBFS (sine amplitude) from the 100 ms blocks; broadband RMS in dBFS.
        public string Report()
        {
            if (Error != null) return Name + ": capture failed - " + Error;
            double blocks = Math.Max(1, Frames / 4800.0);
            double amp = 2 * Math.Sqrt(ToneEnergy / blocks) / 4800;
            double rms = Math.Sqrt(AllEnergy / Math.Max(1, Frames));
            return string.Format("{0,-34} tone {1,6:0.0} dBFS   everything {2,6:0.0} dBFS   ({3:0.0} s captured)", Name + ":", 20 * Math.Log10(amp + 1e-9), 20 * Math.Log10(rms * Math.Sqrt(2) + 1e-9), Frames / (double)Fs);
        }
    }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "play") { Play(int.Parse(args[1])); return; }
        string self = Process.GetCurrentProcess().MainModule.FileName, exe = self;
        if (args.Contains("chrome"))
        {
            string dir = Path.Combine(Path.GetTempPath(), "ShareProbe-chrome"); Directory.CreateDirectory(dir);
            exe = Path.Combine(dir, "chrome.exe"); File.Copy(self, exe, true);
        }
        var sl = Process.GetProcessesByName("StreamLite").FirstOrDefault();
        var other = Process.GetProcessesByName("explorer").First();
        var player = Process.Start(new ProcessStartInfo(exe, "play 7") { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(2500);   // StreamLite routes a new app within a few seconds (Windows applies it once the app plays)
        var caps = new[]
        {
            new Cap { Name = "window share (the app)", Pid = player.Id },
            new Cap { Name = "screen share (all but one app)", Pid = other.Id, Exclude = true },
            sl != null ? new Cap { Name = "StreamLite's own sound", Pid = sl.Id } : null,
        }.Where(c => c != null).ToArray();
        var threads = caps.Select(c => new Thread(() => c.Run(3000))).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        player.WaitForExit(10000);
        Console.WriteLine("stand-in app: " + Path.GetFileName(exe) + " playing a -30 dBFS tone; Windows default output: " + AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console));
        foreach (var c in caps) Console.WriteLine("  " + c.Report());
    }
}
