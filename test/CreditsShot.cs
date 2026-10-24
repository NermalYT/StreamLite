// Renders the Credits window off-screen for one version to a PNG. Usage: CreditsShot <out.png> [version]
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class CreditsShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var w = new CreditsWindow(() => { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        w.Show();
        if (args.Length > 1)
        {
            var body = (Panel)typeof(DarkDialog).GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w);
            foreach (Control c in body.Controls)
                if (c is DarkCombo) { var cb = (DarkCombo)c; for (int i = 0; i < cb.Items.Count; i++) if (((string)cb.Items[i]).Contains(args[1])) cb.SelectedIndex = i; }
        }
        Application.DoEvents();
        using (var bmp = new Bitmap(w.Width, w.Height))
        {
            w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
            bmp.Save(args[0]);
        }
        w.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
