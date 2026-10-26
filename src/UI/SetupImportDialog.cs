using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Import a setup: what the file holds, part by part, each with a tick, and where each of its devices goes on this
    /// PC (matched by name; pick another one or leave it out). Nothing changes until Import.
    /// </summary>
    internal sealed class SetupImportDialog : DarkDialog
    {
        public ImportPlan Plan { get; private set; }

        static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            { "mic", "Mic chain" }, { "mics", "Microphones" }, { "outputs", "Outputs" }, { "headphones", "Headphones" },
            { "3d", "3D sound" }, { "profiles", "Game profiles and app rules" }, { "hotkeys", "Hotkeys" }, { "camera", "Camera look" }
        };

        const int Row = 30, Part = 50;

        static int HeightFor(SetupFile f)
        {
            int devices = (f.Mics != null ? Math.Max(0, f.Mics.Count - 1) : 0) + (f.Outputs != null ? f.Outputs.Count : 0) + (f.Headphones != null ? f.Headphones.Count : 0);
            return Math.Min(680, 140 + SetupExport.Describe(f).Count * Part + devices * Row);
        }

        public SetupImportDialog(SetupFile f, ImportPlan auto, IList<SetupDevice> inputs, IList<SetupDevice> outputs)
            : base("Import setup", 660, HeightFor(f))
        {
            int h = HeightFor(f);
            Text2("From StreamLite " + (f.AppVersion ?? "?") + (f.Created != null ? ", saved " + f.Created : "") + ". Tick what to bring in. Devices are matched by name - pick another one where needed. " +
                "Your current settings are kept first: Settings > Undo import puts them back.", 20, 12, 620, 40, Theme.Small, Theme.Muted);
            ScrollHost scrollHost;
            var scroll = ScrollLists.Make(Body, new Rectangle(0, Theme.Px(58), Theme.Px(660), Theme.Px(h - 58 - 56)), Theme.Bg, out scrollHost);
            var checks = new Dictionary<string, ToggleSwitch>();
            var micCombos = new Dictionary<int, DarkCombo>(); var outCombos = new Dictionary<int, DarkCombo>(); var phoneCombos = new Dictionary<int, DarkCombo>();
            int y = 0;
            foreach (var part in SetupExport.Describe(f))
            {
                var cb = new ToggleSwitch { Checked = true, Location = new Point(Theme.Px(20), Theme.Px(y + 6)) };
                scroll.Controls.Add(cb); checks[part.Key] = cb;
                scroll.Controls.Add(new Label { Text = Titles[part.Key], Font = Theme.Bold, ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = true, UseMnemonic = false,
                    Location = new Point(Theme.Px(74), Theme.Px(y + 4)) });
                scroll.Controls.Add(new Label { Text = part.Value, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false,
                    Location = new Point(Theme.Px(74), Theme.Px(y + 25)), Size = new Size(Theme.Px(560), Theme.Px(18)) });
                y += Part;
                if (part.Key == "mics")
                    for (int i = 1; i < f.Mics.Count; i++) { micCombos[i] = DeviceRow(scroll, ref y, "Microphone " + (i + 1) + ": " + f.Mics[i].Name, inputs, auto.MicMap.ContainsKey(i) ? auto.MicMap[i] : null, cb); }
                if (part.Key == "outputs")
                    for (int i = 0; i < f.Outputs.Count; i++) { outCombos[i] = DeviceRow(scroll, ref y, "Output " + (i + 2) + ": " + f.Outputs[i].Name, outputs, auto.OutputMap.ContainsKey(i) ? auto.OutputMap[i] : null, cb); }
                if (part.Key == "headphones")
                    for (int i = 0; i < f.Headphones.Count; i++) { phoneCombos[i] = DeviceRow(scroll, ref y, f.Headphones[i].Name, outputs, auto.HeadphoneMap.ContainsKey(i) ? auto.HeadphoneMap[i] : null, cb); }
            }
            ScrollLists.Fit(scrollHost);
            var import = new FlatButton("Import", true);
            var cancel = new FlatButton("Cancel");
            Body.Controls.Add(import); Body.Controls.Add(cancel);
            cancel.Location = new Point(Theme.Px(640) - cancel.Width, Theme.Px(h - 44));
            import.Location = new Point(cancel.Left - Theme.Px(10) - import.Width, Theme.Px(h - 44));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            CancelButton = cancel;
            Func<string, bool> on = k => checks.ContainsKey(k) && checks[k].Checked;
            import.Click += (s, e) =>
            {
                var p = new ImportPlan
                {
                    MicChain = on("mic"), Mics = on("mics"), Outputs = on("outputs"), Headphones = on("headphones"),
                    Game3D = on("3d"), Profiles = on("profiles"), Hotkeys = on("hotkeys"), Camera = on("camera")
                };
                foreach (var k in micCombos) p.MicMap[k.Key] = Picked(k.Value);
                foreach (var k in outCombos) p.OutputMap[k.Key] = Picked(k.Value);
                foreach (var k in phoneCombos) p.HeadphoneMap[k.Key] = Picked(k.Value);
                Plan = p;
                DialogResult = DialogResult.OK; Close();
            };
        }

        static readonly SetupDevice LeaveOut = new SetupDevice { Id = null, Name = "Leave it out" };

        DarkCombo DeviceRow(Panel scroll, ref int y, string what, IList<SetupDevice> devices, string picked, ToggleSwitch part)
        {
            var l = new Label { Text = what, Font = Theme.Small, ForeColor = Theme.Text, BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false,
                Location = new Point(Theme.Px(74), Theme.Px(y + 4)), Size = new Size(Theme.Px(250), Theme.Px(18)) };
            var c = new DarkCombo { Location = new Point(Theme.Px(330), Theme.Px(y)), Width = Theme.Px(300) };
            c.Items.Add(LeaveOut);
            foreach (var d in devices) c.Items.Add(d);
            var hit = devices.FirstOrDefault(d => d.Id == picked);
            c.SelectedItem = hit != null ? (object)hit : LeaveOut;
            scroll.Controls.Add(l); scroll.Controls.Add(c);
            part.CheckedChanged += (s, e) => c.Enabled = part.Checked;
            y += Row;
            return c;
        }

        static string Picked(DarkCombo c) { var d = c.SelectedItem as SetupDevice; return d != null ? d.Id : null; }
    }
}
