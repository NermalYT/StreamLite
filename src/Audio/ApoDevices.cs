using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StreamLite.Audio
{
    /// <summary>
    /// Puts Equalizer APO on a sound device and takes it off, the way its own device list does (DeviceSelector; its
    /// source's DeviceAPOInfo::install / uninstall) - so StreamLite does it for the headphones you pick and nobody sees
    /// that window (4.0.2). The device's own effects are kept under SOFTWARE\EqualizerAPO\Child APOs\{device} and
    /// Equalizer APO runs them as before; taking it off puts them back. Needs admin rights: Setup asks Windows once and
    /// does everything in one go (installing Equalizer APO too, when it isn't there).
    /// </summary>
    internal static class ApoDevices
    {
        public const string PreMixApo = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", PostMixApo = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
        const string NoKey = "!KEY", NoValue = "!VALUE";
        // The device's effect slots: LFX, GFX (Windows 7 style), SFX, MFX, EFX.
        const string Fx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},";
        static readonly string[] Slots = { Fx + "1", Fx + "2", Fx + "5", Fx + "6", Fx + "7" };
        const int Lfx = 0, Gfx = 1, Sfx = 2, Mfx = 3, Efx = 4;
        static readonly string[] MultiSlots = { Fx + "13", Fx + "14", Fx + "15" };
        const string Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},", DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
        const string DisableEnhancements = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        const string CombinedDevice = "{b3f8fa53-0004-438e-9003-51a46e139bfc},41";   // a Bluetooth headset Windows 11 joined into one
        const string FxTitle = "{b725f130-47ef-101a-a5f1-02608c9eebac},10";
        enum Mode { LfxGfx, SfxMfx, SfxEfx }

        // ------------------------------------------------------------------ on and off (any registry: tests use a copy)

        static string DevicePath(string guid, bool input) { return (input ? "Capture\\" : "Render\\") + guid; }

        /// <summary>Equalizer APO is on this device (one of its slots runs it).</summary>
        public static bool IsAttached(RegistryKey audio, string guid, bool input)
        {
            using (var fx = audio.OpenSubKey(DevicePath(guid, input) + "\\FxProperties"))
                return fx != null && Slots.Any(s => IsApo(fx.GetValue(s) as string));
        }

        static bool IsApo(string v) { return v != null && (v.Equals(PreMixApo, StringComparison.OrdinalIgnoreCase) || v.Equals(PostMixApo, StringComparison.OrdinalIgnoreCase)); }

        /// <summary>
        /// Puts Equalizer APO on the device (audio = ...\MMDevices\Audio, apo = SOFTWARE\EqualizerAPO). Before and after
        /// the mix on outputs, before only on inputs - with the device's own effects kept running inside it. false = it
        /// already was.
        /// </summary>
        public static bool Attach(RegistryKey audio, RegistryKey apo, string guid, bool input)
        {
            string path = DevicePath(guid, input);
            using (var dev = audio.OpenSubKey(path)) if (dev == null) throw new ArgumentException("No such sound device: " + guid);
            if (IsAttached(audio, guid, input)) return false;
            var orig = new string[Slots.Length];
            Mode mode;
            using (var child = apo.CreateSubKey("Child APOs\\" + guid))
            {
                var fx = OpenFx(audio, path);
                if (fx == null)
                {
                    // A device without effects of its own: the key is new (and goes again when it's taken off).
                    using (var dev = audio.OpenSubKey(path, true)) fx = dev.CreateSubKey("FxProperties");
                    fx.SetValue(FxTitle, "Equalizer APO");
                    for (int i = 0; i < orig.Length; i++) orig[i] = NoKey;
                    mode = Mode.LfxGfx;
                }
                else
                {
                    for (int i = 0; i < orig.Length; i++) orig[i] = fx.GetValue(Slots[i]) as string ?? NoValue;
                    var names = fx.GetValueNames();
                    Func<string, bool> has = n => names.Contains(n, StringComparer.OrdinalIgnoreCase);
                    bool onlyOld = (has(Slots[Lfx]) || has(Slots[Gfx])) && !has(Slots[Sfx]) && !has(Slots[Mfx]) && !has(Slots[Efx]) && !MultiSlots.Any(has);
                    bool combined;
                    using (var props = audio.OpenSubKey(path + "\\Properties")) combined = props != null && props.GetValue(CombinedDevice) != null;
                    // Only Windows 7 style effects: there. A joined Bluetooth headset: after the mix (EFX doesn't run on it).
                    mode = onlyOld ? Mode.LfxGfx : combined ? Mode.SfxMfx : Mode.SfxEfx;
                }
                using (fx)
                {
                    for (int i = 0; i < orig.Length; i++) child.SetValue(Slots[i], orig[i]);
                    child.SetValue("PreMixChild", OriginalPre(orig, mode));
                    child.SetValue("PostMixChild", input ? "" : OriginalPost(orig, mode));
                    child.SetValue("AllowSilentBufferModification", "false");
                    child.DeleteValue("DisableAutomaticAdjustment", false);
                    child.SetValue("Version", "2");
                    if (mode == Mode.LfxGfx)
                    {
                        fx.SetValue(Slots[Lfx], PreMixApo);
                        if (!input) fx.SetValue(Slots[Gfx], PostMixApo);
                        foreach (int s in new[] { Sfx, Mfx, Efx }) fx.DeleteValue(Slots[s], false);
                    }
                    else
                    {
                        fx.DeleteValue(Slots[Lfx], false); fx.DeleteValue(Slots[Gfx], false);
                        Set(fx, Sfx, PreMixApo);
                        if (!input) Set(fx, mode == Mode.SfxMfx ? Mfx : Efx, PostMixApo);
                    }
                    fx.DeleteValue(DisableEnhancements, false);   // Windows' "disable all enhancements" would switch it off
                }
            }
            return true;
        }

        /// <summary>The device's effects key, to change its values - all administrators may do (no new keys under it).</summary>
        static RegistryKey OpenFx(RegistryKey audio, string path)
        {
            return audio.OpenSubKey(path + "\\FxProperties", RegistryKeyPermissionCheck.ReadWriteSubTree,
                System.Security.AccessControl.RegistryRights.ReadKey | System.Security.AccessControl.RegistryRights.SetValue);
        }

        static void Set(RegistryKey fx, int slot, string apoGuid)
        {
            fx.SetValue(Slots[slot], apoGuid);
            string modes = Modes + (slot == Sfx ? "5" : slot == Mfx ? "6" : "7");
            if (fx.GetValue(modes) == null) fx.SetValue(modes, new[] { DefaultMode }, RegistryValueKind.MultiString);
        }

        // The device's own effect that Equalizer APO runs inside itself, before and after the mix.
        static string OriginalPre(string[] o, Mode m)
        {
            string g = m == Mode.LfxGfx ? (o[Lfx] == NoValue && o[Gfx] == NoValue ? o[Sfx] : o[Lfx])
                : o[Sfx] == NoValue && o[m == Mode.SfxMfx ? Mfx : Efx] == NoValue ? o[Lfx] : o[Sfx];
            return g == NoKey || g == NoValue ? "" : g;
        }

        static string OriginalPost(string[] o, Mode m)
        {
            string g = m == Mode.LfxGfx ? (o[Lfx] == NoValue && o[Gfx] == NoValue ? o[Mfx] : o[Gfx])
                : o[Sfx] == NoValue && o[m == Mode.SfxMfx ? Mfx : Efx] == NoValue ? o[Gfx] : o[m == Mode.SfxMfx ? Mfx : Efx];
            return g == NoKey || g == NoValue ? "" : g;
        }

        /// <summary>Takes Equalizer APO off the device and puts its own effects back. false = it wasn't on.</summary>
        public static bool Detach(RegistryKey audio, RegistryKey apo, string guid, bool input)
        {
            string path = DevicePath(guid, input);
            if (!IsAttached(audio, guid, input)) return false;
            using (var child = apo.OpenSubKey("Child APOs\\" + guid))
            using (var fx = OpenFx(audio, path))
            {
                if (child == null)
                {
                    // Nothing kept (put on by something else): just take Equalizer APO out of the slots.
                    foreach (var s in Slots) if (IsApo(fx.GetValue(s) as string)) fx.DeleteValue(s, false);
                    return true;
                }
                var orig = Slots.Select(s => child.GetValue(s) as string ?? (IsApo(fx.GetValue(s) as string) ? NoValue : fx.GetValue(s) as string ?? NoValue)).ToArray();
                if (orig[Lfx] == NoKey) { fx.Close(); audio.DeleteSubKeyTree(path + "\\FxProperties", false); }
                else
                    for (int i = 0; i < Slots.Length; i++)
                    {
                        if (orig[i] == NoValue) fx.DeleteValue(Slots[i], false);
                        else if (orig[i] != "") fx.SetValue(Slots[i], orig[i]);
                    }
            }
            apo.DeleteSubKeyTree("Child APOs\\" + guid, false);
            using (var all = apo.OpenSubKey("Child APOs"))
                if (all != null && all.SubKeyCount == 0 && all.ValueCount == 0) { all.Close(); apo.DeleteSubKey("Child APOs", false); }
            return true;
        }

        // ------------------------------------------------------------------ on this PC

        /// <summary>A Windows sound device id ("{0.0.0.00000000}.{guid}"): its guid, and whether it's an input.</summary>
        public static string Guid(string deviceId, out bool input)
        {
            input = deviceId != null && deviceId.StartsWith("{0.0.1.", StringComparison.Ordinal);
            return EqualizerApo.EndpointGuid(deviceId);
        }

        static RegistryKey Machine(string path, bool write)
        {
            var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return write ? hklm.CreateSubKey(path) : hklm.OpenSubKey(path);
        }

        const string AudioPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio", ApoPath = @"SOFTWARE\EqualizerAPO";

        /// <summary>Equalizer APO is on this device right now (read only, no rights needed).</summary>
        public static bool IsAttached(string deviceId)
        {
            try { bool input; string g = Guid(deviceId, out input); using (var a = Machine(AudioPath, false)) return a != null && IsAttached(a, g, input); }
            catch { return false; }
        }

        /// <summary>
        /// Everything in one go, with Windows' permission asked once (none when StreamLite already runs as administrator):
        /// Equalizer APO installed when setupExe is given (its own device list closed the moment it opens), put on the
        /// headphones, taken off the mic, and Windows' audio restarted so it works at once. null = done, else why not.
        /// </summary>
        public static string Setup(string setupExe, string headphonesId, string micId)
        {
            var args = new System.Collections.Generic.List<string>();
            if (setupExe != null) args.Add("install=" + setupExe);
            if (headphonesId != null) args.Add("on=" + headphonesId);
            if (micId != null) args.Add("off=" + micId);
            if (Elevation.IsElevated) return Message(RunHelper(args.ToArray()));
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--apo-setup " + string.Join(" ", args.Select(a => "\"" + a + "\""))) { Verb = "runas", UseShellExecute = true };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(6 * 60 * 1000)) return "Equalizer APO's setup didn't finish in 6 minutes.";
                    return Message(p.ExitCode);
                }
            }
            catch (Win32Exception) { return EqualizerApo.NotGranted; }
            catch (Exception ex) { return ex.Message; }
        }

        static string Message(int code)
        {
            switch (code)
            {
                case 0: return null;
                case 2: return "Equalizer APO didn't install. Try again, or install it from sourceforge.net/projects/equalizerapo.";
                case 3: return "Windows didn't let StreamLite put Equalizer APO on these headphones.";
                default: return "Equalizer APO couldn't be set up (code " + code + ").";
            }
        }

        /// <summary>
        /// "--apo-setup install=&lt;setup.exe&gt; on=&lt;headphones id&gt; off=&lt;mic id&gt;", run as administrator. Exit code
        /// 0 = done, 2 = the install failed, 3 = the device couldn't be changed.
        /// </summary>
        public static int RunHelper(string[] args)
        {
            Func<string, string> arg = k => args.Where(a => a.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(k.Length + 1)).FirstOrDefault();
            string setup = arg("install"), on = arg("on"), off = arg("off");
            if (setup != null)
            {
                try { RunSetupQuietly(setup, 5 * 60 * 1000); } catch { return 2; }
                if (!EqualizerApo.IsInstalled) return 2;
            }
            bool changed = false;
            try
            {
                using (var audio = Machine(AudioPath, false))
                using (var apo = Machine(ApoPath, true))
                {
                    bool input;
                    if (on != null) { string g = Guid(on, out input); changed |= Attach(audio, apo, g, input); }
                    if (off != null) { string g = Guid(off, out input); changed |= Detach(audio, apo, g, input); }
                }
            }
            catch { return 3; }
            if (changed || setup != null) RestartAudio();
            return 0;
        }

        /// <summary>
        /// Equalizer APO's own setup, silently - its installer always opens its device list at the end and waits for
        /// it; StreamLite closes that list the moment it appears (nothing has been changed in it yet) and sets the
        /// devices itself. Also used by the MSI's install step.
        /// </summary>
        public static void RunSetupQuietly(string setupExe, int timeoutMs, Func<string, string, int> run = null)
        {
            bool done = false;
            var watcher = new Thread(() =>
            {
                while (!Volatile.Read(ref done))
                {
                    foreach (var p in Process.GetProcessesByName("DeviceSelector")) try { p.Kill(); } catch { } finally { p.Dispose(); }
                    Thread.Sleep(150);
                }
            }) { IsBackground = true, Name = "close Equalizer APO's device list" };
            watcher.Start();
            try
            {
                if (run != null) { run(setupExe, "/S"); return; }
                using (var p = Process.Start(new ProcessStartInfo(setupExe, "/S") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(setupExe) }))
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException(Path.GetFileName(setupExe) + " didn't finish in time"); }
            }
            finally { Volatile.Write(ref done, true); }
        }

        /// <summary>Windows' audio service restarted, so Equalizer APO runs on the device at once (sound stops for a moment).</summary>
        static void RestartAudio()
        {
            foreach (var a in new[] { "stop audiosrv /y", "start audiosrv" })
                try { using (var p = Process.Start(new ProcessStartInfo("net.exe", a) { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(30000); } catch { }
        }
    }
}
