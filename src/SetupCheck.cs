using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Audio;
using StreamLite.Interop;

namespace StreamLite
{
    /// <summary>One line of the setup check: what was checked, how it went, and a fix when there is one.</summary>
    internal sealed class CheckItem
    {
        public const int Ok = 0, Warn = 1, Fail = 2, Info = 3;
        public string Title, Detail;
        public int State;
        /// <summary>A fix the window knows how to do (install-vbcable, install-hifi, install-apo, apo-devices,
        /// mic-privacy, mic-volume, restart-audio, restart-3d, default-mic, sound-settings), with its button text.</summary>
        public string FixId, FixLabel;
    }

    /// <summary>
    /// The one-click setup check: drivers, the physical mic, the virtual mic end to end (a short test tone, recorded
    /// back the way Discord hears it), headphones end to end, Windows' default devices, which mic Discord / OBS really
    /// record from, and the camera. Runs on a background thread; takes a few seconds.
    /// </summary>
    internal static class SetupCheck
    {
        const int Fs = 48000;

        public static List<CheckItem> Run(AppCore core, bool tones, Action<string> progress)
        {
            var r = new List<CheckItem>();
            Action<string, int, string, string, string> add = (t, st, d, fix, label) => r.Add(new CheckItem { Title = t, State = st, Detail = d, FixId = fix, FixLabel = label });
            var S = core.S;
            progress("Drivers...");
            var cable = core.FindVirtualOutput();
            if (cable == null) add("Virtual mic driver", CheckItem.Fail, "VB-CABLE isn't installed - it's how apps get your clean mic (as \"" + AppCore.MicName + "\").", "install-vbcable", "Install VB-CABLE");
            else add("Virtual mic driver", CheckItem.Ok, "VB-CABLE is installed.", null, null);
            bool wantsGame = S.Game.Enabled || core.MultiOut;
            var sink = core.FindGameSink();
            if (sink == null) add("Game audio driver", wantsGame ? CheckItem.Fail : CheckItem.Info, "VB-Audio Hi-Fi Cable isn't installed - needed for 3D audio and for several outputs.", "install-hifi", "Install Hi-Fi Cable");
            else add("Game audio driver", CheckItem.Ok, "Hi-Fi Cable is installed (\"" + AppCore.GameOutName + "\").", null, null);
            string phones = core.GamePhonesId ?? S.Game.HeadphonesId ?? core.RealDefaultOutput();
            if (!EqualizerApo.IsInstalled) add("Headphone EQ engine", CheckItem.Warn, "Equalizer APO isn't installed: headphone corrections and game profiles can't change the sound.", "install-apo", "Install Equalizer APO");
            else
            {
                bool? on = phones != null ? EqualizerApo.IsEnabledForDevice(phones) : null;
                if (on == false) add("Headphone EQ engine", CheckItem.Warn, "Equalizer APO isn't switched on for your headphones - tick them in its device list and restart Windows.", "apo-devices", "Open its device list");
                else add("Headphone EQ engine", CheckItem.Ok, "Equalizer APO is installed" + (on == true ? " and on for your headphones." : "."), null, null);
            }

            progress("Microphone...");
            var e = core.Engine;
            if (!S.VirtualMicOn) add("Microphone", CheckItem.Warn, "The virtual mic is switched off (Microphone page, top right) - apps hear nothing from StreamLite.", null, null);
            else if (DeviceErrors.MicBlocked()) add("Microphone", CheckItem.Fail, "Windows' privacy settings block desktop apps from the microphone.", "mic-privacy", "Open privacy settings");
            else if (!e.Running || !e.Status.StartsWith("Running")) add("Microphone", CheckItem.Fail, "Your mic isn't running: " + e.Status, "restart-audio", "Restart the mic");
            else
            {
                float peak = -100; for (int i = 0; i < 12; i++) { peak = Math.Max(peak, e.InputLevelDb); Thread.Sleep(100); }
                if (peak <= -99) add("Microphone", CheckItem.Warn, "Your mic sends pure silence - is it muted (a button or switch on the mic or headset), or is its gain at zero?", null, null);
                else add("Microphone", CheckItem.Ok, (e.ExclusiveCapture ? "Running in exclusive mode (" + e.PacketMs.ToString("0") + " ms packets)" : "Running in shared mode (" + (e.ExclusiveError ?? "10 ms packets") + ")") + ", level " + peak.ToString("0") + " dB.", null, null);
            }

            progress("Virtual mic, end to end...");
            var feed = core.PairedCapture(core.ActiveOutId);
            if (S.VirtualMicOn && e.Running && feed != null)
            {
                float vol, db;
                if (AudioDevices.GetVolume(feed.Id, out vol, out db) && vol < 0.99f)
                    add("Virtual mic volume", CheckItem.Warn, "Windows has \"" + AppCore.MicName + "\" at " + (vol * 100).ToString("0") + " (" + db.ToString("0.0") + " dB): apps hear you that much quieter.", "mic-volume", "Set it to 100");
                bool busy = AudioSessions.AnyoneRecording(feed.Id);
                bool tone = tones && !busy && !e.Muted && !e.PttClosed;
                if (tone) e.TestTone = true;
                float[] rec = null; string err = null;
                try { Thread.Sleep(tone ? 150 : 0); rec = Record(feed.Id, 1.0); } catch (Exception ex) { err = ex.Message; }
                e.TestTone = false;
                if (rec == null) add("Virtual mic, end to end", CheckItem.Fail, "Couldn't record \"" + AppCore.MicName + "\" the way apps do: " + err, "restart-audio", "Restart the mic");
                else if (rec.Length < Fs * 0.85) add("Virtual mic, end to end", CheckItem.Fail, "\"" + AppCore.MicName + "\" delivered only " + (rec.Length * 100.0 / Fs).ToString("0") + "% of the sound it should - apps would hear you choppy.", "restart-audio", "Restart the mic");
                else if (tone)
                {
                    double t = ToneDb(rec, 1000);
                    if (t > -30) add("Virtual mic, end to end", CheckItem.Ok, "A test tone sent through StreamLite came out of \"" + AppCore.MicName + "\" at " + t.ToString("0") + " dB - Discord, OBS and games hear exactly what StreamLite sends.", null, null);
                    else add("Virtual mic, end to end", CheckItem.Fail, "A test tone sent through StreamLite didn't come out of \"" + AppCore.MicName + "\" (" + t.ToString("0") + " dB). The cable may be stuck - restarting the mic usually fixes it.", "restart-audio", "Restart the mic");
                }
                else add("Virtual mic, end to end", CheckItem.Ok, "Sound flows at the full rate" + (busy ? " (the test tone was skipped: an app is recording your mic right now)." : "."), null, null);
            }

            progress("Headphones...");
            if (phones == null) add("Headphones", CheckItem.Fail, "No headphones or speakers found.", null, null);
            else if (wantsGame && core.Game.Running && sink != null && tones)
            {
                // A quiet tone into the game device, recorded back from the headphones (what you hear).
                float[] rec = null; string err = null;
                var th = new Thread(() => { try { rec = Record(phones, 1.2, true); } catch (Exception ex) { err = ex.Message; } });
                th.Start(); Thread.Sleep(200);
                try { PlayTone(sink.Id, 0.7, 0.01f); } catch (Exception ex) { err = err ?? ex.Message; }
                th.Join();
                double t = rec != null ? ToneDb(rec, 700) : -200;
                if (t > -75) add("Headphones, end to end", CheckItem.Ok, "A quiet test tone played into \"" + AppCore.GameOutName + "\" reached " + core.GamePhonesName + (core.MultiOut ? " and your other outputs." : "."), null, null);
                else add("Headphones, end to end", CheckItem.Fail, "A test tone played into \"" + AppCore.GameOutName + "\" didn't reach your headphones" + (err != null ? " (" + err + ")" : "") + ".", "restart-3d", "Restart 3D audio");
            }
            else add("Headphones", CheckItem.Ok, (core.GamePhonesName ?? "Your default output") + " is connected.", null, null);
            if (wantsGame)
            {
                string spatial = core.SpatialCheck(sink != null ? sink.Id : null, phones);
                core.SpatialWarning = spatial;
                if (spatial != null) add("Windows spatial sound", CheckItem.Warn, spatial, "sound-settings", "Open Sound settings");
                else add("Windows spatial sound", CheckItem.Ok, "Off where it would fight StreamLite's 3D - games hand StreamLite their 7.1 mix untouched.", null, null);
            }

            progress("Windows default devices...");
            string defOut = AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console);
            if (wantsGame && sink != null && defOut != sink.Id) add("Windows playback default", CheckItem.Fail, "Windows plays into " + Name(defOut) + " instead of \"" + AppCore.GameOutName + "\" - games skip " + (S.Game.Enabled ? "3D audio." : "your other outputs."), "restart-3d", "Fix it");
            else add("Windows playback default", CheckItem.Ok, Name(defOut) + (wantsGame ? " - games go through StreamLite." : "."), null, null);
            string defIn = AudioDevices.GetDefaultId(EDataFlow.Capture, ERole.Console);
            if (feed != null && defIn != feed.Id)
                add("Windows recording default", CheckItem.Info, "Apps set to \"Default\" use " + Name(defIn, EDataFlow.Capture) + ", not your clean mic. Pick \"" + AppCore.MicName + "\" in each app, or make it the default.", "default-mic", "Make it the default");
            else if (feed != null) add("Windows recording default", CheckItem.Ok, "\"" + AppCore.MicName + "\" - apps set to Default get your clean mic.", null, null);

            progress("Discord and OBS...");
            var rec2 = AudioSessions.All(EDataFlow.Capture);
            foreach (var app in new[] { Tuple.Create("Discord", new[] { "Discord", "DiscordPTB", "DiscordCanary", "Vesktop" }, "Settings > Voice & Video > Input Device"),
                                        Tuple.Create("OBS", new[] { "obs64", "obs32", "obs" }, "Settings > Audio > Mic/Auxiliary Audio (or the source's properties)") })
            {
                var mine = rec2.Where(x => app.Item2.Contains(x.Process, StringComparer.OrdinalIgnoreCase)).ToList();
                bool running = System.Diagnostics.Process.GetProcesses().Any(p => { try { return app.Item2.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });
                if (!running) { add(app.Item1, CheckItem.Info, app.Item1 + " isn't running - start it to check which mic it uses.", null, null); continue; }
                var active = mine.Where(x => x.Active).ToList();
                var use = active.Count > 0 ? active : mine;
                if (use.Count == 0) { add(app.Item1, CheckItem.Info, app.Item1 + " isn't using a mic right now - " + (app.Item1 == "OBS" ? "add or open a mic source" : "join a voice channel or call") + ", then check again.", null, null); continue; }
                if (feed != null && use.All(x => x.DeviceId == feed.Id)) add(app.Item1, CheckItem.Ok, app.Item1 + " records from \"" + AppCore.MicName + "\" - it gets your clean mic.", null, null);
                else add(app.Item1, CheckItem.Fail, app.Item1 + " records from " + string.Join(", ", use.Where(x => feed == null || x.DeviceId != feed.Id).Select(x => Name(x.DeviceId, EDataFlow.Capture)).Distinct()) +
                    " - not your clean mic. In " + app.Item1 + ": " + app.Item3 + " -> \"" + AppCore.MicName + "\".", null, null);
            }

            progress("Camera...");
            var cam = core.CamFx;
            if (cam == null || !S.CamFx.Enabled) add("Camera", CheckItem.Info, "StreamLite Camera is off.", null, null);
            else if (cam.Error != null) add("Camera", CheckItem.Warn, "StreamLite Camera: " + cam.Error, null, null);
            else add("Camera", cam.Status.StartsWith("Needs") ? CheckItem.Info : cam.Status.Contains("missing") || cam.Status.StartsWith("Virtual camera unavailable") ? CheckItem.Warn : CheckItem.Ok, "StreamLite Camera: " + cam.Status, null, null);
            return r;
        }

        static string Name(string id, EDataFlow flow = EDataFlow.Render)
        {
            if (id == null) return "nothing";
            var d = AudioDevices.List(flow).FirstOrDefault(x => x.Id == id);
            return d != null ? "\"" + d.Name + "\"" : "another device";
        }

        /// <summary>Level of a tone (dB, its RMS) in a recording, by correlating with that frequency.</summary>
        static double ToneDb(float[] x, double hz)
        {
            double re = 0, im = 0;
            for (int i = 0; i < x.Length; i++) { double a = 2 * Math.PI * hz * i / Fs; re += x[i] * Math.Cos(a); im += x[i] * Math.Sin(a); }
            double amp = 2 * Math.Sqrt(re * re + im * im) / Math.Max(1, x.Length);
            return 20 * Math.Log10(amp / Math.Sqrt(2) + 1e-12);
        }

        static IAudioClient Open(string id)
        {
            IMMDevice dev;
            return AudioEngine.OpenClient(id, out dev);
        }

        /// <summary>Records a capture device (or, loopback, what an output plays) in shared mode, mono at 48 kHz.</summary>
        static float[] Record(string id, double secs, bool loopback = false)
        {
            var ac = Open(id);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, Fs, true, 32);
            object svc = null;
            try
            {
                int hr = ac.Initialize(0, (loopback ? 0x00020000 : 0) | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException(DeviceErrors.Describe(hr, true), hr);
                Guid ci = CoreAudioConst.IID_IAudioCaptureClient; ac.GetService(ref ci, out svc);
                var cap = (IAudioCaptureClient)svc; var outp = new List<float>(); var buf = new float[Fs * 2];
                ac.Start();
                var end = DateTime.UtcNow.AddSeconds(secs);
                while (DateTime.UtcNow < end)
                {
                    Thread.Sleep(10);
                    uint packet; cap.GetNextPacketSize(out packet);
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong p1, p2;
                        if (cap.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) break;
                        int n = (int)frames;
                        if ((flags & 2) != 0) Array.Clear(buf, 0, n * 2); else Marshal.Copy(data, buf, 0, n * 2);
                        for (int i = 0; i < n; i++) outp.Add(0.5f * (buf[2 * i] + buf[2 * i + 1]));
                        cap.ReleaseBuffer(frames);
                        cap.GetNextPacketSize(out packet);
                    }
                }
                ac.Stop();
                return outp.ToArray();
            }
            finally { Marshal.FreeHGlobal(fmt); if (svc != null) Marshal.ReleaseComObject(svc); Marshal.ReleaseComObject(ac); }
        }

        /// <summary>A 700 Hz tone into an output (shared mode, front left/right), faded in and out.</summary>
        static void PlayTone(string id, double secs, float amp)
        {
            int ch, mask, rate;
            if (!AudioDevices.MixFormat(id, out ch, out mask, out rate)) { ch = 2; mask = 3; }
            var ac = Open(id);
            IntPtr fmt = AudioDevices.MakeFormat(ch, mask, Fs, true, 32);
            object svc = null;
            try
            {
                int hr = ac.Initialize(0, CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM | CoreAudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, fmt, IntPtr.Zero);
                if (hr != 0) throw new COMException("Couldn't play into the game device (0x" + hr.ToString("X8") + ")", hr);
                uint size; ac.GetBufferSize(out size);
                Guid rid = CoreAudioConst.IID_IAudioRenderClient; ac.GetService(ref rid, out svc);
                var rc = (IAudioRenderClient)svc; var tmp = new float[size * ch];
                ac.Start();
                long total = (long)(secs * Fs), done = 0;
                while (done < total)
                {
                    Thread.Sleep(5);
                    uint pad; ac.GetCurrentPadding(out pad);
                    int n = (int)Math.Min(size - pad, total - done); if (n <= 0) continue;
                    IntPtr data; rc.GetBuffer((uint)n, out data);
                    Array.Clear(tmp, 0, n * ch);
                    for (int i = 0; i < n; i++)
                    {
                        long k = done + i;
                        double fade = Math.Min(1, Math.Min(k, total - k) / 960.0);
                        float v = (float)(amp * fade * Math.Sin(2 * Math.PI * 700 * k / Fs));
                        tmp[i * ch] = v; if (ch > 1) tmp[i * ch + 1] = v;
                    }
                    Marshal.Copy(tmp, 0, data, n * ch); rc.ReleaseBuffer((uint)n, 0); done += n;
                }
                Thread.Sleep(250);
                ac.Stop();
            }
            finally { Marshal.FreeHGlobal(fmt); if (svc != null) Marshal.ReleaseComObject(svc); Marshal.ReleaseComObject(ac); }
        }
    }
}
