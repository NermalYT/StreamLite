// Renders the Auto EQ apps dialog off-screen (built-in profiles, Music selected, one app added) to a PNG.
// Usage: AutoEqShot <out.png>
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class AutoEqShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var ps = new ProfileSettings();
        GameProfiles.Merge(ps, null);
        var w = new AutoEqAppsDialog(ps, GameProfiles.Music) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        w.Show();
        var body = (Panel)typeof(DarkDialog).GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w);
        foreach (Control c in body.Controls) if (c is TextBox) c.Text = "tidal";
        typeof(AutoEqAppsDialog).GetMethod("AddTyped", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, null);
        Application.DoEvents();
        using (var bmp = new Bitmap(w.Width, w.Height))
        {
            w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
            bmp.Save(args[0]);
        }
        Console.WriteLine("Music apps: " + string.Join(", ", ps.Profiles.Find(p => p.Name == GameProfiles.Music).Exes));
        w.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
