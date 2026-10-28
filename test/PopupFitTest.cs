// Popups (4.1): every label as tall as its words need - the setup's Equalizer APO note used to cut off after
// "when" on its second line - with the buttons below the text, and popups can't be dragged. Pictures in
// test\shots\popups. Usage: run.ps1 PopupFitTest
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class PopupFitTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Shot(Form f, string dir, string name)
    {
        f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-20000, -20000);
        f.Show(); Application.DoEvents();
        using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(Path.Combine(dir, name + ".png")); }
    }

    /// <summary>Every label shows all its text, and nothing starts inside a label above it.</summary>
    static bool Fits(Form f, out string why)
    {
        var body = (Control)f.GetType().GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy).GetValue(f);
        foreach (var l in body.Controls.OfType<Label>().Where(x => !x.AutoSize && x.Visible && x.Text.Length > 0))
        {
            int need = l.GetPreferredSize(new Size(l.Width, 0)).Height;
            if (need > l.Height) { why = "\"" + l.Text.Substring(0, Math.Min(40, l.Text.Length)) + "...\" needs " + need + " px, has " + l.Height; return false; }
            foreach (Control c in body.Controls)
                if (c != l && c.Visible && c.Bounds.IntersectsWith(l.Bounds) && !(c is Label)) { why = c.GetType().Name + " " + c.Text + " covers \"" + l.Text.Substring(0, Math.Min(30, l.Text.Length)) + "\""; return false; }
            if (l.Bottom > body.ClientSize.Height) { why = "\"" + l.Text.Substring(0, 30) + "\" runs past the popup"; return false; }
        }
        why = null; return true;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\popups"); Directory.CreateDirectory(dir);
        string why;
        // The setup's step 2 note (Equalizer APO), both wordings.
        foreach (bool installed in new[] { false, true })
        {
            using (var w = new WaitDialog("Your headphones", (installed ? "Switching on Equalizer APO for your headphones" : "Installing Equalizer APO (free) for your headphones' EQ") +
                " - approve the Windows prompt. Your sound stops for a moment when it's done."))
            {
                Shot(w, dir, "wait-apo-" + (installed ? "on" : "install"));
                Check(Fits(w, out why), "step 2 (" + (installed ? "switching on" : "installing") + "): all the text shows, Cancel below it" + (why != null ? " - " + why : ""));
            }
        }
        using (var c = new ConfirmDialog("Set up StreamLite again", "StreamLite now takes your mic's raw sound - none of the effects Windows or your mic's maker run first - and tunes everything from it. So everything is set up again, one step at a time (about 6 minutes): your mic, your voice, your headphones' EQ and 3D audio tuned to your ears. Your voice and 3D settings start fresh; your devices, profiles and hotkeys stay.", "Start", "Later", 440, 150))
        {
            Shot(c, dir, "confirm-long");
            Check(Fits(c, out why), "a long question in a small popup grows to fit" + (why != null ? " - " + why : ""));
        }
        // Can't be dragged: the title bar is fixed.
        using (var d = new ConfirmDialog("Test", "Short.", "OK", "Cancel"))
        {
            var bar = d.Controls.OfType<TitleBar>().FirstOrDefault();
            Check(bar != null && !bar.Movable, "popups can't be moved (title bar fixed)");
        }
        // 4.1.1: the developer's thank-you - Continue grey until "I understand" is ticked.
        using (var w = new WelcomeDialog())
        {
            Shot(w, dir, "welcome");
            Check(Fits(w, out why), "the thank-you page: all its text shows" + (why != null ? " - " + why : ""));
            var body = (Control)typeof(DarkDialog).GetField("Body", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy).GetValue(w);
            var go = body.Controls.OfType<FlatButton>().First(b => b.Text == "Continue");
            var tick = body.Controls.OfType<DarkCheck>().First();
            bool greyFirst = !go.Enabled && go.BackColor == Theme.Track;
            tick.Checked = true;
            Check(greyFirst && go.Enabled && go.BackColor == Theme.Accent, "Continue is grey until I understand is ticked, then green");
            Shot(w, dir, "welcome-ticked");
        }
        // The setup's camera step and the in-app bug report.
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-popupfit"); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore(true);
        using (var c = new CameraSetupDialog(core)) { Shot(c, dir, "camera-step"); Check(Fits(c, out why), "the camera step: all its text shows" + (why != null ? " - " + why : "")); }
        using (var b = new BugReportDialog(core)) { Shot(b, dir, "bug-report"); Check(Fits(b, out why), "Report a bug: all its text shows" + (why != null ? " - " + why : "")); }
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
