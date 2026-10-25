// Renders the Sync outputs window off-screen (two outputs, one measured). Usage: SyncShot <out.png>
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class SyncShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "sl_syncshot"); Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var core = new AppCore(true);
        core.S.ExtraOutputs.Add(new ExtraOutputSetting { Id = "{out-2}", Name = "Headphones (AirPods Pro)" });
        core.GamePhonesId = "{out-1}"; core.GamePhonesName = "Headphones (QCC Dongle Pro)";
        var d = new OutputSyncDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        d.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height)); bmp.Save(args[0]); }
        d.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
