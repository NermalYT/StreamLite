using System; using StreamLite.Audio;
class T { static void Main() {
  var rnd = new Random(1); int fs = 48000, n = fs * 6;
  var sig = new float[n]; var noise = new float[n];
  for (int i = 0; i < n; i++) { noise[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.02f; bool speech = (i / (fs/2)) % 2 == 1 && i > fs*3; sig[i] = speech ? (float)(0.3 * Math.Sin(2*Math.PI*220*i/fs) + 0.15*Math.Sin(2*Math.PI*880*i/fs)) : 0; }
  // Passthrough reconstruction check (suppressor disabled)
  var ns = new NoiseSuppressor { Enabled = false, UseAi = false }; /* classic engine: fixed 1024-sample delay */ var b = new float[n]; for (int i=0;i<n;i++) b[i]=sig[i]+noise[i];
  var orig = (float[])b.Clone(); ns.Process(b, n); double err=0; for (int i=2048;i<n;i++) err=Math.Max(err,Math.Abs(b[i]-orig[i-1024])); Console.WriteLine("bypass max reconstruction error: " + err.ToString("E2"));
  // Noise-only region reduction (seconds 1-3) and tone preservation (speech bursts after 3s)
  ns = new NoiseSuppressor { Enabled = true, Strength = 0.7f, UseAi = false }; for (int i=0;i<n;i++) b[i]=sig[i]+noise[i]; ns.Process(b, n);
  Func<float[],int,int,double> rms = (a,s,e)=>{double q=0;for(int i=s;i<e;i++)q+=a[i]*a[i];return Math.Sqrt(q/(e-s));};
  Console.WriteLine("noise-only in/out dB: " + (20*Math.Log10(rms(orig,fs,3*fs))).ToString("0.0") + " / " + (20*Math.Log10(rms(b,fs+1024,3*fs+1024))).ToString("0.0"));
  int s0 = 3*fs + fs/2 + 4800, e0 = 4*fs - 4800;
  Console.WriteLine("speech burst in/out dB: " + (20*Math.Log10(rms(orig,s0,e0))).ToString("0.0") + " / " + (20*Math.Log10(rms(b,s0+1024,e0+1024))).ToString("0.0"));
  var g = new NoiseGate(fs) { Enabled = true, ThresholdDb = -30 }; var c = new float[n]; Array.Copy(orig, c, n); g.Process(c, n);
  Console.WriteLine("gate noise-only out dB: " + (20*Math.Log10(rms(c,fs,3*fs)+1e-9)).ToString("0.0") + ", speech out dB: " + (20*Math.Log10(rms(c,s0,e0))).ToString("0.0"));
}}
