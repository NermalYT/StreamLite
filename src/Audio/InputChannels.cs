using System;

namespace StreamLite.Audio
{
    /// <summary>
    /// Turns a microphone's channels into the one channel the voice chain works on.
    /// "auto" uses only the channels that carry your voice: an audio interface with the mic on input 1 would otherwise be
    /// averaged with a silent input 2 (6 dB quieter, plus that input's noise), and a laptop mic whose second channel is
    /// phase-inverted would nearly cancel itself out. "mix" averages every channel (what Windows does), "1", "2"... use
    /// one input. Channels of a stereo USB mic or headset are near-identical, so auto keeps both.
    /// </summary>
    public sealed class InputChannels
    {
        public const string Auto = "auto", Mix = "mix";

        /// <summary>"auto", "mix" or a 1-based input number.</summary>
        public volatile string Mode = Auto;
        public int Channels { get { return channels; } }
        /// <summary>Which inputs are used right now (1-based), e.g. "1" or "1+2".</summary>
        public string InUse { get; private set; }
        /// <summary>Recent level of each input (dBFS), for the interface.</summary>
        public float[] LevelDb = new float[0];

        const int Window = 12000;            // 0.25 s at 48 kHz between decisions
        const float Decay = 2f;              // dB per window (8 dB/s): a moved cable is followed within ~3 s; pauses affect every input alike
        const float VoiceFloor = -60f;       // below this nobody is talking yet: keep the current choice
        const float Spread = 15f;            // inputs this much quieter than the loudest are left out
        const int Settle = 4;                // a new choice must hold for 1 s

        int channels, filled, pending;
        float[] gain = new float[0], target = new float[0];
        double[] sum = new double[0], cross = new double[0];
        bool[] use = new bool[0], want = new bool[0];

        void Reset(int ch)
        {
            channels = ch;
            gain = new float[ch]; target = new float[ch]; sum = new double[ch]; cross = new double[ch];
            use = new bool[ch]; want = new bool[ch]; LevelDb = new float[ch];
            for (int c = 0; c < ch; c++) { use[c] = true; LevelDb[c] = -100f; }
            SetTargets(use);
            Array.Copy(target, gain, ch);
            filled = 0; pending = 0;
        }

        void SetTargets(bool[] on)
        {
            int n = 0; for (int c = 0; c < channels; c++) if (on[c]) n++;
            if (n == 0) { for (int c = 0; c < channels; c++) on[c] = true; n = channels; }
            for (int c = 0; c < channels; c++) target[c] = on[c] ? 1f / n : 0f;
            string s = "";
            for (int c = 0; c < channels; c++) if (on[c]) s += (s.Length > 0 ? "+" : "") + (c + 1);
            InUse = s;
        }

        /// <summary>Interleaved samples (frames x ch) in, one channel out.</summary>
        public void Process(float[] interleaved, int frames, int ch, float[] mono)
        {
            if (ch <= 1) { if (!ReferenceEquals(interleaved, mono)) Array.Copy(interleaved, mono, frames); InUse = "1"; return; }
            if (ch != channels) Reset(ch);
            string mode = Mode ?? Auto;
            int fixedInput;
            if (mode == Mix) { for (int c = 0; c < ch; c++) want[c] = true; Apply(); }
            else if (int.TryParse(mode, out fixedInput) && fixedInput >= 1 && fixedInput <= ch) { for (int c = 0; c < ch; c++) want[c] = c == fixedInput - 1; Apply(); }

            // Mix with gains that glide to their targets (~20 ms), so switching inputs never clicks.
            const float step = 1f / 960f;
            int loud = Loudest();
            for (int i = 0; i < frames; i++)
            {
                int o = i * ch;
                float y = 0f, lead = interleaved[o + loud];
                for (int c = 0; c < ch; c++)
                {
                    float x = interleaved[o + c];
                    float g = gain[c], t = target[c];
                    if (g < t) { g += step; if (g > t) g = t; } else if (g > t) { g -= step; if (g < t) g = t; }
                    gain[c] = g;
                    y += g * x;
                    sum[c] += x * x;
                    cross[c] += x * lead;
                }
                mono[i] = y;
            }
            filled += frames;
            if (filled >= Window) Decide(mode);
        }

        int Loudest()
        {
            int best = 0;
            for (int c = 1; c < channels; c++) if (LevelDb[c] > LevelDb[best]) best = c;
            return best;
        }

        void Decide(string mode)
        {
            int loud = Loudest();
            double eLead = sum[loud];
            for (int c = 0; c < channels; c++)
            {
                float db = (float)(10 * Math.Log10(sum[c] / filled + 1e-12));
                LevelDb[c] = Math.Max(db, LevelDb[c] - Decay);
            }
            if (mode == Auto)
            {
                float top = LevelDb[Loudest()];
                if (top > VoiceFloor)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        bool loudEnough = LevelDb[c] >= top - Spread;
                        // Strongly opposite to the loudest input (a phase-inverted mic channel): mixing it would cancel the voice.
                        double corr = cross[c] / Math.Sqrt(sum[c] * eLead + 1e-20);
                        want[c] = loudEnough && (c == loud || corr > -0.3);
                    }
                    bool same = true;
                    for (int c = 0; c < channels; c++) if (want[c] != use[c]) same = false;
                    if (same) pending = 0;
                    else if (++pending >= Settle) { pending = 0; Apply(); }
                }
            }
            for (int c = 0; c < channels; c++) { sum[c] = 0; cross[c] = 0; }
            filled = 0;
        }

        void Apply()
        {
            bool changed = false;
            for (int c = 0; c < channels; c++) if (use[c] != want[c]) { use[c] = want[c]; changed = true; }
            if (changed || InUse == null) SetTargets(use);
        }

        /// <summary>"Using input 1 of 2 (input 2 is silent)" and similar, for the interface.</summary>
        public string Describe()
        {
            if (channels <= 1) return null;
            string mode = Mode ?? Auto;
            string inUse = InUse ?? "";
            if (mode == Mix) return "Mixing all " + channels + " inputs.";
            if (mode != Auto) return "Using input " + mode + " of " + channels + ".";
            if (inUse.Contains("+") && inUse.Split('+').Length == channels) return "Auto: all " + channels + " inputs carry your voice - mixing them.";
            return "Auto: using input " + inUse + " of " + channels + " (the other" + (channels > 2 ? "s are" : " is") + " quiet or out of phase).";
        }
    }
}
