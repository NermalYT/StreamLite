using System; using System.Linq; using StreamLite.Interop;
class ListCap { static void Main() { foreach (var d in AudioDevices.List(EDataFlow.Capture)) Console.WriteLine(d.Name + "  |  " + d.Id); } }
