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
            /// <summary>Windows signals it when a packet of this app's sound is ready.</summary>
            public readonly AutoResetEvent Ready = new AutoResetEvent(false);
            public long Frames;
            /// <summary>The app also plays into the game cable right now: 3D audio plays it, so it isn't added again.</summary>
            public volatile bool AlsoOnSink;
            // Voice/music: 10 ms in reserve (a packet's worth) - it's 40 ms late anyway, and must never cut out.
            public Tap(int pid, string name) { Pid = pid; Name = name; Ring.SetBaseMargin(480, 480); }
        }

        /// <summary>The side output (the apps that play as they are go there); null = the mixer is off.</summary>
        public volatile string DeviceId;
        /// <summary>The game cable (3D audio's input). Windows' per-app capture takes everything an app plays, on any device.</summary>
        public volatile string SinkId;
        public volatile string LastError;
        /// <summary>Windows' per-app capture doesn't work on this PC (older Windows, or it failed for every app it tried):
        /// apps on the muted side output would play to nobody, so StreamLite stops using it.</summary>
        public volatile bool CaptureBroken;
        int opened, failures;
        volatile Tap[] taps = new Tap[0];
        public Tap[] Taps { get { return taps; } }
        readonly List<KeyValuePair<Tap, int>> graveyard = new List<KeyValuePair<Tap, int>>();
        readonly Dictionary<int, int> failedAt = new Dictionary<int, int>();
        readonly float[] inBuf = new float[48000 * 2], l = new float[48000], r = new float[48000];
        float[] mixTmp = new float[48000 * 2];
        Thread worker, scanner;
        volatile bool stop;
        readonly AutoResetEvent tapsChanged = new AutoResetEvent(false);

        public void Start()
        {
            if (worker != null) return;
            stop = false;
            worker = new Thread(Work) { IsBackground = true, Name = "App mixer", Priority = ThreadPriority.Highest };
            worker.Start();
            scanner = new Thread(ScanLoop) { IsBackground = true, Name = "App mixer scan", Priority = ThreadPriority.BelowNormal };
            scanner.Start();
        }

        public void Stop()
        {
            stop = true; tapsChanged.Set();
            var w = worker; if (w != null) w.Join(3000);
            var s = scanner; if (s != null) s.Join(3000);
            worker = null; scanner = null;
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
                if (ts[k].AlsoOnSink) continue;
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

        /// <summary>
        /// Moves the sound: wakes when Windows says a packet is ready (or after 5 ms anyway) and takes everything there
        /// is. Nothing else runs on this thread - looking for apps (10-20 ms of process and session lists) used to,
        /// four times a second, and the music cut out every few seconds.
        /// </summary>
        void Work()
        {
            IntPtr mm = AudioEngine.ProAudio();
            try
            {
                WaitHandle[] waits = new WaitHandle[0]; Tap[] waitFor = null;
                while (!stop)
                {
                    var ts = taps;
                    if (ts.Length == 0) { tapsChanged.WaitOne(250); continue; }
                    if (!ReferenceEquals(ts, waitFor))
                    {
                        waitFor = ts;
                        waits = new WaitHandle[Math.Min(ts.Length, 63) + 1];
                        for (int k = 0; k < waits.Length - 1; k++) waits[k] = ts[k].Ready;
                        waits[waits.Length - 1] = tapsChanged;
                    }
                    WaitHandle.WaitAny(waits, 5);
                    try { Pump(); }
                    catch (Exception ex) { LastError = ex.Message; }
                }
            }
            finally { AudioEngine.EndProAudio(mm); }
        }

        void ScanLoop()
        {
            while (!stop)
            {
                try { Scan(); }
                catch (Exception ex) { LastError = ex.Message; }
                for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(50);
            }
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
            var parents = ProcessLoopback.Parents();
            var roots = Roots(sessions.Keys, parents);
            // Apps playing into the game cable too (a call that opened there before it was moved): heard through 3D.
            string sink = SinkId;
            var hot = sink != null ? ProcessLoopback.Sessions(sink, true) : new Dictionary<int, string>();
            foreach (var t in taps) t.AlsoOnSink = InFamily(hot.Keys, t.Pid, parents);
            var ts = taps;
            var next = new List<Tap>(ts);
            bool changed = false;
            foreach (int pid in roots)
            {
                if (Array.Exists(ts, t => t.Pid == pid)) continue;
                int at; if (failedAt.TryGetValue(pid, out at) && now - at < 10000) continue;
                try { next.Add(Open(pid, sessions[pid])); changed = true; failedAt.Remove(pid); opened++; }
                catch (Exception ex)
                {
                    failedAt[pid] = now; LastError = sessions[pid] + ": " + ex.Message;
                    if (++failures >= 2 && opened == 0) CaptureBroken = true;
                }
            }
            foreach (var t in ts)
            {
                bool gone = !roots.Contains(t.Pid);
                if (!gone) try { using (var pr = Process.GetProcessById(t.Pid)) gone = pr.HasExited; } catch { gone = true; }
                if (gone) { next.Remove(t); changed = true; lock (graveyard) graveyard.Add(new KeyValuePair<Tap, int>(t, now)); }
            }
            if (changed) { taps = next.ToArray(); tapsChanged.Set(); }
        }

        /// <summary>Is one of these processes this one or started by it (any generation)?</summary>
        internal static bool InFamily(IEnumerable<int> pids, int root, Dictionary<int, int> parent)
        {
            foreach (int p in pids)
            {
                int cur = p, hops = 0;
                if (cur == root) return true;
                while (parent.TryGetValue(cur, out cur) && cur != 0 && hops++ < 64) if (cur == root) return true;
            }
            return false;
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
                // Loopback, signalled per packet; 100 ms of room, so a late wake-up never loses sound (it adds no delay:
                // packets are taken as they come).
                int hr = client.Initialize(0, 0x00020000 | CoreAudioConst.STREAMFLAGS_EVENTCALLBACK | CoreAudioConst.STREAMFLAGS_AUTOCONVERTPCM, 1000000, 0, fmt, IntPtr.Zero);
                if (hr != 0) { Marshal.ReleaseComObject(client); throw new COMException("per-app capture init failed (0x" + hr.ToString("X8") + ")", hr); }
                object svc; Guid iid = CoreAudioConst.IID_IAudioCaptureClient;
                hr = client.GetService(ref iid, out svc);
                if (hr != 0) { Marshal.ReleaseComObject(client); throw new COMException("per-app capture service failed", hr); }
                var t = new Tap(pid, name) { Client = client, Cap = (IAudioCaptureClient)svc };
                client.SetEventHandle(t.Ready.SafeWaitHandle.DangerousGetHandle());
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
            taps = new Tap[0]; tapsChanged.Set();
            int now = Environment.TickCount;
            lock (graveyard) foreach (var t in ts) graveyard.Add(new KeyValuePair<Tap, int>(t, now));
        }
    }
}
