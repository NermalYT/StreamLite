using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Interop
{
    /// <summary>
    /// The virtual cables under StreamLite's names, so apps list "StreamLite - In" / "StreamLite - Out" instead of
    /// VB-Audio's "CABLE Output (VB-Audio Virtual Cable)" and "Hi-Fi Cable Input": VB-CABLE is the mic (both its ends
    /// and its adapter), VB-Audio Hi-Fi Cable the 3D game device. Renaming needs admin rights: the installer does it
    /// (it runs with them, also on every update), and StreamLite itself when it runs as administrator.
    /// </summary>
    internal static class CableNames
    {
        /// <summary>The mic cable's render end (what StreamLite writes your voice into).</summary>
        public static bool IsMicCable(AudioDeviceInfo d)
        {
            if (IsGameCable(d)) return false;
            string n = d.Name ?? "", a = d.Adapter ?? "";
            return n.StartsWith(AppCore.MicFeedName, StringComparison.OrdinalIgnoreCase) || a.Equals(AppCore.MicAdapterName, StringComparison.OrdinalIgnoreCase)
                || ((n.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) >= 0)
                    && n.IndexOf("16ch", StringComparison.OrdinalIgnoreCase) < 0);
        }

        /// <summary>The 3D game device's render end.</summary>
        public static bool IsGameCable(AudioDeviceInfo d)
        {
            string n = d.Name ?? "", a = d.Adapter ?? "";
            return n.StartsWith(AppCore.GameOutName, StringComparison.OrdinalIgnoreCase) || n.IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase) >= 0 || a.Equals(AppCore.GameAdapterName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Renames whatever isn't named yet. What was renamed (for the log); empty when everything already was.</summary>
        public static List<string> Apply()
        {
            var done = new List<string>();
            const int Any = 1 | 2 | 8;   // active, disabled (hidden by StreamLite), unplugged
            var outs = AudioDevices.List(EDataFlow.Render, Any);
            var ins = AudioDevices.List(EDataFlow.Capture, Any);
            var micFeed = outs.FirstOrDefault(IsMicCable);
            if (micFeed != null)
            {
                Name(micFeed, AppCore.MicFeedName, done);
                Adapter(micFeed, AppCore.MicAdapterName, done);
                foreach (var cap in ins.Where(c => AudioDevices.SameDevice(c, micFeed))) { Name(cap, AppCore.MicName, done); Adapter(cap, AppCore.MicAdapterName, done); }
            }
            var game = outs.FirstOrDefault(d => IsGameCable(d) && (micFeed == null || !AudioDevices.SameDevice(d, micFeed)));
            if (game != null)
            {
                Name(game, AppCore.GameOutName, done);
                Adapter(game, AppCore.GameAdapterName, done);
            }
            return done;
        }

        // ---- Hiding what nobody needs: Windows and every app list only real devices and StreamLite - In / Out.

        /// <summary>Devices StreamLite switched off besides its cables' spare ends (restored by Untidy / uninstalling).</summary>
        static string HiddenFile { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamLite", "hidden-devices.txt"); } }

        /// <summary>
        /// Shown devices that only clutter the lists: VB-CABLE's spare end (CABLE In 16ch), the game cable's recording side,
        /// and other virtual cables nothing is set to use (Virtual Audio Cable's lines, more VB-Audio cables). Never a Windows
        /// default device; never Voicemeeter or Steam's devices (their apps need them).
        /// </summary>
        public static List<AudioDeviceInfo> Clutter()
        {
            var list = new List<AudioDeviceInfo>();
            var outs = AudioDevices.List(EDataFlow.Render, 1 | 2 | 8);
            var micFeed = outs.FirstOrDefault(IsMicCable);
            var game = outs.FirstOrDefault(d => IsGameCable(d) && (micFeed == null || !AudioDevices.SameDevice(d, micFeed)));
            var defaults = new HashSet<string>();
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                foreach (var role in new[] { ERole.Console, ERole.Communications })
                {
                    string id = AudioDevices.GetDefaultId(flow, role);
                    if (id != null) defaults.Add(id);
                }
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                foreach (var d in AudioDevices.List(flow, 1))
                {
                    if (defaults.Contains(d.Id)) continue;
                    bool spare = flow == EDataFlow.Render
                        ? micFeed != null && d.Id != micFeed.Id && AudioDevices.SameDevice(d, micFeed)
                        : game != null && AudioDevices.SameDevice(d, game);
                    string s = (d.Name + " " + d.Adapter).ToLowerInvariant();
                    bool ours = (micFeed != null && AudioDevices.SameDevice(d, micFeed)) || (game != null && AudioDevices.SameDevice(d, game));
                    bool otherCable = !ours && AudioDevices.LooksVirtual(d) && !s.Contains("voicemeeter") && !s.Contains("steam")
                        && (s.Contains("virtual audio cable") || s.Contains("vb-audio") || s.Contains("cable input") || s.Contains("cable output") || s.Contains("hi-fi cable"));
                    if (spare || otherCable) list.Add(d);
                }
            return list;
        }

        /// <summary>Switches the clutter off (admin rights); what was switched off, for the log.</summary>
        public static List<string> Tidy()
        {
            var done = new List<string>();
            foreach (var d in Clutter())
                if (AudioDevices.SetEnabled(d.Id, false))
                {
                    done.Add("hid \"" + d.Name + "\"");
                    try
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HiddenFile));
                        System.IO.File.AppendAllText(HiddenFile, d.Id + "\t" + d.Name + Environment.NewLine);
                    }
                    catch { }
                }
            return done;
        }

        /// <summary>Everything Tidy switched off, back on (admin rights). What came back.</summary>
        public static List<string> Untidy()
        {
            var done = new List<string>();
            try
            {
                if (System.IO.File.Exists(HiddenFile))
                {
                    foreach (var line in System.IO.File.ReadAllLines(HiddenFile))
                    {
                        var parts = line.Split('\t');
                        if (parts[0].Length > 0 && AudioDevices.State(parts[0]) == 2 && AudioDevices.SetEnabled(parts[0], true))
                            done.Add("showed \"" + (parts.Length > 1 ? parts[1] : parts[0]) + "\" again");
                    }
                    System.IO.File.Delete(HiddenFile);
                }
            }
            catch { }
            return done;
        }

        /// <summary>The device lists aren't as StreamLite keeps them: cables under other names, or clutter showing.</summary>
        public static bool NeedsTidying(bool hide) { return !Named() || (hide && Clutter().Count > 0); }

        static bool NameOk(AudioDeviceInfo d, string want) { return d.Name == want || (d.Name ?? "").StartsWith(want + " (", StringComparison.Ordinal); }

        /// <summary>Every cable that's there carries StreamLite's name (true too when there are no cables at all).</summary>
        public static bool Named()
        {
            const int Any = 1 | 2 | 8;
            var outs = AudioDevices.List(EDataFlow.Render, Any);
            var micFeed = outs.FirstOrDefault(IsMicCable);
            if (micFeed != null)
            {
                if (!NameOk(micFeed, AppCore.MicFeedName)) return false;
                if (AudioDevices.List(EDataFlow.Capture, Any).Where(c => AudioDevices.SameDevice(c, micFeed)).Any(c => !NameOk(c, AppCore.MicName))) return false;
            }
            var game = outs.FirstOrDefault(d => IsGameCable(d) && (micFeed == null || !AudioDevices.SameDevice(d, micFeed)));
            return game == null || NameOk(game, AppCore.GameOutName);
        }

        /// <summary>
        /// Right after a cable's driver is installed, Windows needs a few seconds to create its devices: renaming at once
        /// finds nothing (3.5.0 installs kept VB-Audio's names). Waits up to waitMs for the cables that are installed
        /// (mic: both ends; game: its output), renames, and checks it took. What was renamed, for the log.
        /// </summary>
        public static List<string> ApplyWhenReady(int waitMs, bool mic, bool gameCable)
        {
            var done = new List<string>();
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            const int Any = 1 | 2 | 8;
            while (true)
            {
                var outs = AudioDevices.List(EDataFlow.Render, Any);
                var micFeed = outs.FirstOrDefault(IsMicCable);
                bool micReady = !mic || (micFeed != null && AudioDevices.List(EDataFlow.Capture, Any).Any(c => AudioDevices.SameDevice(c, micFeed)));
                bool gameReady = !gameCable || outs.Any(d => IsGameCable(d) && (micFeed == null || !AudioDevices.SameDevice(d, micFeed)));
                bool last = DateTime.UtcNow >= until;
                if (micReady && gameReady || last)
                {
                    done.AddRange(Apply());
                    if (Named() || last) return done;
                }
                System.Threading.Thread.Sleep(500);
            }
        }

        static void Name(AudioDeviceInfo d, string want, List<string> done)
        {
            if (d.Name == want || (d.Name ?? "").StartsWith(want + " (", StringComparison.Ordinal)) return;
            if (AudioDevices.Rename(d.Id, want)) done.Add("\"" + d.Name + "\" -> \"" + want + "\"");
        }

        static void Adapter(AudioDeviceInfo d, string want, List<string> done)
        {
            if (AudioDevices.AdapterName(d.Id) == want) return;
            if (AudioDevices.RenameAdapter(d.Id, want)) done.Add("adapter of \"" + d.Name + "\" -> \"" + want + "\"");
        }
    }
}
