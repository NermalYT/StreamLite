using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StreamLite.Audio;

namespace StreamLite.UI
{
    /// <summary>
    /// The EQ of one microphone or one output (Microphones / Outputs lists): edited live, applied by StreamLite itself
    /// on that device's own stream, so it works for every added device without any setup.
    /// </summary>
    internal sealed class DeviceEqDialog : DarkDialog
    {
        public DeviceEqDialog(string title, string note, EqProfile profile, Dictionary<string, double[]> presets, Action changed) : base(title, 690, 600)
        {
            var eq = new EqEditor(presets, true, false, false) { Location = new Point(Theme.Px(5), Theme.Px(8)) };
            eq.SetProfile(profile);
            eq.Changed += (s, e) => changed();
            eq.Committed += (s, e) => changed();
            Body.Controls.Add(eq);
            Text2(note, 12, 532, 520, 40, Theme.Small, Theme.Muted);
            var done = new FlatButton("Done", true);
            done.Location = new Point(Theme.Px(678) - done.Width, Theme.Px(540));
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(done);
            CancelButton = done;
        }
    }
}
