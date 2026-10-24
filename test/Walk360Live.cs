using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite;
using StreamLite.Audio;
using StreamLite.Interop;

// End-to-end check of true 360° mode in the running app: plays quiet noise panned between 7.1 speakers (the way games
// pan) into "StreamLite - Out", records the headphones (loopback) and measures the time difference between the ears
// for each angle. Compared with the same signal rendered offline in 360° mode and in 7-speaker mode.
class Walk360Live
{
    const int Fs = 48000;
    static readonly int[] Angles = { 75, 60, 120, 180, 240, 300 };   // never a single speaker alone: that reads as stereo content
    const double Seg = 1.2;

    static IAudioClient Open(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev; en.GetDevice(id, out dev);
        object o; Guid iid = CoreAudioConst.IID_IAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        return (IAudioClient)o;
    }

    static float[] Pink(int n, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        double b0 = 0, b1 = 0, b2 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            b0 = 0.99765 * b0 + w * 0.0990460; b1 = 0.96300 * b1 + w * 0.2965164; b2 = 0.57000 * b2 + w * 1.0526913;
            x[i] = (float)((b0 + b1 + b2 + w * 0.1848) * 0.012);   // about -30 dBFS
        }
        return x;
    }

    static double Itd(float[] l, float[] r, int from, int len, int rate)
    {
        // Low-pass both (< ~1.5 kHz) with a simple 4x one-pole, then cross-correlate over +-1.2 ms.
        Func<float[], double[]> lp = x =>
        {
            var y = new double[len]; double a = Math.Exp(-2 * Math.PI * 1500.0 / rate), s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            for (int i = 0; i < len; i++) { s1 = a * s1 + (1 - a) * x[from + i]; s2 = a * s2 + (1 - a) * s1; s3 = a * s3 + (1 - a) * s2; s4 = a * s4 + (1 - a) * s3; y[i] = s4; }
            return y;
        };
        var L = lp(l); var R = lp(r);
        int max = (int)(0.0012 * rate); int best = 0; double bv = double.MinValue; var c = new double[2 * max + 1];
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = max; i < len - max; i++) s += L[i] * R[i + lag];
            c[lag + max] = s; if (s > bv) { bv = s; best = lag; }
        }
        double frac = 0;
        if (best > -max && best < max) { double y0 = c[best + max - 1], y1 = c[best + max], y2 = c[best + max + 1]; frac = 0.5 * (y0 - y2) / (y0 - 2 * y1 + y2); }
        return (best + frac) * 1e6 / rate;      // + = right ear later = sound on the left
    }

    static double Ild(float[] l, float[] r, int from, int len)
    {
        double el = 0, er = 0;
        for (int i = from; i < from + len; i++) { el += l[i] * l[i]; er += r[i] * r[i]; }
        return 10 * Math.Log10(el / er);
    }

    /// <summary>ILD below ~1 kHz: unaffected by the headphone EQ's treble tilt (loopback records after Equalizer APO).</summary>
    static double IldLow(float[] l, float[] r, int from, int len, int rate)
    {
        double a = Math.Exp(-2 * Math.PI * 1000.0 / rate), l1 = 0, l2 = 0, r1 = 0, r2 = 0, el = 0, er = 0;
        for (int i = from; i < from + len; i++)
        {
            l1 = a * l1 + (1 - a) * l[i]; l2 = a * l2 + (1 - a) * l1; r1 = a * r1 + (1 - a) * r[i]; r2 = a * r2 + (1 - a) * r1;
            el += l2 * l2; er += r2 * r2;
        }
        return 10 * Math.Log10(el / er);
    }

    static float[][] Input(float[] slotAz)
    {
        int seg = (int)(Seg * Fs), total = seg * Angles.Length + Fs;
        var ch = new float[8][]; for (int c = 0; c < 8; c++) ch[c] = new float[total];
        var pink = Pink(total, 5);
        for (int k = 0; k < Angles.Length; k++)
        {
            int a, b; float ga, gb;
            TestSignal.Pan(Angles[k], slotAz, out a, out b, out ga, out gb);
            for (int i = 0; i < seg; i++)
            {
                double fade = Math.Min(1, Math.Min(i, seg - i) / 2400.0);
                float v = (float)(pink[k * seg + i] * fade);
                ch[a][k * seg + i] += v * ga; ch[b][k * seg + i] += v * gb;
            }
        }
        return ch;
    }

    static void Offline(SpatialConfig c, float[][] input, out float[] L, out float[] R)
    {
        var p = new SpatialProcessor(); p.Configure(c);
        HrtfSet set = HrtfSet.Get(c.HrtfKind, c.CustomPath);
        ContinuousRenderer.Prepare(set); while (!ContinuousRenderer.IsReady(set)) Thread.Sleep(5);
        int n = input[0].Length, B = SpatialProcessor.B;
        L = new float[n]; R = new float[n];
        var l = new float[B]; var r = new float[B];
        for (int s = 0; s + B <= n; s += B)
        {
            var sl = new float[8][]; for (int k = 0; k < 8; k++) { sl[k] = new float[B]; Array.Copy(input[k], s, sl[k], 0, B); }
            p.Process(sl, SpatialProcessor.Source.Surround71, l, r);
            Array.Copy(l, 0, L, s, B); Array.Copy(r, 0, R, s, B);
        }
    }

    static void Main()
    {
        var S = AppSettings.Load();
        var cfg = S.Game.ToConfig();
        var radar = new SoundRadar(); radar.SetLayout(SpatialProcessor.Source.Surround71, cfg);
        Console.WriteLine("Settings: " + (S.Game.Render360 ? "true 360" : "7 speakers") + ", head " + cfg.HrtfKind + ", speakers at " +
            string.Join(" ", new[] { 2, 0, 6, 4, 5, 7, 1 }.Select(s => radar.SlotAz[s].ToString("0"))));
        var input = Input(radar.SlotAz);

        string sink = AudioDevices.List(EDataFlow.Render).First(d => d.Name.IndexOf("StreamLite - Out", StringComparison.OrdinalIgnoreCase) >= 0).Id;
        string phones = AudioDevices.List(EDataFlow.Render).First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0).Id;

        // Record the headphones while playing into the game device.
        float[][] rec = null; int recRate = 0;
        var recTh = new Thread(() => rec = Loop(phones, Seg * Angles.Length + 2.5, out recRate));
        recTh.Start();
        Thread.Sleep(400);
        Play(sink, input);
        recTh.Join();

        // Find where the playback starts in the recording (first loud block).
        var Lr = rec[0]; var Rr = rec[1];
        int start = 0; int blk = recRate / 100;
        for (int i = 0; i + blk < Lr.Length; i += blk)
        {
            double e = 0; for (int k = i; k < i + blk; k++) e += Lr[k] * Lr[k] + Rr[k] * Rr[k];
            if (10 * Math.Log10(e / blk + 1e-20) > -60) { start = i; break; }
        }
        float[] l3, r3, ls, rs, lg, rg;
        var c360 = Natural(S.Game.ToConfig()); c360.Render360 = true;
        var cSpk = Natural(S.Game.ToConfig()); cSpk.Render360 = false;
        Offline(c360, input, out l3, out r3);
        Offline(cSpk, input, out ls, out rs);
        var cGame = S.Game.ToConfig(); cGame.Render360 = true;   // a game profile active: cue sharpening etc. on
        Offline(cGame, input, out lg, out rg);
        Console.WriteLine("recorded " + (Lr.Length / (double)recRate).ToString("0.0") + " s at " + recRate + " Hz, sound starts at " + (start / (double)recRate).ToString("0.00") + " s");
        Console.WriteLine();
        Console.WriteLine("angle   live ITD us   offline 360   offline 7-spk   |  live ILD dB   360 ILD   360+game ILD   7-spk ILD");
        int ok = 0;
        for (int k = 0; k < Angles.Length; k++)
        {
            int segR = (int)(Seg * recRate), from = start + k * segR + segR / 5, len = segR * 3 / 5;
            int seg = (int)(Seg * Fs), fromO = k * seg + seg / 5, lenO = seg * 3 / 5;
            double live = Itd(Lr, Rr, from, len, recRate), o3 = Itd(l3, r3, fromO, lenO, Fs), os = Itd(ls, rs, fromO, lenO, Fs);
            double iLive = Ild(Lr, Rr, from, len), i3 = Ild(l3, r3, fromO, lenO), iS = Ild(ls, rs, fromO, lenO), iG = Ild(lg, rg, fromO, lenO);
            bool match = Math.Abs(live - o3) < 25 && (Math.Abs(iLive - i3) < 1.5 || Math.Abs(iLive - iG) < 1.5);
            if (match) ok++;
            Console.WriteLine(string.Format("        below 1 kHz ILD: live {0:0.0}  natural {1:0.0}  game {2:0.0}", IldLow(Lr, Rr, from, len, recRate), IldLow(l3, r3, fromO, lenO, Fs), IldLow(lg, rg, fromO, lenO, Fs)));
            Console.WriteLine(string.Format("{0,5}   {1,10:0}   {2,11:0}   {3,13:0}   |  {4,10:0.0}   {5,7:0.0}   {8,12:0.0}   {6,9:0.0}   {7}",
                Angles[k], live, o3, os, iLive, i3, iS, match ? "matches 360" : "MISMATCH", iG));
        }
        Console.WriteLine(ok == Angles.Length ? "LIVE 360 OK" : (Angles.Length - ok) + " angles differ");
    }

    /// <summary>No game running: the app plays natural (same overrides as AppCore.PushGameParams).</summary>
    static SpatialConfig Natural(SpatialConfig c)
    {
        c.FootstepBoost = 0; c.CueSharpen = 0; c.LoudTamer = 0;
        foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d].Enabled = false; c.DirGainDb[d] = 0; }
        return c;
    }

    static void Play(string id, float[][] ch)
    {
        var ac = Open(id);
        IntPtr fmt = AudioDevices.MakeFormat(8, 0x63F, Fs, true, 32);
        int hr = ac.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 2000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) throw new Exception("play init 0x" + hr.ToString("X8"));
        uint buf; ac.GetBufferSize(out buf);
        object svc; Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
        var rc = (IAudioRenderClient)svc;
        ac.Start();
        int pos = 0, n0 = ch[0].Length; var tmp = new float[buf * 8];
        while (pos < n0)
        {
            Thread.Sleep(10);
            uint pad; ac.GetCurrentPadding(out pad);
            int n = (int)Math.Min(buf - pad, (uint)(n0 - pos));
            if (n <= 0) continue;
            IntPtr data; rc.GetBuffer((uint)n, out data);
            for (int i = 0; i < n; i++) for (int c = 0; c < 8; c++) tmp[8 * i + c] = ch[c][pos + i];
            Marshal.Copy(tmp, 0, data, n * 8);
            rc.ReleaseBuffer((uint)n, 0);
            pos += n;
        }
        Thread.Sleep(400);
        ac.Stop();
    }

    static float[][] Loop(string id, double seconds, out int rate)
    {
        var ac = Open(id);
        IntPtr fmt; ac.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2); rate = Marshal.ReadInt32(fmt, 4);
        int hr = ac.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero);
        if (hr != 0) throw new Exception("loopback init 0x" + hr.ToString("X8"));
        object svc; Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
        var cap = (IAudioCaptureClient)svc;
        var outp = new List<float>[ch]; for (int c = 0; c < ch; c++) outp[c] = new List<float>();
        var buf = new float[rate * ch];
        ac.Start();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            Thread.Sleep(5);
            uint pk; cap.GetNextPacketSize(out pk);
            while (pk > 0)
            {
                IntPtr data; uint frames, fl; ulong a, b;
                cap.GetBuffer(out data, out frames, out fl, out a, out b);
                int count = (int)frames * ch;
                if ((fl & 2) != 0 || count > buf.Length) for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) outp[c].Add(0);
                else { Marshal.Copy(data, buf, 0, count); for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) outp[c].Add(buf[i * ch + c]); }
                cap.ReleaseBuffer(frames);
                cap.GetNextPacketSize(out pk);
            }
        }
        ac.Stop();
        Marshal.FreeCoTaskMem(fmt);
        return outp.Select(l => l.ToArray()).ToArray();
    }
}
