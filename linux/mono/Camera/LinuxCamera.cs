using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.Interop
{
    /// <summary>A camera (V4L2 video device): its name and /dev/video path.</summary>
    internal class VideoDeviceInfo
    {
        public string Name;
        public string DevicePath;
        public override string ToString() { return Name; }
        public string Key { get { return string.IsNullOrEmpty(DevicePath) ? Name : DevicePath; } }
    }

    internal class VideoMode
    {
        public int Width, Height;
        public double Fps;
        public string Subtype;
        public int CapsIndex;
        public override string ToString() { return Width + "x" + Height + "  " + Math.Round(Fps) + " fps"; }
        public string Key { get { return Width + "x" + Height + "@" + Math.Round(Fps); } }
    }

    /// <summary>
    /// STREAMLITE_TEST_CAMERA (tests and the Linux lab, which have no webcam or v4l2loopback): a camera without hardware -
    /// "1" = ffmpeg's moving test picture, else a video file played in a loop - and StreamLite Camera written to a picture
    /// file instead (STREAMLITE_TEST_CAMERA_OUT, default /tmp/streamlite-camera.jpg, twice a second).
    /// </summary>
    internal static class TestCamera
    {
        public const string InPath = "test:camera", OutPath = "test:streamlite-camera";
        static string Source { get { return Environment.GetEnvironmentVariable("STREAMLITE_TEST_CAMERA"); } }
        public static bool On { get { return !string.IsNullOrEmpty(Source); } }
        public static string Out
        {
            get { string o = Environment.GetEnvironmentVariable("STREAMLITE_TEST_CAMERA_OUT"); return string.IsNullOrEmpty(o) ? "/tmp/streamlite-camera.jpg" : o; }
        }

        /// <summary>ffmpeg's input arguments for the test camera at this size, in real time.</summary>
        public static string Input(int w, int h, double fps)
        {
            string r = Math.Round(fps).ToString(CultureInfo.InvariantCulture);
            return Source == "1" ? "-re -f lavfi -i testsrc2=size=" + w + "x" + h + ":rate=" + r
                : "-re -stream_loop -1 -i " + Shell.Quote(Source) + " -vf scale=" + w + ":" + h + ",fps=" + r;
        }

        public static List<VideoMode> Modes()
        {
            return new List<VideoMode> { new VideoMode { Width = 1920, Height = 1080, Fps = 30, Subtype = "TEST" }, new VideoMode { Width = 1280, Height = 720, Fps = 30, Subtype = "TEST" } };
        }
    }

    internal static class VideoDevices
    {
        /// <summary>Cameras that deliver video (a webcam's metadata node is left out), StreamLite Camera included.</summary>
        public static List<VideoDeviceInfo> List()
        {
            var l = new List<VideoDeviceInfo>();
            if (TestCamera.On)
            {
                l.Add(new VideoDeviceInfo { Name = "Test camera", DevicePath = TestCamera.InPath });
                l.Add(new VideoDeviceInfo { Name = "StreamLite Camera", DevicePath = TestCamera.OutPath });
            }
            const string sys = "/sys/class/video4linux";
            if (!Directory.Exists(sys)) return l;
            foreach (var dir in Directory.GetDirectories(sys).OrderBy(d => d.Length).ThenBy(d => d))
            {
                string node = Path.GetFileName(dir);
                if (!node.StartsWith("video")) continue;
                string name = Read(Path.Combine(dir, "name")) ?? node;
                string index = Read(Path.Combine(dir, "index"));
                if (index != null && index != "0" && !name.StartsWith("StreamLite", StringComparison.Ordinal)) continue;   // the same camera's metadata node
                l.Add(new VideoDeviceInfo { Name = name, DevicePath = "/dev/" + node });
            }
            return l;
        }

        static string Read(string f) { try { return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; } }

        /// <summary>The modes a camera offers (v4l2-ctl --list-formats-ext): MJPEG first (it reaches 1080p/4K at 30 fps), then raw.</summary>
        public static List<VideoMode> Modes(string path)
        {
            var l = new List<VideoMode>();
            if (path == TestCamera.InPath) return TestCamera.Modes();
            if (!Shell.Has("v4l2-ctl")) return l;
            string fmt = null; int w = 0, h = 0;
            try
            {
                foreach (var raw in Shell.Run("v4l2-ctl", "-d", path, "--list-formats-ext").Split('\n'))
                {
                    string line = raw.Trim();
                    var f = Regex.Match(line, @"^\[\d+\]:\s*'(\w+)'"); if (f.Success) { fmt = f.Groups[1].Value; continue; }
                    var s = Regex.Match(line, @"^Size:\s*\w+\s*(\d+)x(\d+)"); if (s.Success) { w = int.Parse(s.Groups[1].Value); h = int.Parse(s.Groups[2].Value); continue; }
                    var i = Regex.Match(line, @"\(([\d.]+) fps\)");
                    if (i.Success && fmt != null && w > 0)
                        l.Add(new VideoMode { Width = w, Height = h, Fps = double.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture), Subtype = fmt == "MJPG" ? "MJPG" : fmt });
                }
            }
            catch { }
            return l.OrderByDescending(m => m.Subtype == "MJPG").ThenByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Fps).ToList();
        }
    }
}

namespace StreamLite.Camera
{
    /// <summary>One of the camera's own picture settings (a V4L2 control): brightness, exposure, focus, white balance...</summary>
    internal class CameraProperty
    {
        public string Key;
        public string Name;
        public bool IsCameraControl;
        public int Id;
        public int Min, Max, Step, Default, Caps;
        public bool SupportsAuto { get { return (Caps & 1) != 0; } }
        /// <summary>Linux: the control's name, and the one that switches it to automatic (null = none).</summary>
        public string Control, AutoControl;
        public int AutoOn = 1, AutoOff = 0;
    }

    /// <summary>A camera's own settings through V4L2 (v4l2-ctl), the same list the Camera page shows on Windows.</summary>
    internal sealed class CameraDevice : IDisposable
    {
        public readonly VideoDeviceInfo Info;
        public readonly List<CameraProperty> Properties = new List<CameraProperty>();

        // V4L2 control -> the name the Camera page uses (and its automatic switch).
        static readonly string[][] Known =
        {
            new[] { "brightness", "Brightness", null }, new[] { "contrast", "Contrast", null }, new[] { "saturation", "Saturation", null },
            new[] { "hue", "Hue", null }, new[] { "sharpness", "Sharpness", null }, new[] { "gamma", "Gamma", null }, new[] { "gain", "Gain", null },
            new[] { "white_balance_temperature", "White balance", "white_balance_automatic|white_balance_temperature_auto" },
            new[] { "backlight_compensation", "Backlight compensation", null }, new[] { "power_line_frequency", "Power line frequency", null },
            new[] { "exposure_time_absolute", "Exposure", "auto_exposure|exposure_auto" }, new[] { "exposure_absolute", "Exposure", "auto_exposure|exposure_auto" },
            new[] { "focus_absolute", "Focus", "focus_automatic_continuous|focus_auto" }, new[] { "zoom_absolute", "Zoom", null },
            new[] { "pan_absolute", "Pan", null }, new[] { "tilt_absolute", "Tilt", null },
        };

        public CameraDevice(VideoDeviceInfo info)
        {
            Info = info;
            if (!Shell.Has("v4l2-ctl")) return;
            var all = new Dictionary<string, Dictionary<string, string>>();
            try
            {
                foreach (var raw in Shell.Run("v4l2-ctl", "-d", info.DevicePath, "--list-ctrls").Split('\n'))
                {
                    var m = Regex.Match(raw.Trim(), @"^(\w+)\s+0x[0-9a-f]+\s+\((\w+)\)\s*:\s*(.*)$");
                    if (!m.Success) continue;
                    var kv = new Dictionary<string, string> { { "type", m.Groups[2].Value } };
                    foreach (Match p in Regex.Matches(m.Groups[3].Value, @"(\w+)=(-?\w+)")) kv[p.Groups[1].Value] = p.Groups[2].Value;
                    all[m.Groups[1].Value] = kv;
                }
            }
            catch { }
            foreach (var k in Known)
            {
                Dictionary<string, string> c;
                if (!all.TryGetValue(k[0], out c) || Properties.Any(p => p.Name == k[1])) continue;
                var prop = new CameraProperty { Key = "v4l2:" + k[0], Name = k[1], Control = k[0], Min = Int(c, "min", 0), Max = Int(c, "max", 1), Step = Math.Max(1, Int(c, "step", 1)), Default = Int(c, "default", 0) };
                if (k[2] != null)
                    foreach (var a in k[2].Split('|'))
                    {
                        Dictionary<string, string> ac;
                        if (!all.TryGetValue(a, out ac)) continue;
                        prop.AutoControl = a; prop.Caps = 1;
                        // auto_exposure is a menu: 3 = aperture priority (automatic), 1 = manual.
                        if (ac["type"] == "menu") { prop.AutoOn = 3; prop.AutoOff = 1; }
                        break;
                    }
                Properties.Add(prop);
            }
        }

        static int Int(Dictionary<string, string> c, string k, int d) { string v; int i; return c.TryGetValue(k, out v) && int.TryParse(v, out i) ? i : d; }

        public bool Get(CameraProperty p, out int value, out bool auto)
        {
            value = p.Default; auto = false;
            try
            {
                var names = p.AutoControl != null ? p.Control + "," + p.AutoControl : p.Control;
                foreach (var raw in Shell.Run("v4l2-ctl", "-d", Info.DevicePath, "--get-ctrl=" + names).Split('\n'))
                {
                    var m = Regex.Match(raw.Trim(), @"^(\w+):\s*(-?\d+)");
                    if (!m.Success) continue;
                    int v = int.Parse(m.Groups[2].Value);
                    if (m.Groups[1].Value == p.Control) value = v; else if (m.Groups[1].Value == p.AutoControl) auto = v == p.AutoOn;
                }
                return true;
            }
            catch { return false; }
        }

        public bool Set(CameraProperty p, int value, bool auto)
        {
            try
            {
                if (p.AutoControl != null) Shell.Run("v4l2-ctl", "-d", Info.DevicePath, "--set-ctrl=" + p.AutoControl + "=" + (auto ? p.AutoOn : p.AutoOff));
                if (!auto || p.AutoControl == null) Shell.Run("v4l2-ctl", "-d", Info.DevicePath, "--set-ctrl=" + p.Control + "=" + value);
                return true;
            }
            catch { return false; }
        }

        public List<VideoMode> Modes() { return VideoDevices.Modes(Info.DevicePath); }

        /// <summary>The camera's full settings: qv4l2 or another V4L2 tool when installed.</summary>
        public void ShowDriverDialog(IWin32Window owner)
        {
            foreach (var tool in new[] { "qv4l2", "cameractrls", "guvcview" })
                if (Shell.Has(tool)) { try { Process.Start(new ProcessStartInfo(tool, tool == "qv4l2" || tool == "guvcview" ? "-d " + Shell.Quote(Info.DevicePath) : "") { UseShellExecute = false }); return; } catch { } }
            MessageBox.Show(owner, "Every setting this camera has is on the Camera page. For its extra driver settings, install qv4l2 (package v4l-utils / qv4l2).", Info.Name);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// One capture per camera, shared: Linux lets one app at a time stream a camera, and the Camera page's preview and
    /// StreamLite Camera both read it. ffmpeg decodes (MJPEG on its threads) to NV12, the format StreamLite works in.
    /// </summary>
    internal sealed class CameraHub
    {
        static readonly Dictionary<string, CameraHub> hubs = new Dictionary<string, CameraHub>();
        public readonly string Path;
        public int Width, Height; public double Fps; public string Format = "";
        public volatile string Error;
        public event Action<byte[], byte[], long> Frame;
        public volatile int Frames;
        int users;
        Process ff;
        Thread reader;
        volatile bool stop;

        CameraHub(string path) { Path = path; }

        public static CameraHub Open(string path, int w, int h, double fps)
        {
            lock (hubs)
            {
                CameraHub hub;
                if (!hubs.TryGetValue(path, out hub)) { hub = new CameraHub(path); hubs[path] = hub; }
                if (hub.users++ == 0) hub.Start(w, h, fps);
                return hub;
            }
        }

        public void Release()
        {
            lock (hubs)
            {
                if (--users > 0) return;
                hubs.Remove(Path);
                Stop();
            }
        }

        /// <summary>Restarts the capture in another mode (the Camera page's format choice).</summary>
        public void Reopen(int w, int h, double fps) { lock (hubs) { Stop(); Start(w, h, fps); } }

        void Start(int w, int h, double fps)
        {
            Error = null; stop = false;
            if (!Shell.Has("ffmpeg")) { Error = "StreamLite needs ffmpeg for cameras (the installer adds it - run it again)"; return; }
            var modes = VideoDevices.Modes(Path);
            VideoMode pick = null;
            if (w > 0) pick = modes.Where(m => m.Width == w && m.Height == h).OrderBy(m => Math.Abs(m.Fps - fps)).FirstOrDefault();
            if (pick == null) pick = modes.Where(m => m.Height <= Math.Max(h, 1080) && m.Fps >= 24).OrderByDescending(m => m.Height).ThenByDescending(m => m.Subtype == "MJPG").FirstOrDefault() ?? modes.FirstOrDefault();
            if (pick != null) { Width = pick.Width; Height = pick.Height; Fps = pick.Fps; Format = pick.Subtype; } else { Width = w > 0 ? w : 1280; Height = h > 0 ? h : 720; Fps = fps > 0 ? fps : 30; }
            string input = pick != null && pick.Subtype == "MJPG" ? "-input_format mjpeg " : pick != null && pick.Subtype == "YUYV" ? "-input_format yuyv422 " : "";
            string args = "-hide_banner -loglevel error -f v4l2 " + input + "-video_size " + Width + "x" + Height + " -framerate " + Math.Round(Fps).ToString(CultureInfo.InvariantCulture) +
                " -i " + Shell.Quote(Path) + " -f rawvideo -pix_fmt nv12 pipe:1";
            if (Path == TestCamera.InPath) args = "-hide_banner -loglevel error " + TestCamera.Input(Width, Height, Fps) + " -f rawvideo -pix_fmt nv12 pipe:1";
            try
            {
                ff = Process.Start(new ProcessStartInfo("ffmpeg", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                var me = ff;
                ff.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Error = e.Data.Contains("Device or resource busy") ? "Another app is using the camera" : e.Data; };
                ff.BeginErrorReadLine();
                reader = new Thread(() => Read(me)) { IsBackground = true, Name = "Camera" };
                reader.Start();
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        void Read(Process p)
        {
            int ys = Width * Height, uvs = ys / 2;
            var s = p.StandardOutput.BaseStream;
            while (!stop)
            {
                var y = new byte[ys]; var uv = new byte[uvs];
                if (!Fill(s, y) || !Fill(s, uv)) { if (!stop && Error == null) Error = "The camera stopped"; return; }
                Frames++;
                var f = Frame; if (f != null) try { f(y, uv, Stopwatch.GetTimestamp()); } catch { }
            }
        }

        static bool Fill(Stream s, byte[] b)
        {
            int got = 0;
            while (got < b.Length) { int r = s.Read(b, got, b.Length - got); if (r <= 0) return false; got += r; }
            return true;
        }

        void Stop()
        {
            stop = true;
            try { if (ff != null && !ff.HasExited) ff.Kill(); } catch { }
            ff = null;
            if (reader != null) reader.Join(1000);
            reader = null;
        }
    }

    /// <summary>Reads a camera as NV12 frames (the same as the Windows reader), through the shared capture.</summary>
    internal sealed class CameraReader : IDisposable
    {
        public int Width, Height;
        public double Fps;
        public string Format = "";
        public string Error;
        public volatile int Frames;
        public event Action<byte[], byte[], long> FrameArrived;
        CameraHub hub;

        public bool Open(string devicePath, string name, int wantW, int wantH, double wantFps)
        {
            if (string.IsNullOrEmpty(devicePath)) { Error = "No camera"; return false; }
            hub = CameraHub.Open(devicePath, wantW, wantH, wantFps);
            hub.Frame += OnFrame;
            for (int i = 0; i < 40 && hub.Frames == 0 && hub.Error == null; i++) Thread.Sleep(100);
            Width = hub.Width; Height = hub.Height; Fps = hub.Fps; Format = hub.Format;
            if (hub.Frames == 0) { Error = hub.Error ?? "The camera sends no pictures"; Dispose(); return false; }
            return true;
        }

        void OnFrame(byte[] y, byte[] uv, long t) { Frames++; var f = FrameArrived; if (f != null) f(y, uv, t); }

        public void Dispose()
        {
            var h = hub; hub = null;
            if (h != null) { h.Frame -= OnFrame; h.Release(); }
        }
    }

    /// <summary>The Camera page's live picture: the shared capture, scaled into the page's box.</summary>
    internal sealed class CameraPreview : IDisposable
    {
        public int VideoWidth = 16, VideoHeight = 9;
        public VideoMode ActiveMode;
        public bool RequestRefused;
        public string RefuseReason;
        Control control;
        CameraHub hub;
        volatile Bitmap shown;
        readonly Queue<long> stamps = new Queue<long>();
        int painting;

        public string Start(VideoDeviceInfo info, Control target, int width, int height, double fps)
        {
            Dispose();
            control = target;
            hub = CameraHub.Open(info.DevicePath, width, height, fps);
            if (width > 0 && (hub.Width != width || hub.Height != height)) hub.Reopen(width, height, fps);
            for (int i = 0; i < 40 && hub.Frames == 0 && hub.Error == null; i++) Thread.Sleep(100);
            if (hub.Error != null && hub.Frames == 0) { string e = hub.Error; Dispose(); return e; }
            VideoWidth = hub.Width; VideoHeight = hub.Height;
            ActiveMode = new VideoMode { Width = hub.Width, Height = hub.Height, Fps = hub.Fps, Subtype = hub.Format };
            RequestRefused = width > 0 && (hub.Width != width || hub.Height != height);
            RefuseReason = RequestRefused ? "the camera doesn't offer it" : null;
            hub.Frame += OnFrame;
            control.Paint += OnPaint;
            return null;
        }

        void OnFrame(byte[] y, byte[] uv, long t)
        {
            lock (stamps) { stamps.Enqueue(t); while (stamps.Count > 60) stamps.Dequeue(); }
            var c = control;
            if (c == null || c.Width < 2 || c.Height < 2 || Interlocked.CompareExchange(ref painting, 1, 0) != 0) return;
            var bmp = Small(y, uv, hub.Width, hub.Height, c.Width, c.Height);
            var old = shown; shown = bmp; if (old != null) old.Dispose();
            try { c.BeginInvoke(new Action(() => { c.Invalidate(); painting = 0; })); } catch { painting = 0; }
        }

        /// <summary>NV12 -> a bitmap the size of the box (nearest pixel: cheap enough for a live preview).</summary>
        static Bitmap Small(byte[] y, byte[] uv, int w, int h, int bw, int bh)
        {
            double s = Math.Min((double)bw / w, (double)bh / h);
            int ow = Math.Max(1, (int)(w * s)), oh = Math.Max(1, (int)(h * s));
            var bmp = new Bitmap(ow, oh, PixelFormat.Format32bppRgb);
            var d = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            var row = new int[ow];
            for (int j = 0; j < oh; j++)
            {
                int sy = j * h / oh;
                for (int i = 0; i < ow; i++)
                {
                    int sx = i * w / ow;
                    int Y = y[sy * w + sx], u = uv[(sy / 2) * w + (sx & ~1)] - 128, v = uv[(sy / 2) * w + (sx & ~1) + 1] - 128;
                    int r = Y + (int)(1.402 * v), g = Y - (int)(0.344 * u + 0.714 * v), b = Y + (int)(1.772 * u);
                    row[i] = (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, d.Scan0 + j * d.Stride, ow);
            }
            bmp.UnlockBits(d);
            return bmp;
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        void OnPaint(object sender, PaintEventArgs e)
        {
            var b = shown; var c = control; if (b == null || c == null) return;
            try { e.Graphics.DrawImage(b, (c.Width - b.Width) / 2, (c.Height - b.Height) / 2, b.Width, b.Height); } catch { }
        }

        /// <summary>Frames per second actually arriving (last ~2 s).</summary>
        public double SampleFps()
        {
            lock (stamps)
            {
                if (stamps.Count < 2) return 0;
                double secs = (stamps.Last() - stamps.Peek()) / (double)Stopwatch.Frequency;
                return secs > 0 ? (stamps.Count - 1) / secs : 0;
            }
        }

        public void Resize() { var c = control; if (c != null) c.Invalidate(); }
        public bool Active { get { return control != null; } }

        public void Dispose()
        {
            var h = hub; hub = null;
            if (h != null) { h.Frame -= OnFrame; h.Release(); }
            if (control != null) { control.Paint -= OnPaint; control = null; }
            var b = shown; shown = null; if (b != null) b.Dispose();
        }
    }

    internal class PnpCamera { public string Name; public string InstanceId; public bool Enabled; }

    /// <summary>Turning a camera off for every app is a Windows device setting; on Linux the list is empty (nothing offered).</summary>
    internal static class CameraPnp
    {
        public static List<PnpCamera> List() { return new List<PnpCamera>(); }
        public static string SetEnabled(string instanceId, bool enable) { return "not on Linux"; }
    }

    /// <summary>
    /// "StreamLite Camera" on Linux: a v4l2loopback virtual camera every app can pick (Discord, OBS, browsers, Zoom). The
    /// installer sets up the v4l2loopback module; StreamLite writes its picture into it with ffmpeg.
    /// </summary>
    internal sealed class VirtualCamera : IDisposable
    {
        public const string FriendlyName = "StreamLite Camera";
        public const int Width = 1920, Height = 1080;
        public const string NeedsAdmin = "Setting up StreamLite Camera needs your password once (it adds the virtual camera to the system).";
        public string Error;
        Process ff;
        Stream input;
        string device;
        long lastConsumer = Environment.TickCount;
        int lastCheck;
        volatile bool writing;

        /// <summary>The loopback camera's device, or null.</summary>
        static string Device()
        {
            var d = VideoDevices.List().FirstOrDefault(v => v.Name.StartsWith(FriendlyName, StringComparison.Ordinal));
            return d != null ? d.DevicePath : null;
        }

        /// <summary>The virtual camera can work here: v4l2loopback is installed (the module exists) and ffmpeg too.</summary>
        public static bool Supported
        {
            get
            {
                if (!Shell.Has("ffmpeg")) return false;
                if (Device() != null) return true;
                try { int code; string err; Shell.Run("modinfo", new[] { "v4l2loopback" }, 3000, out code, out err); return code == 0; } catch { return false; }
            }
        }

        /// <summary>Why StreamLite Camera can't work here (when Supported is false).</summary>
        public static string WhyNot
        {
            get
            {
                return !Shell.Has("ffmpeg") ? "Needs ffmpeg - run StreamLite's installer again"
                    : "Needs the virtual camera module (v4l2loopback) - run StreamLite's installer again, or install v4l2loopback from your distro";
            }
        }

        /// <summary>null when StreamLite Camera exists; NeedsAdmin when it must be added first (RegisterWithPrompt).</summary>
        public static string Register() { return Device() != null ? null : NeedsAdmin; }

        /// <summary>Adds StreamLite Camera (asks for your password through the desktop) - kept across restarts by the installer's module settings.</summary>
        public static string RegisterWithPrompt()
        {
            if (Device() != null) return null;
            try
            {
                int code; string err;
                Shell.Run("pkexec", new[] { "modprobe", "v4l2loopback", "devices=1", "exclusive_caps=1", "card_label=" + FriendlyName }, 120000, out code, out err);
                for (int i = 0; i < 20 && Device() == null; i++) Thread.Sleep(100);
                return Device() != null ? null : "Couldn't add StreamLite Camera: " + (string.IsNullOrEmpty(err) ? "the virtual camera module (v4l2loopback) isn't installed - run StreamLite's installer again" : err.Trim());
            }
            catch (Exception ex) { return "Couldn't add StreamLite Camera: " + ex.Message; }
        }

        public static void Unregister() { }

        public bool Start()
        {
            Error = null;
            device = Device();
            if (device == null) { Error = NeedsAdmin; return false; }
            try
            {
                string to = device == TestCamera.OutPath ? "-vf fps=2 -update 1 -y " + Shell.Quote(TestCamera.Out) : "-f v4l2 -pix_fmt yuv420p " + Shell.Quote(device);
                ff = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -f rawvideo -pix_fmt nv12 -video_size " + Width + "x" + Height + " -framerate 30 -i pipe:0 " + to)
                    { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                ff.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Error = e.Data; };
                ff.BeginErrorReadLine();
                input = ff.StandardInput.BaseStream;
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        /// <summary>Milliseconds since an app last had StreamLite Camera open (any process with the device open, other than StreamLite's writer).</summary>
        public long SinceLastConsumer
        {
            get
            {
                if (Environment.TickCount - lastCheck > 1000)
                {
                    lastCheck = Environment.TickCount;
                    if (Watched()) lastConsumer = Environment.TickCount;
                }
                return Environment.TickCount - lastConsumer;
            }
        }

        bool Watched()
        {
            if (device == null) return false;
            if (device == TestCamera.OutPath) return true;   // the test picture file: as if an app watched
            int me = Process.GetCurrentProcess().Id, writer = ff != null ? ff.Id : -1;
            foreach (var dir in Directory.GetDirectories("/proc"))
            {
                int pid; if (!int.TryParse(Path.GetFileName(dir), out pid) || pid == me || pid == writer) continue;
                try { foreach (var fd in Directory.GetFiles(dir + "/fd")) if (ReadLink(fd) == device) return true; } catch { }
            }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] static extern int readlink(string path, byte[] buf, int size);
        static string ReadLink(string p) { var b = new byte[256]; int n = readlink(p, b, b.Length); return n > 0 ? System.Text.Encoding.UTF8.GetString(b, 0, n) : null; }

        /// <summary>One 1080p picture (NV12); skipped when the writer is still busy with the last one.</summary>
        public void Publish(byte[] y, byte[] uv)
        {
            var s = input; if (s == null || writing) return;
            writing = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { s.Write(y, 0, Math.Min(y.Length, Width * Height)); s.Write(uv, 0, Math.Min(uv.Length, Width * Height / 2)); s.Flush(); }
                catch (Exception ex) { Error = ex.Message; }
                finally { writing = false; }
            });
        }

        public void Dispose()
        {
            try { if (input != null) input.Close(); } catch { }
            try { if (ff != null && !ff.HasExited) ff.Kill(); } catch { }
            input = null; ff = null;
        }
    }
}
