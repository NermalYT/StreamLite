using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using StreamLite.Interop;

namespace StreamLite.UI
{
    /// <summary>A settings file found on this PC (Scan for settings).</summary>
    internal sealed class FoundSettings
    {
        public string Path, Title, Detail;
        public DateTime When;
        public bool IsSetupFile;
    }

    /// <summary>
    /// Export settings, Import settings and Scan for settings (4.1.3), from the Settings page and from the setup's first
    /// step: piece by piece and device by device (SetupPartsDialog), with your settings kept first so Undo import puts
    /// them back.
    /// </summary>
    internal static class SettingsTransfer
    {
        /// <summary>This PC's devices of a kind (mic, output, camera) for matching a file's devices.</summary>
        public static List<SetupDevice> DevicesHere(AppCore core, string kind)
        {
            try
            {
                if (kind == "mic") return core.MicChoices().Where(d => !AudioDevices.IsCable(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
                if (kind == "output") return core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).Select(d => new SetupDevice { Id = d.Id, Name = d.Name }).ToList();
                if (kind == "camera") return VideoDevices.List().Where(c => !c.Name.StartsWith("StreamLite", StringComparison.OrdinalIgnoreCase)).Select(c => new SetupDevice { Id = c.Key, Name = c.Name }).ToList();
            }
            catch { }
            return new List<SetupDevice>();
        }

        /// <summary>Every device's name here by id (mics, outputs, cameras); null = unknown.</summary>
        public static Func<string, string> Names(AppCore core)
        {
            var all = new Dictionary<string, string>();
            try { foreach (var d in AudioDevices.List(EDataFlow.Render, 1 | 2 | 8).Concat(AudioDevices.List(EDataFlow.Capture, 1 | 2 | 8))) all[d.Id] = d.Name; } catch { }
            try { foreach (var c in VideoDevices.List()) all[c.Key] = c.Name; } catch { }
            return id => { string n; return id != null && all.TryGetValue(id, out n) ? n : null; };
        }

        /// <summary>Export settings: pick the pieces, then where to save them.</summary>
        public static string Export(IWin32Window owner, AppCore core)
        {
            var names = Names(core);
            var parts = SetupParts.From(core.S, names(core.ActiveMicId), names);
            using (var d = new SetupPartsDialog("Export settings", "Tick what goes into the file - a whole area, a device, or single settings (fold a line open with its arrow). " +
                "Devices go by name, so the file works on any PC.", parts, false, null, "Export..."))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                using (var dlg = new SaveFileDialog { Title = "Export your StreamLite settings", Filter = "StreamLite settings (*.streamlite)|*.streamlite", FileName = "StreamLite settings " + DateTime.Now.ToString("yyyy-MM-dd") + SetupExport.Extension })
                {
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                    var f = new SetupFile { Format = SetupFile.Kind, FormatVersion = 2, AppVersion = Program.Version.ToString(3), Created = DateTime.Now.ToString("yyyy-MM-dd"), Parts = d.Chosen };
                    File.WriteAllText(dlg.FileName, SetupExport.ToJson(f));
                    return "Saved " + Path.GetFileName(dlg.FileName) + " - " + d.Chosen.Count + " setting" + (d.Chosen.Count == 1 ? "" : "s") + ".";
                }
            }
        }

        /// <summary>
        /// Import pieces (from a file or a scan): pick them and their devices, then they're applied (your settings kept
        /// first for Undo import). Returns the pieces applied, or null.
        /// </summary>
        public static List<SetupPart> Import(IWin32Window owner, AppCore core, List<SetupPart> parts, string from)
        {
            if (parts == null || parts.Count == 0) { DarkMessage.Show(owner, "There are no settings in " + from + ".", "Import settings"); return null; }
            using (var d = new SetupPartsDialog("Import settings", "From " + from + ". Tick what to bring in - a whole area, a device, or single settings. Each device goes to the device here " +
                "shown on its line (click it to pick another, or leave it out). Your current settings are kept first: Undo import puts them back.", parts, true, k => DevicesHere(core, k), "Import"))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                SetupExport.Snapshot(core.S);
                var names = Names(core);
                SetupParts.Apply(core.S, d.Chosen, p => { string id; return d.Targets.TryGetValue(p, out id) ? id : null; }, core.ActiveMicId, names);
                try { HealthMonitor.LogLine("Settings imported from " + from + ": " + string.Join(", ", d.Chosen.Select(p => p.Id).Distinct())); } catch { }
                return d.Chosen;
            }
        }

        /// <summary>Import settings from a file (path null: pick one).</summary>
        public static List<SetupPart> ImportFile(IWin32Window owner, AppCore core, string path)
        {
            if (path == null)
                using (var dlg = new OpenFileDialog { Title = "Import StreamLite settings", Filter = "StreamLite settings (*.streamlite)|*.streamlite|All files (*.*)|*.*" })
                {
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                    path = dlg.FileName;
                }
            List<SetupPart> parts;
            try { parts = SetupParts.Of(SetupExport.Parse(File.ReadAllText(path))); }
            catch (Exception ex) { DarkMessage.Show(owner, "Couldn't import: " + ex.Message, "Import settings"); return null; }
            return Import(owner, core, parts, Path.GetFileName(path));
        }

        // ---- Scan for settings: what StreamLite kept on this PC, and settings files in your folders

        /// <summary>Settings StreamLite kept (also after uninstalling, when you chose to keep them) and *.streamlite files in your usual folders.</summary>
        public static List<FoundSettings> Scan()
        {
            var l = new List<FoundSettings>();
            string folder = AppSettings.Folder;
            Action<string, string> settings = (file, title) =>
            {
                if (!File.Exists(file)) return;
                var s = AppSettings.ReadFile(file);
                if (s == null || !LooksSetUp(s)) return;
                l.Add(new FoundSettings { Path = file, Title = title, When = File.GetLastWriteTime(file), Detail = Describe(s) });
            };
            settings(Path.Combine(folder, "settings.json"), "Your StreamLite settings");
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "settings.kept-*.json").OrderByDescending(File.GetLastWriteTime))
                    settings(f, "Settings kept before a setup");
            settings(SetupExport.UndoPath, "Settings from before your last import");
            foreach (var dir in UserFolders())
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*" + SetupExport.Extension, SearchOption.TopDirectoryOnly).Take(40))
                        try
                        {
                            var sf = SetupExport.Parse(File.ReadAllText(f));
                            var parts = SetupParts.Of(sf);
                            l.Add(new FoundSettings { Path = f, Title = Path.GetFileName(f), IsSetupFile = true, When = File.GetLastWriteTime(f),
                                Detail = "Exported from StreamLite " + (sf.AppVersion ?? "?") + " - " + parts.Count + " settings in " + string.Join(", ", parts.Select(p => p.Area).Distinct()) });
                        }
                        catch { }
                }
                catch { }
            return l.OrderByDescending(x => x.When).ToList();
        }

        static IEnumerable<string> UserFolders()
        {
            var l = new List<string>();
            foreach (var sf in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory })
                try { var p = Environment.GetFolderPath(sf); if (!string.IsNullOrEmpty(p)) l.Add(p); } catch { }
            try { l.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")); } catch { }
            return l.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Settings worth bringing back: the voice tuned, a headphone EQ, a 3D head, profiles of your own, hotkeys or a camera.</summary>
        static bool LooksSetUp(AppSettings s)
        {
            return s.VoiceSetupDone || s.Profiles.Corrections.Count > 0 || s.Game.PersonalHeads.Count > 0 || s.Hotkeys.Count > 0 || s.Cameras.Count > 0 || s.MicPresets.Count > 0 || s.Profiles.Profiles.Any(p => !p.Builtin);
        }

        static string Describe(AppSettings s)
        {
            var have = new List<string>();
            if (s.VoiceSetupDone || s.MicPresets.Count > 0) have.Add("mic tuning");
            if (s.Profiles.Corrections.Count > 0) have.Add(s.Profiles.Corrections.Count == 1 ? "a headphone EQ" : s.Profiles.Corrections.Count + " headphone EQs");
            if (s.Game.PersonalHeads.Count > 0) have.Add("personal 3D");
            if (s.Cameras.Count > 0 || s.CamFx.FaceTracking || s.CamFx.Background != "off") have.Add("camera");
            if (s.Hotkeys.Count > 0) have.Add(s.Hotkeys.Count + " hotkey" + (s.Hotkeys.Count == 1 ? "" : "s"));
            return (s.LastVersion != null ? "StreamLite " + s.LastVersion + " - " : "") + (have.Count > 0 ? string.Join(", ", have) : "settings");
        }

        /// <summary>Keeps a copy of the settings before the setup starts some of them fresh, so Scan for settings can bring pieces back.</summary>
        public static void Keep(AppSettings s)
        {
            try
            {
                if (!LooksSetUp(s)) return;
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(Path.Combine(AppSettings.Folder, "settings.kept-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(s));
                // The five newest are enough.
                foreach (var old in Directory.GetFiles(AppSettings.Folder, "settings.kept-*.json").OrderByDescending(File.GetLastWriteTime).Skip(5)) File.Delete(old);
            }
            catch { }
        }

        /// <summary>Scan for settings: the list of what was found; picking one opens its settings piece by piece.</summary>
        public static List<SetupPart> ScanAndImport(IWin32Window owner, AppCore core)
        {
            var found = Scan();
            if (found.Count == 0)
            {
                DarkMessage.Show(owner, "No saved StreamLite settings were found on this PC (StreamLite's own folder, Documents, Desktop and Downloads). Import settings opens a settings file from anywhere.", "Scan for settings");
                return null;
            }
            using (var d = new FoundSettingsDialog(found))
            {
                if (d.ShowDialog(owner) != DialogResult.OK || d.Picked == null) return null;
                var f = d.Picked;
                List<SetupPart> parts;
                try { parts = f.IsSetupFile ? SetupParts.Of(SetupExport.Parse(File.ReadAllText(f.Path))) : SetupParts.OfSettingsFile(f.Path, Names(core)); }
                catch (Exception ex) { DarkMessage.Show(owner, "Couldn't read it: " + ex.Message, "Scan for settings"); return null; }
                return Import(owner, core, parts, f.Title + " (" + f.When.ToString("d MMM yyyy") + ")");
            }
        }
    }

    /// <summary>Scan for settings: each settings file found, newest first; click one to pick its settings.</summary>
    internal sealed class FoundSettingsDialog : DarkDialog
    {
        public FoundSettings Picked { get; private set; }

        public FoundSettingsDialog(List<FoundSettings> found) : base("Scan for settings", 640, Math.Min(560, 150 + found.Count * 64))
        {
            int h = Math.Min(560, 150 + found.Count * 64);
            Text2("Settings found on this PC - StreamLite's own (also kept after uninstalling) and settings files in Documents, Desktop and Downloads. Pick one, then tick what to bring back.", 20, 12, 600, 40, Theme.Small, Theme.Muted);
            ScrollHost host;
            var scroll = ScrollLists.Make(Body, new Rectangle(0, Theme.Px(58), Theme.Px(640), Theme.Px(h - 58 - 56)), Theme.Bg, out host);
            int y = 0;
            foreach (var f in found)
            {
                var ff = f;
                var b = new FlatButton(f.Title) { Location = new Point(Theme.Px(20), Theme.Px(y)), Size = new Size(Theme.Px(600), Theme.Px(56)), TextAlign = ContentAlignment.TopLeft };
                b.Padding = new Padding(Theme.Px(10), Theme.Px(6), Theme.Px(10), 0);
                b.Click += (s, e) => { Picked = ff; DialogResult = DialogResult.OK; Close(); };
                scroll.Controls.Add(b);
                var detail = new Label { Text = f.When.ToString("d MMM yyyy, HH:mm") + "  -  " + f.Detail, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false,
                    Location = new Point(Theme.Px(10), Theme.Px(30)), Size = new Size(Theme.Px(580), Theme.Px(18)), Cursor = Cursors.Hand };
                detail.Click += (s, e) => b.PerformClick();
                b.Controls.Add(detail);
                y += 64;
            }
            ScrollLists.Fit(host);
            var cancel = new FlatButton("Cancel") { Location = new Point(Theme.Px(620) - Theme.Px(90), Theme.Px(h - 44)), Width = Theme.Px(90) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Body.Controls.Add(cancel); CancelButton = cancel;
        }
    }
}
