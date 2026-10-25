// Renders the Auto EQ priority window off-screen (Music moved above the games). Usage: PriorityShot <out.png>
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class PriorityShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        var ps = new ProfileSettings(); GameProfiles.Merge(ps, null);
        ps.Priority = new[] { GameProfiles.Music }.Concat(GameProfiles.PriorityOrder(ps).Select(p => p.Name).Where(n => n != GameProfiles.Music)).ToList();
        var d = new ProfilePriorityDialog(ps) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        d.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height)); bmp.Save(args[0]); }
        d.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
