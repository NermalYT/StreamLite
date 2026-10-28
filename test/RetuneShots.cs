// Pictures of the required setup (test\shots\retune): the setup screen, Set up StreamLite again, the devices, Tune to my voice with the
// big green record button (ready, counting down, recording, Done) - and the flow's checks: the countdown starts from 5,
// recording starts when it ends, the reset puts every voice and 3D setting back. Settings in a temp folder, no devices
// changed. Usage: run.ps1 RetuneShots
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class RetuneShots
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Save(Form f, string dir, string name)
    {
        f.Show(); Application.DoEvents();
        using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(Path.Combine(dir, name + ".png")); }
    }

    [STAThread]
    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-retunetest");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Application.EnableVisualStyles();
        var core = new AppCore(true);
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\retune"); Directory.CreateDirectory(dir);

        // The reset: voice and 3D settings back to the start, devices kept.
        core.S.Game.Room = 77; core.S.Game.OpenSphere = false; core.S.Game.CueSharpen = 90; core.S.Game.HeadSize = 112; core.S.Game.HeadphonesId = "{phones}";
        core.S.Game.PersonalHeads["{phones}"] = "kemar|70|75|112"; core.S.DeEsser = !AppSettings.Defaults().DeEsser; core.S.MicId = "{mic}";
        core.ResetTuning();
        var d0 = new GameAudioSettings(); d0.Repair(); GamePresets.Apply(d0, d0.Preset);
        Check(core.S.Game.Room == d0.Room && core.S.Game.OpenSphere && core.S.Game.HeadSize == 100 && core.S.Game.PersonalHeads.Count == 0, "reset: 3D settings back to the start (open sphere, head size 100%, no personal head)");
        Check(core.S.DeEsser == AppSettings.Defaults().DeEsser, "reset: voice settings back to the start");
        Check(core.S.Game.HeadphonesId == "{phones}" && core.S.MicId == "{mic}", "reset: your devices are kept");

        // The setup screen (4.0.4): the window fills the (stand-in) primary monitor, the settings covered, the steps along
        // the bottom, the setup's window in the middle; afterwards a normal window in the middle of that monitor.
        {
            // A stand-in for the primary monitor off to the side, no bigger than the real one (Windows won't stretch a
            // window past the real screen - a CI machine has a small one).
            var real = Screen.PrimaryScreen.Bounds;
            var screen = new Rectangle(-30000, -30000, Math.Min(1920, real.Width), Math.Min(1080, real.Height));
            MainForm.SetupScreenForTests = screen;
            var full = new AppCore();   // the window needs the whole app (devices only looked at; its timers stopped)
            foreach (var f in typeof(AppCore).GetFields(Any)) if (f.FieldType == typeof(Timer)) { var tm = (Timer)f.GetValue(full); tm.Stop(); tm.Enabled = false; }
            var main = new MainForm(full) { ShowInTaskbar = false };
            main.BeginSetupScreen(RetuneWizard.FirstTitle, RetuneWizard.Subtitle);
            main.Show(); Application.DoEvents();
            Check(main.Bounds == screen && main.InSetupScreen, "the window fills the primary monitor (" + main.Bounds + ")");
            var cover = main.Controls.OfType<SetupCover>().FirstOrDefault();
            Check(cover != null && cover.Bounds.Contains(new Rectangle(main.ClientRectangle.X + 2, main.ClientRectangle.Y + 2, main.ClientRectangle.Width - 4, main.ClientRectangle.Height - 4)) && main.Controls.GetChildIndex(cover) == 0,
                "the settings are covered (the cover is on top of everything)");
            main.SetupStep(2);
            main.ToggleFullScreen();
            Check(main.Bounds == screen, "F11 can't take the setup out of fullscreen");
            using (var bmp = new Bitmap(screen.Width, screen.Height))
            {
                main.DrawToBitmap(bmp, new Rectangle(0, 0, screen.Width, screen.Height));
                // (DrawToBitmap paints overlapping children in the wrong order: the cover goes on top by hand, as on screen.)
                using (var cb = new Bitmap(cover.Width, cover.Height)) using (var g = Graphics.FromImage(bmp)) { cover.DrawToBitmap(cb, new Rectangle(0, 0, cb.Width, cb.Height)); g.DrawImage(cb, cover.Left, cover.Top); }
                using (var dlg = new VoiceSetupDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
                {
                    dlg.Show(); Application.DoEvents();
                    using (var db = new Bitmap(dlg.Width, dlg.Height))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        dlg.DrawToBitmap(db, new Rectangle(0, 0, dlg.Width, dlg.Height));
                        g.DrawImage(db, (screen.Width - dlg.Width) / 2, (screen.Height - dlg.Height) / 2);   // where CenterParent puts it
                    }
                    dlg.Close();
                }
                bmp.Save(Path.Combine(dir, "0-setup-screen.png"));
            }
            // Personalize 3D in the setup (4.0.6): its own page with a copy of the 3D Audio page's diagram - the settings
            // stay hidden - and Personalize 3D's picture to zoom into is that page (not the settings under it).
            {
                main.SetupStep(4);
                bool later = false;
                main.OpenPersonalize3D(() => later = true);
                Application.DoEvents();
                var copy = cover.Controls.OfType<SphereView>().FirstOrDefault();
                var buttons = cover.Controls.OfType<FlatButton>().Select(b => b.Text).ToList();
                Check(copy != null && copy.Width >= Theme.Px(160) && main.Controls.GetChildIndex(cover) == 0, "Personalize 3D's setup page: its sphere, which you can drag (" + (copy == null ? "none" : copy.Size.ToString()) + "), still over the settings");
                Check(buttons.Contains("Start") && buttons.Contains("Later"), "with Start and Later");
                using (var bmp = new Bitmap(main.Width, main.Height))
                {
                    main.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    typeof(StreamLite.UI.Personal3DView).GetMethod("DrawCover", Any).Invoke(null, new object[] { main, copy, bmp });
                    bmp.Save(Path.Combine(dir, "0b-personalize-page.png"));
                    // The diagram's middle, in the picture Personalize 3D zooms into: the copy's card colour, not the settings.
                    var c = main.PointToScreen(copy.Location); var px = bmp.GetPixel(c.X - main.Left + Theme.Px(6), c.Y - main.Top + copy.Height / 2);
                    Check(Math.Abs(px.R - Theme.Card.R) + Math.Abs(px.G - Theme.Card.G) + Math.Abs(px.B - Theme.Card.B) < 12, "Personalize 3D zooms into that copy (its picture shows the setup page, " + px + ")");
                }
                cover.Controls.OfType<FlatButton>().First(b => b.Text == "Later").PerformClick();
                Check(later && !StreamLite.UI.Personal3DView.LastRunCompleted, "Later leaves it not done (the setup comes back next time)");
            }
            main.EndSetupScreen();
            Check(!main.InSetupScreen && main.Width < screen.Width && Math.Abs(main.Left + main.Width / 2 - (screen.Left + screen.Width / 2)) <= 1 && Math.Abs(main.Top + main.Height / 2 - (screen.Top + screen.Height / 2)) <= 1,
                "afterwards: a normal window in the middle of the primary monitor (" + main.Bounds + ")");
            main.Hide(); main.Dispose();
            MainForm.SetupScreenForTests = null;
        }

        // Required (4.0.5): until it's done through Personalize 3D - new installs and updates from any version.
        Func<string, bool> needed = ver => { var st = new AppSettings { SetupDoneFor = ver }; return RetuneWizard.Needed(st); };
        Check(needed(null) && needed("3.5.0") && needed("4.0.4") && !needed("4.0.5") && !needed("4.1.0"), "the setup is required for everyone until it's been done in 4.0.5 (or later)");
        using (var q = new ConfirmDialog(RetuneWizard.UpdateTitle, RetuneWizard.UpdateText, "Start", "Later", 520, 250) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            Save(q, dir, "1-question");
        using (var p = new DevicePickDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            Save(p, dir, "2-devices");

        var v = new VoiceSetupDialog(core) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        var t = typeof(VoiceSetupDialog);
        Func<string> phase = () => t.GetField("phase", Any).GetValue(v).ToString();
        Save(v, dir, "3-intro");
        t.GetField("idx", Any).SetValue(v, 0); t.GetMethod("ShowStep", Any).Invoke(v, null);
        var rec = (RecordButton)t.GetField("rec", Any).GetValue(v);
        Check(rec.Visible && rec.Mode == RecordButton.Look.Ready, "each step shows the big green record button");
        Check(Math.Abs(rec.Left + rec.Width / 2 - v.ClientSize.Width / 2) < Theme.Px(12), "in the middle of the window");
        Save(v, dir, "4-ready");
        t.GetMethod("RecordClicked", Any).Invoke(v, null);
        Check(phase() == "Countdown", "a click starts the countdown");
        t.GetMethod("Tick", Any).Invoke(v, null);
        Save(v, dir, "5-countdown");
        Check(rec.Mode == RecordButton.Look.Countdown, "it counts down inside the button");
        t.GetField("countEnd", Any).SetValue(v, DateTime.Now.AddSeconds(-0.1));
        t.GetMethod("Tick", Any).Invoke(v, null);
        Check(phase() == "Quiet" || phase() == "Talk", "when the countdown ends, recording starts (" + phase() + ")");
        Check(rec.Mode == RecordButton.Look.Recording, "the button turns red while recording");
        Save(v, dir, "6-recording");
        // Half way through the first line: the words read so far green, the rest white (the picture in the manual).
        var prompter = (Teleprompter)t.GetField("prompter", Any).GetValue(v);
        if (phase() == "Quiet") { t.GetField("phase", Any).SetValue(v, Enum.Parse(t.GetField("phase", Any).FieldType, "Talk")); }
        prompter.Visible = true; prompter.Heard = 15; prompter.Run(true); prompter.Run(false);
        // Its frames stepped by hand (no message loop: the dialog's own timer would set the words heard back to the mic's).
        var step = typeof(Teleprompter).GetMethod("Step", Any);
        var settleUntil = DateTime.Now.AddMilliseconds(700);
        while (DateTime.Now < settleUntil) { step.Invoke(prompter, null); System.Threading.Thread.Sleep(8); }
        ((Timer)t.GetField("timer", Any).GetValue(v)).Stop();
        ((Label)t.GetField("progress", Any).GetValue(v)).Text = "15 of " + prompter.WordCount + " words";
        Save(v, dir, "7-reading");
        v.Close(); v.Dispose();
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
