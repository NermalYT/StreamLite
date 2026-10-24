using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// "streamlite --selftest": checks libpulse and pactl, creates StreamLite's devices, plays a test tone through the
    /// clean-mic path (a fake microphone) and the 3D path, and checks it comes out - then removes everything.
    /// Uses its own settings folder, so your real settings are never touched.
    /// </summary>
    internal static class SelfTest
    {
        static int fails, dumps;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

        public static int Run(string[] args)
        {
            Environment.SetEnvironmentVariable("STREAMLITE_DATA", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "streamlite-selftest"));
            Console.WriteLine("StreamLite " + Program.Version + " self-test");
            string sys = Core.CheckSystem();
            Check(sys == null, sys ?? "libpulse and pactl found; sound server: " + Pactl.ServerName());
            if (sys != null) return 1;

            // A fake microphone: a null sink whose monitor is the input, fed with a 1 kHz tone.
            int fakeMic = Pactl.LoadModule("module-null-sink", "sink_name=streamlite_selftest_mic", "rate=48000", "channels=1", "channel_map=mono");
            int fakePhones = Pactl.LoadModule("module-null-sink", "sink_name=streamlite_selftest_phones", "rate=48000", "channels=2");
            try
            {
                LinuxAudio.CreateMic(); LinuxAudio.CreateHeadphones(); LinuxAudio.CreateGame();
                var all = Pactl.AllSinks().Select(s => s.Name).ToList();
                var srcs = Pactl.AllSources().Select(s => s.Name).ToList();
                Check(srcs.Contains(LinuxAudio.MicSource), "\"StreamLite Mic\" input created");
                Check(all.Contains(LinuxAudio.Headphones) && all.Contains(LinuxAudio.Game), "\"StreamLite Headphones (EQ)\" and \"StreamLite Game Audio (7.1)\" outputs created");
                Check(Pactl.AllSinks().First(s => s.Name == LinuxAudio.Game).Channels == 8, "game device is 7.1 (8 channels)");

                // Clean mic: tone into the fake mic -> StreamLite -> record "StreamLite Mic".
                var S = new AppSettings(); S.NoiseRemoval = false; S.AutoLevel = false; S.Gate = false; S.Compressor = false;
                S.Game.Repair(); GamePresets.Apply(S.Game, S.Game.Preset);
                var mic = new AudioEngine();
                mic.Chain.Configure(S);
                mic.Start("streamlite_selftest_mic.monitor");
                double level = ToneThrough("streamlite_selftest_mic", 1, LinuxAudio.MicSource, 1);
                Check(level > -30, "clean mic: a 1 kHz tone arrives in \"StreamLite Mic\" (" + level.ToString("0.0") + " dBFS)");
                Check(mic.Status == "Running", "mic engine: " + mic.Status);
                mic.Stop();

                // A 2-input interface with the mic on input 1 only: Auto uses input 1 at full level (a mix would be 6 dB down).
                int fakeIface = Pactl.LoadModule("module-null-sink", "sink_name=streamlite_selftest_iface", "rate=48000", "channels=2", "channel_map=front-left,front-right");
                try
                {
                    S.MicChannel = InputChannels.Auto; mic.Chain.Configure(S);
                    mic.Start("streamlite_selftest_iface.monitor");
                    double lvl = ToneThrough("streamlite_selftest_iface", 2, LinuxAudio.MicSource, 1, leftOnly: true, seconds: 4);
                    // The bursts are ~3 dB below the steady tone; mixing with the empty input would cost another 6 dB.
                    Check(mic.InputChannelCount == 2 && lvl > level - 5, "2-input interface, mic on input 1: " + mic.Chain.Channels.Describe() + " (" + lvl.ToString("0.0") + " dBFS, mono tone gave " + level.ToString("0.0") + ")");
                    mic.Stop();
                }
                finally { Pactl.UnloadModule(fakeIface); }

                // Noise removal on: steady noise is pulled down.
                S.NoiseRemoval = true; S.NsEngine = NoiseEngines.Ai; mic.Chain.Configure(S);
                mic.Start("streamlite_selftest_mic.monitor");
                double noiseIn, noiseOut = NoiseThrough("streamlite_selftest_mic", LinuxAudio.MicSource, out noiseIn);
                Check(noiseOut < noiseIn - 10, "noise removal (RNNoise): steady noise " + noiseIn.ToString("0") + " -> " + noiseOut.ToString("0") + " dBFS");
                mic.Stop();

                if (OnnxSession.Available && DeepFilter.Installed)
                {
                    S.NsEngine = NoiseEngines.DfnCpu; mic.Chain.Configure(S);
                    mic.Start("streamlite_selftest_mic.monitor");
                    for (int i = 0; i < 40 && !(mic.Chain.Suppressor.DfnEngine != null && mic.Chain.Suppressor.DfnEngine.Ready); i++) Thread.Sleep(100);
                    var dfn = mic.Chain.Suppressor.DfnEngine;
                    Check(dfn != null && dfn.Ready && dfn.Error == null, "DeepFilterNet 3 loads on the CPU" + (dfn != null && dfn.Error != null ? ": " + dfn.Error : dfn != null ? " (" + dfn.LoadMs + " ms)" : ""));
                    double dIn, dOut = NoiseThrough("streamlite_selftest_mic", LinuxAudio.MicSource, out dIn);
                    Check(dOut < dIn - 10, "noise removal (DeepFilterNet): steady noise " + dIn.ToString("0") + " -> " + dOut.ToString("0") + " dBFS" + (dfn != null ? ", " + dfn.AvgMs.ToString("0.0") + " ms per 10 ms" : ""));
                    mic.Stop();
                }
                else Console.WriteLine("  (skipped DeepFilterNet: libonnxruntime.so or the model isn't next to the program)");

                // 3D processor on its own (no devices): footsteps in, sane stereo out.
                foreach (var kind in new[] { HrtfSet.Kemar, HrtfSet.Ku100 })
                {
                    var sp = new SpatialProcessor();
                    var cfg = S.Game.ToConfig(); cfg.HrtfKind = kind; sp.Configure(cfg);
                    sp.StartTest(true);
                    var slotsT = new float[SpatialProcessor.Slots][]; for (int i = 0; i < slotsT.Length; i++) slotsT[i] = new float[SpatialProcessor.B];
                    var lo = new float[SpatialProcessor.B]; var ro = new float[SpatialProcessor.B];
                    double pk = 0, e = 0; long cnt = 0; bool bad = false;
                    for (int k = 0; k < 600; k++)
                    {
                        sp.Process(slotsT, SpatialProcessor.Source.Surround71, lo, ro);
                        for (int i = 0; i < lo.Length; i++) { if (float.IsNaN(lo[i]) || float.IsInfinity(lo[i])) bad = true; pk = Math.Max(pk, Math.Abs(lo[i])); e += lo[i] * lo[i]; cnt++; }
                    }
                    Check(!bad && pk > 0.01 && pk < 1 && 10 * Math.Log10(e / cnt) < -6, "3D processor (" + kind + ", offline): peak " + pk.ToString("0.00") + ", level " + (10 * Math.Log10(e / cnt + 1e-20)).ToString("0") + " dBFS" + (bad ? ", NaN/Inf!" : "") + ", 360: " + sp.Rendering360);
                }

                // 3D: the footstep test through the game path, recorded from the (fake) headphones.
                var game = new GamePath();
                game.Processor.Configure(S.Game.ToConfig());
                game.Start(LinuxAudio.Game, "streamlite_selftest_phones");
                game.Processor.StartTest(true);
                double l, r; Record("streamlite_selftest_phones.monitor", 2, 3.0, out l, out r);
                Check(Math.Max(l, r) > -45, "3D game audio: test footsteps reach the headphones (L " + l.ToString("0") + " / R " + r.ToString("0") + " dBFS)");
                Check(game.Status == "Running", "3D engine: " + game.Status);
                game.Processor.StopTest(); game.Stop();

                // Headphone EQ path: tone into "StreamLite Headphones (EQ)" comes out of the headphones.
                var eq = new EqPath();
                eq.SetEq(new EqProfile());
                eq.Start(LinuxAudio.Headphones, "streamlite_selftest_phones");
                double t = ToneThrough(LinuxAudio.Headphones, 2, "streamlite_selftest_phones.monitor", 2);
                Check(t > -30, "headphone EQ path: sound passes through (" + t.ToString("0.0") + " dBFS)");
                eq.Stop();
            }
            catch (Exception ex) { Check(false, "error: " + ex.Message); }
            finally
            {
                LinuxAudio.RemoveAll();
                Pactl.UnloadModule(fakeMic); Pactl.UnloadModule(fakePhones);
            }
            if (args.Contains("--unplug")) Unplug();
            var left = Pactl.AllSinks().Select(s => s.Name).Concat(Pactl.AllSources().Select(s => s.Name)).Where(n => n.StartsWith("streamlite_")).ToList();
            Check(left.Count == 0, "everything removed again" + (left.Count > 0 ? ": " + string.Join(", ", left) : ""));
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return fails == 0 ? 0 : 1;
        }

        /// <summary>
        /// The running app with two "headsets" (null sinks named like real devices): unplug the one in use while it plays,
        /// then pick the other in the sound settings. StreamLite must never end up playing into its own device (a
        /// feedback loop) and must follow to a real output both times.
        /// </summary>
        static void Unplug()
        {
            int a = Pactl.LoadModule("module-null-sink", "sink_name=sltest_headset_a", "rate=48000", "channels=2", "sink_properties=device.description=Headset-A");
            int b = Pactl.LoadModule("module-null-sink", "sink_name=sltest_headset_b", "rate=48000", "channels=2", "sink_properties=device.description=Headset-B");
            string prevDefault = Pactl.DefaultSink();
            Core core = null;
            try
            {
                Pactl.SetDefaultSink("sltest_headset_a");
                core = new Core();
                core.S.OutputDeviceId = "sltest_headset_a"; core.S.HeadphoneEq = true; core.S.Game.Enabled = true; core.S.VirtualMicOn = false;
                core.Start();
                Thread.Sleep(1500);
                Check(core.Eq.Output == "sltest_headset_a" && core.Game.Output == "sltest_headset_a", "unplug test: playing to Headset-A");
                if (Environment.GetEnvironmentVariable("STREAMLITE_TRACE") == "1") Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " unplugging");
                Pactl.UnloadModule(a); a = -1;              // pull the plug
                bool moved = false; var sw = System.Diagnostics.Stopwatch.StartNew(); long loopUntil = -1; string loopWhat = null;
                while (sw.ElapsedMilliseconds < 3000 && !(moved && sw.ElapsedMilliseconds > loopUntil + 500))
                {
                    var all = Pactl.AllSinks();
                    var ours = all.Where(d => LinuxAudio.IsOurs(d.Name) && d.Name != LinuxAudio.MicFeed).ToDictionary(d => d.Index.ToString(), d => d.Name);
                    var bad = Pactl.SinkInputs().FirstOrDefault(x => x.AppName == "StreamLite" && x.Sink != null && ours.ContainsKey(x.Sink));
                    if (bad != null) { loopUntil = sw.ElapsedMilliseconds; loopWhat = bad.Media + " -> " + ours[bad.Sink]; }
                    moved = core.Eq.Output != null && core.Eq.Output != "sltest_headset_a" && !LinuxAudio.IsOurs(core.Eq.Output) && core.Eq.Running;
                }
                // The server may park a stream on StreamLite's device for an instant (it carries silence by then); it must not stay there.
                Check(moved && loopUntil < 1000, "Headset-A unplugged: sound moves to a real output (" + core.Eq.Output + ")" +
                    (loopWhat != null ? ", silent stream parked on StreamLite's device until " + loopUntil + " ms (" + loopWhat + ")" : ", never parked"));
                // Headset-A plugged back in: StreamLite goes back to it (it's the one you chose).
                a = Pactl.LoadModule("module-null-sink", "sink_name=sltest_headset_a", "rate=48000", "channels=2", "sink_properties=device.description=Headset-A");
                Func<string, bool> settled = name => core.Eq.Output == name && core.Game.Output == name && core.Eq.Running && core.Game.Running && LinuxAudio.IsOurs(Pactl.DefaultSink());
                bool back = false;
                for (int i = 0; i < 40 && !back; i++) { Thread.Sleep(100); back = settled("sltest_headset_a"); }
                Check(back, "Headset-A plugged back in: StreamLite goes back to it (" + core.Eq.Output + ")");
                Pactl.SetDefaultSink("sltest_headset_b");   // you pick Headset-B in the sound settings
                bool followed = false;
                for (int i = 0; i < 40 && !followed; i++) { Thread.Sleep(100); followed = settled("sltest_headset_b"); }
                Check(followed, "Headset-B picked in the sound settings: StreamLite plays there and keeps its device as the default (" + core.Eq.Output + ", default " + Pactl.DefaultSink() + ")");
            }
            catch (Exception ex) { Check(false, "unplug test: " + ex.Message); }
            finally
            {
                if (core != null) core.Dispose();
                if (a >= 0) Pactl.UnloadModule(a);
                Pactl.UnloadModule(b);
                try { if (prevDefault != null) Pactl.SetDefaultSink(prevDefault); } catch { }
            }
        }

        /// <summary>Plays a 1 kHz tone into a sink for 2 s while recording a source; returns the recorded level.</summary>
        static double ToneThrough(string sink, int playCh, string source, int recCh, bool leftOnly = false, double seconds = 2.5)
        {
            double level = -200;
            // Recording waits out the first part, so a mic that has to pick its input first is measured after it has.
            var rec = new Thread(() => { if (seconds > 3) Thread.Sleep((int)((seconds - 2.5) * 1000)); double a, b; Record(source, recCh, 2.0, out a, out b); level = Math.Max(a, b); });
            rec.Start();
            using (var p = PulseStream.Play(sink, playCh, "selftest tone", 40))
            {
                var buf = new float[480 * playCh]; double ph = 0;
                for (int k = 0; k < (int)(seconds * 100); k++)
                {
                    for (int i = 0; i < 480; i++)
                    {
                        // Syllable-like bursts (4 per second), so the input picker hears a voice and not a constant tone.
                        double env = leftOnly ? Math.Max(0.2, Math.Abs(Math.Sin(Math.PI * 4 * (k * 480 + i) / 48000.0))) : 1;
                        float v = (float)(0.3 * env * Math.Sin(ph)); ph += 2 * Math.PI * 1000 / 48000;
                        for (int c = 0; c < playCh; c++) buf[i * playCh + c] = leftOnly && c > 0 ? 0f : v;
                    }
                    p.Write(buf, 480);
                }
            }
            rec.Join();
            return level;
        }

        /// <summary>Plays steady noise into the fake mic for 4 s; returns input and output levels of the last second.</summary>
        static double NoiseThrough(string sink, string source, out double inDb)
        {
            double outDb = -200;
            var rnd = new Random(1);
            var rec = new Thread(() => { Thread.Sleep(2500); double a, b; Record(source, 1, 1.2, out a, out b); outDb = a; });
            rec.Start();
            double e = 0; long n = 0;
            using (var p = PulseStream.Play(sink, 1, "selftest noise", 40))
            {
                var buf = new float[480];
                for (int k = 0; k < 420; k++)
                {
                    for (int i = 0; i < 480; i++) { buf[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.05); e += buf[i] * buf[i]; n++; }
                    p.Write(buf, 480);
                }
            }
            rec.Join();
            inDb = 10 * Math.Log10(e / n);
            return outDb;
        }

        static void Record(string source, int ch, double seconds, out double lDb, out double rDb)
        {
            double el = 0, er = 0; long n = 0;
            string dump = Environment.GetEnvironmentVariable("STREAMLITE_DUMP");
            var dumpFile = dump != null && ch == 2 ? new System.IO.BinaryWriter(System.IO.File.Create(dump + "." + (++dumps))) : null;
            using (var r = PulseStream.Record(source, ch, "selftest", 20))
            {
                var buf = new float[480 * ch];
                int blocks = (int)(seconds * 100);
                for (int k = 0; k < blocks; k++)
                {
                    r.Read(buf, 480);
                    if (dumpFile != null) foreach (var v in buf) dumpFile.Write(v);
                    if (k < blocks / 4) continue;
                    for (int i = 0; i < 480; i++) { el += buf[i * ch] * buf[i * ch]; er += buf[i * ch + ch - 1] * buf[i * ch + ch - 1]; n++; }
                }
            }
            if (dumpFile != null) dumpFile.Dispose();
            lDb = 10 * Math.Log10(el / Math.Max(1, n) + 1e-20); rDb = 10 * Math.Log10(er / Math.Max(1, n) + 1e-20);
        }
    }
}
