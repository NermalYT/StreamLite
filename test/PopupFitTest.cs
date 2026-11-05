// Popups (4.1): every label as tall as its words need - the setup's Equalizer APO note used to cut off after
// "when" on its second line - with the buttons below the text, and popups can't be dragged. Pictures in
// test\shots\popups. Usage: run.ps1 PopupFitTest
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
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
        // 4.1.3: export / import piece by piece, Scan for settings, and the setup's first step with its two new buttons.
        core.S.Hotkeys.Clear();
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Key = 0x77, Mods = 1 });
        core.S.Profiles.Corrections["{0.0.0.00000000}.{phones}"] = new DeviceCorrection { Name = "Sennheiser MOMENTUM 5", Filters = new List<EqFilter>() };
        var parts = SetupParts.From(core.S, "Microphone (Blue Yeti)", id => id == "{0.0.0.00000000}.{phones}" ? "Headphones (Sennheiser MOMENTUM 5)" : null);
        using (var x = new SetupPartsDialog("Export settings", "Tick what goes into the file - a whole area, a device, or single settings (fold a line open with its arrow). Devices go by name, so the file works on any PC.", parts, false, null, "Export..."))
        { Shot(x, dir, "export-parts"); Check(Fits(x, out why), "Export settings: all its text shows" + (why != null ? " - " + why : "")); }
        using (var x = new SetupPartsDialog("Import settings", "From test.streamlite. Tick what to bring in.", parts, true,
            k => new List<SetupDevice> { new SetupDevice { Id = "a", Name = k == "output" ? "Headphones (MOMENTUM 5)" : "Some device" } }, "Import"))
        { Shot(x, dir, "import-parts"); Check(Fits(x, out why), "Import settings: all its text shows" + (why != null ? " - " + why : "")); }
        using (var x = new FoundSettingsDialog(new List<FoundSettings> {
            new FoundSettings { Title = "Settings kept before a setup", Detail = "StreamLite 4.1.2 - mic tuning, a headphone EQ, personal 3D, camera", When = DateTime.Now },
            new FoundSettings { Title = "StreamLite settings 2026-10-28.streamlite", Detail = "Exported from StreamLite 4.1.3 - 53 settings in Microphone, Headphones, Camera", When = DateTime.Now.AddDays(-1), IsSetupFile = true } }))
        { Shot(x, dir, "scan-found"); Check(Fits(x, out why), "Scan for settings: all its text shows" + (why != null ? " - " + why : "")); }
        // 4.4
        using (var x = new BigChoiceDialog("Your StreamLite settings", "StreamLite is being removed. What should happen to your settings: mic tuning, EQs, 3D audio, camera, hotkeys?", 60, new[] {
            new ChoiceRow("Keep all of them", "They stay in StreamLite's folder (" + AppSettings.Folder + "). Install StreamLite again and Scan for settings brings them back."),
            new ChoiceRow("Choose what to save...", "Tick the settings you want, then keep them in StreamLite's folder or export them anywhere. The rest is deleted."),
            new ChoiceRow("Delete all of them", "Nothing of yours stays on this PC.") }))
        { Shot(x, dir, "uninstall-settings"); Check(Fits(x, out why), "uninstalling, your settings: all its text shows" + (why != null ? ": " + why : "")); }
        using (var x = new BigChoiceDialog("Where should they go?", "12 settings picked.", 0, new[] {
            new ChoiceRow("In StreamLite's folder", AppSettings.Folder + " - install StreamLite again and Scan for settings finds it."),
            new ChoiceRow("Choose a place...", "A settings file you can keep anywhere, or take to another PC.") }))
        { Shot(x, dir, "uninstall-where"); Check(Fits(x, out why), "uninstalling, where they go: all its text shows" + (why != null ? ": " + why : "")); }
        using (var x = new VoicePhraseDialog(core, "Do a barrel roll", null, 100)) { Shot(x, dir, "voice-phrase"); Check(Fits(x, out why), "a voice hotkey's phrase: all its text shows" + (why != null ? ": " + why : "")); }
        core.S.AppEq["spotify"] = new AppEqSetting { Preset = "Bass boost" }; core.S.AppEq["discord"] = new AppEqSetting();
        using (var x = new AppEqDialog(core)) { Shot(x, dir, "app-eq"); Check(Fits(x, out why), "App EQ: all its text shows" + (why != null ? ": " + why : "")); }
        core.S.AppEq.Clear();
        using (var x = new HotkeyEditDialog(core, null, new HotkeyBinding { Action = "cam.anim.roll", Key = 0x77, VoicePhrase = "Do a barrel roll", VoiceTakes = new List<string> { "x" } })) { Shot(x, dir, "hotkey-edit"); Check(Fits(x, out why), "a hotkey with a phrase: all its text shows" + (why != null ? ": " + why : "")); }
        using (var x = new VoiceSetupDialog(core))
        {
            Shot(x, dir, "mic-setup-intro"); Check(Fits(x, out why), "mic setup, first page: all its text shows" + (why != null ? ": " + why : ""));
            var any = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(VoiceSetupDialog).GetMethod("ShowMenu", any).Invoke(x, new object[] { new[] { "Your S sits around 6.2 kHz and it's sharp (2 dB against your voice): the de-esser works right there, 6 dB." } });
            Application.DoEvents();
            Shot(x, dir, "mic-setup-optional"); Check(Fits(x, out why), "mic setup, optional sections: all its text shows" + (why != null ? ": " + why : ""));
            typeof(VoiceSetupDialog).GetMethod("StartOptional", any).Invoke(x, new object[] { VoiceSetup.Kind.Hums });
            Application.DoEvents();
            Shot(x, dir, "mic-setup-hums"); Check(Fits(x, out why), "mic setup, hums: all its text shows" + (why != null ? ": " + why : ""));
        }
        using (var x = new DevicePickDialog(core)) { Shot(x, dir, "device-pick"); Check(Fits(x, out why), "the setup's first step: all its text shows" + (why != null ? " - " + why : "")); }
        Console.WriteLine("  pictures in " + Path.GetFullPath(dir));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
