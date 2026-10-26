using System; using System.Linq; using StreamLite.Interop; using StreamLite.Audio;
class FamilyProbe { static void Main() {
  var outs = AudioDevices.List(EDataFlow.Render);
  var par = ProcessLoopback.Parents();
  foreach (var d in outs) {
    var all = ProcessLoopback.Sessions(d.Id); var hot = ProcessLoopback.Sessions(d.Id, true);
    Console.WriteLine(d.Name + ": " + string.Join(", ", all.Select(k => k.Value + "#" + k.Key + (hot.ContainsKey(k.Key) ? "*" : "") + "(parent " + (par.ContainsKey(k.Key) ? par[k.Key] : 0) + ")")));
  }
  foreach (var p in System.Diagnostics.Process.GetProcessesByName("DiscordPTB")) Console.WriteLine("DiscordPTB pid " + p.Id + " parent " + (par.ContainsKey(p.Id) ? par[p.Id] : 0));
} }
