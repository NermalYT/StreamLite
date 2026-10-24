using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using StreamLite.Camera;
using StreamLite.Interop;

// Streams the NexiGo at 1080p60 into a sample grabber, applies camera settings, saves a frame and prints image stats.
// Usage: CamTune <out.png> [key=value ...]   (key = amp.N / cam.N as in the app; value "auto" for auto mode)
// Several shots: separate groups with "--next <out.png>".
[ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISampleGrabber
{
    [PreserveSig] int SetOneShot(bool oneShot);
    [PreserveSig] int SetMediaType(IntPtr mt);
    [PreserveSig] int GetConnectedMediaType(IntPtr mt);
    [PreserveSig] int SetBufferSamples(bool buffer);
    [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
    [PreserveSig] int GetCurrentSample(out IntPtr sample);
    [PreserveSig] int SetCallback(ISampleGrabberCB cb, int which);
}

[ComImport, Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISampleGrabberCB
{
    [PreserveSig] int SampleCB(double time, IntPtr sample);
    [PreserveSig] int BufferCB(double time, IntPtr buffer, int length);
}

class Grab : ISampleGrabberCB
{
    public readonly object Gate = new object();
    public byte[] Last;
    public int Frames;
    public int SampleCB(double time, IntPtr sample) { return 0; }
    public int BufferCB(double time, IntPtr buffer, int length)
    {
        lock (Gate)
        {
            if (Last == null || Last.Length != length) Last = new byte[length];
            Marshal.Copy(buffer, Last, 0, length);
            Frames++;
        }
        return 0;
    }
}

class CamTune
{
    const int W = 1920, H = 1080;

    [STAThread]
    static void Main(string[] args)
    {
        var info = VideoDevices.List().First(x => x.Name.Contains("NexiGo"));
        var dev = new CameraDevice(info);

        object graph = Activator.CreateInstance(Type.GetTypeFromCLSID(DsGuids.CLSID_FilterGraph));
        var cgb = (ICaptureGraphBuilder2)Activator.CreateInstance(Type.GetTypeFromCLSID(DsGuids.CLSID_CaptureGraphBuilder2));
        cgb.SetFiltergraph((IGraphBuilder)graph);
        object source = VideoDevices.BindFilter(info.Moniker);
        ((IGraphBuilder)graph).AddFilter((IBaseFilter)source, "Camera");
        var sc = DsFormats.CaptureConfig(source);
        Console.WriteLine("force 1080p60: " + (DsFormats.ForceMode(sc, W, H, 60) ?? "refused"));

        object grabberObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C1F400A0-3F08-11d3-9F0B-006008039E37")));
        var grabber = (ISampleGrabber)grabberObj;
        IntPtr mt = Marshal.AllocCoTaskMem(88);
        for (int i = 0; i < 88; i++) Marshal.WriteByte(mt, i, 0);
        Marshal.StructureToPtr(DsGuids.MEDIATYPE_Video, mt, false);
        Marshal.StructureToPtr(new Guid("e436eb7d-524f-11ce-9f53-0020af0ba770"), mt + 16, false); // RGB24
        grabber.SetMediaType(mt);
        var cb = new Grab();
        grabber.SetBufferSamples(false);
        grabber.SetCallback(cb, 1);
        ((IGraphBuilder)graph).AddFilter((IBaseFilter)grabberObj, "Grabber");
        object nullRenderer = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C1F400A4-3F08-11d3-9F0B-006008039E37")));
        ((IGraphBuilder)graph).AddFilter((IBaseFilter)nullRenderer, "Null");
        Guid cat = DsGuids.PIN_CATEGORY_CAPTURE, type = DsGuids.MEDIATYPE_Video;
        int hr = cgb.RenderStream(ref cat, ref type, source, (IBaseFilter)grabberObj, (IBaseFilter)nullRenderer);
        if (hr < 0) { Console.WriteLine("RenderStream failed 0x" + hr.ToString("X8")); return; }
        var mc = (IMediaControl)graph;
        hr = mc.Run();
        if (hr < 0) { Console.WriteLine("Run failed 0x" + hr.ToString("X8") + " (camera busy?)"); return; }

        // Split args into shots.
        var shots = new List<List<string>>();
        var cur = new List<string>();
        foreach (var a in args)
        {
            if (a == "--next") { shots.Add(cur); cur = new List<string>(); continue; }
            cur.Add(a);
        }
        shots.Add(cur);

        foreach (var shot in shots)
        {
            string outPath = shot[0];
            foreach (var kv in shot.Skip(1))
            {
                var parts = kv.Split('=');
                var p = dev.Properties.FirstOrDefault(x => x.Key == parts[0]);
                if (p == null) { Console.WriteLine("unknown " + parts[0]); continue; }
                int cv; bool ca; dev.Get(p, out cv, out ca);
                if (parts[1] == "auto") dev.Set(p, cv, true); else dev.Set(p, int.Parse(parts[1]), false);
            }
            Thread.Sleep(2500); // let exposure / white balance settle
            int f0; lock (cb.Gate) f0 = cb.Frames;
            var t0 = DateTime.Now;
            Thread.Sleep(2000);
            int f1; byte[] frame; lock (cb.Gate) { f1 = cb.Frames; frame = (byte[])cb.Last.Clone(); }
            double fps = (f1 - f0) / (DateTime.Now - t0).TotalSeconds;
            Analyze(frame, outPath, fps, dev);
        }
        mc.Stop();
    }

    static void Analyze(byte[] rgb, string outPath, double fps, CameraDevice dev)
    {
        int stride = W * 3;
        double sumY = 0, sumR = 0, sumG = 0, sumB = 0, sumS = 0; long n = 0, clip = 0, crush = 0, mid = 0;
        var hist = new int[256];
        for (int y = 0; y < H; y += 2)
            for (int x = 0; x < W; x += 2)
            {
                int o = (H - 1 - y) * stride + x * 3; // bottom-up DIB
                int b = rgb[o], g = rgb[o + 1], r = rgb[o + 2];
                double Y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                hist[(int)Y]++;
                sumY += Y; n++;
                if (r >= 250 || g >= 250 || b >= 250) clip++;
                if (Y < 10) crush++;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                sumS += mx == 0 ? 0 : (mx - mn) / (double)mx;
                if (Y > 40 && Y < 200) { sumR += r; sumG += g; sumB += b; mid++; }
            }
        // Sharpness: variance of the Laplacian on luma (center crop).
        double lapSum = 0, lapSq = 0; long ln = 0;
        for (int y = H / 4; y < H * 3 / 4; y++)
            for (int x = W / 4; x < W * 3 / 4; x++)
            {
                Func<int, int, double> L = (xx, yy) => { int o = (H - 1 - yy) * stride + xx * 3; return 0.2126 * rgb[o + 2] + 0.7152 * rgb[o + 1] + 0.0722 * rgb[o]; };
                double lap = 4 * L(x, y) - L(x - 1, y) - L(x + 1, y) - L(x, y - 1) - L(x, y + 1);
                lapSum += lap; lapSq += lap * lap; ln++;
            }
        double lapVar = lapSq / ln - Math.Pow(lapSum / ln, 2);
        int p5 = Pct(hist, n, 0.05), p50 = Pct(hist, n, 0.5), p95 = Pct(hist, n, 0.95), p99 = Pct(hist, n, 0.99);
        Console.WriteLine(string.Format("{0}: fps {1:0.0} | luma mean {2:0} p5 {3} p50 {4} p95 {5} p99 {6} | clipped {7:0.0}% crushed {8:0.0}% | sat {9:0.00} | R/G {10:0.000} B/G {11:0.000} | sharp {12:0}",
            System.IO.Path.GetFileName(outPath), fps, sumY / n, p5, p50, p95, p99, 100.0 * clip / n, 100.0 * crush / n, sumS / n,
            mid > 0 ? sumR / sumG : 0, mid > 0 ? sumB / sumG : 0, lapVar));
        var parts = new List<string>();
        foreach (var p in dev.Properties.Where(p => p.Key.StartsWith("amp") || p.Key == "cam.4" || p.Key == "cam.6"))
        { int v; bool a; dev.Get(p, out v, out a); parts.Add(p.Name + "=" + v + (a ? "A" : "")); }
        Console.WriteLine("   settings: " + string.Join(", ", parts));

        using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
        {
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            for (int y = 0; y < H; y++) Marshal.Copy(rgb, (H - 1 - y) * stride, data.Scan0 + y * data.Stride, stride);
            bmp.UnlockBits(data);
            using (var small = new Bitmap(bmp, new Size(960, 540))) small.Save(outPath, ImageFormat.Png);
            // Full-res face/center crop for detail checks.
            using (var crop = bmp.Clone(new Rectangle(300, 600, 800, 450), PixelFormat.Format24bppRgb))
                crop.Save(outPath.Replace(".png", "-crop.png"), ImageFormat.Png);
        }
    }

    static int Pct(int[] hist, long n, double q)
    {
        long acc = 0;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= q * n) return i; }
        return 255;
    }
}


