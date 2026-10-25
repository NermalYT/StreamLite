// Per-app capture of an app on a muted output (or volume 0) still has its sound: the side output can be muted.
using System; using System.Diagnostics; using System.IO; using System.Linq; using System.Runtime.InteropServices; using System.Threading; using StreamLite.Interop;
class MuteProbe { [MTAThread] static void Main() {
  var d = AudioDevices.List(EDataFlow.Render).First(x => x.Name.StartsWith("Speakers (Realtek"));
  bool? was = AudioDevices.GetMute(d.Id); float v0, vdb; AudioDevices.GetVolume(d.Id, out v0, out vdb);
  try {
    foreach (int mode in new[] { 0, 1, 2 }) {   // 0 as is, 1 muted, 2 volume 0
      AudioDevices.SetMute(d.Id, mode == 1); AudioDevices.SetVolume(d.Id, mode == 2 ? 0f : v0);
      var play = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SinkPlay.exe"), "4 0.0003 \"Speakers (Realtek\"") { UseShellExecute = false, CreateNoWindow = true });
      Thread.Sleep(700);
      var ac = ProcessLoopback.Open(play.Id); IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
      ac.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero);
      object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref iid, out svc); var cap = (IAudioCaptureClient)svc; ac.Start();
      var sw = Stopwatch.StartNew(); double e = 0; long n = 0; var buf = new float[48000*2];
      while (sw.Elapsed.TotalSeconds < 1.5) { Thread.Sleep(3); uint pk; cap.GetNextPacketSize(out pk);
        while (pk > 0) { IntPtr p; uint fr, fl; ulong a, b; cap.GetBuffer(out p, out fr, out fl, out a, out b); if ((fl & 2) == 0) { Marshal.Copy(p, buf, 0, (int)fr*2); for (int i=0;i<fr*2;i++) e += buf[i]*buf[i]; } n += fr*2; cap.ReleaseBuffer(fr); cap.GetNextPacketSize(out pk); } }
      Console.WriteLine((mode == 0 ? "as is   " : mode == 1 ? "muted   " : "volume 0") + ": per-app capture " + (10*Math.Log10(e/Math.Max(1,n)+1e-30)).ToString("0") + " dB");
      try { play.Kill(); } catch {} Thread.Sleep(300);
    }
  } finally { AudioDevices.SetMute(d.Id, was == true); AudioDevices.SetVolume(d.Id, v0); } } }
