using System; using StreamLite.Audio;
class T { static void Main() {
  int fs = 48000, n = fs * 12; var rnd = new Random(2); var x = new float[n];
  // 2 s silence (noise -70 dBFS), then alternating 1.5 s "speech" (-45 dBFS RMS, modulated) / 0.5 s pause.
  for (int i = 0; i < n; i++) {
    double noise = (rnd.NextDouble() * 2 - 1) * 0.0005;
    double t = (double)i / fs; bool talk = t > 2 && ((t - 2) % 2.0) < 1.5;
    double env = 0.5 + 0.5 * Math.Abs(Math.Sin(2 * Math.PI * 3 * t));
    double v = talk ? 0.0079 * Math.Sqrt(2) * env * Math.Sin(2 * Math.PI * 180 * t) * 1.2 : 0;
    x[i] = (float)(v + noise); }
  var agc = new AutoLevel(fs); var y = (float[])x.Clone();
  var blk = new float[480]; for (int i = 0; i + 480 <= n; i += 480) { Array.Copy(y, i, blk, 0, 480); agc.Process(blk, 480); Array.Copy(blk, 0, y, i, 480); }
  Func<float[],double,double,double> rms = (a,s,e) => { double q=0; int c=0; for (int i=(int)(s*fs); i<(int)(e*fs); i++) { q+=a[i]*a[i]; c++; } return 20*Math.Log10(Math.Sqrt(q/c)+1e-12); };
  Console.WriteLine("silence before talking: in " + rms(x,0.5,1.9).ToString("0.0") + " out " + rms(y,0.5,1.9).ToString("0.0") + " dB");
  Console.WriteLine("first word:             in " + rms(x,2.1,3.4).ToString("0.0") + " out " + rms(y,2.1,3.4).ToString("0.0") + " dB");
  Console.WriteLine("later speech (8-9.4s):  in " + rms(x,8.1,9.4).ToString("0.0") + " out " + rms(y,8.1,9.4).ToString("0.0") + " dB");
  Console.WriteLine("pause (9.6-9.9s):       in " + rms(x,9.6,9.95).ToString("0.0") + " out " + rms(y,9.6,9.95).ToString("0.0") + " dB");
  Console.WriteLine("final gain: +" + agc.CurrentGainDb.ToString("0.0") + " dB");
}
  static float[] buf = new float[480]; static float[] src; static int off;
  static float[] Sub(float[] a, int o, int c) { src = a; off = o; return new Wrap(a, o).Arr; }
  class Wrap { public float[] Arr; public Wrap(float[] a, int o) { Arr = a; } }
}

