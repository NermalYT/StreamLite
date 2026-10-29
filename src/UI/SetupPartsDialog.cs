using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Export or import settings piece by piece (4.1.3): every area (Microphone, Headphones, 3D audio, Camera...) folds open
    /// to its settings, and every device is a group of its own - tick a whole device, one of its settings, or one game's
    /// headphone EQ. Importing, each device in the file goes to a device here (matched by name; pick another or leave it
    /// out). Never in a file: device ids and file paths from this PC, and anything learned from a recording.
    /// </summary>
    internal sealed class SetupPartsDialog : DarkDialog
    {
        readonly CheckTree tree;
        readonly bool importing;
        readonly Dictionary<CheckNode, List<SetupDevice>> deviceChoices = new Dictionary<CheckNode, List<SetupDevice>>();
        readonly Label count;
        /// <summary>The pieces ticked.</summary>
        public List<SetupPart> Chosen { get; private set; }
        /// <summary>Importing: where each ticked device piece goes here (device id).</summary>
        public Dictionary<SetupPart, string> Targets { get; private set; }

        static readonly Dictionary<string, string> KindNames = new Dictionary<string, string> { { "mic", "microphone" }, { "output", "output" }, { "camera", "camera" } };

        /// <summary>devicesHere(kind) = this PC's devices of a kind (mic, output, camera) - only needed for importing.</summary>
        public SetupPartsDialog(string title, string intro, List<SetupPart> parts, bool import, Func<string, List<SetupDevice>> devicesHere, string okText)
            : base(title, 720, 640)
        {
            importing = import;
            Text2(intro, 20, 12, 680, 40, Theme.Small, Theme.Muted);
            tree = new CheckTree { Location = new Point(Theme.Px(20), Theme.Px(58)), Size = new Size(Theme.Px(680), Theme.Px(470)) };
            Body.Controls.Add(tree);
            Text2("Never in a settings file: device ids and file paths from this PC (devices go by name), your voiceprint and any recorded sound - only you have those.", 20, 534, 680, 34, Theme.Small, Theme.Muted);

            foreach (var area in SetupParts.Areas)
            {
                var inArea = parts.Where(p => p.Area == area).ToList();
                if (inArea.Count == 0) continue;
                var a = new CheckNode(area, Summary(inArea));
                tree.Roots.Add(a);
                // Settings not tied to a device, by section.
                foreach (var sec in inArea.Where(p => p.Device == null).GroupBy(p => p.Section ?? ""))
                {
                    var parent = sec.Key == "" ? a : a.Add(sec.Key);
                    foreach (var p in sec) parent.Add(Leaf(p));
                }
                // Each device: its own group, with where it goes when importing.
                foreach (var dev in inArea.Where(p => p.Device != null).GroupBy(p => p.DeviceKind + "|" + p.Device))
                {
                    var first = dev.First();
                    string kind; KindNames.TryGetValue(first.DeviceKind ?? "", out kind);
                    var d = a.Add(first.Device, kind);
                    foreach (var p in dev) d.Add(Leaf(p));
                    if (import && devicesHere != null)
                    {
                        var here = devicesHere(first.DeviceKind) ?? new List<SetupDevice>();
                        string match = SetupExport.Match(first.Device, here, null);
                        var choices = new List<SetupDevice>(here);
                        choices.Add(new SetupDevice { Id = null, Name = "Leave it out" });
                        d.Choices = choices.Select(c => c.Name).ToList(); d.LastChoiceIsOff = true;
                        d.Choice = match != null ? here.FindIndex(c => c.Id == match) : choices.Count - 1;
                        deviceChoices[d] = choices;
                        if (match == null) d.Checked = false;   // nothing here to put it on yet
                    }
                }
            }
            if (tree.Roots.Count > 0) tree.Roots[0].Expanded = true;
            tree.Rebuild();

            count = Text2("", 20, 590, 300, 22, Theme.Small, Theme.Muted);
            var all = new FlatButton("All") { Location = new Point(Theme.Px(330), Theme.Px(584)), Width = Theme.Px(70) };
            var none = new FlatButton("None") { Location = new Point(Theme.Px(406), Theme.Px(584)), Width = Theme.Px(70) };
            all.Click += (s, e) => { foreach (var r in tree.Roots) r.Checked = true; tree.Rebuild(); Count(); };
            none.Click += (s, e) => { foreach (var r in tree.Roots) r.Checked = false; tree.Rebuild(); Count(); };
            var ok = new FlatButton(okText, true) { Width = Theme.Px(120) };
            var cancel = new FlatButton("Cancel") { Width = Theme.Px(90) };
            cancel.Location = new Point(Theme.Px(700) - cancel.Width, Theme.Px(584));
            ok.Location = new Point(cancel.Left - Theme.Px(8) - ok.Width, Theme.Px(584));
            Body.Controls.Add(all); Body.Controls.Add(none); Body.Controls.Add(ok); Body.Controls.Add(cancel);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            CancelButton = cancel;
            ok.Click += (s, e) => Done();
            tree.Changed += (s, e) => Count();
            Count();
        }

        static CheckNode Leaf(SetupPart p) { return new CheckNode(p.Label, null, p); }

        static string Summary(List<SetupPart> l)
        {
            int devices = l.Where(p => p.Device != null).Select(p => p.Device).Distinct().Count();
            return devices > 0 ? devices + " device" + (devices == 1 ? "" : "s") : null;
        }

        IEnumerable<CheckNode> Leaves() { return tree.Roots.SelectMany(r => r.All()).Where(n => n.Tag is SetupPart); }

        void Count()
        {
            int on = Leaves().Count(n => n.Checked), total = Leaves().Count();
            count.Text = on + " of " + total + " settings ticked";
        }

        void Done()
        {
            var chosen = new List<SetupPart>(); var targets = new Dictionary<SetupPart, string>();
            foreach (var n in Leaves().Where(n => n.Checked))
            {
                var p = (SetupPart)n.Tag;
                if (importing && p.Device != null)
                {
                    List<SetupDevice> choices;
                    var dn = n.Parent;
                    if (dn == null || !deviceChoices.TryGetValue(dn, out choices)) continue;
                    var c = choices[Math.Max(0, Math.Min(choices.Count - 1, dn.Choice))];
                    if (c.Id == null) continue;   // left out
                    targets[p] = c.Id;
                }
                chosen.Add(p);
            }
            if (chosen.Count == 0) { DarkMessage.Show(this, "Tick at least one setting first.", Text); return; }
            Chosen = chosen; Targets = targets;
            DialogResult = DialogResult.OK; Close();
        }
    }
}
