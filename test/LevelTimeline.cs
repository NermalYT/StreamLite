// include: DnsmosRun.cs
using System; using System.IO;
// Level of a WAV every half second (dBFS RMS and peak). Usage: LevelTimeline <wav>
class LevelTimeline { static void Main(string[] a) {
  var x = DnsmosRun.ReadWav(a[0]); var sb = new System.Text.StringBuilder();
  for (int i = 0; i + 24000 <= x.Length; i += 24000) { double p = 0, pk = 0; for (int k = i; k < i + 24000; k++) { p += x[k] * x[k]; pk = Math.Max(pk, Math.Abs(x[k])); }
    sb.Append((i / 48000.0).ToString("0.0") + "s " + (10 * Math.Log10(p / 24000 + 1e-20)).ToString("0") + "/" + (20 * Math.Log10(pk + 1e-12)).ToString("0") + "  "); }
  Console.WriteLine(sb); } }
