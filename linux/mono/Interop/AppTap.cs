using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace StreamLite.Interop
{
    /// <summary>
    /// One app's own sound for the listening EQ (4.2), Linux: parec monitoring the app's playback stream (PulseAudio or
    /// pipewire-pulse: --monitor-stream), 48 kHz stereo floats. Same members as Windows' per-app capture
    /// (src/Interop/AppTap.cs). It hears the app's stream itself - never StreamLite's own EQ.
    /// </summary>
    internal sealed class AppTap : IDisposable
    {
        Process proc;
        readonly float[] ring = new float[2 * 48000];   // a second of room; older sound is dropped if nobody reads
        int head, count;
        readonly object gate = new object();

        public static AppTap Open(int pid)
        {
            var s = Pactl.Streams(true).FirstOrDefault(x => x.Pid == pid && !x.Corked) ?? Pactl.Streams(true).FirstOrDefault(x => x.Pid == pid);
            if (s == null) throw new InvalidOperationException("no playback stream for process " + pid);
            var psi = new ProcessStartInfo("parec", "--monitor-stream=" + s.Index + " --format=float32le --rate=48000 --channels=2 --raw --latency-msec=100 --client-name=StreamLite-listening")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            var t = new AppTap { proc = Process.Start(psi) };
            new Thread(t.Pump) { IsBackground = true, Name = "StreamLite listening (parec)" }.Start();
            return t;
        }

        void Pump()
        {
            var bytes = new byte[8192]; int keep = 0;
            try
            {
                var st = proc.StandardOutput.BaseStream;
                while (true)
                {
                    int n = st.Read(bytes, keep, bytes.Length - keep);
                    if (n <= 0) break;
                    n += keep;
                    int floats = n / 4;
                    lock (gate)
                        for (int i = 0; i < floats; i++)
                        {
                            ring[(head + count) % ring.Length] = BitConverter.ToSingle(bytes, i * 4);
                            if (count < ring.Length) count++; else head = (head + 1) % ring.Length;
                        }
                    keep = n - floats * 4;
                    if (keep > 0) Buffer.BlockCopy(bytes, floats * 4, bytes, 0, keep);
                }
            }
            catch { }
        }

        /// <summary>What the app played since the last read, as interleaved stereo (up to the buffer's size): frames.</summary>
        public int Read(float[] stereo)
        {
            lock (gate)
            {
                int n = Math.Min(count, stereo.Length) & ~1;
                for (int i = 0; i < n; i++) stereo[i] = ring[(head + i) % ring.Length];
                head = (head + n) % ring.Length; count -= n;
                return n / 2;
            }
        }

        public void Dispose()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { if (proc != null) proc.Dispose(); } catch { }
            proc = null;
        }
    }
}
