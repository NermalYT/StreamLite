using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Audio
{
    internal sealed class Fft
    {
        readonly int n;
        readonly int[] rev;
        readonly float[] cos, sin;

        public Fft(int size)
        {
            n = size;
            rev = new int[n];
            int bits = (int)Math.Round(Math.Log(n, 2));
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                rev[i] = r;
            }
            cos = new float[n / 2];
            sin = new float[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                cos[i] = (float)Math.Cos(2 * Math.PI * i / n);
                sin[i] = (float)Math.Sin(2 * Math.PI * i / n);
            }
        }

        public void Transform(float[] re, float[] im, bool inverse)
        {
            for (int i = 0; i < n; i++)
            {
                int j = rev[i];
                if (j > i)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            float sign = inverse ? 1f : -1f;
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1, step = n / size;
                for (int start = 0; start < n; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = cos[k * step], wi = sign * sin[k * step];
                        int a = start + k, b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
        }
    }

    /// <summary>One EQ filter in Equalizer APO terms: PK, LSC, HSC, LP, HP.</summary>
    public class EqFilter
    {
        public string Type = "PK";
        public double Freq = 1000;
        public double Gain = 0;
        public double Q = 1.41;
        public bool On = true;

        public EqFilter Copy() { return new EqFilter { Type = Type, Freq = Freq, Gain = Gain, Q = Q, On = On }; }
    }

    public class EqProfile
    {
        public static readonly double[] BandFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        /// <summary>ISO 1/3-octave centers for the 31-point fine EQ.</summary>
        public static readonly double[] FineFreqs =
        {
            20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
            1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
        };
        public const double FineQ = 3.0;
        public bool Enabled = true;
        public bool AutoPreamp = true;
        public double Preamp = 0;
        public string Mode = "graphic";                         // graphic | fine | parametric
        public double[] Bands = new double[10];
        public double[] Fine = new double[31];
        public List<EqFilter> Parametric = new List<EqFilter>();
        public List<EqFilter> Extra = new List<EqFilter>();      // imported correction, applies in both modes
        public string ExtraName = "";

        public bool IsParametric { get { return Mode == "parametric"; } }
        public bool IsFine { get { return Mode == "fine"; } }

        public List<EqFilter> AllFilters()
        {
            var list = new List<EqFilter>();
            if (IsParametric)
            {
                if (Parametric != null) foreach (var f in Parametric) if (f.On) list.Add(f);
            }
            else if (IsFine)
            {
                if (Fine != null)
                    for (int i = 0; i < FineFreqs.Length && i < Fine.Length; i++)
                        if (Math.Abs(Fine[i]) > 0.01) list.Add(new EqFilter { Type = "PK", Freq = FineFreqs[i], Gain = Fine[i], Q = FineQ });
            }
            else if (Bands != null)
                for (int i = 0; i < BandFreqs.Length && i < Bands.Length; i++)
                    if (Math.Abs(Bands[i]) > 0.01) list.Add(new EqFilter { Type = "PK", Freq = BandFreqs[i], Gain = Bands[i], Q = 1.41 });
            if (Extra != null) foreach (var f in Extra) if (f.On) list.Add(f);
            return list;
        }

        /// <summary>Shifts the EQ so its loudest point is 0 dB: same shape, but cuts instead of boosts (no volume trim needed).</summary>
        public void Normalize(double sampleRate)
        {
            var filters = AllFilters();
            double max = double.MinValue;
            for (int i = 0; i < 200; i++) max = Math.Max(max, Biquad.ResponseDb(filters, 20 * Math.Pow(1000, i / 199.0), sampleRate));
            if (max <= 0.05) return;
            if (IsParametric)
            {
                // Lower every bell/shelf by the peak amount; frequencies no band touches stay at 0 dB.
                foreach (var f in Parametric)
                    if (f.Type == "PK" || f.Type == "LSC" || f.Type == "HSC") f.Gain = Math.Round((f.Gain - max) * 2) / 2;
            }
            else if (IsFine)
                for (int i = 0; i < Fine.Length; i++) Fine[i] = Math.Max(-15, Math.Round((Fine[i] - max) * 10) / 10);
            else
                for (int i = 0; i < Bands.Length; i++) Bands[i] = Math.Max(-12, Math.Round((Bands[i] - max) * 2) / 2);
        }

        /// <summary>Seeds the 31 fine points from the current curve so switching modes keeps the sound.</summary>
        public void SeedFineFromCurve(double sampleRate)
        {
            var filters = AllFilters().Where(f => Extra == null || !Extra.Contains(f)).ToList();
            for (int i = 0; i < FineFreqs.Length; i++) Fine[i] = Math.Round(Biquad.ResponseDb(filters, FineFreqs[i], sampleRate) * 10) / 10;
        }

        public double EffectivePreamp(double sampleRate)
        {
            if (!AutoPreamp) return Preamp;
            double max = 0;
            var filters = AllFilters();
            for (int i = 0; i < 200; i++)
            {
                double f = 20 * Math.Pow(1000, i / 199.0);
                double db = Biquad.ResponseDb(filters, f, sampleRate);
                if (db > max) max = db;
            }
            return -max;
        }

        public EqProfile Clone()
        {
            var p = new EqProfile { Enabled = Enabled, AutoPreamp = AutoPreamp, Preamp = Preamp, ExtraName = ExtraName, Mode = Mode ?? "graphic" };
            p.Bands = (double[])(Bands ?? new double[10]).Clone();
            if (p.Bands.Length != 10) Array.Resize(ref p.Bands, 10);
            p.Fine = (double[])(Fine ?? new double[31]).Clone();
            if (p.Fine.Length != 31) Array.Resize(ref p.Fine, 31);
            if (Extra != null) foreach (var f in Extra) p.Extra.Add(f.Copy());
            if (Parametric != null) foreach (var f in Parametric) p.Parametric.Add(f.Copy());
            return p;
        }

        /// <summary>Fills in fields missing from older settings files.</summary>
        public void Repair()
        {
            if (Bands == null || Bands.Length != 10) { var b = new double[10]; if (Bands != null) Array.Copy(Bands, b, Math.Min(10, Bands.Length)); Bands = b; }
            if (Fine == null || Fine.Length != 31) { var f = new double[31]; if (Fine != null) Array.Copy(Fine, f, Math.Min(31, Fine.Length)); Fine = f; }
            if (Extra == null) Extra = new List<EqFilter>();
            if (Parametric == null) Parametric = new List<EqFilter>();
            if (Mode == null) Mode = "graphic";
        }
    }

    internal sealed class Biquad
    {
        double b0, b1, b2, a1, a2, z1, z2;

        public static Biquad Create(EqFilter f, double fs)
        {
            var q = new Biquad();
            q.Design(f.Type, f.Freq, f.Gain, f.Q, fs);
            return q;
        }

        public static Biquad HighPass(double freq, double fs)
        {
            var q = new Biquad();
            q.Design("HP", freq, 0, 0.707, fs);
            return q;
        }

        void Design(string type, double freq, double gain, double qv, double fs)
        {
            freq = Math.Max(10, Math.Min(freq, fs * 0.49));
            if (qv <= 0) qv = 0.707;
            double A = Math.Pow(10, gain / 40), w0 = 2 * Math.PI * freq / fs;
            double c = Math.Cos(w0), s = Math.Sin(w0), alpha = s / (2 * qv), sa = 2 * Math.Sqrt(A) * alpha;
            double nb0, nb1, nb2, na0, na1, na2;
            switch ((type ?? "PK").ToUpperInvariant())
            {
                case "LSC": case "LS":
                    nb0 = A * ((A + 1) - (A - 1) * c + sa); nb1 = 2 * A * ((A - 1) - (A + 1) * c); nb2 = A * ((A + 1) - (A - 1) * c - sa);
                    na0 = (A + 1) + (A - 1) * c + sa; na1 = -2 * ((A - 1) + (A + 1) * c); na2 = (A + 1) + (A - 1) * c - sa;
                    break;
                case "HSC": case "HS":
                    nb0 = A * ((A + 1) + (A - 1) * c + sa); nb1 = -2 * A * ((A - 1) + (A + 1) * c); nb2 = A * ((A + 1) + (A - 1) * c - sa);
                    na0 = (A + 1) - (A - 1) * c + sa; na1 = 2 * ((A - 1) - (A + 1) * c); na2 = (A + 1) - (A - 1) * c - sa;
                    break;
                case "HP": case "HPQ":
                    nb0 = (1 + c) / 2; nb1 = -(1 + c); nb2 = (1 + c) / 2; na0 = 1 + alpha; na1 = -2 * c; na2 = 1 - alpha;
                    break;
                case "LP": case "LPQ":
                    nb0 = (1 - c) / 2; nb1 = 1 - c; nb2 = (1 - c) / 2; na0 = 1 + alpha; na1 = -2 * c; na2 = 1 - alpha;
                    break;
                case "NOTCH":
                    nb0 = 1; nb1 = -2 * c; nb2 = 1; na0 = 1 + alpha; na1 = -2 * c; na2 = 1 - alpha;
                    break;
                case "BP":
                    nb0 = alpha; nb1 = 0; nb2 = -alpha; na0 = 1 + alpha; na1 = -2 * c; na2 = 1 - alpha;
                    break;
                default:
                    nb0 = 1 + alpha * A; nb1 = -2 * c; nb2 = 1 - alpha * A; na0 = 1 + alpha / A; na1 = -2 * c; na2 = 1 - alpha / A;
                    break;
            }
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        /// <summary>Carries the filter memory over when the EQ is edited live, so changes don't click.</summary>
        public void CopyState(Biquad other) { z1 = other.z1; z2 = other.z2; }

        public float Process(float x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return (float)y;
        }

        public double MagnitudeDb(double f, double fs)
        {
            double w = 2 * Math.PI * f / fs;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
            double dr = 1 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
            double mag = Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            return 20 * Math.Log10(Math.Max(mag, 1e-9));
        }

        public static double ResponseDb(List<EqFilter> filters, double f, double fs)
        {
            double sum = 0;
            foreach (var flt in filters) sum += Create(flt, fs).MagnitudeDb(f, fs);
            return sum;
        }
    }

    /// <summary>Thread-safe EQ: the UI swaps in a new filter chain, the audio thread picks it up.</summary>
    internal sealed class EqChain
    {
        volatile Biquad[] chain = new Biquad[0];
        volatile float preampGain = 1f;
        volatile bool enabled;

        public void Update(EqProfile p, double fs)
        {
            if (p == null) { enabled = false; return; }
            var filters = p.AllFilters();
            var arr = new Biquad[filters.Count];
            var old = chain;
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = Biquad.Create(filters[i], fs);
                if (i < old.Length) arr[i].CopyState(old[i]);
            }
            preampGain = (float)Math.Pow(10, p.EffectivePreamp(fs) / 20);
            chain = arr;
            enabled = p.Enabled;
        }

        public void Process(float[] buf, int count)
        {
            if (!enabled) return;
            var c = chain;
            float g = preampGain;
            for (int i = 0; i < count; i++)
            {
                float x = buf[i] * g;
                for (int k = 0; k < c.Length; k++) x = c[k].Process(x);
                buf[i] = x;
            }
        }
    }

    /// <summary>
    /// Speech-aware noise suppressor (MCRA noise tracking + OM-LSA gain, the family of methods behind the
    /// WebRTC/Speex suppressors). Per frequency it estimates how likely you are talking, learns noise only
    /// where you aren't, and attenuates noise smoothly toward a floor instead of carving it out - which is
    /// what keeps voices from sounding thin, watery or robotic.
    /// </summary>
    internal sealed class NoiseSuppressor
    {
        const int N = 1024, Hop = 256, Bins = N / 2 + 1;
        const double Fs = 48000;
        readonly Fft fft = new Fft(N);
        readonly float[] win = new float[N], inBuf = new float[N], outBuf = new float[N], outQueue = new float[Hop];
        readonly float[] re = new float[N], im = new float[N];
        readonly float[] power = new float[Bins], smoothS = new float[Bins], sMin = new float[Bins], sTmp = new float[Bins], presence = new float[Bins],
            noise = new float[Bins], prevGainSq = new float[Bins], prevPost = new float[Bins], gains = new float[Bins], lastGains = new float[Bins], work = new float[Bins], indicator = new float[Bins];
        int pos, frames, minCount;
        readonly int voiceLo = (int)(80 * N / Fs), voiceHi = (int)(6000 * N / Fs), speechLo = (int)(300 * N / Fs), speechHi = (int)(3000 * N / Fs), fricHi = (int)(9000 * N / Fs);

        public volatile bool Enabled = true;
        public volatile float Strength = 0.6f;      // 0..1 -> how far noise is turned down (6..30 dB)
        public volatile float AdaptSpeed = 0.3f;    // 0..1 -> how fast it learns new background noise
        public volatile float Smoothing = 0.5f;     // 0..1 -> fewer "watery"/chirpy artifacts
        public volatile float VoiceProtect = 0.6f;  // 0..1 -> keep the voice full while talking
        public volatile bool Bypass;                // A/B compare: pass audio through untouched
        public volatile float SpeechPresence;       // 0..1, for the UI
        public SpectrumAnalyzer RawSpectrum;
        public SpectrumAnalyzer NoiseSpectrum;      // what the suppressor currently thinks is noise
        public Action<float[]> GainTap;             // test hook: per-frame gains
        /// <summary>true = AI voice isolation (RNNoise neural net), false = classic statistical suppressor.</summary>
        public volatile bool UseAi = true;

        /// <summary>0 = off, 1 = DeepFilterNet on the CPU, 2 = on the graphics card (falls back to RNNoise while loading or if it fails).</summary>
        public volatile int Dfn;
        /// <summary>
        /// False while no app records the virtual mic: DeepFilterNet is unloaded (frees its video memory / CPU) and
        /// RNNoise keeps the meters and monitor going. Back on as soon as Discord, OBS... start recording.
        /// </summary>
        public volatile bool DfnWanted = true;
        public DeepFilter DfnEngine { get { return dfn; } }
        /// <summary>Offline use (the voice test clip): wait for each frame instead of buffering for a real-time device.</summary>
        public bool Synchronous;
        public void ReleaseEngine() { var d = dfn; dfn = null; if (d != null) d.Dispose(); }
        DeepFilter dfn;
        readonly float[] dfnIn = new float[DeepFilter.Frame], dfnOut = new float[DeepFilter.Frame];
        readonly float[] dfnHist = new float[1024], dfnRe = new float[1024], dfnIm = new float[1024], dfnPow = new float[513], dfnNoise = new float[513], dfnWin = new float[1024];
        readonly Fft dfnFft = new Fft(1024);
        int dfnPos, dfnFrames;

        Rnnoise ai;
        readonly float[] aiIn = new float[Rnnoise.FrameSize], aiOut = new float[Rnnoise.FrameSize], aiNoise = new float[Rnnoise.Bins];
        int aiPos, aiFrames;
        float vadSmooth;

        public NoiseSuppressor()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            for (int k = 0; k < Bins; k++) { prevGainSq[k] = 1; lastGains[k] = 1; }
        }

        /// <summary>Why the noise remover isn't the one you picked right now (null = it is).</summary>
        public volatile string FallbackNote;
        bool gpuBroken, cpuTooSlow;
        int lateBase, slowCheck, lastChoice = -1;

        public void Process(float[] buf, int count)
        {
            // Picking an engine again gives it a fresh chance.
            if (Dfn != lastChoice) { lastChoice = Dfn; gpuBroken = cpuTooSlow = false; FallbackNote = null; }
            int want = UseAi && DfnWanted ? Dfn : 0;
            // A graphics card that can't run DirectML: DeepFilterNet on the CPU keeps the quality you picked.
            if (dfn != null && dfn.Gpu && dfn.Error != null && !gpuBroken)
            {
                gpuBroken = true;
                FallbackNote = "DeepFilterNet couldn't use the graphics card (" + dfn.Error + ") - it runs on the CPU instead.";
            }
            if (want == 2 && gpuBroken) want = 1;
            // A CPU too slow for DeepFilterNet in real time: RNNoise (light AI) instead of choppy audio.
            if (dfn != null && !dfn.Gpu && dfn.Ready && ++slowCheck >= 500)
            {
                slowCheck = 0;
                if (dfn.Late - lateBase > 50) { cpuTooSlow = true; FallbackNote = "DeepFilterNet is too heavy for this CPU (" + dfn.AvgMs.ToString("0.0") + " ms per 10 ms) - using RNNoise instead."; }
                lateBase = dfn.Late;
            }
            if (want == 1 && cpuTooSlow) want = 0;
            if (want == 0 && dfn != null) { dfn.Dispose(); dfn = null; }
            if (want != 0 && (dfn == null || dfn.Gpu != (want == 2))) { if (dfn != null) dfn.Dispose(); dfn = new DeepFilter(want == 2) { Synchronous = Synchronous }; }
            if (dfn != null && dfn.Ready && dfn.Error == null) { ProcessDfn(buf, count); return; }
            if (UseAi) { ProcessAi(buf, count); return; }
            for (int i = 0; i < count; i++)
            {
                inBuf[N - Hop + pos] = buf[i];
                buf[i] = outQueue[pos];
                if (++pos == Hop) { pos = 0; Frame(); }
            }
        }

        void ProcessAi(float[] buf, int count)
        {
            if (ai == null) ai = new Rnnoise();
            for (int i = 0; i < count; i++)
            {
                aiIn[aiPos] = buf[i] * 32768f;
                buf[i] = aiOut[aiPos] * (1f / 32768f);
                if (++aiPos == Rnnoise.FrameSize) { aiPos = 0; AiFrame(); }
            }
        }

        void ProcessDfn(float[] buf, int count)
        {
            for (int i = 0; i < count; i++)
            {
                dfnIn[dfnPos] = buf[i];
                buf[i] = dfnOut[dfnPos];
                if (++dfnPos == DeepFilter.Frame) { dfnPos = 0; DfnFrame(); }
            }
        }

        void DfnFrame()
        {
            bool on = Enabled && !Bypass;
            // Strength: below 30% it limits how far the network turns noise down (12-30 dB); from there on the network
            // runs fully and the strength sets how much quieter pauses get on top (up to 40 dB).
            double s = Strength;
            dfn.AttenLimitDb = s < 0.3 ? (float)(12 + 60 * s) : 100f;
            dfn.CleanupDb = s < 0.3 ? 0f : (float)(40 * (s - 0.3) / 0.7);
            dfn.Process(dfnIn, dfnOut);
            if (!on)
            {
                // Bypassed: the raw input, delayed the same so switching A/B doesn't jump.
                Array.Copy(dfnIn, dfnOut, DeepFilter.Frame);
            }
            float l = dfn.Lsnr;
            float p = Math.Max(0, Math.Min(1, (l + 5) / 20f));
            vadSmooth = p > vadSmooth ? p : 0.92f * vadSmooth + 0.08f * p;
            SpeechPresence = vadSmooth;

            // Spectra for the graph (every other frame): input, and what's around while you're not talking.
            Array.Copy(dfnHist, DeepFilter.Frame, dfnHist, 0, 1024 - DeepFilter.Frame);
            Array.Copy(dfnIn, 0, dfnHist, 1024 - DeepFilter.Frame, DeepFilter.Frame);
            if ((++dfnFrames & 1) != 0 || (RawSpectrum == null && NoiseSpectrum == null)) return;
            if (dfnWin[512] == 0) for (int i = 0; i < 1024; i++) dfnWin[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 1024));
            for (int i = 0; i < 1024; i++) { dfnRe[i] = dfnHist[i] * dfnWin[i]; dfnIm[i] = 0; }
            dfnFft.Transform(dfnRe, dfnIm, false);
            for (int k = 0; k < 513; k++) dfnPow[k] = (dfnRe[k] * dfnRe[k] + dfnIm[k] * dfnIm[k]) * 4f;   // Hann window: x4 so a full-scale sine reads ~0 dB
            float a = p < 0.3f ? 0.85f : p < 0.6f ? 0.97f : 1f;
            for (int k = 0; k < 513; k++)
            {
                if (dfnFrames <= 2) dfnNoise[k] = dfnPow[k];
                else if (dfnPow[k] < dfnNoise[k]) dfnNoise[k] = 0.7f * dfnNoise[k] + 0.3f * dfnPow[k];
                else dfnNoise[k] = a * dfnNoise[k] + (1 - a) * dfnPow[k];
            }
            if (RawSpectrum != null) RawSpectrum.PushPower(dfnPow, 513, 1024);
            if (NoiseSpectrum != null) NoiseSpectrum.PushPower(dfnNoise, 513, 1024);
        }

        void AiFrame()
        {
            bool on = Enabled && !Bypass;
            // Strength: below 50% it limits how far noise is turned down (10..30 dB); 50% is stock RNNoise; above
            // that it pushes harder on whatever the network calls noise, but only when you're not talking.
            // Voice protection keeps a floor in the voice bands while you talk so words never get "underwater".
            // Smoothing = how slowly gains fall (stock RNNoise = about 50%).
            double s = Strength;
            double att = s <= 0.5 ? 10 + 40 * s : 30 + 40 * (s - 0.5);
            float sharpen = s <= 0.5 ? 0f : (float)(3 * (s - 0.5));
            float minGain = (float)Math.Pow(10, -att / 20);
            float voiceFloor = (float)Math.Pow(minGain, 1 - 0.7 * VoiceProtect);
            float decay = (float)(0.3 + 0.55 * Smoothing);
            float vad = ai.ProcessFrame(aiIn, aiOut, minGain, voiceFloor, decay, sharpen, on);
            vadSmooth = vad > vadSmooth ? vad : 0.92f * vadSmooth + 0.08f * vad;
            SpeechPresence = vadSmooth;

            // Background estimate for the graph: learn while you're quiet, follow dips immediately.
            float[] pw = ai.Power;
            float a = vad < 0.3f ? 0.92f : vad < 0.6f ? 0.985f : 1f;
            for (int k = 0; k < Rnnoise.Bins; k++)
            {
                if (aiFrames == 0) aiNoise[k] = pw[k];
                else if (pw[k] < aiNoise[k]) aiNoise[k] = 0.7f * aiNoise[k] + 0.3f * pw[k];
                else aiNoise[k] = a * aiNoise[k] + (1 - a) * pw[k];
            }
            aiFrames++;
            if ((aiFrames & 1) == 0)
            {
                if (RawSpectrum != null) RawSpectrum.PushPower(pw, Rnnoise.Bins, Rnnoise.FftSize);
                if (NoiseSpectrum != null) NoiseSpectrum.PushPower(aiNoise, Rnnoise.Bins, Rnnoise.FftSize);
            }
            if (GainTap != null) GainTap(on ? ai.BinGain : null);
        }

        static double E1(double x)
        {
            if (x <= 1e-10) return 23;
            if (x < 1) return -0.5772156649 - Math.Log(x) + x - x * x / 4 + x * x * x / 18 - x * x * x * x / 96;
            return Math.Exp(-x) / x * (x * x + 2.334733 * x + 0.250621) / (x * x + 3.330657 * x + 1.681534);
        }

        void Frame()
        {
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            for (int k = 0; k < Bins; k++) power[k] = re[k] * re[k] + im[k] * im[k];

            double strength = Strength, smoothing = Smoothing, protect = VoiceProtect;
            double att = 6 + 24 * strength;
            double gMin = Math.Pow(10, -att / 20);
            double alphaD = 0.97 + 0.025 * (1 - AdaptSpeed);        // noise learning (when no speech)
            double alphaDD = 0.92 + 0.07 * smoothing;               // decision-directed smoothing
            double over = 1 + 0.6 * strength;
            int window = (int)(0.6 * Fs / Hop);

            // Smoothed periodogram + running minimum (MCRA).
            minCount++;
            bool resetMin = minCount >= window;
            for (int k = 0; k < Bins; k++)
            {
                float sf = k > 0 && k < Bins - 1 ? 0.25f * power[k - 1] + 0.5f * power[k] + 0.25f * power[k + 1] : power[k];
                if (frames == 0) { smoothS[k] = sf; sMin[k] = sTmp[k] = sf; noise[k] = power[k] + 1e-12f; }
                else
                {
                    smoothS[k] = 0.8f * smoothS[k] + 0.2f * sf;
                    sMin[k] = Math.Min(sMin[k], smoothS[k]);
                    sTmp[k] = Math.Min(sTmp[k], smoothS[k]);
                }
                if (resetMin) { sMin[k] = Math.Min(sTmp[k], smoothS[k]); sTmp[k] = smoothS[k]; }
                indicator[k] = smoothS[k] > 5f * sMin[k] ? 1f : 0f;
            }
            if (resetMin) minCount = 0;
            // Real speech lights up much of the voice band at once; a random noise peak doesn't.
            double lit = 0, litHi = 0;
            for (int k = speechLo; k <= speechHi; k++) lit += indicator[k];
            for (int k = speechHi + 1; k <= fricHi; k++) litHi += indicator[k];
            lit = Math.Max(lit / (speechHi - speechLo + 1), litHi / (fricHi - speechHi));   // voiced sounds or "s"/"sh"
            float attack = lit > 0.12 ? 0.6f : 0.12f;
            for (int k = 0; k < Bins; k++)
            {
                // Detect speech fast (keeps word starts and "s" sounds), forget it slowly (no flicker in pauses).
                presence[k] = indicator[k] > 0 ? (1 - attack) * presence[k] + attack : 0.9f * presence[k];
                double a = alphaD + (1 - alphaD) * presence[k];
                noise[k] = (float)(a * noise[k] + (1 - a) * power[k]) + 1e-12f;
            }

            // How much of the voice band is speech right now.
            double sp = 0;
            for (int k = speechLo; k <= speechHi; k++) sp += presence[k];
            sp /= (speechHi - speechLo + 1);
            SpeechPresence = (float)sp;
            double voiceFloor = sp > 0.35 ? Math.Pow(gMin, 1 - 0.85 * protect) : gMin;

            for (int k = 0; k < Bins; k++)
            {
                double post = Math.Min(1000, power[k] / (noise[k] * over));
                double prio = Math.Max(0.003, alphaDD * prevGainSq[k] * prevPost[k] + (1 - alphaDD) * Math.Max(post - 1, 0));
                double v = prio / (1 + prio) * post;
                double gLsa = Math.Min(1, prio / (1 + prio) * Math.Exp(0.5 * E1(v)));
                prevGainSq[k] = (float)(gLsa * gLsa);
                prevPost[k] = (float)post;
                double p = presence[k];
                double g = Math.Pow(gLsa, p) * Math.Pow(gMin, 1 - p);   // OM-LSA: glide to the floor where there's no speech
                if (k >= voiceLo && k <= voiceHi) g = Math.Max(g, voiceFloor);
                // Time smoothing: where you're talking, open instantly (crisp consonants) and close gently;
                // where it's only noise, move slowly both ways so random noise peaks can't "chirp".
                double rel = 0.6 * smoothing;
                if (p < 0.3) g = (0.5 + 0.4 * smoothing) * lastGains[k] + (0.5 - 0.4 * smoothing) * g;
                else if (g < lastGains[k]) g = rel * lastGains[k] + (1 - rel) * g;
                work[k] = (float)Math.Max(gMin, Math.Min(1, g));
            }
            // Frequency smoothing.
            float fs2 = (float)(0.5 * smoothing);
            for (int k = 0; k < Bins; k++)
            {
                float g = k > 0 && k < Bins - 1 ? (1 - fs2) * work[k] + 0.5f * fs2 * (work[k - 1] + work[k + 1]) : work[k];
                gains[k] = g;
                lastGains[k] = g;
            }
            frames++;
            if ((frames & 1) == 0)
            {
                if (RawSpectrum != null) RawSpectrum.PushPower(power, Bins, N);
                if (NoiseSpectrum != null) NoiseSpectrum.PushPower(noise, Bins, N);
            }

            bool on = Enabled && !Bypass;
            if (GainTap != null) GainTap(on ? gains : null);
            for (int k = 0; k < Bins; k++)
            {
                float g = on ? gains[k] : 1f;
                re[k] *= g; im[k] *= g;
                if (k > 0 && k < N / 2) { re[N - k] = re[k]; im[N - k] = -im[k]; }
            }
            fft.Transform(re, im, true);

            // sqrt-Hann analysis + synthesis at 75% overlap sums to 2.
            float scale = 1f / (N * 2f);
            for (int i = 0; i < N; i++) outBuf[i] += re[i] * scale * win[i];
            Array.Copy(outBuf, 0, outQueue, 0, Hop);
            Array.Copy(outBuf, Hop, outBuf, 0, N - Hop);
            Array.Clear(outBuf, N - Hop, Hop);
            Array.Copy(inBuf, Hop, inBuf, 0, N - Hop);
        }
    }

    /// <summary>Noise gate, or a softer downward expander that turns quiet sounds down gradually.</summary>
    internal sealed class NoiseGate
    {
        readonly float fs;
        float env, gain = 1f;
        int holdLeft;
        bool open = true;

        public volatile bool Enabled;
        public volatile bool Expander;          // false = classic gate
        public volatile float ExpanderRatio = 2.5f;
        public volatile float ThresholdDb = -50f;
        public volatile float AttackMs = 2f;
        public volatile float HoldMs = 200f;
        public volatile float ReleaseMs = 150f;
        public volatile float RangeDb = -80f;
        public volatile bool IsOpen = true;
        public volatile float LevelDb = -100f;   // level the gate sees, for meters

        public NoiseGate(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int count)
        {
            float envAtt = (float)Math.Exp(-1.0 / (0.0005 * fs));
            float envRel = (float)Math.Exp(-1.0 / (0.05 * fs));
            float gAtt = (float)Math.Exp(-1.0 / (Math.Max(0.1f, AttackMs) * 0.001 * fs));
            float gRel = (float)Math.Exp(-1.0 / (Math.Max(1f, ReleaseMs) * 0.001 * fs));
            float thr = ThresholdDb;
            float openLin = (float)Math.Pow(10, thr / 20);
            float closeLin = (float)Math.Pow(10, (thr - 4) / 20);
            float range = RangeDb;
            float closed = (float)Math.Pow(10, range / 20);
            int hold = (int)(HoldMs * 0.001f * fs);
            bool on = Enabled, expander = Expander;
            float ratio = Math.Max(1.1f, ExpanderRatio);
            float peak = 0;
            for (int i = 0; i < count; i++)
            {
                float a = Math.Abs(buf[i]);
                env = a > env ? a + (env - a) * envAtt : a + (env - a) * envRel;
                if (env > peak) peak = env;
                if (env > openLin) { open = true; holdLeft = hold; }
                else if (env < closeLin) { if (holdLeft > 0) holdLeft--; else open = false; }
                float target;
                if (!on || open) target = 1f;
                else if (expander)
                {
                    // Below the threshold: every 1 dB quieter in becomes 'ratio' dB quieter out.
                    float envDb = (float)(20 * Math.Log10(Math.Max(env, 1e-7f)));
                    float gDb = Math.Max(range, (envDb - thr) * (ratio - 1));
                    target = (float)Math.Pow(10, gDb / 20);
                }
                else target = closed;
                gain = target > gain ? target + (gain - target) * gAtt : target + (gain - target) * gRel;
                buf[i] *= gain;
            }
            IsOpen = !on || open;
            LevelDb = (float)(20 * Math.Log10(Math.Max(peak, 1e-6f)));
        }
    }

    /// <summary>Removes mains hum: narrow notches at 50 or 60 Hz and harmonics.</summary>
    internal sealed class DeHum
    {
        public volatile int Hz;               // 0 = off, 50, 60
        int builtHz = -1;
        Biquad[] notches = new Biquad[0];

        public void Process(float[] buf, int count)
        {
            int hz = Hz;
            if (hz != builtHz)
            {
                builtHz = hz;
                var list = new List<Biquad>();
                if (hz > 0) for (int h = 1; h <= 5; h++) list.Add(Biquad.Create(new EqFilter { Type = "NOTCH", Freq = hz * h, Q = 18 }, 48000));
                notches = list.ToArray();
            }
            if (notches.Length == 0) return;
            for (int i = 0; i < count; i++)
            {
                float x = buf[i];
                for (int k = 0; k < notches.Length; k++) x = notches[k].Process(x);
                buf[i] = x;
            }
        }
    }

    /// <summary>Gentle low-pass that trims hiss above the voice.</summary>
    internal sealed class HighCut
    {
        public volatile float Hz;             // 0 = off
        float builtHz = -1;
        Biquad lp1, lp2;

        public void Process(float[] buf, int count)
        {
            float hz = Hz;
            if (hz != builtHz)
            {
                builtHz = hz;
                if (hz > 0 && hz < 20000)
                {
                    var f = new EqFilter { Type = "LP", Freq = hz, Q = 0.707 };
                    var nl1 = Biquad.Create(f, 48000); var nl2 = Biquad.Create(f, 48000);
                    if (lp1 != null) { nl1.CopyState(lp1); nl2.CopyState(lp2); }
                    lp1 = nl1; lp2 = nl2;
                }
                else { lp1 = lp2 = null; }
            }
            if (lp1 == null) return;
            for (int i = 0; i < count; i++) buf[i] = lp2.Process(lp1.Process(buf[i]));
        }
    }

    /// <summary>Compressor: evens out loud and quiet words so your voice sits steady in the mix.</summary>
    internal sealed class Compressor
    {
        readonly float fs;
        double rms, grDb;
        public volatile bool Enabled;
        public volatile float ThresholdDb = -24f;
        public volatile float Ratio = 3f;
        public volatile float AttackMs = 5f;
        public volatile float ReleaseMs = 120f;
        public volatile float MakeupDb;
        public volatile float GainReductionDb;

        public Compressor(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int count)
        {
            if (!Enabled) { GainReductionDb = 0; return; }
            double rmsCoef = 1 - Math.Exp(-1.0 / (0.010 * fs));
            double att = Math.Exp(-1.0 / (Math.Max(0.5, AttackMs) * 0.001 * fs));
            double rel = Math.Exp(-1.0 / (Math.Max(5, ReleaseMs) * 0.001 * fs));
            double thr = ThresholdDb, slope = 1.0 / Math.Max(1, Ratio) - 1, knee = 6, makeup = MakeupDb;
            double maxGr = 0;
            for (int i = 0; i < count; i++)
            {
                double x = buf[i];
                rms += (x * x - rms) * rmsCoef;
                double over = 10 * Math.Log10(rms + 1e-12) - thr;
                double target = over <= -knee / 2 ? 0 : over < knee / 2 ? slope * (over + knee / 2) * (over + knee / 2) / (2 * knee) : slope * over;
                grDb = target < grDb ? target + (grDb - target) * att : target + (grDb - target) * rel;
                buf[i] = (float)(x * Math.Pow(10, (grDb + makeup) / 20));
                if (grDb < maxGr) maxGr = grDb;
            }
            GainReductionDb = (float)maxGr;
        }
    }

    /// <summary>De-esser: turns down harsh "s"/"sh" sounds only while they happen.</summary>
    internal sealed class DeEsser
    {
        readonly float fs;
        Biquad detector, cut;
        float builtFreq = -1, envBand, envFull, cutDb, appliedDb = 1;
        public volatile bool Enabled;
        public volatile float FreqHz = 6500f;
        public volatile float AmountDb = 6f;
        public volatile float ReductionDb;

        public DeEsser(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int count)
        {
            if (!Enabled) { ReductionDb = 0; return; }
            float freq = FreqHz;
            if (freq != builtFreq)
            {
                builtFreq = freq;
                detector = Biquad.Create(new EqFilter { Type = "BP", Freq = freq, Q = 1.0 }, fs);
                appliedDb = 1;
            }
            float att = (float)Math.Exp(-1.0 / (0.001 * fs)), rel = (float)Math.Exp(-1.0 / (0.040 * fs));
            float amount = AmountDb, maxCut = 0;
            for (int i = 0; i < count; i++)
            {
                float x = buf[i];
                float b = Math.Abs(detector.Process(x)), a = Math.Abs(x);
                envBand = b > envBand ? b + (envBand - b) * att : b + (envBand - b) * rel;
                envFull = a > envFull ? a + (envFull - a) * att : a + (envFull - a) * rel;
                if ((i & 31) == 0)
                {
                    // Sibilance = the high band carries a big share of the energy.
                    float ratio = envBand / (envFull + 1e-9f);
                    float target = envBand > 0.002f && ratio > 0.35f ? -Math.Min(amount, (float)(20 * Math.Log10(ratio / 0.35f)) * 2.5f) : 0f;
                    cutDb = target < cutDb ? target : cutDb + (target - cutDb) * 0.15f;
                    if (Math.Abs(cutDb - appliedDb) > 0.25f)
                    {
                        var nc = Biquad.Create(new EqFilter { Type = "PK", Freq = freq, Gain = cutDb, Q = 1.4 }, fs);
                        if (cut != null) nc.CopyState(cut);
                        cut = nc;
                        appliedDb = cutDb;
                    }
                    if (cutDb < maxCut) maxCut = cutDb;
                }
                buf[i] = cut != null ? cut.Process(x) : x;
            }
            ReductionDb = maxCut;
        }
    }

    /// <summary>
    /// Automatic gain: lifts speech toward a target loudness. Gain only changes while you're talking
    /// (level clearly above the tracked background), so silence and room noise aren't pumped up.
    /// </summary>
    internal sealed class AutoLevel
    {
        readonly float fs;
        float rms2 = 1e-8f, floorDb = -70f, gainDb;
        public volatile bool Enabled = true;
        public volatile float TargetDb = -20f;     // speech RMS target
        public volatile float MaxBoostDb = 20f;
        public volatile float CurrentGainDb;

        public AutoLevel(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int count)
        {
            if (count == 0) return;
            float startGain = gainDb;
            if (Enabled)
            {
                float a = (float)Math.Exp(-1.0 / (0.05 * fs)); // 50 ms RMS window
                for (int i = 0; i < count; i++) rms2 = a * rms2 + (1 - a) * buf[i] * buf[i];
                float levelDb = (float)(10 * Math.Log10(rms2 + 1e-12f));
                float block = count / fs;
                // Background tracker: falls quickly, rises slowly (3 dB/s).
                floorDb = levelDb < floorDb ? levelDb : Math.Min(levelDb, floorDb + 3f * block);
                bool speech = levelDb > Math.Max(floorDb + 10f, -65f);
                if (speech)
                {
                    float desired = Math.Max(-6f, Math.Min(MaxBoostDb, TargetDb - levelDb));
                    // Turn down fast (40 dB/s) so shouting isn't blasted; turn up gently (10 dB/s).
                    float rate = desired < gainDb ? 40f : 10f;
                    float step = rate * block;
                    gainDb = desired > gainDb ? Math.Min(desired, gainDb + step) : Math.Max(desired, gainDb - step);
                }
                if (gainDb > MaxBoostDb) gainDb = MaxBoostDb;
            }
            else gainDb = 0;
            CurrentGainDb = gainDb;
            if (Math.Abs(startGain) < 0.01f && Math.Abs(gainDb) < 0.01f) return;
            // Ramp across the block to avoid zipper noise.
            float g0 = (float)Math.Pow(10, startGain / 20), g1 = (float)Math.Pow(10, gainDb / 20);
            for (int i = 0; i < count; i++) buf[i] *= g0 + (g1 - g0) * (i + 1) / count;
        }
    }

    /// <summary>Last ~12 seconds of levels (one entry per ~10 ms block) for the tuning graphs.</summary>
    internal sealed class LevelHistory
    {
        public const int Length = 1200;
        readonly float[] raw = new float[Length], gateIn = new float[Length], clean = new float[Length], output = new float[Length];
        readonly bool[] gateOpen = new bool[Length];
        int pos;
        readonly object gate = new object();

        public LevelHistory() { for (int i = 0; i < Length; i++) raw[i] = gateIn[i] = clean[i] = output[i] = -100; }

        /// <param name="gateInDb">level after noise removal, i.e. what the gate actually listens to</param>
        public void Push(float rawDb, float gateInDb, float cleanDb, float outDb, bool open)
        {
            lock (gate)
            {
                raw[pos] = rawDb; gateIn[pos] = gateInDb; clean[pos] = cleanDb; output[pos] = outDb; gateOpen[pos] = open;
                pos = (pos + 1) % Length;
            }
        }

        public void SnapshotGateInput(float[] dst)
        {
            lock (gate)
                for (int i = 0; i < Length; i++) dst[i] = gateIn[(pos + i) % Length];
        }

        /// <summary>Copies the history oldest-first.</summary>
        public void Snapshot(float[] r, float[] c, float[] o, bool[] g)
        {
            lock (gate)
                for (int i = 0; i < Length; i++)
                {
                    int k = (pos + i) % Length;
                    r[i] = raw[k]; c[i] = clean[k]; o[i] = output[k]; g[i] = gateOpen[k];
                }
        }

        public static float RmsDb(float[] buf, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += buf[i] * buf[i];
            return (float)(10 * Math.Log10(s / Math.Max(1, n) + 1e-12));
        }
    }

    /// <summary>Log-frequency spectrum (dB) for the EQ screen's analyzer.</summary>
    internal sealed class SpectrumAnalyzer
    {
        public const int Points = 120;
        readonly double fs;
        readonly float[] db = new float[Points];
        readonly object gate = new object();
        Fft fft;
        float[] re, im, win, ring;
        int ringPos, sinceLast;

        public SpectrumAnalyzer(double sampleRate)
        {
            fs = sampleRate;
            for (int i = 0; i < Points; i++) db[i] = -120;
        }

        public static double PointFreq(double i) { return 20 * Math.Pow(1000, i / (double)(Points - 1)); }

        public void PushPower(float[] power, int bins, int n)
        {
            var next = new float[Points];
            for (int i = 0; i < Points; i++)
            {
                double f0 = PointFreq(Math.Max(0, i - 0.5)), f1 = PointFreq(Math.Min(Points - 1, i + 0.5));
                int k0 = (int)(f0 * n / fs), k1 = Math.Max(k0, (int)(f1 * n / fs));
                double sum = 0; int c = 0;
                for (int k = k0; k <= k1 && k < bins; k++) { sum += power[k]; c++; }
                double p = c > 0 ? sum / c : 0;
                // Normalize so a full-scale sine reads about 0 dB.
                next[i] = (float)(10 * Math.Log10(p * 4.0 / ((double)n * n) + 1e-14));
            }
            lock (gate)
                for (int i = 0; i < Points; i++) db[i] = next[i] > db[i] ? next[i] : db[i] * 0.85f + next[i] * 0.15f;
        }

        public void PushSamples(float[] buf, int count)
        {
            const int n = 2048;
            if (fft == null) { fft = new Fft(n); re = new float[n]; im = new float[n]; ring = new float[n]; win = new float[n]; for (int i = 0; i < n; i++) win[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / n))); }
            for (int i = 0; i < count; i++)
            {
                ring[ringPos] = buf[i];
                ringPos = (ringPos + 1) % n;
                if (++sinceLast >= 1024)
                {
                    sinceLast = 0;
                    for (int j = 0; j < n; j++) { re[j] = ring[(ringPos + j) % n] * win[j] * 1.41f; im[j] = 0; }
                    fft.Transform(re, im, false);
                    var p = new float[n / 2 + 1];
                    for (int k = 0; k < p.Length; k++) p[k] = re[k] * re[k] + im[k] * im[k];
                    PushPower(p, p.Length, n);
                }
            }
        }

        public float[] Snapshot()
        {
            lock (gate) return (float[])db.Clone();
        }
    }
}

