// The Hotkeys page and its "Add a hotkey" popup (4.1), drawn off screen: test\shots\hotkeys. Usage: run.ps1 HotkeysShot
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class HotkeysShot
{
    [STAThread]
    static void Main()
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-hotkeyshot");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        Application.EnableVisualStyles();
        var core = new AppCore(true);
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.ptt", Key = (int)Keys.CapsLock });
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "all", Key = (int)Keys.M, Mods = 3 });
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "out.16d", Arg = "next", Key = (int)Keys.F9 });
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "cam.bg", Arg = "next", Mouse = 4 });
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\shots\hotkeys"); Directory.CreateDirectory(dir);
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ClientSize = new Size(Theme.Px(760), Theme.Px(560)), BackColor = Theme.Bg, ShowInTaskbar = false };
        var page = new HotkeysPage(core, () => null) { Location = new Point(Theme.Px(20), Theme.Px(20)) };
        form.Controls.Add(page); form.Show(); Application.DoEvents();
        using (var b = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(dir, "page.png")); }
        foreach (var existing in new[] { null, core.S.Hotkeys[2] })
            using (var d = new HotkeyEditDialog(core, null, existing) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            {
                d.Show(); Application.DoEvents();
                using (var b = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(dir, existing == null ? "add.png" : "change.png")); }
                d.Close();
            }
        Console.WriteLine("ALL PASS - pictures in " + Path.GetFullPath(dir) + " (" + HotkeyActions.All.Count + " actions in " + HotkeyActions.All.Select(a => a.Group).Distinct().Count() + " groups)");
        Environment.Exit(0);
    }
}
