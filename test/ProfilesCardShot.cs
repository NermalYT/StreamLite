// A picture of the Headphones page's Game profiles card (test\shots\profiles-card.png): the Presets together switch in
// its header beside the title and the buttons. Settings in a temp folder. Usage: run.ps1 ProfilesCardShot
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class ProfilesCardShot
{
    [STAThread]
    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-cardshot");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Application.EnableVisualStyles();
        var core = new AppCore();
        foreach (var f in typeof(AppCore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) if (f.FieldType == typeof(Timer)) { var tm = (Timer)f.GetValue(core); if (tm != null) { tm.Stop(); tm.Enabled = false; } }
        var main = new MainForm(core) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        main.Show(); Application.DoEvents();
        var sw = (Control)typeof(MainForm).GetField("profTogether", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
        var card = sw.Parent;
        // Its page on screen first (a page that isn't shown draws nothing).
        Control page = card; while (page.Parent != null && !(page.Parent is Form) && page.Parent.GetType() != typeof(MainForm)) { var pages = typeof(MainForm).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main) as System.Collections.IList; if (pages != null && pages.Contains(page)) { typeof(MainForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(main, new object[] { pages.IndexOf(page) }); break; } page = page.Parent; }
        Application.DoEvents();
        using (var b = new Bitmap(card.Width, card.Height)) { card.DrawToBitmap(b, new Rectangle(0, 0, card.Width, card.Height)); b.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\profiles-card.png")); }
        var overlap = card.Controls.Cast<Control>().Where(c => c != sw && c.Visible && c.Bounds.IntersectsWith(sw.Bounds)).Select(c => c.GetType().Name + " " + c.Text).ToList();
        Console.WriteLine(overlap.Count == 0 ? "PASS nothing overlaps the switch" : "FAIL overlaps: " + string.Join(", ", overlap));
        main.Hide(); Environment.Exit(0);
    }
}
