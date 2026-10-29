using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Hotkeys page (4.1): each hotkey a row - what it does, which mic / output / profile / background it's for, and its
    /// key as a chip; click a row to change it. "Add hotkey" opens a popup: pick a part of StreamLite, then what to do,
    /// then press the key. They work while games and other apps have the focus.
    /// </summary>
    internal sealed class HotkeysPage : Panel
    {
        readonly AppCore core;
        readonly Func<HotkeyManager> manager;
        readonly Card card;
        readonly ToggleSwitch toasts;
        readonly FlatButton add;
        readonly Label hint;
        readonly List<Control> rows = new List<Control>();
        bool loading;

        public HotkeysPage(AppCore core, Func<HotkeyManager> manager)
        {
            this.core = core; this.manager = manager;
            Location = new Point(Theme.Px(28), Theme.Px(18));
            Size = new Size(Theme.Px(690), Theme.Px(400));
            BackColor = Theme.Bg;
            Theme.Label(this, "Hotkeys", 0, 0, Theme.Title);
            card = new Card { Location = new Point(0, Theme.Px(54)), Size = new Size(Theme.Px(690), Theme.Px(200)) };
            Controls.Add(card);
            Theme.Help(Theme.Label(card, "Your hotkeys", 20, 16, Theme.Section),
                "Bind a key, a key combination (Ctrl, Alt, Shift, Win + a key), a single key like Left Ctrl or Caps Lock, or the middle / back / forward mouse button to almost anything in StreamLite.\nHotkeys work while games, Discord or any other app is in front. They never take the key away from the app.\nPush to talk keeps your mic silent unless you hold its key. Mute and output mutes start unmuted every time StreamLite starts.");
            toasts = new ToggleSwitch();
            toasts.Location = new Point(Theme.Px(400), Theme.Px(14));
            card.Controls.Add(toasts);
            var tl = Theme.Label(card, "Note on screen", 454, 15, Theme.Bold);
            Theme.Help(tl, "Shows a small note near the bottom of the screen when a hotkey does something (\"Microphone 2 muted\"). It never takes the focus. Exclusive-fullscreen games draw over it.");
            Responsive.Fixed(toasts); Responsive.Fixed(tl);
            toasts.CheckedChanged += (s, e) => { if (loading) return; core.S.HotkeyToasts = toasts.Checked; core.SaveSoon(); };
            hint = Theme.Label(card, "", 20, 48, Theme.Small, Theme.Muted, 650);
            hint.Height = Theme.Px(34);
            add = new FlatButton("+  Add hotkey", true) { Width = Theme.Px(160) };
            card.Controls.Add(add);
            add.Click += (s, e) => Edit(null);
            Build();
        }

        public void Reload()
        {
            loading = true;
            toasts.SetSilently(core.S.HotkeyToasts);
            loading = false;
            Build();
        }

        /// <summary>The popup for a new hotkey (bind = null) or one of yours.</summary>
        void Edit(HotkeyBinding bind)
        {
            var m = manager();
            using (var d = new HotkeyEditDialog(core, m, bind))
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) { Build(); return; }
                if (bind == null) core.S.Hotkeys.Add(d.Result);
                else { bind.Action = d.Result.Action; bind.Arg = d.Result.Arg; bind.Key = d.Result.Key; bind.Mods = d.Result.Mods; bind.Mouse = d.Result.Mouse; }
            }
            Save();
            Build();
        }

        void Build()
        {
            foreach (var c in rows) { card.Controls.Remove(c); c.Dispose(); }
            rows.Clear();
            var list = core.S.Hotkeys;
            hint.Text = list.Count == 0
                ? "No hotkeys yet. Add one - push to talk, mute microphone 2, next camera background, flip the camera upside down..."
                : "Click a hotkey to change it.";
            int y = 88;
            foreach (var b in list.ToList())
            {
                var bind = b;
                var a = HotkeyActions.Find(bind.Action);
                // The same key twice: both run - say so.
                var twin = list.FirstOrDefault(o => o != bind && (o.Key != 0 || o.Mouse != 0) && o.Key == bind.Key && o.Mouse == bind.Mouse && o.Mods == bind.Mods);
                var row = new HotkeyRow(a, ArgLabel(a, bind), HotkeyManager.Describe(bind), twin != null) { Location = new Point(Theme.Px(14), Theme.Px(y)), Size = new Size(card.Width - Theme.Px(28), Theme.Px(50)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                row.Clicked += () => Edit(bind);
                row.Removed += () => { core.S.Hotkeys.Remove(bind); Save(); Build(); };
                if (twin != null) Theme.Help(row, "Hotkey " + (list.IndexOf(twin) + 1) + " uses the same key - both run.");
                else if (a != null && a.Help != null) Theme.Help(row, a.Help);
                card.Controls.Add(row); rows.Add(row);
                y += 56;
            }
            add.Location = new Point(Theme.Px(20), Theme.Px(y + 6));
            card.Height = Theme.Px(y + 56);
            Height = card.Bottom + Theme.Px(20);
        }

        string ArgLabel(HotkeyAction a, HotkeyBinding bind)
        {
            if (a == null || a.Args == null) return null;
            try { var c = a.Args(core).FirstOrDefault(x => x.Key == bind.Arg); return c.Value; } catch { return null; }
        }

        void Save()
        {
            core.SaveSoon();
            var m = manager(); if (m != null) m.Reload();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { var m = manager(); if (m != null) m.CancelCapture(); }
        }
    }

    /// <summary>One hotkey on the Hotkeys page: its part of StreamLite, what it does (and for what), its key as a chip, and an X.</summary>
    internal sealed class HotkeyRow : Control
    {
        readonly HotkeyAction action;
        readonly string arg, key;
        readonly bool clash;
        bool hover, hoverX;
        public event Action Clicked, Removed;

        public HotkeyRow(HotkeyAction a, string arg, string key, bool clash)
        {
            action = a; this.arg = arg; this.key = key; this.clash = clash;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        Rectangle XRect { get { return new Rectangle(Width - Theme.Px(40), (Height - Theme.Px(28)) / 2, Theme.Px(28), Theme.Px(28)); } }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); bool x = XRect.Contains(e.Location); if (x != hoverX || !hover) { hoverX = x; hover = true; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = hoverX = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (XRect.Contains(e.Location)) { if (Removed != null) Removed(); }
            else if (Clicked != null) Clicked();
        }

        /// <summary>A part of StreamLite's colour and icon (Segoe MDL2).</summary>
        internal static void GroupLook(string group, out string glyph, out Color color)
        {
            switch (group)
            {
                case "Microphone": glyph = ""; color = Color.FromArgb(91, 210, 122); break;
                case "Headphones & 3D": glyph = ""; color = Color.FromArgb(110, 165, 240); break;
                case "Camera": glyph = ""; color = Color.FromArgb(235, 170, 90); break;
                default: glyph = ""; color = Color.FromArgb(180, 150, 230); break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, Theme.Px(10)))
            using (var b = new SolidBrush(hover && !hoverX ? Theme.FieldHover : Theme.Field)) g.FillPath(b, path);
            string glyph; Color col; GroupLook(action != null ? action.Group : "", out glyph, out col);
            Icons.Draw(g, glyph, 13f, new Rectangle(Theme.Px(10), 0, Theme.Px(34), Height), col);
            // The key: a chip on the right.
            string k = string.IsNullOrEmpty(key) ? "No key yet" : key;
            var ks = TextRenderer.MeasureText(k, Theme.Bold);
            var chip = new Rectangle(XRect.Left - Theme.Px(12) - ks.Width - Theme.Px(24), (Height - Theme.Px(30)) / 2, ks.Width + Theme.Px(24), Theme.Px(30));
            using (var path = Theme.Rounded(chip, Theme.Px(8)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(clash ? Theme.Warn : Theme.Track)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, k, Theme.Bold, chip, clash ? Theme.Warn : string.IsNullOrEmpty(key) ? Theme.Muted : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            int tx = Theme.Px(52), tw = chip.Left - tx - Theme.Px(10);
            string what = action != null ? action.Label : "(an action this version doesn't have)";
            if (arg == null)
                TextRenderer.DrawText(g, what, Theme.Bold, new Rectangle(tx, 0, tw, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            else
            {
                TextRenderer.DrawText(g, what, Theme.Bold, new Rectangle(tx, Height / 2 - Theme.Px(19), tw, Theme.Px(20)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, arg, Theme.Small, new Rectangle(tx, Height / 2 + Theme.Px(1), tw, Theme.Px(18)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            // The X.
            var xr = XRect;
            if (hoverX) using (var b = new SolidBrush(Color.FromArgb(70, Theme.Danger))) g.FillEllipse(b, xr);
            Icons.Draw(g, "", 8f, xr, hoverX ? Theme.Text : Theme.Muted);
        }
    }

    /// <summary>
    /// "Add a hotkey" / change one (4.1): the parts of StreamLite on the left, what each can do on the right (a short list
    /// with what it does under each), what it's for (a mic, an output, a background...) when that applies, and the key -
    /// click the key box and press it.
    /// </summary>
    internal sealed class HotkeyEditDialog : DarkDialog
    {
        readonly AppCore core;
        readonly HotkeyManager manager;
        readonly List<FlatButton> groups = new List<FlatButton>();
        readonly Panel list;
        readonly ScrollHost listHost;
        readonly DarkCombo argCombo;
        readonly Label argLabel, keyLabel;
        readonly FlatButton keyBox, ok;
        HotkeyAction picked;
        public readonly HotkeyBinding Result;

        sealed class ArgItem { public string Value, Label; public override string ToString() { return Label; } }

        public HotkeyEditDialog(AppCore core, HotkeyManager manager, HotkeyBinding existing) : base(existing == null ? "Add a hotkey" : "Change a hotkey", 760, 520)
        {
            this.core = core; this.manager = manager;
            Result = existing == null ? new HotkeyBinding() : new HotkeyBinding { Action = existing.Action, Arg = existing.Arg, Key = existing.Key, Mods = existing.Mods, Mouse = existing.Mouse };
            Text2("What should it do?", 22, 14, 400, 24, Theme.Section, Theme.Text);
            var names = HotkeyActions.All.Select(a => a.Group).Distinct().ToList();
            for (int i = 0; i < names.Count; i++)
            {
                string gname = names[i];
                var b = new FlatButton(gname.Replace("&", "&&")) { Location = new Point(Theme.Px(22), Theme.Px(52 + i * 46)), Width = Theme.Px(170), Tag = gname };
                b.Click += (s, e) => ShowGroup(gname);
                Body.Controls.Add(b); groups.Add(b);
            }
            list = ScrollLists.Make(Body, new Rectangle(Theme.Px(206), Theme.Px(52), Theme.Px(532), Theme.Px(300)), Theme.Card, out listHost);
            argLabel = Text2("For", 206, 368, 60, 24, Theme.Bold, Theme.Text);
            argCombo = new DarkCombo { Location = new Point(Theme.Px(266), Theme.Px(364)), Width = Theme.Px(472) };
            argCombo.SelectionChangeCommitted += (s, e) => { var it = argCombo.SelectedItem as ArgItem; if (it != null) Result.Arg = it.Value; };
            Body.Controls.Add(argCombo);
            keyLabel = Text2("Key", 206, 412, 60, 24, Theme.Bold, Theme.Text);
            keyBox = new FlatButton("") { Location = new Point(Theme.Px(266), Theme.Px(406)), Width = Theme.Px(472) };
            keyBox.Click += (s, e) => Capture();
            Theme.Help(keyBox, "Click, then press the key, key combination or mouse button (middle, back, forward). Esc cancels.");
            Body.Controls.Add(keyBox);
            ok = new FlatButton(existing == null ? "Add hotkey" : "Save", true) { Location = new Point(Theme.Px(738) - Theme.Px(150), Theme.Px(466)), Width = Theme.Px(150) };
            ok.Click += (s, e) => { if (picked == null) return; Result.Action = picked.Id; manager.CancelCapture(); DialogResult = DialogResult.OK; Close(); };
            var cancel = new FlatButton("Cancel") { Location = new Point(ok.Left - Theme.Px(10) - Theme.Px(110), Theme.Px(466)), Width = Theme.Px(110) };
            cancel.Click += (s, e) => { if (manager != null) manager.CancelCapture(); DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(ok); Body.Controls.Add(cancel);
            CancelButton = cancel;
            // Nothing to choose "For" and nothing to add until an action is picked (4.1.4).
            argCombo.Visible = argLabel.Visible = false; ok.Enabled = false;
            var start = HotkeyActions.Find(Result.Action);
            ShowGroup(start != null ? start.Group : names[0]);
            if (start != null) Pick(start);
            ShowKey();
            // The pick shown once the popup is up (a list set before it exists can show blank).
            Shown += (s, e) => { if (picked != null) { var arg = Result.Arg; Pick(picked); if (arg != null) { var it = argCombo.Items.OfType<ArgItem>().FirstOrDefault(i => i.Value == arg); if (it != null) { argCombo.SelectedItem = it; Result.Arg = arg; } } } };
        }

        void ShowGroup(string name)
        {
            foreach (var b in groups) { bool on = (string)b.Tag == name; b.ForeColor = on ? Theme.Accent : Theme.Text; b.Font = on ? Theme.Bold : Theme.Body; }
            foreach (Control c in list.Controls.Cast<Control>().ToList()) { list.Controls.Remove(c); c.Dispose(); }
            int y = 0;
            foreach (var a in HotkeyActions.All.Where(x => x.Group == name))
            {
                var act = a;
                var row = new ActionRow(a) { Location = new Point(0, y), Width = Theme.Px(522), Selected = picked == a, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                row.Click += (s, e) => Pick(act);
                list.Controls.Add(row);
                y += row.Height;
            }
            // (Fit counts only visible rows - before the popup is up none are.)
            list.Height = y + Theme.Px(6);
            listHost.Relayout(); listHost.Offset = 0;
        }

        void Pick(HotkeyAction a)
        {
            if (picked != a) Result.Arg = null;
            picked = a;
            foreach (var r in list.Controls.OfType<ActionRow>()) r.Selected = r.Action == a;
            argCombo.Items.Clear();
            var choices = a.Args != null ? a.Args(core) : null;
            argCombo.Visible = argLabel.Visible = choices != null && choices.Count > 0;
            if (argCombo.Visible)
            {
                foreach (var c in choices) argCombo.Items.Add(new ArgItem { Value = c.Key, Label = c.Value });
                var sel = argCombo.Items.OfType<ArgItem>().FirstOrDefault(i => i.Value == Result.Arg) ?? (ArgItem)argCombo.Items[0];
                argCombo.SelectedItem = sel; Result.Arg = sel.Value;
            }
            ok.Enabled = true;
        }

        void ShowKey()
        {
            string k = HotkeyManager.Describe(Result);
            keyBox.Text = string.IsNullOrEmpty(k) || (Result.Key == 0 && Result.Mouse == 0) ? "Click here, then press the key" : k;
            keyBox.ForeColor = Theme.Text;
        }

        void Capture()
        {
            if (manager == null) return;
            keyBox.Text = "Press a key or mouse button...  (Esc cancels)";
            keyBox.ForeColor = Theme.Accent;
            manager.Capture(b =>
            {
                if (IsDisposed) return;
                if (b != null) { Result.Key = b.Key; Result.Mods = b.Mods; Result.Mouse = b.Mouse; }
                ShowKey();
            });
        }

        /// <summary>One thing a hotkey can do: its name, and what it does underneath.</summary>
        sealed class ActionRow : Control
        {
            public readonly HotkeyAction Action;
            bool selected, hover;
            public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }

            public ActionRow(HotkeyAction a)
            {
                Action = a;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Card;
                Height = Theme.Px(string.IsNullOrEmpty(a.Help) ? 40 : 54);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(selected ? Color.FromArgb(34, 52, 40) : hover ? Theme.FieldHover : Theme.Card);
                if (selected) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, Theme.Px(8), Theme.Px(3), Height - Theme.Px(16));
                int tx = Theme.Px(16), tw = Width - Theme.Px(30);
                if (string.IsNullOrEmpty(Action.Help))
                    TextRenderer.DrawText(g, Action.Label, Theme.Bold, new Rectangle(tx, 0, tw, Height), selected ? Theme.Accent : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                else
                {
                    TextRenderer.DrawText(g, Action.Label, Theme.Bold, new Rectangle(tx, Theme.Px(7), tw, Theme.Px(20)), selected ? Theme.Accent : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, Action.Help.Split('\n')[0], Theme.Small, new Rectangle(tx, Theme.Px(27), tw, Theme.Px(20)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (var p = new Pen(Theme.Track)) g.DrawLine(p, Theme.Px(12), Height - 1, Width - Theme.Px(12), Height - 1);
            }
        }
    }
}
