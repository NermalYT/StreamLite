using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// The personal 3D listening test: footsteps at random spots (front, behind, left, right, above, below), each with
    /// one of a few settings - no 3D at all (plain left/right panning), the KU100 or KEMAR head, cue sharpening off /
    /// 35% / 70% - and you say where you heard it. Blind and shuffled; scored per setting, so you see which one lets
    /// you place sounds best, and whether 3D helps you at all. One more version plays the footsteps the way a 7.1 game
    /// sends them (panned between speakers, no height) through StreamLite's game path: how well the real thing works.
    /// </summary>
    internal static class Personal3D
    {
        public const int Fs = SpatialProcessor.Fs;
        public static readonly string[] Places = { "Front", "Behind", "Left", "Right", "Above", "Below" };

        public sealed class Variant
        {
            public string Name, Hrtf;
            public int Cue;
            public bool Bypass;
            /// <summary>Through a game's 7.1 mix with your current 3D settings - shown for comparison, never picked as a setting.</summary>
            public bool Game71;
            public int Right, Total, FrontBack, UpDown;
            public double Score { get { return Total == 0 ? 0 : (double)Right / Total; } }
        }

        public sealed class Trial { public Variant V; public int Place; public float Az, El; public int Answer = -1; }

        /// <summary>Round 1: no 3D vs the two heads (cue sharpening at 35%), and a 7.1 game's mix with your settings.</summary>
        public static List<Variant> Round1()
        {
            return new List<Variant>
            {
                new Variant { Name = "No 3D (left/right only)", Bypass = true },
                new Variant { Name = "Neumann KU100", Hrtf = HrtfSet.Ku100, Cue = 35 },
                new Variant { Name = "KEMAR", Hrtf = HrtfSet.Kemar, Cue = 35 },
                new Variant { Name = "As a 7.1 game sends it (your 3D settings)", Game71 = true },
            };
        }

        /// <summary>Round 2: the better head with cue sharpening off and at 70% (35% was round 1).</summary>
        public static List<Variant> Round2(Variant head)
        {
            string n = head.Hrtf == HrtfSet.Kemar ? "KEMAR" : "Neumann KU100";
            return new List<Variant>
            {
                new Variant { Name = n + ", cue sharpening off", Hrtf = head.Hrtf, Cue = 0 },
                new Variant { Name = n + ", cue sharpening 70%", Hrtf = head.Hrtf, Cue = 70 },
            };
        }

        /// <summary>Every variant at every place once, shuffled. Above / below sit at a random side.</summary>
        public static List<Trial> Trials(IList<Variant> variants, Random rnd)
        {
            var l = new List<Trial>();
            foreach (var v in variants)
                for (int p = 0; p < Places.Length; p++)
                {
                    float az = p == 0 ? 0 : p == 1 ? 180 : p == 2 ? 90 : p == 3 ? 270 : new[] { 0f, 90f, 180f, 270f }[rnd.Next(4)];
                    float el = p == 4 ? 60 : p == 5 ? -45 : 0;
                    l.Add(new Trial { V = v, Place = p, Az = az, El = el });
                }
            return l.OrderBy(_ => rnd.Next()).ToList();
        }

        /// <summary>Records an answer: right, or which confusion it was.</summary>
        public static void Answer(Trial t, int answer)
        {
            t.Answer = answer;
            t.V.Total++;
            if (answer == t.Place) { t.V.Right++; return; }
            if ((t.Place == 0 && answer == 1) || (t.Place == 1 && answer == 0)) t.V.FrontBack++;
            if ((t.Place == 4 && answer == 5) || (t.Place == 5 && answer == 4)) t.V.UpDown++;
        }

        /// <summary>The best setting (the 7.1 game version isn't one); on a tie the one with 3D, then the one you already use.</summary>
        public static Variant Best(IEnumerable<Variant> vs, string currentHrtf, int currentCue)
        {
            return vs.Where(v => !v.Game71).OrderByDescending(v => v.Score).ThenBy(v => v.Bypass ? 1 : 0).ThenBy(v => v.Hrtf == currentHrtf && v.Cue == currentCue ? 0 : 1).First();
        }

        /// <summary>Two footsteps at (az, el) with one setting, as stereo (interleaved) for the headphones.</summary>
        public static float[] Render(Variant v, float az, float el, SpatialConfig baseCfg)
        {
            const int B = SpatialProcessor.B;
            int frames = Fs * 11 / 10;
            var outp = new float[frames * 2];
            if (v.Bypass)
            {
                // No 3D: the same footsteps panned left/right only (what plain stereo can say about a direction).
                var steps = FootstepSynth.Bank(2, 11);
                double p = Math.Sin(az * Math.PI / 180);                          // +1 left .. -1 right
                float gl = (float)Math.Sqrt((1 + p) / 2), gr = (float)Math.Sqrt((1 - p) / 2);
                for (int s = 0; s < 2; s++)
                    for (int i = 0; i < steps[s].Length && s * 21600 + i < frames; i++)
                    {
                        float x = steps[s][i] * 0.7f;
                        outp[2 * (s * 21600 + i)] = x * gl; outp[2 * (s * 21600 + i) + 1] = x * gr;
                    }
                return Level(outp);
            }
            if (v.Game71) return RenderGame71(az, baseCfg);
            var c = new SpatialConfig
            {
                HrtfKind = v.Hrtf, CustomPath = baseCfg.CustomPath, Room = baseCfg.Room, CueSharpen = v.Cue / 100.0,
                FootstepBoost = 0, LoudTamer = 0, Limiter = true, Render360 = true, Focus360 = 1, MasterDb = 0
            };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { Enabled = false }; c.DirGainDb[d] = 0; }
            var sp = new SpatialProcessor();
            sp.Configure(c);
            var slots = new float[SpatialProcessor.Slots][];
            for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B];
            sp.Process(slots, SpatialProcessor.Source.Silent, l, r);   // builds the head model first
            sp.StartPointTest(az, el);
            for (int pos = 0; pos + B <= frames; pos += B)
            {
                for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, B);
                sp.Process(slots, SpatialProcessor.Source.Silent, l, r);
                for (int i = 0; i < B; i++) { outp[2 * (pos + i)] = l[i]; outp[2 * (pos + i) + 1] = r[i]; }
            }
            return Level(outp);
        }

        /// <summary>
        /// The footsteps as a 7.1 game sends them: panned between the two nearest speakers (elevation is lost - a channel
        /// mix has none), then through StreamLite's game path with your head model, cue sharpening and speaker angles -
        /// the 360 degree renderer works out each sound's angle from the speaker levels, as it does in a game.
        /// </summary>
        static float[] RenderGame71(float az, SpatialConfig baseCfg)
        {
            const int B = SpatialProcessor.B;
            var c = new SpatialConfig
            {
                HrtfKind = baseCfg.HrtfKind, CustomPath = baseCfg.CustomPath, Room = baseCfg.Room, CueSharpen = baseCfg.CueSharpen,
                FrontAngle = baseCfg.FrontAngle, SideAngle = baseCfg.SideAngle, RearAngle = baseCfg.RearAngle, Elevation = baseCfg.Elevation,
                FootstepBoost = 0, LoudTamer = 0, Limiter = true, Render360 = true, Focus360 = baseCfg.Focus360, MasterDb = 0
            };
            foreach (var d in SpatialProcessor.Dirs) { c.DirEq[d] = new EqProfile { Enabled = false }; c.DirGainDb[d] = 0; }
            HrtfSet set;
            try { set = HrtfSet.Get(c.HrtfKind, c.CustomPath); } catch { set = HrtfSet.Get(HrtfSet.Ku100, null); c.HrtfKind = HrtfSet.Ku100; }
            var sp = new SpatialProcessor();
            sp.Configure(c);
            // The 360 degree table builds in the background; a game would hear the speakers until it's ready.
            for (int w = 0; w < 1000 && ContinuousRenderer.Supports(set) && !ContinuousRenderer.IsReady(set); w++) Thread.Sleep(10);
            var slots = new float[SpatialProcessor.Slots][];
            for (int s = 0; s < slots.Length; s++) slots[s] = new float[B];
            var l = new float[B]; var r = new float[B];
            sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);   // builds the 7.1 layout
            int a, b; float ga, gb;
            TestSignal.Pan(az, sp.Radar.SlotAz, out a, out b, out ga, out gb);
            var steps = FootstepSynth.Bank(2, 11);
            var src = new float[Fs * 11 / 10];
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < steps[s].Length && s * 21600 + i < src.Length; i++) src[s * 21600 + i] = steps[s][i] * 0.7f;
            int lat = sp.ExtraLatency, frames = src.Length;
            var outp = new float[frames * 2];
            for (int pos = 0; pos < frames + lat; pos += B)
            {
                for (int s = 0; s < slots.Length; s++) Array.Clear(slots[s], 0, B);
                for (int i = 0; i < B && pos + i < frames; i++) { float x = src[pos + i]; slots[a][i] = x * ga; slots[b][i] += x * gb; }
                sp.Process(slots, SpatialProcessor.Source.Surround71, l, r);
                for (int i = 0; i < B; i++)
                {
                    int o = pos + i - lat;
                    if (o >= 0 && o < frames) { outp[2 * o] = l[i]; outp[2 * o + 1] = r[i]; }
                }
            }
            return Level(outp);
        }

        /// <summary>Every version at the same loudness (louder would seem clearer, whatever the setting).</summary>
        static float[] Level(float[] x)
        {
            double p = 0; int n = 0;
            for (int i = 0; i + 960 <= x.Length; i += 960)
            {
                double b = 0; for (int k = i; k < i + 960; k++) b += x[k] * x[k];
                b /= 960; if (b > 1e-6) { p += b; n++; }
            }
            if (n == 0) return x;
            float g = (float)(0.06 / Math.Sqrt(p / n));
            for (int i = 0; i < x.Length; i++) { float v = x[i] * g; x[i] = v > 0.95f ? 0.95f : v < -0.95f ? -0.95f : v; }
            return x;
        }

        /// <summary>Plays a stereo buffer on an output (shared mode, or through 3D audio's stream while it holds the
        /// headphones), waiting until it's done.</summary>
        public static void Play(string deviceId, float[] stereo, Func<bool> stop = null)
        {
            var direct = HeadphonesDirect.Open(deviceId);
            if (direct != null)
            {
                try { HeadphonesDirect.Play(direct, stereo, stop); }
                finally { HeadphonesDirect.Close(direct); }
                return;
            }
            IMMDevice dev;
            var ac = AudioEngine.OpenClient(deviceId, out dev);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
            object svc = null;
            try
            {
                int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Couldn't play on your headphones (0x" + hr.ToString("X8") + ")", hr);
                uint size; ac.GetBufferSize(out size);
                Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
                var rc = (IAudioRenderClient)svc;
                ac.Start();
                int frames = stereo.Length / 2, done = 0;
                var tmp = new float[size * 2];
                while (done < frames && (stop == null || !stop()))
                {
                    Thread.Sleep(5);
                    uint pad; ac.GetCurrentPadding(out pad);
                    int n = (int)Math.Min(size - pad, frames - done); if (n <= 0) continue;
                    IntPtr data; if (rc.GetBuffer((uint)n, out data) != 0) continue;
                    Array.Copy(stereo, done * 2, tmp, 0, n * 2);
                    Marshal.Copy(tmp, 0, data, n * 2); rc.ReleaseBuffer((uint)n, 0); done += n;
                }
                Thread.Sleep(200);
                ac.Stop();
            }
            finally { Marshal.FreeHGlobal(fmt); if (svc != null) Marshal.ReleaseComObject(svc); Marshal.ReleaseComObject(ac); Marshal.ReleaseComObject(dev); }
        }
    }
}
