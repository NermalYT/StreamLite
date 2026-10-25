using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// StreamLite's own mixer for "Direct to headphones". Only StreamLite plays to the headphones then, so the apps
    /// that should sound as they are - voice chat, music, browsers - play to a side output instead (another output of
    /// this PC, its own sound muted meanwhile), and StreamLite captures each of them there (Windows' per-app capture)
    /// and mixes them into the headphones, untouched by 3D. Windows hands out per-app sound about 40 ms after the app
    /// played it: fine for voice and music, which is why games stay on the fast 3D path. Each app gets its own
    /// drift-corrected buffer (the side output's clock isn't the headphones').
    /// </summary>
    internal sealed class AppMixer
    {
        /// <summary>One app's sound on the side output.</summary>
        internal sealed class Tap
        {
            public readonly int Pid; public readonly string Name;
            public IAudioClient Client; public IAudioCaptureClient Cap;
            public readonly StereoRing Ring = new StereoRing(96);
            public long Frames;
            public Tap(int pid, string name) { Pid = pid; Name = name; Ring.SetBaseMargin(240, 480); }   // voice/music: a few ms more buffer, no gaps
        }

        /// <summary>The side output (the apps that play as they are go there); null = the mixer is off.</summary>
        public volatile string DeviceId;
        public volatile string LastError;
        volatile Tap[] taps = new Tap[0];
        public Tap[] Taps { get { return taps; } }
        readonly List<KeyValuePair<Tap, int>> graveyard = new List<KeyValuePair<Tap, int>>();
        readonly Dictionary<int, int> failedAt = new Dictionary<int, int>();
        readonly float[] inBuf = new float[48000 * 2], l = new float[48000], r = new float[48000];
        float[] mixTmp = new float[48000 * 2];
        Thread worker;
        volatile bool stop;
        int nextScan;

        public void Start()
        {
            if (worker != null) return;
            stop = false;
            worker = new Thread(Work) { IsBackground = true, Name = "App mixer", Priority = ThreadPriority.Highest };
            worker.Start();
        }

        public void Stop()
        {
            stop = true;
            var w = worker; if (w != null) w.Join(3000);
            worker = null;
            RetireAll();
            lock (graveyard) { foreach (var g in graveyard) Release(g.Key); graveyard.Clear(); }
        }

        /// <summary>Render thread: adds every app's sound (stereo, interleaved) to what goes to the headphones.</summary>
        public void AddTo(float[] inter, int frames)
        {
            var ts = taps;
            if (ts.Length == 0) return;
            if (mixTmp.Length < frames * 2) mixTmp = new float[frames * 2];
            for (int k = 0; k < ts.Length; k++)
            {
                ts[k].Ring.Read(mixTmp, frames);
                for (int i = 0; i < frames * 2; i++) inter[i] += mixTmp[i];
            }
        }

        /// <summary>Tests: an app's sound handed over by hand (no Windows capture).</summary>
        internal void Add(Tap t) { var ts = taps; var n = new Tap[ts.Length + 1]; Array.Copy(ts, n, ts.Length); n[ts.Length] = t; taps = n; }

        /// <summary>One packet of an app's sound into its buffer (stereo, interleaved).</summary>
        internal void Feed(Tap t, float[] inter, int n, bool silent)
        {
            for (int i = 0; i < n; i++) { l[i] = silent ? 0 : inter[2 * i]; r[i] = silent ? 0 : inter[2 * i + 1]; }
            t.Ring.Write(l, r, n);
            t.Ring.InputArrived();
            t.Frames += n;
        }

        void Work()
        {
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                while (!stop)
                {
                    try
                    {
                        Pump();
                        int now = Environment.TickCount;
                        if (now - nextScan >= 0) { nextScan = now + 250; Scan(); }
                    }
                    catch (Exception ex) { LastError = ex.Message; }
                    // Every 2 ms while apps play through it; otherwise just a look now and then (no CPU when unused).
                    Thread.Sleep(taps.Length > 0 ? 2 : DeviceId != null ? 20 : 250);
                }
            }
            finally { AudioEngine.EndProAudio(mm); }
        }

        void Pump()
        {
            var ts = taps;
            for (int k = 0; k < ts.Length; k++)
            {
                var t = ts[k];
                if (t.Cap == null) continue;
                try
                {
                    uint packet;
                    if (t.Cap.GetNextPacketSize(out packet) != 0) continue;
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong p1, p2;
                        if (t.Cap.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) break;
                        int n = (int)Math.Min(frames, 48000u);
                        bool silent = (flags & CoreAudioConst.BUFFERFLAGS_SILENT) != 0;
                        if (!silent) Marshal.Copy(data, inBuf, 0, n * 2);
                        Feed(t, inBuf, n, silent);
                        t.Cap.ReleaseBuffer(frames);
                        if (t.Cap.GetNextPacketSize(out packet) != 0) break;
                    }
                }
                catch { }
            }
        }

        /// <summary>Which apps play on the side output: a capture for each new one, gone ones released.</summary>
        void Scan()
        {
            string dev = DeviceId;
            int now = Environment.TickCount;
            lock (graveyard)
            {
                for (int i = graveyard.Count - 1; i >= 0; i--)
                    if (now - graveyard[i].Value > 2000) { Release(graveyard[i].Key); graveyard.RemoveAt(i); }
            }
            if (dev == null) { RetireAll(); return; }
            var sessions = ProcessLoopback.Sessions(dev);
            // An app's capture includes the programs it started: capture only the topmost of each family.
            var roots = Roots(sessions.Keys, ProcessLoopback.Parents());
            var ts = taps;
            var next = new List<Tap>(ts);
            bool changed = false;
            foreach (int pid in roots)
            {
                if (Array.Exists(ts, t => t.Pid == pid)) continue;
                int at; if (failedAt.TryGetValue(pid, out at) && now - at < 10000) continue;
                try { next.Add(Open(pid, sessions[pid])); changed = true; failedAt.Remove(pid); }
                catch (Exception ex) { failedAt[pid] = now; LastError = sessions[pid] + ": " + ex.Message; }
            }
            foreach (var t in ts)
            {
                bool gone = !roots.Contains(t.Pid);
                if (!gone) try { using (var pr = Process.GetProcessById(t.Pid)) gone = pr.HasExited; } catch { gone = true; }
                if (gone) { next.Remove(t); changed = true; lock (graveyard) graveyard.Add(new KeyValuePair<Tap, int>(t, now)); }
            }
            if (changed) taps = next.ToArray();
        }

        /// <summary>The processes whose ancestors aren't in the set too (their capture already includes them).</summary>
        internal static HashSet<int> Roots(IEnumerable<int> pids, Dictionary<int, int> parent)
        {
            var set = new HashSet<int>(pids);
            var roots = new HashSet<int>();
            foreach (int p in set)
            {
                int cur = p, hops = 0; bool covered = false;
                while (parent.TryGetValue(cur, out cur) && cur != 0 && hops++ < 64) if (set.Contains(cur)) { covered = true; break; }
                if (!covered) roots.Add(p);
            }
            return roots;
        }

        Tap Open(int pid, string name)
        {
            var client = ProcessLoopback.Open(pid);
            IntPtr fmt = AudioDevices.MakeFormat(2, 3, 48000, true, 32);
            try
            {
                int hr = client.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 200000, 0, fmt, IntPtr.Zero);
                if (hr != 0) { Marshal.ReleaseComObject(client); throw new COMException("per-app capture init failed (0x" + hr.ToString("X8") + ")", hr); }
                object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) { Marshal.ReleaseComObject(client); throw new COMException("per-app capture service failed", hr); }
                var t = new Tap(pid, name) { Client = client, Cap = (IAudioCaptureClient)svc };
                t.Ring.Grace(1500);
                client.Start();
                return t;
            }
            finally { Marshal.FreeHGlobal(fmt); }
        }

        static void Release(Tap t)
        {
            try { if (t.Client != null) t.Client.Stop(); } catch { }
            try { if (t.Cap != null) Marshal.ReleaseComObject(t.Cap); } catch { }
            try { if (t.Client != null) Marshal.ReleaseComObject(t.Client); } catch { }
            t.Cap = null; t.Client = null;
        }

        void RetireAll()
        {
            var ts = taps;
            if (ts.Length == 0) return;
            taps = new Tap[0];
            int now = Environment.TickCount;
            lock (graveyard) foreach (var t in ts) graveyard.Add(new KeyValuePair<Tap, int>(t, now));
        }
    }
}
