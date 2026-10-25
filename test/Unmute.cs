// Unmutes an output by name start. Usage: Unmute "Speakers (Realtek"
using System; using System.Linq; using StreamLite.Interop;
class Unmute { static void Main(string[] a) { var d = AudioDevices.List(EDataFlow.Render).First(x => x.Name.StartsWith(a[0])); AudioDevices.SetMute(d.Id, false); Console.WriteLine(d.Name + ": muted " + AudioDevices.GetMute(d.Id)); } }
