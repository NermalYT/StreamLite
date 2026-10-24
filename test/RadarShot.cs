using System;
using System.Drawing;
using System.IO;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

// Offscreen renders of the 3D sound sphere: 360 ring with sound, sphere test above-left, below-behind.
class RadarShot
{
    [STAThread]
    static void Main(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-radarshot"); Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore();
        var radar = core.Game.Processor.Radar;
        var c = new SpatialConfig(); radar.SetLayout(SpatialProcessor.Source.Surround71, c);
        var v = new SoundRadarView(core) { Size = new Size(300, 300) };
        var sheet = new Bitmap(900, 300);
        using (var g = Graphics.FromImage(sheet))
        {
            var ring = new float[72]; for (int i = 0; i < 72; i++) ring[i] = -100; for (int i = 22; i < 28; i++) ring[i] = -20 - Math.Abs(i - 25) * 4; for (int i = 0; i < 3; i++) ring[i] = -35;
            radar.Ring = ring;
            Render(v, sheet, g, 0, false, 0, 0, radar);
            radar.Ring = null;
            Render(v, sheet, g, 300, true, 90, 45, radar);
            Render(v, sheet, g, 600, true, 180, -45, radar);
        }
        sheet.Save(args.Length > 0 ? args[0] : "radar.png");
        Console.WriteLine("ok");
        Environment.Exit(0);
    }

    static void Render(SoundRadarView v, Bitmap sheet, Graphics g, int x, bool sphere, float az, float el, SoundRadar radar)
    {
        radar.TestSphere = sphere; radar.TestAz = az; radar.TestEl = el;
        using (var b = new Bitmap(300, 300)) { v.DrawToBitmap(b, new Rectangle(0, 0, 300, 300)); g.DrawImage(b, x, 0); }
    }
}
