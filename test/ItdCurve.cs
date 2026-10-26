using System; using StreamLite.Audio;
class ItdCurve { static void Main(string[] a) { var set = HrtfSet.Get(a[0], null); var sb = new System.Text.StringBuilder();
  var itdM = typeof(HrtfSet).GetMethod("Itd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
  for (int az = 0; az <= 90; az += 1) { float[] l, r; set.BuildPrecise(az, 0, 0, out l, out r); double itd = (double)itdM.Invoke(null, new object[] { l, r }); sb.Append(az + ":" + itd.ToString("0.0") + " "); }
  Console.WriteLine(sb); sb.Clear();
  for (int i = 0; i < set.Az.Length; i++) if (Math.Abs(set.El[i]) < 1 && set.Az[i] <= 100) { double itd = (double)itdM.Invoke(null, new object[] { set.L[i], set.R[i] }); sb.Append(set.Az[i] + ":" + itd.ToString("0.0") + " "); }
  Console.WriteLine("measured: " + sb); } }
