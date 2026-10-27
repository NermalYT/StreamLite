// Pictures of 4.0's retune (test\shots\retune): the question, picking the mic and headphones, Tune to my voice with the
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
            var screen = new Rectangle(-30000, -30000, 1920, 1080);
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
            main.EndSetupScreen();
            Check(!main.InSetupScreen && main.Width < screen.Width && Math.Abs(main.Left + main.Width / 2 - (screen.Left + screen.Width / 2)) <= 1 && Math.Abs(main.Top + main.Height / 2 - (screen.Top + screen.Height / 2)) <= 1,
                "afterwards: a normal window in the middle of the primary monitor (" + main.Bounds + ")");
            main.Hide(); main.Dispose();
            MainForm.SetupScreenForTests = null;
        }

        using (var q = new ConfirmDialog("StreamLite 4.0", RetuneWizard.Question + "\n\nStreamLite 4.0 tunes your mic from its raw sound, your headphones' EQ and 3D audio to your ears. Yes starts every tuning setting fresh and walks you through it (about 6 minutes). Your devices, profiles and hotkeys stay.", "Yes", "No", 520, 250) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
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
        v.Close(); v.Dispose();
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
