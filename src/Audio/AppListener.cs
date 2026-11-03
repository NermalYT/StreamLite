using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// The listening EQ at work (4.2): while apps of your profiles play, StreamLite listens to each one's own sound
    /// (Windows' per-app capture; on Linux the app's stream monitor) and keeps its profile's AppSpectrum up to date. It
    /// only listens - what it heard goes to the profile manager (TakeHeard, on its own timer), which fits the EQ and
    /// applies it when it really changed. Nothing of the sound is kept or sent anywhere: 31 numbers per profile.
    /// </summary>
    internal sealed class AppListener : IDisposable
    {
        readonly ProfileSettings ps;
        readonly Thread thread;
        volatile bool stop;

        sealed class Ear { public int Pid; public string Exe, Profile; public AppTap Tap; }
        readonly Dictionary<int, Ear> ears = new Dictionary<int, Ear>();
        readonly Dictionary<string, AppSpectrum> heard = new Dictionary<string, AppSpectrum>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> handedAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        readonly HashSet<int> failed = new HashSet<int>();
        /// <summary>"Spotify (Music)": the apps it listens to right now, for the page.</summary>
        public volatile string Hearing = "";

        public AppListener(ProfileSettings ps)
        {
            this.ps = ps;
            thread = new Thread(Run) { IsBackground = true, Name = "StreamLite listening EQ", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// <summary>What was heard per profile since the last call (dB per band, seconds): only profiles with at least 10 s
        /// more sound than when last handed over.</summary>
        public Dictionary<string, Tuple<double[], double>> TakeHeard()
        {
            var d = new Dictionary<string, Tuple<double[], double>>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
                foreach (var kv in heard)
                {
                    double at; handedAt.TryGetValue(kv.Key, out at);
                    if (kv.Value.Seconds - at < 10) continue;
                    handedAt[kv.Key] = kv.Value.Seconds;
                    d[kv.Key] = Tuple.Create(kv.Value.Db(), kv.Value.Seconds);
                }
            return d;
        }

        /// <summary>Starts this profile's listening from nothing (Forget on the page).</summary>
        public void Forget(string profile) { lock (gate) { heard.Remove(profile); handedAt.Remove(profile); } }

        void Run()
        {
            var buf = new float[2 * 48000];
            int nextScan = 0;
            while (!stop)
            {
                try
                {
                    if (Environment.TickCount - nextScan >= 0) { Scan(); nextScan = Environment.TickCount + 3000; }
                    foreach (var e in ears.Values)
                    {
                        int n;
                        try { n = e.Tap.Read(buf); } catch { n = 0; }
                        if (n <= 0) continue;
                        lock (gate)
                        {
                            AppSpectrum s;
                            if (!heard.TryGetValue(e.Profile, out s)) heard[e.Profile] = s = Resume(e.Profile);
                            s.Feed(buf, n);
                        }
                    }
                }
                catch { }
                Thread.Sleep(100);
            }
            foreach (var e in ears.Values) e.Tap.Dispose();
            ears.Clear();
        }

        /// <summary>Carries on from what the profile heard before (saved in the settings).</summary>
        AppSpectrum Resume(string profile)
        {
            var p = ps.Profiles.FirstOrDefault(x => string.Equals(x.Name, profile, StringComparison.OrdinalIgnoreCase));
            return p != null && p.Heard != null ? new AppSpectrum(p.Heard, p.HeardSeconds) : new AppSpectrum();
        }

        /// <summary>Windows' own sounds and helpers, never an app's sound to learn from.</summary>
        static readonly HashSet<string> NotAnApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "explorer", "ShellExperienceHost", "SystemSettings", "SearchHost", "StartMenuExperienceHost", "LockApp", "audiodg", "svchost", "StreamLite", "pipewire", "wireplumber" };

        /// <summary>Every 3 s: the apps playing sound now that belong to a profile listened to - one ear each.</summary>
        void Scan()
        {
            var want = new Dictionary<int, Ear>();
            if (ps.ListenEq)
            {
                var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ps.Profiles.ToList())
                    if (p.Exes != null)
                        foreach (var x in p.Exes.ToList()) { string n = GameProfiles.NormalizeExe(x); if (n != null && !byExe.ContainsKey(n)) byExe[n] = p.Listen ? p.Name : null; }
                // Apps in no profile play under the fallback profile (a browser under Movies & videos): it learns from them.
                var fallback = ps.Profiles.FirstOrDefault(p => p.Name == ps.DefaultProfile);
                string other = fallback != null && fallback.Listen ? fallback.Name : null;
                foreach (var dev in AudioDevices.List(EDataFlow.Render))
                    foreach (var s in ProcessLoopback.Sessions(dev.Id, true))
                    {
                        if (want.ContainsKey(s.Key) || NotAnApp.Contains(s.Value)) continue;
                        string prof;
                        if (!byExe.TryGetValue(s.Value, out prof)) prof = other;
                        if (prof != null) want[s.Key] = new Ear { Pid = s.Key, Exe = s.Value, Profile = prof };
                    }
            }
            foreach (var pid in ears.Keys.ToList())
            {
                Ear have = ears[pid], w;
                if (want.TryGetValue(pid, out w) && w.Profile == have.Profile) continue;
                have.Tap.Dispose(); ears.Remove(pid);
            }
            foreach (var w in want.Values)
            {
                if (ears.ContainsKey(w.Pid) || failed.Contains(w.Pid)) continue;
                try { w.Tap = AppTap.Open(w.Pid); ears[w.Pid] = w; }
                catch { failed.Add(w.Pid); }   // not again for this process (it may not allow it)
            }
            Hearing = string.Join(", ", ears.Values.Select(e => e.Exe + " (" + e.Profile + ")").Distinct());
        }

        public void Dispose() { stop = true; thread.Join(2000); }
    }
}
