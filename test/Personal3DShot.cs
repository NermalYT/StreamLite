// Renders the Personalize 3D window off-screen, mid-test (answer buttons on). Usage: Personal3DShot <out.png>
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class Personal3DShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "sl_p3dshot"); Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var core = new AppCore(true);
        core.GamePhonesId = "{phones}";
        var d = new Personal3DDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        d.Show();
        foreach (Control c in ((Panel)typeof(DarkDialog).GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(d)).Controls) if (c is FlatButton) c.Enabled = true;
        Application.DoEvents();
        using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height)); bmp.Save(args[0]); }
        d.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
