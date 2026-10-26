using System; using StreamLite.Audio;
// The Fft class at a size that isn't a power of two (mixed-radix path): forward vs a plain DFT, and forward + inverse = n * x.
class FftCheck { static void Main() { foreach (int n in new[] { 480, 512 }) { var rnd = new Random(1); var re = new float[n]; var im = new float[n]; var x = new float[n];
  for (int i = 0; i < n; i++) { x[i] = (float)(rnd.NextDouble() * 2 - 1); re[i] = x[i]; }
  var f = new Fft(n); f.Transform(re, im, false);
  double err = 0, mag = 0; for (int k = 0; k < n; k += 7) { double sr = 0, si = 0; for (int i = 0; i < n; i++) { double a = -2 * Math.PI * k * i / n; sr += x[i] * Math.Cos(a); si += x[i] * Math.Sin(a); } err = Math.Max(err, Math.Abs(sr - re[k]) + Math.Abs(si - im[k])); mag = Math.Max(mag, Math.Abs(sr)); }
  f.Transform(re, im, true); double rt = 0; for (int i = 0; i < n; i++) rt = Math.Max(rt, Math.Abs(re[i] / n - x[i]) + Math.Abs(im[i] / n));
  Console.WriteLine(n + ": forward vs DFT " + (err / mag).ToString("E1") + " (relative), round trip " + rt.ToString("E1")); } } }
