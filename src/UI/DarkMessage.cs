using System;
using System.Drawing;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// StreamLite's own message box: the dark dialog with its title bar and flat buttons instead of Windows' grey one.
    /// Same calls as MessageBox.Show (OK, OK/Cancel and Yes/No), same results.
    /// </summary>
    internal sealed class DarkMessage : DarkDialog
    {
        DarkMessage(string title, string text, MessageBoxButtons buttons, int height, bool secondDefault) : base(string.IsNullOrEmpty(title) ? "StreamLite" : title, 460, height)
        {
            var body = Text2(text, 22, 18, 416, height - 70, Theme.Body, Theme.Text);
            bool two = buttons == MessageBoxButtons.YesNo || buttons == MessageBoxButtons.OKCancel;
            var ok = new FlatButton(buttons == MessageBoxButtons.YesNo ? "Yes" : "OK", true);
            ok.Location = new Point(Theme.Px(438) - ok.Width, Theme.Px(height - 44));
            ok.Click += (s, e) => { DialogResult = buttons == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK; Close(); };
            Body.Controls.Add(ok);
            AcceptButton = ok;
            if (two)
            {
                var no = new FlatButton(buttons == MessageBoxButtons.YesNo ? "No" : "Cancel");
                no.Location = new Point(ok.Left - Theme.Px(10) - no.Width, ok.Top);
                no.Click += (s, e) => { DialogResult = buttons == MessageBoxButtons.YesNo ? DialogResult.No : DialogResult.Cancel; Close(); };
                Body.Controls.Add(no);
                CancelButton = no;
                if (secondDefault) { AcceptButton = no; Shown += (s, e) => no.Focus(); }   // Enter takes the careful answer
            }
            else CancelButton = ok;
        }

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        {
            return Show(owner, text, title, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            return Show(null, text, title, buttons, icon, def);
        }

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            var size = TextRenderer.MeasureText(text ?? "", Theme.Body, new Size(Theme.Px(416), 0), TextFormatFlags.WordBreak);
            int h = Math.Max(130, (int)(size.Height / Math.Max(0.5, Theme.Px(100) / 100.0)) + 90);
            using (var d = new DarkMessage(title, text, buttons, h, def == MessageBoxDefaultButton.Button2))
            {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                var r = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
                if (r == DialogResult.None || (r == DialogResult.Cancel && buttons == MessageBoxButtons.YesNo)) r = buttons == MessageBoxButtons.YesNo ? DialogResult.No : DialogResult.Cancel;
                return r;
            }
        }

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        {
            return Show(null, text, title, buttons, icon);
        }
    }
}
