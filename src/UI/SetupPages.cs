using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// The first thing StreamLite shows (4.1.1): a thank-you from its developer for trying it this early, and how to report
    /// what doesn't work. Continue stays grey until "I understand" is ticked. Shown once.
    /// </summary>
    internal sealed class WelcomeDialog : DarkDialog
    {
        public const string DeveloperUrl = "https://github.com/NermalYT";

        public WelcomeDialog() : base("Welcome to StreamLite", 640, 372)
        {
            Text2("Thank you for installing StreamLite!", 28, 18, 584, 34, Theme.Section, Theme.Text);
            var from = new LinkLabel
            {
                Text = "From the lead developer, NermalYT", AutoSize = false, Location = new Point(Theme.Px(28), Theme.Px(52)), Size = new Size(Theme.Px(584), Theme.Px(22)),
                Font = Theme.Body, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Accent, ForeColor = Theme.Muted, BackColor = Color.Transparent,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            from.Links.Add(from.Text.IndexOf("NermalYT", StringComparison.Ordinal), "NermalYT".Length, DeveloperUrl);
            from.LinkClicked += (s, e) => Updater.OpenUrl(DeveloperUrl);
            Body.Controls.Add(from);
            Text2("I know that my app is still in its infancy, but I wanted to thank you for trying it out! I've always wanted an easy " +
                "experience to change any setting or EQ any audio device without installing a proprietary app like GHub or Razer Synapse. " +
                "My goal with StreamLite? I want to provide the easiest experience to achieve high-quality audio on any device with limited bloat.",
                28, 88, 584, 72, Theme.Body, Theme.Text);
            Text2("While I hope this app works in full for all users, that's dangerously optimistic. If you experience ANY bug, broken " +
                "feature, missing device, hardware incompatibility, or just want to improve the experience of StreamLite, please submit a bug " +
                "report using the in-app button (Report a bug, bottom left).",
                28, 172, 584, 72, Theme.Body, Theme.Text);
            var understand = new DarkCheck("I understand this app is in early access, and if something seems broken I will leave a bug report.")
            {
                AutoSize = false, Location = new Point(Theme.Px(28), Theme.Px(258)), Size = new Size(Theme.Px(584), Theme.Px(44)), Font = Theme.Body, ForeColor = Theme.Text
            };
            Body.Controls.Add(understand);
            var go = new FlatButton("Continue", true) { Location = new Point(Theme.Px(612) - Theme.Px(170), Theme.Px(316)), Width = Theme.Px(170) };
            go.Enabled = false;
            understand.CheckedChanged += (s, e) => go.Enabled = understand.Checked;
            go.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(go);
        }
    }

    /// <summary>
    /// The setup's last step (4.1.1): StreamLite Camera and what it can do, switched on (or not) before the full app
    /// opens - the virtual camera, AI tracking, a background, each hand sign and what the V does. Everything is on the
    /// Camera page later too.
    /// </summary>
    internal sealed class CameraSetupDialog : DarkDialog
    {
        public CameraSetupDialog(AppCore core) : base("Your camera", 680, 440)
        {
            var C = core.S.CamFx;
            bool hasCam = core.Cameras != null && core.Cameras.Devices.Count > 0;
            Text2(hasCam
                ? "StreamLite Camera is a webcam with AI built in: pick \"StreamLite Camera\" in Discord, OBS, Zoom or Teams. Switch on what you'd like - all of it is on the Camera page later too."
                : "No webcam found right now. When you plug one in, the Camera page has StreamLite Camera, AI tracking, backgrounds and hand gestures.",
                24, 14, 632, 44, Theme.Body, Theme.Muted);
            int y = 70;
            Func<string, string, bool, Action<bool>, ToggleSwitch> row = (name, help, on, set) =>
            {
                var t = new ToggleSwitch { Location = new Point(Theme.Px(24), Theme.Px(y)), Enabled = hasCam };
                t.SetSilently(on);
                t.CheckedChanged += (s, e) => { set(t.Checked); core.SaveSoon(); };
                Body.Controls.Add(t);
                Text2(name, 80, y, 260, 24, Theme.Bold, Theme.Text);
                Text2(help, 80, y + 22, 576, 22, Theme.Small, Theme.Muted);
                y += 54;
                return t;
            };
            row("StreamLite Camera", "The virtual webcam itself, with the effects below.", C.Enabled, v => C.Enabled = v);
            row("AI tracking", "Follows you like a gimbal camera: you stay in the middle, framed head to half body.", C.FaceTracking, v => C.FaceTracking = v);
            // The background.
            Text2("Background", 80, y, 160, 24, Theme.Bold, Theme.Text);
            var bg = new DarkCombo { Location = new Point(Theme.Px(250), Theme.Px(y - 3)), Width = Theme.Px(260), Enabled = hasCam };
            string[] keys = { "off", "blur", "green", "dim" };
            bg.Items.AddRange(new object[] { "Off (your room)", "Blur the room", "AI green screen", "Dim the room" });
            bg.SelectedIndex = Math.Max(0, Array.IndexOf(keys, C.Background));
            bg.SelectionChangeCommitted += (s, e) => { C.Background = keys[bg.SelectedIndex]; core.SaveSoon(); };
            Body.Controls.Add(bg);
            y += 46;
            Text2("Hand signs beside your face (held half a second) - each on its own:", 24, y, 632, 22, Theme.Body, Theme.Text);
            y += 30;
            int x0 = y;
            var palm = SmallToggle(24, y, "Open palm: AI tracking on / off", C.GesturePalm, v => C.GesturePalm = v, core, hasCam);
            var v2 = SmallToggle(352, y, "V sign: whiteboard or air drawing", C.GestureV, v => C.GestureV = v, core, hasCam);
            y += 34;
            SmallToggle(24, y, "L sign: zoom (hand up / down)", C.GestureL, v => C.GestureL = v, core, hasCam);
            SmallToggle(352, y, "Pointing finger: draw in the air", C.GesturePoint, v => C.GesturePoint = v, core, hasCam);
            y += 40;
            Text2("The V sign", 24, y + 3, 120, 24, Theme.Bold, Theme.Text);
            var vAct = new DarkCombo { Location = new Point(Theme.Px(150), Theme.Px(y)), Width = Theme.Px(300), Enabled = hasCam };
            vAct.Items.AddRange(new object[] { "Whiteboard on / off", "Air drawing on / off (write \"Hi!\" with a finger)" });
            vAct.SelectedIndex = C.VAction == "draw" ? 1 : 0;
            vAct.SelectionChangeCommitted += (s, e) => { C.VAction = vAct.SelectedIndex == 1 ? "draw" : "whiteboard"; core.SaveSoon(); };
            Body.Controls.Add(vAct);
            var done = new FlatButton("Continue", true) { Location = new Point(Theme.Px(656) - Theme.Px(170), Theme.Px(384)), Width = Theme.Px(170) };
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(done);
            AcceptButton = done;
        }

        ToggleSwitch SmallToggle(int x, int y, string text, bool on, Action<bool> set, AppCore core, bool enabled)
        {
            var t = new ToggleSwitch { Location = new Point(Theme.Px(x), Theme.Px(y)), Enabled = enabled };
            t.SetSilently(on);
            t.CheckedChanged += (s, e) => { set(t.Checked); core.SaveSoon(); };
            Body.Controls.Add(t);
            Text2(text, x + 56, y + 2, 270, 24, Theme.Body, Theme.Text);
            return t;
        }
    }
}
