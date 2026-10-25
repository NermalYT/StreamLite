// Prints every output's Windows mute (live check that Direct to headphones gave the side output back).
using System; using System.Linq; using StreamLite.Interop;
class MuteState { static void Main() { foreach (var d in AudioDevices.List(EDataFlow.Render)) Console.WriteLine(d.Name + ": muted " + AudioDevices.GetMute(d.Id)); } }
