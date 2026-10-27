using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamLite.Interop
{
    /// <summary>
    /// Windows' default playback and recording devices (each role: games and apps, media, voice chat), kept through
    /// installing the virtual cables (4.0.3). Installing a cable driver can make it the default - "StreamLite - Out" for
    /// playback, "Hi-Fi Cable Output" as the mic - and until StreamLite runs, your sound and mic then go nowhere. Taken
    /// before the install; afterwards each role a cable took over goes back to the real device you had.
    /// </summary>
    internal sealed class DefaultDevices
    {
        static readonly EDataFlow[] Flows = { EDataFlow.Render, EDataFlow.Capture };
        static readonly ERole[] Roles = { ERole.Console, ERole.Multimedia, ERole.Communications };
        readonly Dictionary<string, string> before;

        DefaultDevices(Dictionary<string, string> before) { this.before = before; }

        static string Key(EDataFlow f, ERole r) { return f + "/" + r; }

        static Dictionary<string, string> Now()
        {
            var d = new Dictionary<string, string>();
            foreach (var f in Flows) foreach (var r in Roles) d[Key(f, r)] = AudioDevices.GetDefaultId(f, r);
            return d;
        }

        /// <summary>The defaults as they are now (before installing anything).</summary>
        public static DefaultDevices Take() { return new DefaultDevices(Now()); }

        /// <summary>One role to put back: flow/role, from (the cable), to (your device).</summary>
        public sealed class Change { public string Role, From, To; }

        /// <summary>
        /// Which roles go back: those that were a real device before and are a cable now, when that device is still
        /// there. A cable you had chosen as the default stays; so does a real device you picked meanwhile.
        /// </summary>
        public static List<Change> Plan(Dictionary<string, string> before, Dictionary<string, string> now, Func<string, bool> isCable, Func<string, bool> present)
        {
            var list = new List<Change>();
            foreach (var kv in before)
            {
                string was = kv.Value, cur;
                if (was == null || !now.TryGetValue(kv.Key, out cur) || cur == null || cur == was) continue;
                if (isCable(cur) && !isCable(was) && present(was)) list.Add(new Change { Role = kv.Key, From = cur, To = was });
            }
            return list;
        }

        /// <summary>Puts back what a cable took over; what was changed, in words (for the log).</summary>
        public List<string> Restore()
        {
            var devices = Flows.SelectMany(f => AudioDevices.List(f)).ToList();
            Func<string, AudioDeviceInfo> dev = id => devices.FirstOrDefault(d => d.Id == id);
            var done = new List<string>();
            foreach (var c in Plan(before, Now(), id => { var d = dev(id); return d != null && AudioDevices.IsCable(d); }, id => dev(id) != null))
            {
                var parts = c.Role.Split('/');
                var role = (ERole)Enum.Parse(typeof(ERole), parts[1]);
                bool ok = AudioDevices.SetDefaultRole(c.To, role);
                done.Add(parts[0] + " " + parts[1] + ": " + dev(c.From).Name + " -> " + dev(c.To).Name + (ok ? "" : " (Windows refused)"));
            }
            return done;
        }
    }
}
