using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// Opening and saving files: Windows' own file dialog, and on Linux the desktop's own (GNOME's / KDE's file picker,
    /// 4.1.4 - Mono's built-in one looked like Windows 95). Every "Open..." and "Save..." in StreamLite goes through here.
    /// </summary>
    internal static class FilePicker
    {
        public static DialogResult Show(FileDialog dlg, IWin32Window owner) { string[] files; return Show(dlg, owner, out files); }

        /// <summary>Shows the dialog; files = what was picked (several when Multiselect is on). dlg.FileName is set too.</summary>
        public static DialogResult Show(FileDialog dlg, IWin32Window owner, out string[] files)
        {
#if LINUX
            var native = StreamLite.Linux.LinuxFilePicker.Pick(dlg, owner);
            if (native != null)
            {
                files = native;
                if (files.Length == 0) return DialogResult.Cancel;
                dlg.FileName = files[0];
                return DialogResult.OK;
            }
#endif
            var r = dlg.ShowDialog(owner);
            var open = dlg as OpenFileDialog;
            files = r != DialogResult.OK ? new string[0] : open != null && open.Multiselect ? open.FileNames : new[] { dlg.FileName };
            return r;
        }
    }
}
