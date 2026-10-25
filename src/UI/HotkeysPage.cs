using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Hotkeys page: a numbered list of bindings - what it does, which mic / output / profile / background it applies
    /// to, and its key or mouse button. They work while games and other apps have the focus.
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

        sealed class ArgItem
        {
            public string Value, Label;
            public override string ToString() { return Label; }
        }

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
                "Bind a key, a key combination (Ctrl, Alt, Shift, Win + a key), a single key like Left Ctrl or Caps Lock, or the middle / back / forward mouse button to anything in the list.\nHotkeys work while games, Discord or any other app is in front. They never take the key away from the app.\nPush to talk keeps your mic silent unless you hold its key. Mute and output mutes start unmuted every time StreamLite starts.");
            toasts = new ToggleSwitch();
            toasts.Location = new Point(Theme.Px(400), Theme.Px(14));
            card.Controls.Add(toasts);
            var tl = Theme.Label(card, "Note on screen", 454, 15, Theme.Bold);
            Theme.Help(tl, "Shows a small note near the bottom of the screen when a hotkey does something (\"Microphone 2 muted\"). It never takes the focus. Exclusive-fullscreen games draw over it.");
            Responsive.Fixed(toasts); Responsive.Fixed(tl);
            toasts.CheckedChanged += (s, e) => { if (loading) return; core.S.HotkeyToasts = toasts.Checked; core.SaveSoon(); };
            hint = Theme.Label(card, "", 20, 48, Theme.Small, Theme.Muted, 650);
            hint.Height = Theme.Px(34);
            add = new FlatButton("+  Add hotkey", true);
            card.Controls.Add(add);
            add.Click += (s, e) =>
            {
                var b = new HotkeyBinding { Action = "mic.mute", Arg = "all" };
                core.S.Hotkeys.Add(b);
                core.SaveSoon();
                Build();
                StartCapture(b, rows.OfType<FlatButton>().LastOrDefault(x => x.Tag == b));
            };
            Build();
        }

        public void Reload()
        {
            loading = true;
            toasts.SetSilently(core.S.HotkeyToasts);
            loading = false;
            Build();
        }

        void Build()
        {
            foreach (var c in rows) { card.Controls.Remove(c); c.Dispose(); }
            rows.Clear();
            var list = core.S.Hotkeys;
            hint.Text = list.Count == 0
                ? "No hotkeys yet. Add one - for example Mute microphone 2, Push to talk, or Next camera background."
                : "Click a key button, then press the key or mouse button you want (Esc cancels).";
            int y = 90, n = 1;
            foreach (var b in list.ToList())
            {
                var bind = b;
                var num = Theme.Label(card, n.ToString(), 24, y + 3, Theme.Bold, Theme.Accent, 26);
                var action = new DarkCombo { Location = new Point(Theme.Px(50), Theme.Px(y)), Width = Theme.Px(236) };
                foreach (var a in HotkeyActions.All) action.Items.Add(a);
                var current = HotkeyActions.Find(bind.Action);
                action.SelectedItem = current;
                var arg = new DarkCombo { Location = new Point(Theme.Px(292), Theme.Px(y)), Width = Theme.Px(190) };
                FillArgs(arg, current, bind);
                var key = new FlatButton(HotkeyManager.Describe(bind)) { Location = new Point(Theme.Px(488), Theme.Px(y - 2)), Width = Theme.Px(112), Tag = bind };
                var remove = new FlatButton("Remove") { Location = new Point(Theme.Px(606), Theme.Px(y - 2)), Width = Theme.Px(70) };
                card.Controls.Add(action); card.Controls.Add(arg); card.Controls.Add(key); card.Controls.Add(remove);
                rows.Add(num); rows.Add(action); rows.Add(arg); rows.Add(key); rows.Add(remove);
                Responsive.Fixed(key); Responsive.Fixed(remove);
                if (current != null && current.Help != null) Theme.Help(action, current.Help);
                action.SelectionChangeCommitted += (s, e) =>
                {
                    var a = action.SelectedItem as HotkeyAction; if (a == null) return;
                    bind.Action = a.Id; bind.Arg = null;
                    FillArgs(arg, a, bind);
                    Save();
                };
                arg.SelectionChangeCommitted += (s, e) => { var it = arg.SelectedItem as ArgItem; if (it != null) { bind.Arg = it.Value; Save(); } };
                key.Click += (s, e) => StartCapture(bind, key);
                Theme.Help(key, "Click, then press the key, key combination or mouse button (middle, back, forward). Esc cancels.");
                remove.Click += (s, e) => { core.S.Hotkeys.Remove(bind); Save(); Build(); };
                // The same key twice: both run - say so.
                var twin = list.FirstOrDefault(o => o != bind && (o.Key != 0 || o.Mouse != 0) && o.Key == bind.Key && o.Mouse == bind.Mouse && o.Mods == bind.Mods);
                if (twin != null) { key.ForeColor = Theme.Warn; Theme.Help(key, "Hotkey " + (list.IndexOf(twin) + 1) + " uses the same key - both run."); }
                y += 42; n++;
            }
            add.Location = new Point(Theme.Px(50), Theme.Px(y + 2));
            card.Height = Theme.Px(y + 50);
            Height = card.Bottom + Theme.Px(20);
        }

        void FillArgs(DarkCombo arg, HotkeyAction a, HotkeyBinding bind)
        {
            arg.Items.Clear();
            var choices = a != null && a.Args != null ? a.Args(core) : null;
            arg.Visible = choices != null && choices.Count > 0;
            if (!arg.Visible) return;
            foreach (var c in choices) arg.Items.Add(new ArgItem { Value = c.Key, Label = c.Value });
            var sel = arg.Items.OfType<ArgItem>().FirstOrDefault(i => i.Value == bind.Arg);
            if (sel == null) { sel = (ArgItem)arg.Items[0]; bind.Arg = sel.Value; }
            arg.SelectedItem = sel;
        }

        void StartCapture(HotkeyBinding bind, FlatButton key)
        {
            var m = manager(); if (m == null || key == null) return;
            key.Text = "Press a key...";
            key.ForeColor = Theme.Accent;
            m.Capture(b =>
            {
                if (b != null) { bind.Key = b.Key; bind.Mods = b.Mods; bind.Mouse = b.Mouse; }
                Save();
                if (!IsDisposed) Build();
            });
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
}
