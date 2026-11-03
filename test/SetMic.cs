using System; using System.Reflection; using StreamLite;
// Sets top-level settings (the mic's) in a test copy of the settings through StreamLite's own loader (4.2.1: never your
// own - it needs STREAMLITE_DATA, which run.ps1 sets to the test copy).
// Usage: run.ps1 SetMic Name=value [Name=value...]   e.g. run.ps1 SetMic NsRealtime=false Gate=false   (no value: prints it)
class SetMic { static void Main(string[] a) {
  if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STREAMLITE_DATA"))) { Console.WriteLine("SetMic changes only a test copy: set STREAMLITE_DATA (run.ps1 does)"); Environment.Exit(1); }
  var s = AppSettings.Load();
  foreach (var arg in a) {
    int eq = arg.IndexOf('='); string name = eq < 0 ? arg : arg.Substring(0, eq);
    var f = typeof(AppSettings).GetField(name); var p = f == null ? typeof(AppSettings).GetProperty(name) : null;
    if (f == null && p == null) { Console.WriteLine("no setting " + name); Environment.Exit(1); }
    Type t = f != null ? f.FieldType : p.PropertyType;
    if (eq >= 0) {
      object v = Convert.ChangeType(arg.Substring(eq + 1), t, System.Globalization.CultureInfo.InvariantCulture);
      if (f != null) f.SetValue(s, v); else p.SetValue(s, v, null);
    }
    Console.WriteLine(name + " = " + (f != null ? f.GetValue(s) : p.GetValue(s, null)));
  }
  if (Array.Exists(a, x => x.Contains("="))) s.Save(); } }
