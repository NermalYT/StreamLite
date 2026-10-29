using System;
using System.Drawing;
using System.Windows.Forms;
using StreamLite.UI;

// Can StreamLite's own windows run on Linux (Mono's Windows Forms)? Shows the welcome page for a few seconds and saves
// a picture of it. Usage: run.ps1 MonoProbe noop (just builds), then in the Linux lab: mono MonoProbe.exe out.png
class MonoProbe
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "noop") return;
        string outFile = args.Length > 0 ? args[0] : "probe.png";
        Application.EnableVisualStyles();
        var d = new WelcomeDialog();
        var t = new Timer { Interval = 4000 };
        t.Tick += (s, e) =>
        {
            t.Stop();
            try
            {
                using (var b = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(b, new Rectangle(0, 0, d.Width, d.Height)); b.Save(outFile); }
                Console.WriteLine("saved " + outFile + " " + d.Width + "x" + d.Height);
            }
            catch (Exception ex) { Console.WriteLine("picture failed: " + ex.Message); }
            d.Close();
        };
        d.Shown += (s, e) => t.Start();
        Application.Run(d);
    }
}
