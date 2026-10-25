// Debug: the Hotkeys page's combo boxes after building (selected index / text). Usage: HotkeysPageProbe
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class HotkeysPageProbe
{
    [STAThread]
    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_hkprobe");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var core = new AppCore(true);
        core.S.Hotkeys.Clear();
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "all", Key = 0x4D, Mods = 1 });
        core.S.Hotkeys.Add(new HotkeyBinding { Action = "cam.bg", Arg = "next", Key = 0x77 });
        var f = new Form { Width = 900, Height = 600 };
        var p = new HotkeysPage(core, () => null);
        f.Controls.Add(p);
        f.Show();
        p.Reload();
        Application.DoEvents();
        foreach (Control c in p.Controls)
            foreach (Control x in c.Controls)
                if (x is ComboBox) Console.WriteLine(x.Location + " visible " + x.Visible + " index " + ((ComboBox)x).SelectedIndex + " items " + ((ComboBox)x).Items.Count + " text '" + x.Text + "'");
        f.Close();
    }
}
