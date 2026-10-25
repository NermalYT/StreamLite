// Windows' per-app capture while the app pauses: packets keep coming (silent), so frame counts stay continuous.
using System; using System.Diagnostics; using System.IO; using System.Linq; using System.Runtime.InteropServices; using System.Threading; using StreamLite.Interop;
class PauseProbe { [MTAThread] static void Main() {
  var play = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SinkPlay.exe"), "6 0.0003 \"Speakers (Realtek\" pause") { UseShellExecute = false, CreateNoWindow = true });
  Thread.Sleep(800);
  var ac = ProcessLoopback.Open(play.Id); IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, 48000, true, 32);
  Console.WriteLine("init 0x" + ac.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero).ToString("X8"));
  object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref iid, out svc); var cap = (IAudioCaptureClient)svc; ac.Start();
  var sw = Stopwatch.StartNew(); long total = 0; double lastT = 0; long lastP = -1; var buf = new float[48000*8];
  while (sw.Elapsed.TotalSeconds < 5) { Thread.Sleep(2); uint pk; cap.GetNextPacketSize(out pk);
    while (pk > 0) { IntPtr d; uint fr, fl; ulong p1, p2; cap.GetBuffer(out d, out fr, out fl, out p1, out p2); 
      Marshal.Copy(d, buf, 0, (int)fr*8); double e=0; for (int i=0;i<fr*8;i++) e+=buf[i]*buf[i];
      double t = sw.Elapsed.TotalMilliseconds;
      if (t - lastT > 30 || fl != 0 || (lastP >= 0 && (long)p1 != lastP)) Console.WriteLine(t.ToString("0") + " ms: frames " + fr + " pos " + p1 + " (expected " + lastP + ") flags " + fl + " energy " + (e > 0 ? "sound" : "zero") + " gap since last packet " + (t-lastT).ToString("0") + " ms");
      lastT = t; lastP = (long)p1 + fr; total += fr; cap.ReleaseBuffer(fr); cap.GetNextPacketSize(out pk); } }
  Console.WriteLine("total frames " + total + " in 5 s (" + (total/48000.0).ToString("0.00") + " s)"); try { play.Kill(); } catch {} } }
