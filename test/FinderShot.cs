// "Find my headphones" as it looks for a few searches (offscreen PNGs in test\shots\finder). Usage: run.ps1 FinderShot
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

class FinderShot
{
    static void Main()
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\finder"); Directory.CreateDirectory(dir);
        var index = AutoEq.Index(Path.Combine(Path.GetTempPath(), "StreamLite-matchtest"));
        foreach (var q in new[] { "momentum 5", "Sennheiser Momentum 6", "zzz headset 9000" })
        {
            var f = new HeadphoneFinder("") { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
            const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(HeadphoneFinder).GetField("index", Any).SetValue(f, index);
            f.Show(); Application.DoEvents();
            ((TextBox)typeof(HeadphoneFinder).GetField("search", Any).GetValue(f)).Text = q;
            typeof(HeadphoneFinder).GetMethod("Filter", Any).Invoke(f, null); Application.DoEvents();
            using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(Path.Combine(dir, q.Replace(' ', '-') + ".png")); }
            var list = (ListBox)typeof(HeadphoneFinder).GetField("results", Any).GetValue(f);
            Console.WriteLine(q + ": " + list.Items.Count + " results; first: " + (list.Items.Count > 0 ? list.Items[0].ToString() : "-") + "; last: " + (list.Items.Count > 0 ? list.Items[list.Items.Count - 1].ToString() : "-"));
            f.Close();
        }
    }
}
