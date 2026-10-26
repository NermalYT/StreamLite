using System; using System.Linq; using StreamLite.Interop;
// Sets an input's Windows volume to 100 (the setup check's fix for "StreamLite - In"). Usage: InputTo100 "StreamLite - In"
class InputTo100 { static void Main(string[] a) { var d = AudioDevices.List(EDataFlow.Capture).First(x => x.Name.StartsWith(a[0])); AudioDevices.SetVolume(d.Id, 1f); float v, db; AudioDevices.GetVolume(d.Id, out v, out db); Console.WriteLine(d.Name + ": " + (v * 100).ToString("0") + " (" + db.ToString("0.0") + " dB)"); } }
