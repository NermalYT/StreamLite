using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.Linux
{
    /// <summary>
    /// The desktop's own file picker: KDE's (kdialog) on Plasma, GTK's (zenity) elsewhere - both come with the desktop
    /// or with StreamLite's installer. null = neither is there (Mono's own dialog is used then).
    /// </summary>
    internal static class LinuxFilePicker
    {
        internal sealed class Filter { public string Name; public string[] Patterns; }

        /// <summary>"Settings (*.streamlite)|*.streamlite|All files|*.*" -> its filters ("*.*" is "*" here).</summary>
        internal static List<Filter> Filters(string filter)
        {
            var l = new List<Filter>();
            var p = (filter ?? "").Split('|');
            for (int i = 0; i + 1 < p.Length; i += 2)
                l.Add(new Filter { Name = p[i].Trim(), Patterns = p[i + 1].Split(';').Select(x => x.Trim() == "*.*" ? "*" : x.Trim()).Where(x => x.Length > 0).ToArray() });
            return l;
        }

        /// <summary>The program and its arguments for this dialog (tests check them).</summary>
        internal static string[] Command(string program, FileDialog dlg)
        {
            bool save = dlg is SaveFileDialog, multi = !save && ((OpenFileDialog)dlg).Multiselect;
            var filters = Filters(dlg.Filter);
            string dir = !string.IsNullOrEmpty(dlg.InitialDirectory) && Directory.Exists(dlg.InitialDirectory) ? dlg.InitialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            string name = Path.GetFileName(dlg.FileName ?? "");
            var a = new List<string> { program };
            if (program == "kdialog")
            {
                a.Add(save ? "--getsavefilename" : "--getopenfilename");
                a.Add(save && name.Length > 0 ? Path.Combine(dir, name) : dir);
                a.Add(string.Join("\n", filters.Select(f => string.Join(" ", f.Patterns) + "|" + f.Name)));
                if (multi) { a.Add("--multiple"); a.Add("--separate-output"); }
                a.Add("--title"); a.Add(dlg.Title ?? "StreamLite");
            }
            else
            {
                a.Add("--file-selection");
                a.Add("--title=" + (dlg.Title ?? "StreamLite"));
                if (save) a.Add("--save");
                if (multi) { a.Add("--multiple"); a.Add("--separator=\n"); }
                a.Add("--filename=" + (save && name.Length > 0 ? Path.Combine(dir, name) : dir.TrimEnd('/') + "/"));
                foreach (var f in filters) a.Add("--file-filter=" + f.Name + " | " + string.Join(" ", f.Patterns));
            }
            return a.ToArray();
        }

        public static string[] Pick(FileDialog dlg, IWin32Window owner)
        {
            bool kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").IndexOf("KDE", StringComparison.OrdinalIgnoreCase) >= 0;
            string program = kde && Shell.Has("kdialog") ? "kdialog" : Shell.Has("zenity") ? "zenity" : Shell.Has("kdialog") ? "kdialog" : null;
            if (program == null) return null;
            var cmd = Command(program, dlg);
            var form = owner as Form ?? (owner as Control != null ? ((Control)owner).FindForm() : null);
            try
            {
                var psi = new ProcessStartInfo(program, string.Join(" ", cmd.Skip(1).Select(Shell.Quote))) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    // Modal, like a file dialog: StreamLite keeps drawing but takes no clicks until it's closed.
                    if (form != null) form.Enabled = false;
                    try { while (!p.WaitForExit(15)) Application.DoEvents(); }
                    finally { if (form != null) { form.Enabled = true; form.Activate(); } }
                    if (p.ExitCode == 1) return new string[0];   // cancelled
                    if (p.ExitCode != 0) return null;            // couldn't run: Mono's own dialog then
                    var files = output.Result.Split('\n').Select(x => x.Trim('\r')).Where(x => x.Length > 0).ToArray();
                    if (dlg is SaveFileDialog && files.Length == 1 && Path.GetExtension(files[0]).Length == 0)
                    {
                        // Saved without an ending: the dialog's own (".streamlite").
                        var f = Filters(dlg.Filter).FirstOrDefault();
                        string ext = f != null ? f.Patterns.Select(x => x.TrimStart('*')).FirstOrDefault(x => x.StartsWith(".")) : null;
                        if (ext != null) files[0] += ext;
                    }
                    return files;
                }
            }
            catch { return null; }
        }
    }
}
