using System;
using System.Drawing;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// The small note near the bottom of the screen after a hotkey ("Microphone 2 muted"). Never takes focus and lets
    /// clicks through, so a game or app in front keeps working. (Exclusive-fullscreen games draw over it.)
    /// </summary>
    internal sealed class HotkeyToast : Form
    {
        readonly Timer hide = new Timer { Interval = 1600 };
        string text = "";

        public HotkeyToast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Bold;
            Opacity = 0.94;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            hide.Tick += (s, e) => { hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000008 | 0x00000080 | 0x08000000 | 0x00000020;   // topmost, tool window, no activate, click-through
                return cp;
            }
        }

        public void Note(string message)
        {
            text = message;
            var size = TextRenderer.MeasureText(message, Font);
            Size = new Size(size.Width + Theme.Px(40), size.Height + Theme.Px(22));
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - Theme.Px(90));
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Theme.Px(14), Theme.Px(14)));
            Invalidate();
            if (!Visible) Show();
            TopMost = true;
            hide.Stop(); hide.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Card);
            using (var pen = new Pen(Theme.Accent, Theme.Px(2))) e.Graphics.DrawLine(pen, Theme.Px(10), Height - Theme.Px(3), Width - Theme.Px(10), Height - Theme.Px(3));
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    }
}
