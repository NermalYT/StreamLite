using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// C# port of RNNoise v0.1.1 (Jean-Marc Valin, Mozilla / Xiph.Org, BSD-3-Clause) - the recurrent neural
    /// network voice isolator used by OBS, NoiseTorch and many voice apps. Every 10 ms it looks at 22 bands of
    /// the spectrum plus pitch, and predicts how much of each band is voice. CPU only, ~90k weights (~350 KB).
    /// Works on 480-sample frames at 48 kHz with samples in 16-bit range (+-32768).
    /// </summary>
    internal sealed class Rnnoise
    {
        public const string License = "StreamLite - AI voice isolation is a C# port of RNNoise v0.1.1 (https://github.com/xiph/rnnoise) and its trained model.\r\n\r\nCopyright (c) 2017, Mozilla\r\nCopyright (c) 2007-2017, Jean-Marc Valin\r\nCopyright (c) 2005-2017, Xiph.Org Foundation\r\nCopyright (c) 2003-2004, Mark Borgerding\r\n\r\nRedistribution and use in source and binary forms, with or without\r\nmodification, are permitted provided that the following conditions\r\nare met:\r\n\r\n- Redistributions of source code must retain the above copyright\r\nnotice, this list of conditions and the following disclaimer.\r\n\r\n- Redistributions in binary form must reproduce the above copyright\r\nnotice, this list of conditions and the following disclaimer in the\r\ndocumentation and/or other materials provided with the distribution.\r\n\r\n- Neither the name of the Xiph.Org Foundation nor the names of its\r\ncontributors may be used to endorse or promote products derived from\r\nthis software without specific prior written permission.\r\n\r\nTHIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS\r\n``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT\r\nLIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR\r\nA PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION\r\nOR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,\r\nSPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT\r\nLIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,\r\nDATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY\r\nTHEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT\r\n(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE\r\nOF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.\r\n";

        public const int FrameSize = 480;
        const int Window = 960, Freq = 481, NbBands = 22, CepsMem = 8, NbDelta = 6, NbFeatures = 42;
        const int PitchMin = 60, PitchMax = 768, PitchFrame = 960, PitchBuf = PitchMax + PitchFrame;
        static readonly int[] eband = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100 };
        static readonly int[] secondCheck = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };
        static readonly float[] halfWindow = new float[FrameSize];
        static readonly float[] dctTable = new float[NbBands * NbBands];

        // Network (shared, read-only).
        sealed class Dense { public float[] W, B; public int In, N; public bool Sigmoid; }
        sealed class Gru { public float[] W, R, B; public int In, N; }
        static Dense inputDense, denoiseOutput, vadOutput;
        static Gru vadGru, noiseGru, denoiseGru;
        static readonly object initLock = new object();
        static bool ready;

        readonly MixedFft fft = new MixedFft(Window);
        readonly float[] analysisMem = new float[FrameSize], synthesisMem = new float[FrameSize];
        readonly float[][] cepstralMem = new float[CepsMem][];
        int memId;
        readonly float[] pitchBuf = new float[PitchBuf];
        float lastGain; int lastPeriod;
        double hp0, hp1;
        readonly float[] lastG = new float[NbBands];
        readonly float[] vadState = new float[24], noiseState = new float[48], denoiseState = new float[96];

        // Work buffers (no allocation per frame).
        readonly float[] x = new float[Window], xr = new float[Window], xi = new float[Window], yr = new float[Window], yi = new float[Window];
        readonly float[] Xr = new float[Freq], Xi = new float[Freq], Pr = new float[Freq], Pi = new float[Freq];
        readonly float[] Ex = new float[NbBands], Ep = new float[NbBands], Exp = new float[NbBands], features = new float[NbFeatures];
        readonly float[] g = new float[NbBands], gf = new float[Freq], rf = new float[Freq], tmpBands = new float[NbBands], ly = new float[NbBands];
        readonly float[] lp = new float[PitchBuf >> 1], hp = new float[FrameSize];
        readonly float[] xLp4 = new float[PitchFrame >> 2], yLp4 = new float[(PitchFrame + PitchMax) >> 2], xcorr = new float[PitchMax >> 1], yyLookup = new float[(PitchMax >> 1) + 1];
        readonly float[] denseOut = new float[24], noiseIn = new float[90], denoiseIn = new float[114], vadOut = new float[1];
        readonly float[] gruAcc = new float[3 * 96], gruRec = new float[3 * 96], gruTmp = new float[96];

        /// <summary>Last voice probability from the network, 0..1.</summary>
        public float Vad;
        /// <summary>Per-bin power of the last input frame (481 bins of a 960-point DFT, unscaled float audio).</summary>
        public readonly float[] Power = new float[Freq];
        /// <summary>Per-bin gain applied to the last frame.</summary>
        public readonly float[] BinGain = new float[Freq];
        public const int Bins = Freq, FftSize = Window;

        public Rnnoise()
        {
            Init();
            for (int i = 0; i < CepsMem; i++) cepstralMem[i] = new float[NbBands];
        }

        static void Init()
        {
            lock (initLock)
            {
                if (ready) return;
                for (int i = 0; i < FrameSize; i++)
                {
                    double s = Math.Sin(.5 * Math.PI * (i + .5) / FrameSize);
                    halfWindow[i] = (float)Math.Sin(.5 * Math.PI * s * s);
                }
                for (int i = 0; i < NbBands; i++)
                    for (int j = 0; j < NbBands; j++)
                    {
                        dctTable[i * NbBands + j] = (float)Math.Cos((i + .5) * j * Math.PI / NbBands);
                        if (j == 0) dctTable[i * NbBands + j] *= (float)Math.Sqrt(.5);
                    }
                inputDense = new Dense { B = W8(RnnoiseModel.InputDenseBias), W = W8(RnnoiseModel.InputDenseWeights), In = 42, N = 24, Sigmoid = false };
                vadGru = new Gru { B = W8(RnnoiseModel.VadGruBias), W = W8(RnnoiseModel.VadGruWeights), R = W8(RnnoiseModel.VadGruRecurrentWeights), In = 24, N = 24 };
                noiseGru = new Gru { B = W8(RnnoiseModel.NoiseGruBias), W = W8(RnnoiseModel.NoiseGruWeights), R = W8(RnnoiseModel.NoiseGruRecurrentWeights), In = 90, N = 48 };
                denoiseGru = new Gru { B = W8(RnnoiseModel.DenoiseGruBias), W = W8(RnnoiseModel.DenoiseGruWeights), R = W8(RnnoiseModel.DenoiseGruRecurrentWeights), In = 114, N = 96 };
                denoiseOutput = new Dense { B = W8(RnnoiseModel.DenoiseOutputBias), W = W8(RnnoiseModel.DenoiseOutputWeights), In = 96, N = 22, Sigmoid = true };
                vadOutput = new Dense { B = W8(RnnoiseModel.VadOutputBias), W = W8(RnnoiseModel.VadOutputWeights), In = 24, N = 1, Sigmoid = true };
                ready = true;
            }
        }

        static float[] W8(string b64)
        {
            byte[] b = Convert.FromBase64String(b64);
            var f = new float[b.Length];
            for (int i = 0; i < b.Length; i++) f[i] = (sbyte)b[i];
            return f;
        }

        static float Sigmoid(float v) { return (float)(1 / (1 + Math.Exp(-v))); }

        static void ComputeDense(Dense l, float[] output, float[] input)
        {
            int n = l.N;
            for (int i = 0; i < n; i++) output[i] = l.B[i];
            for (int j = 0; j < l.In; j++)
            {
                float v = input[j];
                if (v == 0) continue;
                int row = j * n;
                for (int i = 0; i < n; i++) output[i] += l.W[row + i] * v;
            }
            for (int i = 0; i < n; i++)
            {
                float s = output[i] * (1f / 256);
                output[i] = l.Sigmoid ? Sigmoid(s) : (float)Math.Tanh(s);
            }
        }

        void ComputeGru(Gru l, float[] state, float[] input)
        {
            int n = l.N, stride = 3 * n;
            float[] acc = gruAcc, rec = gruRec;
            for (int k = 0; k < stride; k++) { acc[k] = l.B[k]; rec[k] = 0; }
            for (int j = 0; j < l.In; j++)
            {
                float v = input[j];
                if (v == 0) continue;
                int row = j * stride;
                for (int k = 0; k < stride; k++) acc[k] += l.W[row + k] * v;
            }
            // Update + reset gates.
            for (int j = 0; j < n; j++)
            {
                float v = state[j];
                if (v == 0) continue;
                int row = j * stride;
                for (int k = 0; k < 2 * n; k++) rec[k] += l.R[row + k] * v;
            }
            for (int i = 0; i < n; i++)
            {
                float r = Sigmoid((acc[n + i] + rec[n + i]) * (1f / 256));
                gruTmp[i] = state[i] * r;
            }
            // Candidate state uses the reset-gated previous state.
            for (int k = 0; k < n; k++) rec[2 * n + k] = 0;
            for (int j = 0; j < n; j++)
            {
                float v = gruTmp[j];
                if (v == 0) continue;
                int row = j * stride + 2 * n;
                for (int k = 0; k < n; k++) rec[2 * n + k] += l.R[row + k] * v;
            }
            for (int i = 0; i < n; i++)
            {
                float z = Sigmoid((acc[i] + rec[i]) * (1f / 256));
                float h = (acc[2 * n + i] + rec[2 * n + i]) * (1f / 256);
                if (h < 0) h = 0;   // ReLU
                state[i] = z * state[i] + (1 - z) * h;
            }
        }

        void ComputeRnn()
        {
            ComputeDense(inputDense, denseOut, features);
            ComputeGru(vadGru, vadState, denseOut);
            ComputeDense(vadOutput, vadOut, vadState);
            Array.Copy(denseOut, 0, noiseIn, 0, 24);
            Array.Copy(vadState, 0, noiseIn, 24, 24);
            Array.Copy(features, 0, noiseIn, 48, NbFeatures);
            ComputeGru(noiseGru, noiseState, noiseIn);
            Array.Copy(vadState, 0, denoiseIn, 0, 24);
            Array.Copy(noiseState, 0, denoiseIn, 24, 48);
            Array.Copy(features, 0, denoiseIn, 72, NbFeatures);
            ComputeGru(denoiseGru, denoiseState, denoiseIn);
            ComputeDense(denoiseOutput, g, denoiseState);
        }

        // ---- spectral helpers ----

        static void BandEnergy(float[] bandE, float[] ar, float[] ai, float[] br, float[] bi)
        {
            for (int i = 0; i < NbBands; i++) bandE[i] = 0;
            for (int i = 0; i < NbBands - 1; i++)
            {
                int size = (eband[i + 1] - eband[i]) << 2, start = eband[i] << 2;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    int k = start + j;
                    float t = ar[k] * br[k] + ai[k] * bi[k];
                    bandE[i] += (1 - frac) * t;
                    bandE[i + 1] += frac * t;
                }
            }
            bandE[0] *= 2;
            bandE[NbBands - 1] *= 2;
        }

        static void InterpBandGain(float[] outG, float[] bandE)
        {
            for (int i = 0; i < NbBands - 1; i++)
            {
                int size = (eband[i + 1] - eband[i]) << 2, start = eband[i] << 2;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    outG[start + j] = (1 - frac) * bandE[i] + frac * bandE[i + 1];
                }
            }
        }

        static void Dct(float[] output, int outOff, float[] input)
        {
            float scale = (float)Math.Sqrt(2.0 / 22);
            for (int i = 0; i < NbBands; i++)
            {
                float sum = 0;
                for (int j = 0; j < NbBands; j++) sum += input[j] * dctTable[j * NbBands + i];
                output[outOff + i] = sum * scale;
            }
        }

        static void ApplyWindow(float[] v)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                v[i] *= halfWindow[i];
                v[Window - 1 - i] *= halfWindow[i];
            }
        }

        /// <summary>Real forward DFT scaled by 1/N (kiss_fft convention), first 481 bins.</summary>
        void Forward(float[] input, float[] outR, float[] outI)
        {
            for (int i = 0; i < Window; i++) { xr[i] = input[i]; xi[i] = 0; }
            fft.Transform(xr, xi, yr, yi);
            const float s = 1f / Window;
            for (int i = 0; i < Freq; i++) { outR[i] = yr[i] * s; outI[i] = yi[i] * s; }
        }

        void Inverse(float[] output, float[] inR, float[] inI)
        {
            for (int i = 0; i < Freq; i++) { xr[i] = inR[i]; xi[i] = inI[i]; }
            for (int i = Freq; i < Window; i++) { xr[i] = xr[Window - i]; xi[i] = -xi[Window - i]; }
            fft.Transform(xr, xi, yr, yi);
            output[0] = yr[0];
            for (int i = 1; i < Window; i++) output[i] = yr[Window - i];
        }

        // ---- pitch (from celt/pitch.c, float build) ----

        static float Inner(float[] a, int ao, float[] b, int bo, int n)
        {
            float s = 0;
            for (int i = 0; i < n; i++) s += a[ao + i] * b[bo + i];
            return s;
        }

        void PitchDownsample(float[] src, float[] xlp, int len)
        {
            int half = len >> 1;
            for (int i = 1; i < half; i++) xlp[i] = .5f * (.5f * (src[2 * i - 1] + src[2 * i + 1]) + src[2 * i]);
            xlp[0] = .5f * (.5f * src[1] + src[0]);

            var ac = new float[5];
            for (int k = 0; k <= 4; k++)
            {
                float d = 0;
                for (int i = k; i < half; i++) d += xlp[i] * xlp[i - k];
                ac[k] = d;
            }
            ac[0] *= 1.0001f;
            for (int i = 1; i <= 4; i++) ac[i] -= ac[i] * (.008f * i) * (.008f * i);

            var lpc = new float[4];
            float error = ac[0];
            if (ac[0] != 0)
                for (int i = 0; i < 4; i++)
                {
                    float rr = 0;
                    for (int j = 0; j < i; j++) rr += lpc[j] * ac[i - j];
                    rr += ac[i + 1];
                    float r = -rr / error;
                    lpc[i] = r;
                    for (int j = 0; j < (i + 1) >> 1; j++)
                    {
                        float t1 = lpc[j], t2 = lpc[i - 1 - j];
                        lpc[j] = t1 + r * t2;
                        lpc[i - 1 - j] = t2 + r * t1;
                    }
                    error = error - r * r * error;
                    if (error < .001f * ac[0]) break;
                }
            float tmp = 1f;
            for (int i = 0; i < 4; i++) { tmp *= .9f; lpc[i] *= tmp; }
            const float c1 = .8f;
            float n0 = lpc[0] + .8f, n1 = lpc[1] + c1 * lpc[0], n2 = lpc[2] + c1 * lpc[1], n3 = lpc[3] + c1 * lpc[2], n4 = c1 * lpc[3];
            float m0 = 0, m1 = 0, m2 = 0, m3 = 0, m4 = 0;
            for (int i = 0; i < half; i++)
            {
                float xv = xlp[i];
                float sum = xv + n0 * m0 + n1 * m1 + n2 * m2 + n3 * m3 + n4 * m4;
                m4 = m3; m3 = m2; m2 = m1; m1 = m0; m0 = xv;
                xlp[i] = sum;
            }
        }

        static void FindBestPitch(float[] xc, float[] y, int yOff, int len, int maxPitch, int[] best)
        {
            float syy = 1;
            float bn0 = -1, bn1 = -1, bd0 = 0, bd1 = 0;
            best[0] = 0; best[1] = 1;
            for (int j = 0; j < len; j++) syy += y[yOff + j] * y[yOff + j];
            for (int i = 0; i < maxPitch; i++)
            {
                if (xc[i] > 0)
                {
                    float x16 = xc[i] * 1e-12f;
                    float num = x16 * x16;
                    if (num * bd1 > bn1 * syy)
                    {
                        if (num * bd0 > bn0 * syy)
                        {
                            bn1 = bn0; bd1 = bd0; best[1] = best[0];
                            bn0 = num; bd0 = syy; best[0] = i;
                        }
                        else { bn1 = num; bd1 = syy; best[1] = i; }
                    }
                }
                syy += y[yOff + i + len] * y[yOff + i + len] - y[yOff + i] * y[yOff + i];
                if (syy < 1) syy = 1;
            }
        }

        readonly int[] bestPitch = new int[2];

        int PitchSearch(float[] buf, int xOff, int len, int maxPitch)
        {
            int lag = len + maxPitch;
            for (int j = 0; j < len >> 2; j++) xLp4[j] = buf[xOff + 2 * j];
            for (int j = 0; j < lag >> 2; j++) yLp4[j] = buf[2 * j];

            int coarse = maxPitch >> 2;
            for (int i = 0; i < coarse; i++) xcorr[i] = Inner(xLp4, 0, yLp4, i, len >> 2);
            FindBestPitch(xcorr, yLp4, 0, len >> 2, coarse, bestPitch);

            int fine = maxPitch >> 1;
            for (int i = 0; i < fine; i++)
            {
                xcorr[i] = 0;
                if (Math.Abs(i - 2 * bestPitch[0]) > 2 && Math.Abs(i - 2 * bestPitch[1]) > 2) continue;
                float sum = Inner(buf, xOff, buf, i, len >> 1);
                xcorr[i] = Math.Max(-1, sum);
            }
            FindBestPitch(xcorr, buf, 0, len >> 1, fine, bestPitch);

            int offset = 0;
            if (bestPitch[0] > 0 && bestPitch[0] < fine - 1)
            {
                float a = xcorr[bestPitch[0] - 1], b = xcorr[bestPitch[0]], c = xcorr[bestPitch[0] + 1];
                if ((c - a) > .7f * (b - a)) offset = 1;
                else if ((a - c) > .7f * (b - c)) offset = -1;
            }
            return 2 * bestPitch[0] - offset;
        }

        static float PitchGain(double xy, double xx, double yy) { return (float)(xy / Math.Sqrt(1 + xx * yy)); }

        float RemoveDoubling(float[] buf, int maxPeriod, int minPeriod, int n, ref int t0Ref, int prevPeriod, float prevGain)
        {
            int minPeriod0 = minPeriod;
            maxPeriod /= 2; minPeriod /= 2; t0Ref /= 2; prevPeriod /= 2; n /= 2;
            int xo = maxPeriod;   // x = buf + maxPeriod
            if (t0Ref >= maxPeriod) t0Ref = maxPeriod - 1;
            int T = t0Ref, T0 = t0Ref;
            double xx = 0, xy = 0;
            for (int i = 0; i < n; i++) { xx += buf[xo + i] * buf[xo + i]; xy += buf[xo + i] * buf[xo + i - T0]; }
            yyLookup[0] = (float)xx;
            double yy = xx;
            for (int i = 1; i <= maxPeriod; i++)
            {
                yy = yy + buf[xo - i] * buf[xo - i] - buf[xo + n - i] * buf[xo + n - i];
                yyLookup[i] = (float)Math.Max(0, yy);
            }
            yy = yyLookup[T0];
            double bestXy = xy, bestYy = yy;
            float g0 = PitchGain(xy, xx, yy), gBest = g0;
            for (int k = 2; k <= 15; k++)
            {
                int T1 = (2 * T0 + k) / (2 * k);
                if (T1 < minPeriod) break;
                int T1b;
                if (k == 2) T1b = T1 + T0 > maxPeriod ? T0 : T0 + T1;
                else T1b = (2 * secondCheck[k] * T0 + k) / (2 * k);
                double a = 0, b = 0;
                for (int i = 0; i < n; i++) { a += buf[xo + i] * buf[xo + i - T1]; b += buf[xo + i] * buf[xo + i - T1b]; }
                double xy1 = .5 * (a + b);
                double yy1 = .5 * (yyLookup[T1] + yyLookup[T1b]);
                float g1 = PitchGain(xy1, xx, yy1);
                float cont;
                if (Math.Abs(T1 - prevPeriod) <= 1) cont = prevGain;
                else if (Math.Abs(T1 - prevPeriod) <= 2 && 5 * k * k < T0) cont = .5f * prevGain;
                else cont = 0;
                float thresh = Math.Max(.3f, .7f * g0 - cont);
                if (T1 < 3 * minPeriod) thresh = Math.Max(.4f, .85f * g0 - cont);
                else if (T1 < 2 * minPeriod) thresh = Math.Max(.5f, .9f * g0 - cont);
                if (g1 > thresh) { bestXy = xy1; bestYy = yy1; T = T1; gBest = g1; }
            }
            bestXy = Math.Max(0, bestXy);
            float pg = bestYy <= bestXy ? 1f : (float)(bestXy / (bestYy + 1));
            var xc = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double s = 0;
                int lagK = T + k - 1;
                for (int i = 0; i < n; i++) s += buf[xo + i] * buf[xo + i - lagK];
                xc[k] = s;
            }
            int offset = 0;
            if ((xc[2] - xc[0]) > .7 * (xc[1] - xc[0])) offset = 1;
            else if ((xc[0] - xc[2]) > .7 * (xc[1] - xc[2])) offset = -1;
            if (pg > gBest) pg = gBest;
            t0Ref = 2 * T + offset;
            if (t0Ref < minPeriod0) t0Ref = minPeriod0;
            return pg;
        }

        // ---- frame ----

        bool ComputeFrameFeatures(float[] input)
        {
            // Analysis: previous frame + this frame, windowed.
            Array.Copy(analysisMem, 0, x, 0, FrameSize);
            Array.Copy(input, 0, x, FrameSize, FrameSize);
            Array.Copy(input, 0, analysisMem, 0, FrameSize);
            ApplyWindow(x);
            Forward(x, Xr, Xi);
            BandEnergy(Ex, Xr, Xi, Xr, Xi);

            Array.Copy(pitchBuf, FrameSize, pitchBuf, 0, PitchBuf - FrameSize);
            Array.Copy(input, 0, pitchBuf, PitchBuf - FrameSize, FrameSize);
            PitchDownsample(pitchBuf, lp, PitchBuf);
            int pitchIndex = PitchSearch(lp, PitchMax >> 1, PitchFrame, PitchMax - 3 * PitchMin);
            pitchIndex = PitchMax - pitchIndex;
            float gain = RemoveDoubling(lp, PitchMax, PitchMin, PitchFrame, ref pitchIndex, lastPeriod, lastGain);
            lastPeriod = pitchIndex;
            lastGain = gain;

            for (int i = 0; i < Window; i++) x[i] = pitchBuf[PitchBuf - Window - pitchIndex + i];
            ApplyWindow(x);
            Forward(x, Pr, Pi);
            BandEnergy(Ep, Pr, Pi, Pr, Pi);
            BandEnergy(Exp, Xr, Xi, Pr, Pi);
            for (int i = 0; i < NbBands; i++) Exp[i] = (float)(Exp[i] / Math.Sqrt(.001 + Ex[i] * Ep[i]));
            Dct(tmpBands, 0, Exp);
            for (int i = 0; i < NbDelta; i++) features[NbBands + 2 * NbDelta + i] = tmpBands[i];
            features[NbBands + 2 * NbDelta] -= 1.3f;
            features[NbBands + 2 * NbDelta + 1] -= 0.9f;
            features[NbBands + 3 * NbDelta] = .01f * (pitchIndex - 300);

            float logMax = -2, follow = -2, E = 0;
            for (int i = 0; i < NbBands; i++)
            {
                float v = (float)Math.Log10(1e-2 + Ex[i]);
                v = Math.Max(logMax - 7, Math.Max(follow - 1.5f, v));
                ly[i] = v;
                logMax = Math.Max(logMax, v);
                follow = Math.Max(follow - 1.5f, v);
                E += Ex[i];
            }
            if (E < 0.04f)
            {
                Array.Clear(features, 0, NbFeatures);
                return true;
            }
            Dct(features, 0, ly);
            features[0] -= 12;
            features[1] -= 4;
            float[] c0 = cepstralMem[memId];
            float[] c1 = cepstralMem[memId < 1 ? CepsMem + memId - 1 : memId - 1];
            float[] c2 = cepstralMem[memId < 2 ? CepsMem + memId - 2 : memId - 2];
            for (int i = 0; i < NbBands; i++) c0[i] = features[i];
            memId++;
            for (int i = 0; i < NbDelta; i++)
            {
                features[i] = c0[i] + c1[i] + c2[i];
                features[NbBands + i] = c0[i] - c2[i];
                features[NbBands + NbDelta + i] = c0[i] - 2 * c1[i] + c2[i];
            }
            if (memId == CepsMem) memId = 0;
            float variability = 0;
            for (int i = 0; i < CepsMem; i++)
            {
                float minDist = 1e15f;
                for (int j = 0; j < CepsMem; j++)
                {
                    if (j == i) continue;
                    float dist = 0;
                    for (int k = 0; k < NbBands; k++) { float t = cepstralMem[i][k] - cepstralMem[j][k]; dist += t * t; }
                    if (dist < minDist) minDist = dist;
                }
                variability += minDist;
            }
            features[NbBands + 3 * NbDelta + 1] = variability / CepsMem - 2.1f;
            return false;
        }

        void PitchFilter()
        {
            var r = tmpBands;
            for (int i = 0; i < NbBands; i++)
            {
                float v;
                if (Exp[i] > g[i]) v = 1;
                else v = Exp[i] * Exp[i] * (1 - g[i] * g[i]) / (.001f + g[i] * g[i] * (1 - Exp[i] * Exp[i]));
                v = (float)Math.Sqrt(Math.Min(1, Math.Max(0, v)));
                r[i] = v * (float)Math.Sqrt(Ex[i] / (1e-8 + Ep[i]));
            }
            Array.Clear(rf, 0, Freq);
            InterpBandGain(rf, r);
            for (int i = 0; i < Freq; i++) { Xr[i] += rf[i] * Pr[i]; Xi[i] += rf[i] * Pi[i]; }
            var newE = ly;   // reuse as scratch
            BandEnergy(newE, Xr, Xi, Xr, Xi);
            for (int i = 0; i < NbBands; i++) r[i] = (float)Math.Sqrt(Ex[i] / (1e-8 + newE[i]));
            Array.Clear(rf, 0, Freq);
            InterpBandGain(rf, r);
            for (int i = 0; i < Freq; i++) { Xr[i] *= rf[i]; Xi[i] *= rf[i]; }
        }

        /// <summary>
        /// Denoise one 480-sample frame (16-bit range) from <paramref name="input"/> into <paramref name="output"/>.
        /// minGain: lowest gain allowed (attenuation limit). voiceFloor: lowest gain in voice bands while talking.
        /// decay: how slowly gains may fall per frame (0.6 = RNNoise default). sharpen: extra attenuation of what the
        /// network calls noise, weighted by how unlikely speech is (0 = stock RNNoise). apply=false runs the analysis only.
        /// Returns the voice probability.
        /// </summary>
        public float ProcessFrame(float[] input, float[] output, float minGain, float voiceFloor, float decay, float sharpen, bool apply)
        {
            // DC / rumble high-pass from RNNoise.
            for (int i = 0; i < FrameSize; i++)
            {
                double xv = input[i];
                double y = xv + hp0;
                hp0 = hp1 + (-2 * xv - (-1.99599) * y);
                hp1 = (1 * xv - 0.99600 * y);
                hp[i] = (float)y;
            }
            bool silence = ComputeFrameFeatures(hp);
            const float toFloat = Window / 32768f;
            for (int i = 0; i < Freq; i++) Power[i] = (Xr[i] * Xr[i] + Xi[i] * Xi[i]) * toFloat * toFloat;

            for (int i = 0; i < Freq; i++) BinGain[i] = 1;
            if (!silence)
            {
                ComputeRnn();
                Vad = vadOut[0];
                if (apply)
                {
                    PitchFilter();
                    bool talking = Vad > 0.5f;
                    double expo = 1 + sharpen * (1 - Vad);
                    for (int i = 0; i < NbBands; i++)
                    {
                        float gn = expo > 1.001 ? (float)Math.Pow(g[i], expo) : g[i];
                        float gi = Math.Max(gn, decay * lastG[i]);
                        lastG[i] = gi;
                        float floor = talking && i <= 16 ? Math.Max(minGain, voiceFloor) : minGain;   // band 16 ~ 6.8 kHz
                        g[i] = Math.Max(gi, floor);
                    }
                    Array.Clear(gf, 0, Freq);
                    InterpBandGain(gf, g);
                    for (int i = 0; i < Freq; i++) { Xr[i] *= gf[i]; Xi[i] *= gf[i]; BinGain[i] = gf[i]; }
                }
                else for (int i = 0; i < NbBands; i++) lastG[i] = g[i];
            }
            else Vad = 0;

            Inverse(x, Xr, Xi);
            ApplyWindow(x);
            for (int i = 0; i < FrameSize; i++) output[i] = x[i] + synthesisMem[i];
            Array.Copy(x, FrameSize, synthesisMem, 0, FrameSize);
            return Vad;
        }
    }

    /// <summary>Mixed-radix complex FFT (kiss_fft's generic algorithm) for sizes like 960 = 4*4*4*3*5. Unscaled.</summary>
    internal sealed class MixedFft
    {
        readonly int n;
        readonly int[] factors;
        readonly float[] twr, twi, scr = new float[16], sci = new float[16];

        public MixedFft(int size)
        {
            n = size;
            var f = new System.Collections.Generic.List<int>();
            int m = size, p = 4;
            while (m > 1)
            {
                while (m % p != 0)
                {
                    if (p == 4) p = 2; else if (p == 2) p = 3; else p += 2;
                    if (p * p > m) p = m;
                }
                m /= p;
                f.Add(p); f.Add(m);
            }
            factors = f.ToArray();
            twr = new float[n]; twi = new float[n];
            for (int i = 0; i < n; i++)
            {
                double ph = -2 * Math.PI * i / n;
                twr[i] = (float)Math.Cos(ph); twi[i] = (float)Math.Sin(ph);
            }
            int maxP = 0;
            for (int i = 0; i < factors.Length; i += 2) maxP = Math.Max(maxP, factors[i]);
            if (maxP > 16) { scr = new float[maxP]; sci = new float[maxP]; }
        }

        public void Transform(float[] inR, float[] inI, float[] outR, float[] outI)
        {
            Work(outR, outI, 0, inR, inI, 0, 1, 0);
        }

        void Work(float[] oR, float[] oI, int oOff, float[] fR, float[] fI, int fOff, int fstride, int stage)
        {
            int p = factors[2 * stage], m = factors[2 * stage + 1];
            if (m == 1)
                for (int q = 0; q < p; q++) { oR[oOff + q] = fR[fOff + q * fstride]; oI[oOff + q] = fI[fOff + q * fstride]; }
            else
                for (int q = 0; q < p; q++) Work(oR, oI, oOff + q * m, fR, fI, fOff + q * fstride, fstride * p, stage + 1);

            for (int u = 0; u < m; u++)
            {
                for (int q1 = 0; q1 < p; q1++) { scr[q1] = oR[oOff + u + q1 * m]; sci[q1] = oI[oOff + u + q1 * m]; }
                int k = u;
                for (int q1 = 0; q1 < p; q1++)
                {
                    float ar = scr[0], ai = sci[0];
                    int tw = 0;
                    for (int q = 1; q < p; q++)
                    {
                        tw += fstride * k;
                        if (tw >= n) tw -= n;
                        ar += scr[q] * twr[tw] - sci[q] * twi[tw];
                        ai += scr[q] * twi[tw] + sci[q] * twr[tw];
                    }
                    oR[oOff + k] = ar; oI[oOff + k] = ai;
                    k += m;
                }
            }
        }
    }
}
