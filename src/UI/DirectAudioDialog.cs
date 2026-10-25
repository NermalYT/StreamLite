using System.Drawing;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Asked once after installing or updating (when 3D audio plays to headphones): turn on Direct to headphones?
    /// In plain words what it does and what changes; "Not now" leaves it in 3D audio's settings.
    /// </summary>
    internal sealed class DirectAudioDialog : DarkDialog
    {
        public DirectAudioDialog(string headphones, string sideOutput) : base("Lower delay for your headphones", 520, 510)
        {
            Text2("Turn on Direct to headphones?", 22, 16, 480, 28, Theme.Section, Theme.Text);
            Text2("Right now your game sound takes a detour: StreamLite hands it to Windows, and Windows mixes it with every other app " +
                "in 10 ms steps before it reaches " + headphones + ".", 22, 52, 476, 56, Theme.Body, Theme.Text);
            Text2("Direct to headphones skips that detour: StreamLite plays straight to your headphones in their own small steps " +
                "(3 ms on most) - about 7 to 10 ms sooner. You hear footsteps and shots earlier.", 22, 112, 476, 56, Theme.Body, Theme.Text);
            Text2("What changes", 22, 178, 476, 22, Theme.Bold, Theme.Text);
            Text2("• StreamLite is the only thing playing to your headphones and mixes every app itself.\n" +
                (sideOutput != null
                    ? "• Voice chat, music and browsers still sound normal. They play through " + sideOutput + " (StreamLite mutes it meanwhile) and reach you about 40 ms after the game - you won't notice that in calls or music.\n"
                    : "• This PC has no other output for voice chat and music, so they go through 3D audio too (like Windows Sonic does).\n") +
                "• An app set to play to your headphones in its own settings needs \"Default\" there instead.\n" +
                "• If your headphones can't do it, StreamLite plays the normal way by itself - nothing breaks.",
                22, 202, 476, 190, Theme.Body, Theme.Muted);
            Text2("You can turn it on or off any time: 3D audio > Output > Direct to headphones.", 22, 400, 476, 40, Theme.Small, Theme.Muted);

            var on = new FlatButton("Turn on", true) { Location = new Point(Theme.Px(22), Theme.Px(452)), Width = Theme.Px(150) };
            on.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Body.Controls.Add(on);
            var later = new FlatButton("Not now") { Location = new Point(Theme.Px(186), Theme.Px(452)), Width = Theme.Px(120) };
            later.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(later);
            AcceptButton = on;
        }
    }
}
