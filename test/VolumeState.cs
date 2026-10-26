using System; using System.Linq; using StreamLite.Interop;
// Windows volume (and mute) of every input and output: the virtual mic's input must be at 100 for apps to hear it at full level.
class VolumeState { static void Main() { foreach (var flow in new[] { EDataFlow.Capture, EDataFlow.Render }) foreach (var d in AudioDevices.List(flow)) { float v, db; AudioDevices.GetVolume(d.Id, out v, out db); Console.WriteLine((flow == EDataFlow.Capture ? "in  " : "out ") + d.Name + ": " + (v * 100).ToString("0") + " (" + db.ToString("0.0") + " dB), muted " + AudioDevices.GetMute(d.Id)); } } }
