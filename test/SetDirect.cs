using System; using StreamLite;
// Sets Direct to headphones in the real settings through StreamLite's own loader (the app must be closed).
// Usage: SetDirect on|off|reset [3D margin frames]   (reset = off and not asked yet, so the first-start question comes)
class SetDirect { static void Main(string[] a) {
  var s = AppSettings.Load();
  s.Game.Exclusive = a[0] == "on"; s.ExclusiveAsked = a[0] != "reset";
  if (a.Length > 1) s.Game.LearnedMargin = int.Parse(a[1]);   // [3D margin frames]: put a learned buffer back
  s.Save(); Console.WriteLine("Direct to headphones " + (s.Game.Exclusive ? "on" : "off") + ", asked " + s.ExclusiveAsked); } }
