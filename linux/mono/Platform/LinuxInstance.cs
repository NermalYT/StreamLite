using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace StreamLite.Linux
{
    /// <summary>
    /// One StreamLite at a time on Linux (Windows uses a named mutex and window messages): the running copy listens on a
    /// local-only port written, with a random key, into ~/.config/StreamLite/instance (readable by you only). A second
    /// start sends "show" (or "quit", "check") there and exits.
    /// </summary>
    internal static class LinuxInstance
    {
        static string File_ { get { return Path.Combine(AppSettings.Folder, "instance"); } }
        static TcpListener listener;

        /// <summary>Hands a command to a running StreamLite. True = one is running and took it.</summary>
        public static bool Send(string command)
        {
            try
            {
                if (!File.Exists(File_)) return false;
                var parts = File.ReadAllText(File_).Trim().Split(' ');
                int port; if (parts.Length < 2 || !int.TryParse(parts[0], out port)) return false;
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(800) || !c.Connected) return false;
                    c.EndConnect(ar);
                    var b = Encoding.ASCII.GetBytes(parts[1] + " " + command + "\n");
                    c.GetStream().Write(b, 0, b.Length);
                    c.GetStream().ReadTimeout = 2000;
                    return c.GetStream().ReadByte() == 'k';
                }
            }
            catch { return false; }
        }

        /// <summary>Listens for the commands of later starts; onCommand runs on a worker thread.</summary>
        public static void Serve(Action<string> onCommand)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string key = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(File_, port + " " + key);
            try { Interop.Shell.Run("chmod", "600", File_); } catch { }
            new Thread(() =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    try
                    {
                        using (c)
                        {
                            c.ReceiveTimeout = 2000;
                            var r = new StreamReader(c.GetStream(), Encoding.ASCII);
                            var line = (r.ReadLine() ?? "").Split(' ');
                            if (line.Length < 2 || line[0] != key) continue;
                            c.GetStream().WriteByte((byte)'k');
                            onCommand(line[1]);
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "single instance" }.Start();
        }

        public static void Stop()
        {
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (File.Exists(File_)) File.Delete(File_); } catch { }
        }
    }
}
