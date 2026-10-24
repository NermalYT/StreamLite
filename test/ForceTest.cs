using System; using System.Windows.Forms; using StreamLite.Interop; using StreamLite.Camera;
class F { [STAThread] static void Main() {
  var form = new Form { Width = 480, Height = 300, Text = "Broadcast Lite camera test", StartPosition = FormStartPosition.CenterScreen };
  form.Show();
  int[][] req = { new[]{3840,2160,60}, new[]{2560,1440,30}, new[]{1920,1080,60}, new[]{1920,1080,30}, new[]{1280,720,30} };
  foreach (var d in VideoDevices.List()) {
    Console.WriteLine("== " + d.Name);
    foreach (var r in req) {
      var p = new CameraPreview();
      string err = p.Start(d, form, r[0], r[1], r[2]);
      Wait(1500); p.SampleFps(); Wait(2000); double fps = p.SampleFps();
      Console.WriteLine(string.Format("  ask {0}x{1}@{2}: {3}", r[0], r[1], r[2],
        err != null ? "ERROR " + err : (p.RequestRefused ? "REFUSED -> fell back to " : "accepted, streaming ") + (p.ActiveMode != null ? p.ActiveMode.Width + "x" + p.ActiveMode.Height + " " + p.ActiveMode.Subtype : "?") + ", measured " + fps.ToString("0.0") + " fps"));
      p.Dispose();
    }
  }
  form.Close(); }
  static void Wait(int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
}

