// Every page's text fits (4.1, "make sure formatting works and everything fits"): the main window off screen with fresh
// settings, every page shown with its folded cards opened, and every label measured - text that needs more room than
// its label has (cut off, or "..." at the end) is a failure. Usage: run.ps1 PageFitTest
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class PageFitTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void CardsFit(Control page, string name, List<string> bad)
    {
        var cards = page.Controls.OfType<Card>().Where(c => c.Visible).OrderBy(c => c.Top).ToList();
        for (int k = 0; k < cards.Count; k++)
        {
            if (cards[k].Bottom > page.Height + 2) bad.Add(name + ": a card ends at " + cards[k].Bottom + ", the page at " + page.Height);
            for (int j = k + 1; j < cards.Count; j++)
                if (cards[j].Top < cards[k].Bottom - 2 && cards[j].Left < cards[k].Right && cards[k].Left < cards[j].Right) bad.Add(name + ": cards overlap at y " + cards[j].Top + " (the one above ends at " + cards[k].Bottom + ")");
        }
    }

    static IEnumerable<Control> Descend(Control c) { foreach (Control k in c.Controls) { yield return k; foreach (var d in Descend(k)) yield return d; } }

    [STAThread]
    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-pagefit");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Application.EnableVisualStyles();
        var core = new AppCore();
        foreach (var f in typeof(AppCore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) if (f.FieldType == typeof(Timer)) { var tm = (Timer)f.GetValue(core); if (tm != null) tm.Stop(); }
        var main = new MainForm(core) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(980, 820) };
        main.Show(); Application.DoEvents();
        var pages = (System.Collections.IList)typeof(MainForm).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
        var show = typeof(MainForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic);
        var over = new List<string>(); var layout = new List<string>();
        int measured = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            var page = (Control)pages[i];
            show.Invoke(main, new object[] { i });
            Application.DoEvents();
            string name = page.Controls.OfType<Label>().Select(l => l.Text).FirstOrDefault() ?? ("page " + i);
            // Cards (folds closed, as a page first opens): none overlaps the next or sticks out past the page (4.1.1).
            CardsFit(page, name + " (folded)", layout);
            // Open every fold ("Show"), so the text inside them is measured too.
            foreach (var chev in Descend(page).OfType<Label>().Where(l => l.Text.StartsWith("Show")).ToList()) { chev.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chev, new object[] { EventArgs.Empty }); }
            Application.DoEvents();
            CardsFit(page, name + " (folds open)", layout);
            foreach (var l in Descend(page).OfType<Label>())
            {
                if (!l.Visible || string.IsNullOrEmpty(l.Text) || l.Width <= 0) continue;
                measured++;
                var flags = TextFormatFlags.NoPrefix | (l.UseMnemonic ? 0 : TextFormatFlags.NoPrefix);
                Size need;
                if (l.AutoSize) continue;   // grows with its text
                bool oneLine = l.Height < l.Font.Height * 2 - 2;
                if (oneLine) need = TextRenderer.MeasureText(l.Text, l.Font, new Size(int.MaxValue, l.Height), flags | TextFormatFlags.SingleLine);
                else need = TextRenderer.MeasureText(l.Text, l.Font, new Size(l.Width - l.Padding.Horizontal, int.MaxValue), flags | TextFormatFlags.WordBreak);
                bool fits = oneLine ? need.Width <= l.Width + 2 : need.Height <= l.Height + 2;
                if (!fits) over.Add(name + ": \"" + (l.Text.Length > 60 ? l.Text.Substring(0, 60) + "..." : l.Text) + "\" needs " + need.Width + "x" + need.Height + ", has " + l.Width + "x" + l.Height);
            }
        }
        foreach (var o in layout) Console.WriteLine("    " + o);
        Check(layout.Count == 0, "every page's cards stay apart and inside their page, folded and open");
        foreach (var o in over) Console.WriteLine("    " + o);
        Check(over.Count == 0, measured + " labels on " + pages.Count + " pages (folds open): " + over.Count + " don't fit");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        main.Hide(); Environment.Exit(fails);
    }
}
