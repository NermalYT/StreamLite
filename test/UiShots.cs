using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

// Renders every page of the main window off-screen at several window widths and saves PNGs, using a COPY of the
// real settings (STREAMLITE_DATA) and without starting audio, so the running app and the system are untouched.
// Usage: UiShots <outDir> [width ...]          (also: "tuning" anywhere in the args opens the mic Tuning section,
//                                               "fresh" starts from default settings, "lowvram" turns Low VRAM on)
class UiShots
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "shots";
        var widths = new List<int>();
        bool tuning = false;
        bool fresh = false;
        for (int i = 1; i < args.Length; i++) { int w; if (int.TryParse(args[i], out w)) widths.Add(w); else if (args[i] == "tuning") tuning = true; else if (args[i] == "fresh") fresh = true; }
        if (widths.Count == 0) widths.AddRange(new[] { 830, 1100, 1600 });
        Directory.CreateDirectory(outDir);

        string data = Path.Combine(Path.GetTempPath(), "StreamLite-uitest");
        Directory.CreateDirectory(data);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json");
        if (fresh) { if (File.Exists(Path.Combine(data, "settings.json"))) File.Delete(Path.Combine(data, "settings.json")); }   // first run on a new PC
        else if (File.Exists(real)) File.Copy(real, Path.Combine(data, "settings.json"), true);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var core = new AppCore();
        // No routing / EQ-file writes / saves from this copy.
        foreach (var f in typeof(AppCore).GetFields(Any))
            if (f.FieldType == typeof(Timer)) { var t = (Timer)f.GetValue(core); t.Stop(); t.Enabled = false; }
        core.S.WindowBounds = null;
        if (Array.IndexOf(args, "lowvram") >= 0) core.S.LowVram = true;
        core.S.ExtraMics.Clear();
        if (Array.IndexOf(args, "mics") >= 0)   // two people's mics added (not plugged in here)
        {
            core.S.ExtraMics.Add(new ExtraMicSetting { Id = "{test-mic-2}", Name = "Microphone (HyperX QuadCast S)", GainDb = 3 });
            core.S.ExtraMics.Add(new ExtraMicSetting { Id = "{test-mic-3}", Name = "Headset Microphone (Arctis Nova 7)" });
        }
        core.S.ExtraOutputs.Clear();
        if (Array.IndexOf(args, "outs") >= 0)   // a second pair of headphones added (not plugged in here)
            core.S.ExtraOutputs.Add(new ExtraOutputSetting { Id = "{test-out-2}", Name = "Headphones (AirPods Pro)", GainDb = -2 });
        core.S.Hotkeys.Clear();
        if (Array.IndexOf(args, "hotkeys") >= 0)
        {
            core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "2", Key = 0x4D, Mods = 1 });
            core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.ptt", Mouse = 4 });
            core.S.Hotkeys.Add(new HotkeyBinding { Action = "cam.bg", Arg = "next", Key = 0x77 });
            core.S.Hotkeys.Add(new HotkeyBinding { Action = "out.profile", Arg = "auto", Key = 0x50, Mods = 5 });
        }
        core.ActiveMicId = core.S.MicId;          // what RestartAudio would pick (audio isn't started here)
        core.ActiveOutId = core.S.VirtualOutId;
        core.Profiles.ActiveName = args.Length > 0 && Array.IndexOf(args, "cs2") >= 0 ? "Counter-Strike 2" : GameProfiles.Movies;   // as if the profile watcher ran
        core.Profiles.Why = Array.IndexOf(args, "cs2") >= 0 ? "cs2 is running. Also in use: Spotify (Music), Discord (Chat) - lower in your priority order"
            : "none of the apps in your profiles is in use, so your fallback profile plays";

        var form = new MainForm(core);
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        Pump();
        var pages = (List<Panel>)typeof(MainForm).GetField("pages", Any).GetValue(form);
        var show = typeof(MainForm).GetMethod("ShowPage", Any);
        if (tuning)
        {
            typeof(MainForm).GetMethod("SetMicTuning", Any).Invoke(form, new object[] { true });
            typeof(MainForm).GetMethod("SetOutputEq", Any).Invoke(form, new object[] { true });
        }
        if (Array.IndexOf(args, "update") >= 0)   // the in-app update prompt over the page
            form.ShowUpdatePrompt("StreamLite 9.9.9 is available (you have " + Program.Version + ").\nUpdate now? StreamLite restarts by itself; your mic and game sound drop out for a few seconds.", () => { }, () => { });
        string[] names = { "mic", "headphones", "3d", "camera", "hotkeys", "health", "settings" };
        foreach (int w in widths)
        {
            form.Size = new Size(w, 820);
            Pump();
            for (int p = 0; p < pages.Count; p++)
            {
                show.Invoke(form, new object[] { p });
                Pump();
                Save(form, Path.Combine(outDir, names[p] + "-" + w + "-window.png"));
                Save(pages[p], Path.Combine(outDir, names[p] + "-" + w + "-page.png"));
            }
            // DrawToBitmap paints overlapping controls in the wrong order: the update prompt on its own.
            var banner = typeof(MainForm).GetField("updateBanner", Any).GetValue(form) as Control;
            if (banner != null && banner.Visible) Save(banner, Path.Combine(outDir, "update-prompt-" + w + ".png"));
        }
        if (tuning)
        {
            // Every tab of the mic Tuning section.
            var tun = typeof(MainForm).GetField("tuning", Any).GetValue(form);
            var showTab = tun.GetType().GetMethod("ShowTab", Any);
            show.Invoke(form, new object[] { 0 });
            for (int t = 0; t < 5; t++)
            {
                showTab.Invoke(tun, new object[] { t });
                Pump();
                Save((Control)tun, Path.Combine(outDir, "tuning-tab" + t + ".png"));
            }
            // Dialogs.
            var message = (Form)Activator.CreateInstance(typeof(DarkMessage), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                new object[] { "Remove a specific sound", "Nothing to learn: the recording was quiet or only your voice. Let the sound play, stay quiet, then press Record.", MessageBoxButtons.YesNo, 150, true }, null);
            foreach (var d in new Form[] { new DirectAudioDialog("Headphones (QCC Dongle Pro)", "Speakers (Realtek USB Audio)"), new CloseDialog(), new ConfirmDialog("Stop StreamLite?", "Your cleaned-up microphone turns off. Apps set to \"StreamLite Microphone\" (like Discord) will hear silence until StreamLite runs again. Headphone EQ keeps working.", "Stop service & quit", "Cancel"), message })
            {
                d.StartPosition = FormStartPosition.Manual; d.Location = new Point(-30000, -30000); d.ShowInTaskbar = false;
                d.Show(); Pump();
                Save(d, Path.Combine(outDir, "dialog-" + d.GetType().Name + ".png"));
                d.Close();
            }
        }
        Console.WriteLine("Saved to " + Path.GetFullPath(outDir));
        Environment.Exit(0);
    }

    static void Pump() { for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); } }

    static void Save(Control c, string path)
    {
        using (var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
        {
            c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
