using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>What happens to your settings when StreamLite is removed: asked before anything is removed, done after.</summary>
    internal sealed class UninstallSettingsPlan
    {
        /// <summary>Everything in the settings folder goes (except KeepOnly, when that's in it).</summary>
        public bool Delete;
        /// <summary>The settings file you saved, wherever you put it (null = none).</summary>
        public string Saved;
    }

    /// <summary>
    /// Uninstalling (4.4): your settings stay in StreamLite's folder, or you tick the ones you want and save them there or
    /// anywhere else (the rest goes), or they all go. The same on Windows (MSI or the app's own button) and Linux.
    /// Saved settings are a normal settings file: Scan for settings finds it in StreamLite's folder when you install again.
    /// </summary>
    internal static class UninstallSettings
    {
        /// <summary>Asks (nobody there, or no settings: everything stays). Call it before the sound devices go: settings name their devices.</summary>
        public static UninstallSettingsPlan Ask(Action<string> log)
        {
            var plan = new UninstallSettingsPlan();
            try
            {
                if (!Environment.UserInteractive || !File.Exists(Path.Combine(AppSettings.Folder, "settings.json"))) return plan;
                while (true)
                {
                    int pick = Choose("Your StreamLite settings", "StreamLite is being removed. What should happen to your settings: mic tuning, EQs, 3D audio, camera, hotkeys?", 60, new[]
                    {
                        new ChoiceRow("Keep all of them", "They stay in StreamLite's folder (" + AppSettings.Folder + "). Install StreamLite again and Scan for settings brings them back."),
                        new ChoiceRow("Choose what to save...", "Tick the settings you want, then keep them in StreamLite's folder or export them anywhere. The rest is deleted."),
                        new ChoiceRow("Delete all of them", "Nothing of yours stays on this PC."),
                    });
                    if (pick == 2) { plan.Delete = true; log("settings: delete all"); return plan; }
                    if (pick != 1) { log("settings: keep all"); return plan; }
                    if (SaveChosen(plan, log)) return plan;
                }
            }
            catch (Exception ex) { log("settings question: " + ex.Message + " - they stay"); plan.Delete = false; return plan; }
        }

        /// <summary>The picker, then where it goes. False = back to the first question.</summary>
        static bool SaveChosen(UninstallSettingsPlan plan, Action<string> log)
        {
            var s = AppSettings.Load();
            var names = SettingsTransfer.Names(null);
            var parts = SetupParts.From(s, names(s.MicId), names);
            List<SetupPart> chosen;
            using (var d = new SetupPartsDialog("Save your settings", "Tick what to save - a whole area, a device, or single settings (fold a line open with its arrow). What you don't tick is deleted with StreamLite.", parts, false, null, "Next...") { TopMost = true })
            {
                if (d.ShowDialog() != DialogResult.OK || d.Chosen == null || d.Chosen.Count == 0) return false;
                chosen = d.Chosen;
            }
            string inFolder = Path.Combine(AppSettings.Folder, DefaultName());
            int where = Choose("Where should they go?", chosen.Count + " setting" + (chosen.Count == 1 ? "" : "s") + " picked.", 0, new[]
            {
                new ChoiceRow("In StreamLite's folder", inFolder + " - install StreamLite again and Scan for settings finds it."),
                new ChoiceRow("Choose a place...", "A settings file you can keep anywhere, or take to another PC."),
            });
            string path = null;
            if (where == 0) path = inFolder;
            else if (where == 1)
                using (var dlg = new SaveFileDialog { Title = "Export your StreamLite settings", Filter = "StreamLite settings (*.streamlite)|*.streamlite", FileName = DefaultName() })
                {
                    if (FilePicker.Show(dlg, null) != DialogResult.OK) return false;
                    path = dlg.FileName;
                }
            if (path == null) return false;
            Save(chosen, path);
            plan.Saved = path; plan.Delete = true;
            log("settings: saved " + chosen.Count + " to " + path + ", the rest deleted");
            return true;
        }

        static string DefaultName() { return "StreamLite settings " + DateTime.Now.ToString("yyyy-MM-dd") + SetupExport.Extension; }

        /// <summary>The picked settings as a settings file.</summary>
        internal static void Save(List<SetupPart> parts, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var f = new SetupFile { Format = SetupFile.Kind, FormatVersion = 2, AppVersion = Program.Version.ToString(3), Created = DateTime.Now.ToString("yyyy-MM-dd"), Parts = parts };
            File.WriteAllText(path, SetupExport.ToJson(f));
        }

        /// <summary>Does what was decided, once StreamLite is off the PC. deleteFolder also copes with files still in use.</summary>
        public static void Finish(UninstallSettingsPlan plan, Action<string> log, Action<string> deleteFolder)
        {
            if (plan == null || !plan.Delete) return;
            string folder = AppSettings.Folder;
            try
            {
                bool keepInside = plan.Saved != null && File.Exists(plan.Saved) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.Saved)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
                if (!keepInside) { deleteFolder(folder); log("settings deleted" + (plan.Saved != null ? " (saved copy: " + plan.Saved + ")" : "")); return; }
                foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                    if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(plan.Saved), StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch (Exception ex) { log("  still in use: " + Path.GetFileName(f) + " (" + ex.Message + ")"); }
                foreach (var d in Directory.GetDirectories(folder)) try { Directory.Delete(d, true); } catch { }
                log("settings deleted but for " + Path.GetFileName(plan.Saved));
            }
            catch (Exception ex) { log("deleting the settings: " + ex.Message); }
        }

        /// <summary>A few big choices; -1 = closed, or no answer in time (seconds; 0 = waits).</summary>
        internal static int Choose(string title, string intro, int seconds, ChoiceRow[] rows)
        {
            using (var d = new BigChoiceDialog(title, intro, seconds, rows))
            {
                d.ShowDialog();
                return d.Picked;
            }
        }
    }

    internal sealed class ChoiceRow
    {
        public readonly string Title, Detail;
        public ChoiceRow(string title, string detail) { Title = title; Detail = detail; }
    }

    /// <summary>A question with a few big answers, each with a line under it; stays in front (nothing else may hold up an uninstall unseen).</summary>
    internal sealed class BigChoiceDialog : DarkDialog
    {
        const int RowH = 78;
        public int Picked { get; private set; }
        readonly Timer timer;
        int left;

        public BigChoiceDialog(string title, string intro, int seconds, ChoiceRow[] rows) : base(title, 640, 84 + rows.Length * RowH + (seconds > 0 ? 34 : 0))
        {
            Picked = -1; TopMost = true;
            Text2(intro, 22, 16, 596, 50, Theme.Body, Theme.Text);
            int y = 72;
            for (int i = 0; i < rows.Length; i++)
            {
                int pick = i;
                var b = new FlatButton(rows[i].Title, i == 0) { Location = new Point(Theme.Px(22), Theme.Px(y)), Width = Theme.Px(596) };
                b.Click += (s, e) => { Picked = pick; DialogResult = DialogResult.OK; Close(); };
                Body.Controls.Add(b);
                Text2(rows[i].Detail, 24, y + 38, 592, 36, Theme.Small, Theme.Muted);
                y += RowH;
            }
            if (seconds <= 0) return;
            left = seconds;
            var note = Text2("", 24, y + 2, 592, 22, Theme.Small, Theme.Muted);
            Action show = () => note.Text = "No answer in " + left + " s: " + rows[0].Title.ToLowerInvariant() + ".";
            show();
            timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) =>
            {
                if (--left <= 0) { timer.Stop(); Picked = -1; DialogResult = DialogResult.Cancel; Close(); return; }
                show();
            };
            timer.Start();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); try { Activate(); } catch { } }

        protected override void OnFormClosed(FormClosedEventArgs e) { if (timer != null) timer.Dispose(); base.OnFormClosed(e); }
    }
}
