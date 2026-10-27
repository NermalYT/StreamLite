using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamLite.Interop
{
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }

    /// <summary>
    /// "Lock in" (hotkey): every other app's sound turns down (Discord, music, browser...) while the game - the app in
    /// front when you press it - and StreamLite (which plays the game's 3D sound) stay at full volume. Pressing it again
    /// puts each app back where it was. Apps that start making sound while locked in are turned down too.
    /// </summary>
    internal static class LockIn
    {
        static readonly object gate = new object();
        // session instance -> (its volume before, the volume Lock in set)
        static readonly Dictionary<string, KeyValuePair<float, float>> lowered = new Dictionary<string, KeyValuePair<float, float>>();
        static HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static float level = 0.5f;
        static Timer timer;
        static Guid ctx = new Guid("5C1E7A0B-0E3D-4C6E-9A7B-3F1D0C8E2B41");

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);

        public static bool Active { get { lock (gate) return timer != null; } }
        public static string Game { get; private set; }

        /// <summary>Locks in (the app in front stays at 100 %, others at <paramref name="percent"/>) or, when locked in, back.</summary>
        public static string Toggle(int percent, string detectedExe)
        {
            if (Active) { Release(); return "Lock in off - every app is back at its volume"; }
            string game = ForegroundProcess();
            string me = Process.GetCurrentProcess().ProcessName;
            if (game == null || game.Equals(me, StringComparison.OrdinalIgnoreCase) || game.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                game = string.IsNullOrEmpty(detectedExe) ? null : System.IO.Path.GetFileNameWithoutExtension(detectedExe);
            Engage(game, percent);
            return "Locked in" + (game != null ? " on " + game : "") + " - other apps at " + percent + "%";
        }

        public static void Engage(string game, int percent)
        {
            lock (gate)
            {
                keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Process.GetCurrentProcess().ProcessName };
                if (game != null) keep.Add(game);
                Game = game;
                level = Math.Max(0, Math.Min(100, percent)) / 100f;
                if (timer == null) timer = new Timer(_ => { try { Apply(); } catch { } }, null, 0, 2000);
            }
        }

        /// <summary>Back to how each app was (apps whose volume you changed meanwhile keep yours).</summary>
        public static void Release()
        {
            lock (gate)
            {
                if (timer != null) { timer.Dispose(); timer = null; }
                Game = null;
                var back = new Dictionary<string, KeyValuePair<float, float>>(lowered);
                lowered.Clear();
                ForEachSession((inst, name, vol) =>
                {
                    KeyValuePair<float, float> was;
                    if (!back.TryGetValue(inst, out was)) return;
                    float now; if (vol.GetMasterVolume(out now) != 0) return;
                    if (Math.Abs(now - was.Value) < 0.02f) vol.SetMasterVolume(was.Key, ref ctx);
                });
            }
        }

        static void Apply()
        {
            lock (gate)
            {
                if (timer == null) return;
                ForEachSession((inst, name, vol) =>
                {
                    if (name != null && keep.Contains(name)) return;
                    if (lowered.ContainsKey(inst)) return;   // done (or you set it yourself since: yours)
                    float before; if (vol.GetMasterVolume(out before) != 0) return;
                    float target = before * level;
                    if (vol.SetMasterVolume(target, ref ctx) == 0) lowered[inst] = new KeyValuePair<float, float>(before, target);
                });
            }
        }

        static string ForegroundProcess()
        {
            try
            {
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == 0) return null;
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return null; }
        }

        /// <summary>Every playback session on every active output (name = its process, null for system sounds).</summary>
        internal static void ForEachSession(Action<string, string, ISimpleAudioVolume> act)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                foreach (var d in AudioDevices.List(EDataFlow.Render))
                {
                    IMMDevice dev = null; object o = null; IAudioSessionEnumerator e = null;
                    try
                    {
                        if (en.GetDevice(d.Id, out dev) != 0 || dev == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) continue;
                        if (((IAudioSessionManager2)o).GetSessionEnumerator(out e) != 0 || e == null) continue;
                        int n; e.GetCount(out n);
                        for (int i = 0; i < n; i++)
                        {
                            IAudioSessionControl2 s = null;
                            try
                            {
                                if (e.GetSession(i, out s) != 0 || s == null) continue;
                                int st; uint pid; string inst;
                                if (s.GetState(out st) != 0 || st == 2 || s.GetProcessId(out pid) != 0) continue;   // 2 = expired
                                if (s.GetSessionInstanceIdentifier(out inst) != 0 || inst == null) continue;
                                string name = null;
                                if (pid != 0) try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { continue; }
                                var vol = s as ISimpleAudioVolume;
                                if (vol != null) act(inst, name, vol);
                            }
                            catch { }
                            finally { if (s != null) Marshal.ReleaseComObject(s); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (e != null) Marshal.ReleaseComObject(e);
                        if (o != null) Marshal.ReleaseComObject(o);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
    }
}
