using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Interop;

namespace StreamLite.Audio
{
    /// <summary>
    /// The clean mic straight into "StreamLite - In" on PipeWire (4.1.4) - the Linux side of Windows' hidden Mic Feed:
    /// a pw-cat playback stream that no session manager links anywhere, linked by StreamLite to the virtual source's
    /// input. No "Mic Feed" output exists for apps to list, pick or record. The pipe into pw-cat is kept short (about
    /// 10 ms queued), so it adds no more delay than a sink would.
    /// </summary>
    internal sealed class PwFeed : IDisposable
    {
        [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int cmd, int arg);
        [DllImport("libc", SetLastError = true)] static extern int ioctl(int fd, uint request, out int value);
        [DllImport("libc", SetLastError = true)] static extern IntPtr write(int fd, byte[] buf, IntPtr count);
        const int F_SETPIPE_SZ = 1031;
        const uint FIONREAD = 0x541B;

        public const string NodeName = "streamlite_mic_writer";
        readonly Process process;
        readonly int fd;
        byte[] bytes = new byte[0];

        public PwFeed(string source, int rate)
        {
            var props = "{ node.name=" + NodeName + " node.dont-reconnect=true media.name=\"StreamLite: clean mic\" application.name=StreamLite }";
            var args = new[] { "--playback", "--target", "0", "-P", props, "--format", "f32", "--rate", rate.ToString(), "--channels", "1", "--latency", "5ms", "-" };
            process = Process.Start(new ProcessStartInfo("pw-cat", string.Join(" ", args.Select(Shell.Quote)))
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true });
            process.StandardError.ReadToEndAsync(); process.StandardOutput.ReadToEndAsync();
            var fs = process.StandardInput.BaseStream as FileStream;
            if (fs == null) throw new InvalidOperationException("no pipe to pw-cat");
            fd = (int)fs.SafeFileHandle.DangerousGetHandle();
            fcntl(fd, F_SETPIPE_SZ, 4096);   // a page: at most ~21 ms can ever wait in the pipe
            Link(source);
        }

        /// <summary>Links the writer's output to the virtual source's input (waits for pw-cat's port to appear).</summary>
        void Link(string source)
        {
            string from = NodeName + ":output_MONO", to = source + ":input_MONO";
            for (int i = 0; i < 100; i++)
            {
                if (process.HasExited) throw new InvalidOperationException("pw-cat stopped");
                int code; string err;
                string ports = Shell.Run("pw-link", new[] { "-o" }, 3000, out code, out err);
                if (ports.Split('\n').Any(l => l.Trim() == from))
                {
                    Shell.Run("pw-link", new[] { from, to }, 3000, out code, out err);
                    if (code == 0 || err.IndexOf("exists", StringComparison.OrdinalIgnoreCase) >= 0) return;
                }
                Thread.Sleep(30);
            }
            throw new InvalidOperationException("couldn't link the clean mic to " + source);
        }

        /// <summary>Samples waiting in the pipe (not yet taken by PipeWire).</summary>
        public int Queued { get { int n; return ioctl(fd, FIONREAD, out n) == 0 ? n / 4 : 0; } }

        public bool Alive { get { return !process.HasExited; } }

        public void Write(float[] buf, int n)
        {
            if (bytes.Length < n * 4) bytes = new byte[n * 4];
            Buffer.BlockCopy(buf, 0, bytes, 0, n * 4);
            int done = 0;
            while (done < n * 4)
            {
                var b = done == 0 ? bytes : bytes.Skip(done).Take(n * 4 - done).ToArray();
                long w = (long)write(fd, b, (IntPtr)(n * 4 - done));
                if (w < 0) throw new IOException("pw-cat stopped taking sound (" + Marshal.GetLastWin32Error() + ")");
                done += (int)w;
            }
        }

        public void Dispose()
        {
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(500)) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
        }
    }
}
