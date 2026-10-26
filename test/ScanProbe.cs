using System; using System.Diagnostics; using System.Linq; using StreamLite; using StreamLite.Interop; using StreamLite.Audio;
// How long the mixer's app scan takes (it ran on the thread that moves the sound). Usage: ScanProbe
class ScanProbe { static void Main() {
  var dev = AudioDevices.List(EDataFlow.Render).First(d => d.Name.Contains("Realtek")).Id;
  var sw = new Stopwatch(); double[] a = new double[20], b = new double[20], c = new double[20];
  for (int i = 0; i < 20; i++) {
    sw.Restart(); var s = ProcessLoopback.Sessions(dev); a[i] = sw.Elapsed.TotalMilliseconds;
    sw.Restart(); var p = ProcessLoopback.Parents(); b[i] = sw.Elapsed.TotalMilliseconds;
    sw.Restart(); foreach (var pid in s.Keys) try { using (var pr = Process.GetProcessById(pid)) { bool x = pr.HasExited; } } catch { } c[i] = sw.Elapsed.TotalMilliseconds;
    System.Threading.Thread.Sleep(250); }
  Console.WriteLine("sessions " + a.Average().ToString("0.0") + " ms (max " + a.Max().ToString("0.0") + "), process tree " + b.Average().ToString("0.0") + " ms (max " + b.Max().ToString("0.0") + "), exit checks " + c.Average().ToString("0.0") + " ms (max " + c.Max().ToString("0.0") + ")"); } }
