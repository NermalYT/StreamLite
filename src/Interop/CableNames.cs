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
