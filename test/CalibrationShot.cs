// Renders the Calibrate microphones window off-screen with a finished (synthetic) calibration of two mics.
// Usage: CalibrationShot <out.png>
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

class CalibrationShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "sl_calshot"); Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var core = new AppCore(true);
        core.ActiveMicId = "{mic-1}";
        core.Engine.Mixer.Use(new ExtraMic("{mic-2}", 0));
        var d = new MicCalibrationDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        d.Show();
        var cal = new MicCalibration(2);
        var rnd = new Random(1);
        foreach (var ph in new[] { 0, 1, 2 })
        {
            cal.Phase = ph;
            for (int i = 0; i < 400; i++)
            {
                double a = ph == 1 ? 0.01 * (0.5 + rnd.NextDouble()) : 0, b = ph == 2 ? 0.0016 * (0.5 + rnd.NextDouble()) : 0;
                cal.Add(new[] { (float)(1e-7 + a + 0.25 * b), (float)(1e-7 + b + 0.4 * a) });
            }
        }
        var t = typeof(MicCalibrationDialog);
        t.GetField("cal", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(d, cal);
        t.GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(d, null);
        Application.DoEvents();
        using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height)); bmp.Save(args[0]); }
        d.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
