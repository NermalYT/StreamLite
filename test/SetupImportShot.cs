// Renders the Import setup preview off-screen (a setup with 2 added mics, an output and headphones; one mic not here).
// Usage: SetupImportShot <out.png>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using StreamLite;
using StreamLite.UI;

class SetupImportShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "sl_importshot");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        var s = AppSettings.Load();
        GameProfiles.Merge(s.Profiles, null);
        s.ExtraMics.Add(new ExtraMicSetting { Id = "m2", Name = "Microphone (Yeti Nano)", GainDb = 3 });
        s.ExtraMics.Add(new ExtraMicSetting { Id = "m3", Name = "Headset Microphone (Arctis 7)" });
        s.ExtraOutputs.Add(new ExtraOutputSetting { Id = "o2", Name = "Headphones (AirPods Pro)" });
        s.Profiles.Corrections["p1"] = new DeviceCorrection { Name = "Sennheiser HD 600", Filters = new List<StreamLite.Audio.EqFilter>() };
        s.Game.PersonalHeads["p1"] = "kemar|35";
        s.Hotkeys.Add(new HotkeyBinding { Action = "mic.ptt", Key = 0x56 });
        var f = SetupExport.Export(s, "Microphone (Shure MV7)", id => id == "p1" ? "Headphones (QCC Dongle Pro)" : id == "o2" ? "Headphones (AirPods Pro)" : null, "3.0.0");
        var inputs = new List<SetupDevice> { new SetupDevice { Id = "x1", Name = "Yeti Nano" }, new SetupDevice { Id = "x2", Name = "Microphone (Webcam C920)" } };
        var outputs = new List<SetupDevice> { new SetupDevice { Id = "y1", Name = "Headphones (QCC Dongle Pro)" }, new SetupDevice { Id = "y2", Name = "Headphones (AirPods Pro)" }, new SetupDevice { Id = "y3", Name = "Speakers (Realtek(R) Audio)" } };
        var plan = SetupExport.AutoPlan(f, inputs, outputs, "main", "y1");
        var d = new SetupImportDialog(f, plan, inputs, outputs) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        d.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height)); bmp.Save(args[0]); }
        d.Close();
        Console.WriteLine("saved " + args[0]);
    }
}
