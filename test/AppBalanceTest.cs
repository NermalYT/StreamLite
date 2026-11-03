// The listening EQ (4.2) offline: noise shaped like a balanced app, a thin one, a dull one, a boomy one and a phone-band
// voice call through AppSpectrum (what StreamLite hears) and AppBalance.Fit (the EQ it makes): a balanced app is left
// alone, the others are pushed the right way and never beyond the limits; loudness and silence don't change what it
// hears; the EQ grows in over the first ten minutes.
using System;
using System.Collections.Generic;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

class AppBalanceTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    // Noise whose energy per third of an octave follows shape(f) dB (pink noise = flat), level in dBFS RMS: 2^20 samples
    // (21.8 s) made in one inverse FFT, as interleaved stereo.
    static float[] Shaped(Func<double, double> shape, double levelDb, int seed)
    {
        int n = 1 << 20; var rnd = new Random(seed);
        var re = new float[n]; var im = new float[n];
        for (int k = 1; k < n / 2; k++)
        {
            double f = k * 48000.0 / n;
            double amp = Math.Sqrt(Math.Pow(10, shape(Math.Max(10, f)) / 10) / f);
            double ph = rnd.NextDouble() * 2 * Math.PI;
            re[k] = (float)(amp * Math.Cos(ph)); im[k] = (float)(amp * Math.Sin(ph));
            re[n - k] = re[k]; im[n - k] = -im[k];
        }
        new Fft(n).Transform(re, im, true);
        double rms = Math.Sqrt(re.Select(x => (double)x * x).Average()), gain = Math.Pow(10, levelDb / 20) / rms;
        var st = new float[2 * n];
        for (int i = 0; i < n; i++) st[2 * i] = st[2 * i + 1] = (float)(re[i] * gain);
        return st;
    }

    static double[] Hear(float[] st, out double seconds, AppSpectrum s = null)
    {
        s = s ?? new AppSpectrum();
        s.Feed(st, st.Length / 2);
        seconds = s.Seconds;
        return s.Db();
    }

    static double Curve(List<EqFilter> f, double hz) { return Biquad.ResponseDb(f, hz, 48000); }

    static string Show(List<EqFilter> f) { return AppBalance.Describe(f) + " (100 Hz " + Curve(f, 100).ToString("+0.0;-0.0") + ", 1 kHz " + Curve(f, 1000).ToString("+0.0;-0.0") + ", 8 kHz " + Curve(f, 8000).ToString("+0.0;-0.0") + ")"; }

    static void Main()
    {
        Func<double, double> music = f => f < 50 ? -6 * Math.Log(50 / f, 2) : f <= 500 ? 0 : -4.5 * Math.Log(f / 500, 2);
        double secs;

        var balanced = Hear(Shaped(music, -20, 1), out secs);
        var fit = AppBalance.Fit(balanced, 3600, "music", 1);
        Check(EqProfile.FineFreqs.Where(f => f >= 50 && f <= 12500).All(f => Math.Abs(Curve(fit, f)) <= 1.0), "balanced music: left about as it is - " + Show(fit));

        var quiet = Hear(Shaped(music, -45, 1), out secs);
        Check(Enumerable.Range(0, 31).Where(b => EqProfile.FineFreqs[b] >= 40).All(b => Math.Abs(quiet[b] - balanced[b]) < 0.3), "25 dB quieter: it hears the same shape");

        var s0 = new AppSpectrum(); s0.Feed(new float[2 * 48000 * 10], 48000 * 10);
        Check(s0.Seconds == 0, "10 s of silence: nothing heard");

        // A thin app (bass 9 dB down below 200 Hz): more bass, not more than 4 dB.
        var thin = Hear(Shaped(f => music(f) - (f < 200 ? 9 : 0), -20, 2), out secs);
        fit = AppBalance.Fit(thin, 3600, "music", 1);
        Check(Curve(fit, 80) >= 2 && EqProfile.FineFreqs.All(f => Curve(fit, f) <= 4.05), "thin music: bass up, at most +4 dB - " + Show(fit));

        // A harsh, bright app (+8 dB above 3 kHz): treble down.
        var bright = Hear(Shaped(f => music(f) + (f > 3000 ? 8 : 0), -20, 3), out secs);
        fit = AppBalance.Fit(bright, 3600, "music", 1);
        Check(Curve(fit, 8000) <= -3 && EqProfile.FineFreqs.All(f => Curve(fit, f) >= -6.05), "bright music: treble down, at most -6 dB - " + Show(fit));

        // A boomy game (+10 dB around 150 Hz): the boom comes down, the mids stay.
        var boomy = Hear(Shaped(f => music(f) + 10 * Math.Exp(-Math.Pow(Math.Log(f / 150, 2), 2) / 0.5), -20, 4), out secs);
        fit = AppBalance.Fit(boomy, 3600, "game", 1);
        Check(Curve(fit, 150) <= -2 && Math.Abs(Curve(fit, 1500)) <= 1.5 && EqProfile.FineFreqs.All(f => Curve(fit, f) >= -4.05), "boomy game: the boom down (games: at most -4 dB), the mids left - " + Show(fit));

        // A voice call through a phone-like band (300 Hz - 3.4 kHz): nothing pumped below or above where speech is.
        Func<double, double> speech = f => f < 150 ? -12 * Math.Log(150 / f, 2) : f <= 600 ? 0 : -6 * Math.Log(f / 600, 2);
        var phone = Hear(Shaped(f => speech(f) - (f < 300 ? 18 * Math.Log(300 / f, 2) : 0) - (f > 3400 ? 24 * Math.Log(f / 3400, 2) : 0), -24, 5), out secs);
        fit = AppBalance.Fit(phone, 3600, "speech", 1);
        Check(Curve(fit, 50) <= 3.05 && Curve(fit, 16000) <= 3.05 && EqProfile.FineFreqs.All(f => Curve(fit, f) <= 3.05 && Curve(fit, f) >= -5.05),
            "phone-band voice chat: within +3 / -5 dB everywhere, no sub-bass or air pumped in - " + Show(fit));

        // It grows in: a minute of a thin app corrects a tenth of what an hour does; half strength, half.
        var full = AppBalance.Fit(thin, 3600, "music", 1);
        var minute = AppBalance.Fit(thin, 60, "music", 1);
        var half = AppBalance.Fit(thin, 3600, "music", 0.5);
        Check(Math.Abs(Curve(minute, 80) - Curve(full, 80) * 0.1) < 0.35, "one minute heard: a tenth of the correction (" + Curve(minute, 80).ToString("0.0") + " of " + Curve(full, 80).ToString("0.0") + " dB at 80 Hz)");
        Check(Math.Abs(Curve(half, 80) - Curve(full, 80) * 0.5) < 0.35, "strength 50%: half the correction (" + Curve(half, 80).ToString("0.0") + " dB)");

        // It follows what you play now: an hour of the bright app after a long time of the balanced one.
        var follow = new AppSpectrum(balanced, 7200);
        var brightSt = Shaped(f => music(f) + (f > 3000 ? 8 : 0), -20, 6);
        for (int i = 0; i < 165; i++) follow.Feed(brightSt, brightSt.Length / 2);   // ~1 hour
        double moved = follow.Db()[27] - balanced[27];   // 10 kHz band
        Check(moved > 4 && moved < 8, "an hour of something brighter after two hours of balanced: it moved " + moved.ToString("0.0") + " of the 8 dB (old sound fades, not forgotten at once)");

        // Saved and loaded: it carries on from the same numbers.
        var again = new AppSpectrum(thin, 3600);
        Check(again.Db().Zip(thin, (a, b) => Math.Abs(a - b)).Max() < 1e-6 && again.Seconds == 3600, "saved and loaded: the same spectrum and seconds");

        Check(AppBalance.Difference(full, full) < 1e-9 && AppBalance.Difference(full, new List<EqFilter>()) > 1, "difference between fits");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
