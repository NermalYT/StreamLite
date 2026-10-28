using System;
using System.IO;
using System.Threading;

namespace StreamLite.Audio
{
    /// <summary>
    /// Live testing without a person (4.1.1): with STREAMLITE_TEST_MIC set to a folder, a recording named in that folder's
    /// play.txt ("path.wav" or "path.wav|gain dB") is mixed into the mic as it arrives - on top of the real room the mic
    /// hears, at real-time pace, once per change of play.txt. Off (and never looked at again) when the variable isn't set.
    /// The WAV is 16-bit PCM mono at the engine's rate (48 kHz).
    /// </summary>
    internal static class TestMic
    {
        public static readonly bool Active = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STREAMLITE_TEST_MIC"));
        static float[] clip;     // swapped in whole by the watcher; read by the audio thread
        static int pos;
        static Thread watcher;

        /// <summary>The audio thread: the clip (if one is playing) added to this packet.</summary>
        public static void Mix(float[] buf, int n)
        {
            if (watcher == null) Start();
            var c = clip;
            if (c == null) return;
            int p = pos, k = 0;
            for (; k < n && p < c.Length; k++, p++) buf[k] += c[p];
            pos = p;
            if (p >= c.Length) clip = null;
        }

        static void Start()
        {
            lock (typeof(TestMic))
            {
                if (watcher != null) return;
                string dir = Environment.GetEnvironmentVariable("STREAMLITE_TEST_MIC");
                watcher = new Thread(() => Watch(dir)) { IsBackground = true, Name = "test mic" };
                watcher.Start();
            }
        }

        static void Watch(string dir)
        {
            string file = Path.Combine(dir, "play.txt");
            DateTime lastWrite = DateTime.MinValue;
            while (true)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        var w = File.GetLastWriteTimeUtc(file);
                        if (w != lastWrite)
                        {
                            lastWrite = w;
                            string line = File.ReadAllText(file).Trim();
                            if (line.Length > 0)
                            {
                                var parts = line.Split('|');
                                double gain = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                                var c = Load(parts[0].Trim(), (float)Math.Pow(10, gain / 20));
                                pos = 0; clip = c;
                                try { HealthMonitor.LogLine("Test mic: playing " + Path.GetFileName(parts[0]) + " (" + (c.Length / 48000.0).ToString("0.0") + " s, " + gain.ToString("0") + " dB)"); } catch { }
                            }
                        }
                    }
                }
                catch (Exception ex) { try { HealthMonitor.LogLine("Test mic: " + ex.Message); } catch { } }
                Thread.Sleep(100);
            }
        }

        static float[] Load(string path, float gain)
        {
            var b = File.ReadAllBytes(path);
            int i = 12;
            while (i + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
                int len = BitConverter.ToInt32(b, i + 4);
                if (id == "data")
                {
                    int count = Math.Min(len, b.Length - i - 8) / 2;
                    var f = new float[count];
                    for (int k = 0; k < count; k++) f[k] = BitConverter.ToInt16(b, i + 8 + k * 2) / 32768f * gain;
                    return f;
                }
                i += 8 + len + (len & 1);
            }
            throw new InvalidDataException("no audio in " + path);
        }
    }
}
