using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// A card that folds away (4.1): its title row stays, everything else hides behind one click - pages show their
    /// everyday settings first and keep the rest a click away; nothing is removed. Whatever sits below the card moves up
    /// and down with it, and whether it's open is remembered.
    /// </summary>
    internal sealed class Fold
    {
        static readonly List<Fold> all = new List<Fold>();
        readonly Control card, title;
        readonly Label chevron;
        readonly string key;
        readonly List<string> openList;
        readonly Action save;
        readonly int openHeight, closedHeight;
        bool open = true;

        Fold(Control card, Control title, string key, List<string> openList, Action save, bool makeRoom)
        {
            this.card = card; this.title = title; this.key = key; this.openList = openList; this.save = save;
            // A title row with other things in it: everything but the title moves down a row, so the row is the title's.
            if (makeRoom)
            {
                foreach (Control c in card.Controls) if (c != title) c.Top += Theme.Px(44);
                card.Height += Theme.Px(44);
            }
            openHeight = card.Height;
            // Folded, the card is just its title row: what's below is cut off by the card's edge (it keeps its state).
            closedHeight = Theme.Px(52);
            foreach (Control c in card.Controls)
                if (c != title && c.Top > title.Top + Theme.Px(8)) closedHeight = Math.Min(closedHeight, Math.Max(title.Bottom + Theme.Px(6), c.Top - Theme.Px(2)));
            chevron = new Label
            {
                AutoSize = false, Size = new Size(Theme.Px(150), Theme.Px(24)), Location = new Point(card.Width - Theme.Px(170), Theme.Px(18)),
                TextAlign = ContentAlignment.MiddleRight, Font = Theme.Bold, ForeColor = Theme.Accent, BackColor = Color.Transparent, Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right, UseMnemonic = false
            };
            card.Controls.Add(chevron);
            chevron.BringToFront();
            chevron.Click += (s, e) => Toggle();
            title.Cursor = Cursors.Hand;
            title.Click += (s, e) => Toggle();
        }

        /// <summary>
        /// Makes a card foldable; title = its title label (clicking it folds too). key = how it's remembered. The card
        /// starts as it was left (folded the first time) once Settle has run for its page.
        /// </summary>
        public static Fold Make(Control card, Control title, string key, List<string> openList, Action save, bool makeRoom = false)
        {
            var f = new Fold(card, title, key, openList, save, makeRoom);
            all.Add(f);
            return f;
        }

        /// <summary>A page is built: its folds as they were left (folded by default) - the lowest first, so nothing above them moves twice.</summary>
        public static void Settle(Control page)
        {
            foreach (var f in all.Where(x => x.card.Parent == page).OrderByDescending(x => x.card.Top).ToList())
                f.Set(f.openList != null && f.openList.Contains(f.key), false);
        }

        public bool IsOpen { get { return open; } }

        public void Toggle() { Set(!open, true); }

        public void Set(bool on, bool remember)
        {
            if (on == open) { chevron.Text = on ? "Hide  ▴" : "Show  ▾"; return; }
            open = on;
            var parent = card.Parent;
            if (parent != null) parent.SuspendLayout();
            int oldBottom = card.Bottom;
            card.Height = on ? openHeight : closedHeight;
            chevron.Text = on ? "Hide  ▴" : "Show  ▾";
            int delta = card.Bottom - oldBottom;
            if (parent != null && delta != 0)
            {
                foreach (Control c in parent.Controls)
                    if (c != card && c.Top >= oldBottom - Theme.Px(2)) c.Top += delta;
                parent.Height += delta;
            }
            if (parent != null) parent.ResumeLayout();
            if (remember && openList != null)
            {
                openList.Remove(key);
                if (on) openList.Add(key);
                if (save != null) save();
            }
        }
    }
}
