// The Camera page's tracking and gestures cards drawn off-screen (a settings-only core: no camera, no virtual
// camera, a temporary copy of the default settings) -> test\shots\camera_cards.png, and their controls checked for
// overlaps and for staying inside their cards. Usage: run.ps1 CamCardsShot
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class CamCardsShot
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    [STAThread]
    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_camcardsshot");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        Environment.SetEnvironmentVariable("STREAMLITE_AI", Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        Application.EnableVisualStyles();
        var core = new AppCore(true);
        var form = new Form { Width = 760, Height = 1100, BackColor = Theme.Bg };
        var page = new Panel { Location = new Point(0, 0), Size = new Size(Theme.Px(720), Theme.Px(1100)), BackColor = Theme.Bg };
        form.Controls.Add(page);
        var tracking = new CameraTrackingCard(core, page, Theme.Px(10));
        page.Height = tracking.Bottom + Theme.Px(10);
        form.ClientSize = new Size(page.Width, page.Height);
        // Shown far off-screen (painting needs a shown window); pumped briefly - under the cards' 400 ms timers.
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
        form.Show();
        for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
        var cards = page.Controls.OfType<Card>().ToList();
        Check(cards.Count == 2 && cards[1].Top > cards[0].Bottom && tracking.Bottom == cards[1].Bottom, "the gestures card sits under the tracking card (page height follows it)");
        var g = cards[1];
        var boxes = g.Controls.Cast<Control>().Select(c => new KeyValuePair<string, Rectangle>(c.GetType().Name + " \"" + c.Text + "\"", c.Bounds)).ToList();
        var overlaps = new List<string>();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                if (boxes[i].Value.IntersectsWith(boxes[j].Value)) overlaps.Add(boxes[i].Key + " / " + boxes[j].Key);
        Check(overlaps.Count == 0, "gestures card: " + boxes.Count + " controls, none on top of another" + (overlaps.Count > 0 ? ": " + string.Join("; ", overlaps.Take(6)) : ""));
        var outside = boxes.Where(b => b.Value.Right > g.Width - Theme.Px(8) || b.Value.Bottom > g.Height - Theme.Px(4)).Select(b => b.Key).ToList();
        Check(outside.Count == 0, "everything inside the card" + (outside.Count > 0 ? ": " + string.Join("; ", outside) : ""));
        using (var bmp = new Bitmap(page.Width, page.Height))
        {
            page.DrawToBitmap(bmp, new Rectangle(0, 0, page.Width, page.Height));
            string shots = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots"));
            Directory.CreateDirectory(shots);
            bmp.Save(Path.Combine(shots, "camera_cards.png"), ImageFormat.Png);
            Console.WriteLine("  saved " + Path.Combine(shots, "camera_cards.png"));
        }
        form.Dispose();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
