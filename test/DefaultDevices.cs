// The Windows default playback and recording devices per role (console, multimedia, communications).
using System; using System.Linq; using StreamLite.Interop;
class DefaultDevices { static void Main() { var outs = AudioDevices.List(EDataFlow.Render);
  foreach (ERole r in new[] { ERole.Console, ERole.Multimedia, ERole.Communications }) { var id = AudioDevices.GetDefaultId(EDataFlow.Render, r); var d = outs.FirstOrDefault(x => x.Id == id); Console.WriteLine("playback " + r + ": " + (d != null ? d.Name : id)); }
  var ins = AudioDevices.List(EDataFlow.Capture);
  foreach (ERole r in new[] { ERole.Console, ERole.Communications }) { var id = AudioDevices.GetDefaultId(EDataFlow.Capture, r); var d = ins.FirstOrDefault(x => x.Id == id); Console.WriteLine("recording " + r + ": " + (d != null ? d.Name : id)); } } }
