using System; using System.Collections.Generic; using System.Linq; using System.Runtime.InteropServices; using System.Threading; using StreamLite.Interop;
// Correlates the loudness envelope of what's playing on the headphones with the Yeti's input.
class B { static void Main() {
  var mic = AudioDevices.List(EDataFlow.Capture).First(x => x.Name.Contains("Yeti"));
  var spk = AudioDevices.List(EDataFlow.Render).First(x => x.Name.Contains("QCC"));
  List<double> a = null, b = null;
  var t1 = new Thread(() => a = Env(mic.Id, false)); var t2 = new Thread(() => b = Env(spk.Id, true));
  t1.Start(); t2.Start(); t1.Join(); t2.Join();
  int n = Math.Min(a.Count, b.Count);
  Console.WriteLine("headphone playback level avg " + (b.Take(n).Average()).ToString("0.0") + " dB, mic avg " + a.Take(n).Average().ToString("0.0") + " dB, blocks " + n);
  double best = -1; int bestLag = 0;
  for (int lag = 0; lag <= 12; lag++) { // 0..240 ms
    var x = b.Take(n - lag).ToArray(); var y = a.Skip(lag).Take(n - lag).ToArray();
    double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
    for (int i = 0; i < x.Length; i++) { sxy += (x[i]-mx)*(y[i]-my); sxx += (x[i]-mx)*(x[i]-mx); syy += (y[i]-my)*(y[i]-my); }
    double r = sxy / Math.Sqrt(sxx * syy + 1e-12); if (r > best) { best = r; bestLag = lag; } }
  Console.WriteLine("correlation " + best.ToString("0.00") + " at " + (bestLag * 20) + " ms delay  (0 = unrelated, >0.5 = mic is hearing your headphones)");
}
  static List<double> Env(string id, bool loopback) {
    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDevice(id, out dev);
    object o; Guid iid = CoreAudioConst.IID_IAudioClient; dev.Activate(ref iid, 23, IntPtr.Zero, out o); var ac = (IAudioClient)o;
    IntPtr mix; ac.GetMixFormat(out mix); var wf = (WaveFormatEx)Marshal.PtrToStructure(mix, typeof(WaveFormatEx));
    ac.Initialize(0, loopback ? 0x20000 : 0, 1000000, 0, mix, IntPtr.Zero); object svc; Guid cid = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref cid, out svc); var cap = (IAudioCaptureClient)svc;
    int ch = wf.nChannels, blk = wf.nSamplesPerSec / 50; var env = new List<double>(); double sq = 0; int c = 0; var tmp = new float[192000 * 8];
    ac.Start(); var end = DateTime.Now.AddSeconds(10);
    while (DateTime.Now < end) { Thread.Sleep(5); uint pk; cap.GetNextPacketSize(out pk);
      while (pk > 0) { IntPtr p; uint fr, fl; ulong x1, x2; cap.GetBuffer(out p, out fr, out fl, out x1, out x2); Marshal.Copy(p, tmp, 0, (int)fr * ch); cap.ReleaseBuffer(fr);
        for (int i = 0; i < fr; i++) { double v = tmp[i * ch]; sq += v * v; if (++c == blk) { env.Add(10 * Math.Log10(sq / c + 1e-12)); sq = 0; c = 0; } } cap.GetNextPacketSize(out pk); } }
    ac.Stop(); return env; }
}
