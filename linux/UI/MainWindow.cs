using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StreamLite.Audio;
using StreamLite.Interop;
using StreamLite.Linux;

namespace StreamLite.UI
{
    /// <summary>The StreamLite window on Linux: Microphone, Headphones, 3D Audio and Settings pages.</summary>
    internal sealed class MainWindow : Window
    {
        readonly Core core;
        AppSettings S { get { return core.S; } }
        readonly ContentControl content = new ContentControl();
        readonly List<Action> refreshers = new List<Action>();
        public readonly Dictionary<string, Control> Pages = new Dictionary<string, Control>();
        bool loading;
        public bool QuitOnClose;

        public MainWindow(Core core)
        {
            this.core = core;
            Title = "StreamLite";
            Width = 980; Height = 760; MinWidth = 760; MinHeight = 560;
            Background = Palette.Bg;
            Pages["Microphone"] = Scroll(BuildMic());
            Pages["Headphones"] = Scroll(BuildHeadphones());
            Pages["3D Audio"] = Scroll(BuildGame());
            Pages["Settings"] = Scroll(BuildSettings());

            var nav = new ListBox { Width = 190, Background = Palette.Card, Margin = new Thickness(0) };
            foreach (var k in Pages.Keys) nav.Items.Add(new ListBoxItem { Content = k, Padding = new Thickness(18, 12) });
            nav.SelectionChanged += (s, e) => { var it = nav.SelectedItem as ListBoxItem; if (it != null) content.Content = Pages[(string)it.Content]; };
            nav.SelectedIndex = 0;
            var brand = new TextBlock { Text = "StreamLite", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(18, 18, 18, 10), Foreground = Palette.Good };
            var left = new DockPanel { Background = Palette.Card };
            DockPanel.SetDock(brand, Dock.Top);
            var bug = new Button { Content = "Report a bug", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(12), Background = Brushes.Transparent, Foreground = Palette.Muted };
            ToolTip.SetTip(bug, "Opens StreamLite's bug report form on GitHub (free GitHub account needed) with your StreamLite version and distro filled in. Known problems are listed there too.");
            bug.Click += (s, e) => App.ReportBug();
            DockPanel.SetDock(bug, Dock.Bottom);
            left.Children.Add(brand); left.Children.Add(bug); left.Children.Add(nav);
            var root = new DockPanel();
            DockPanel.SetDock(left, Dock.Left);
            root.Children.Add(left); root.Children.Add(content);
            Content = root;

            core.Changed += () => Dispatcher.UIThread.Post(Refresh);
            new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, e) => Refresh()).Start();
            Refresh();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Closing keeps StreamLite running in the background (the clean mic and 3D audio stay on).
            if (!QuitOnClose && S.CloseAction != "exit" && !e.IsProgrammatic) { e.Cancel = true; Hide(); return; }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            var d = Application.Current.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            if (d != null) d.Shutdown();
        }

        void Refresh() { loading = true; foreach (var r in refreshers) { try { r(); } catch { } } loading = false; }

        // ------------------------------------------------------------------ building blocks

        static Control Scroll(Control c) { return new ScrollViewer { Content = c, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; }

        static StackPanel Page(string title)
        {
            var p = new StackPanel { Margin = new Thickness(24, 18, 24, 24), Spacing = 14 };
            p.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text });
            return p;
        }

        static StackPanel Card(StackPanel page, string title)
        {
            var inner = new StackPanel { Spacing = 10 };
            if (title != null) inner.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text });
            page.Children.Add(new Border { Background = Palette.Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 14), Child = inner });
            return inner;
        }

        static TextBlock Note(string text, IBrush color = null)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Palette.Muted, FontSize = 12.5 };
        }

        ToggleSwitch Toggle(Panel parent, string text, Func<bool> get, Action<bool> set, string help = null)
        {
            var t = new ToggleSwitch { Content = text, OnContent = null, OffContent = null };
            if (help != null) ToolTip.SetTip(t, help);
            t.IsCheckedChanged += (s, e) => { if (!loading) set(t.IsChecked == true); };
            refreshers.Add(() => t.IsChecked = get());
            parent.Children.Add(t);
            return t;
        }

        Slider SliderRow(Panel parent, string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> fmt, string help = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,70") };
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text };
            if (help != null) ToolTip.SetTip(l, help);
            var sl = new Slider { Minimum = min, Maximum = max, VerticalAlignment = VerticalAlignment.Center };
            var v = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Palette.Muted };
            Grid.SetColumn(sl, 1); Grid.SetColumn(v, 2);
            grid.Children.Add(l); grid.Children.Add(sl); grid.Children.Add(v);
            sl.PropertyChanged += (s, e) => { if (e.Property == Slider.ValueProperty) { v.Text = fmt(sl.Value); if (!loading) set(Math.Round(sl.Value)); } };
            refreshers.Add(() => { if (!sl.IsPointerOver) sl.Value = get(); v.Text = fmt(sl.Value); });
            parent.Children.Add(grid);
            return sl;
        }

        static Grid LabeledRow(string label, Control c, string help = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text };
            if (help != null) ToolTip.SetTip(l, help);
            Grid.SetColumn(c, 1);
            grid.Children.Add(l); grid.Children.Add(c);
            return grid;
        }

        sealed class Item { public string Key, Text; public override string ToString() { return Text; } }

        ComboBox DeviceCombo(Func<List<PulseDevice>> list, Func<string> current, Action<string> pick)
        {
            var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            Action fill = () =>
            {
                List<PulseDevice> devs; try { devs = list(); } catch { devs = new List<PulseDevice>(); }
                string cur = current();
                bool same = cb.Items.Count == devs.Count && devs.All(d => cb.Items.OfType<Item>().Any(i => i.Key == d.Name));
                if (!same) { cb.Items.Clear(); foreach (var d in devs) cb.Items.Add(new Item { Key = d.Name, Text = d.Description }); }
                var sel = cb.Items.OfType<Item>().FirstOrDefault(i => i.Key == cur);
                if (sel != null && cb.SelectedItem != sel) cb.SelectedItem = sel;
            };
            cb.DropDownOpened += (s, e) => { loading = true; fill(); loading = false; };
            cb.SelectionChanged += (s, e) => { var it = cb.SelectedItem as Item; if (!loading && it != null && it.Key != current()) pick(it.Key); };
            int tick = 0;
            refreshers.Add(() => { if (tick++ % 20 == 0 && !cb.IsDropDownOpen) fill(); });
            return cb;
        }

        // ------------------------------------------------------------------ Microphone

        Control BuildMic()
        {
            var page = Page("Microphone");
            var dev = Card(page, null);
            Toggle(dev, "Virtual mic (clean microphone)", () => S.VirtualMicOn, v => { S.VirtualMicOn = v; core.ApplyMic(); S.Save(); },
                "Turns the cleaned-up microphone on or off. When on, StreamLite listens to your mic, removes background noise and sends it to \"StreamLite Mic\".");
            dev.Children.Add(LabeledRow("Your microphone", DeviceCombo(Pactl.Sources, () => S.MicId, id => core.SetMic(id)), "The real microphone StreamLite listens to: a USB mic, your headset's mic, a webcam mic..."));
            var info = Note("");
            dev.Children.Add(info);
            refreshers.Add(() =>
            {
                bool clip = S.VirtualMicOn && core.Mic.Chain.Clip.Clipping;
                string fallback = S.VirtualMicOn && S.NoiseRemoval ? core.Mic.Chain.Suppressor.FallbackNote : null;
                bool bt = S.VirtualMicOn && core.Mic.Source != null && core.Mic.Source.StartsWith("bluez_");
                info.Text = core.Error ?? (!S.VirtualMicOn ? "Virtual mic is off." :
                    clip ? "Your microphone is clipping (too loud before StreamLite gets it - no effect can repair that). Turn its gain down on the mic or interface, or its input level in your sound settings." :
                    "In Discord, OBS, Zoom, TeamSpeak or games choose \"StreamLite Mic\" as your microphone.   Status: " + core.Mic.Status +
                    (core.Mic.Dropouts > 0 ? ", " + core.Mic.Dropouts + " dropouts" : "") +
                    (bt ? "\nThis is a Bluetooth headset mic: while it's in use the headset drops to call quality (a Bluetooth limit). A USB or wired mic keeps your headphones in full quality." : "") +
                    (fallback != null ? "\n" + fallback : ""));
                info.Foreground = core.Error != null || clip || bt || fallback != null ? Palette.Warn : Palette.Muted;
            });
            var meters = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            var inBar = new ProgressBar { Minimum = -60, Maximum = 0, Height = 8, Margin = new Thickness(0, 4) };
            var outBar = new ProgressBar { Minimum = -60, Maximum = 0, Height = 8, Margin = new Thickness(0, 4) };
            var l1 = new TextBlock { Text = "Mic level", Foreground = Palette.Muted }; var l2 = new TextBlock { Text = "What apps hear", Foreground = Palette.Muted };
            Grid.SetColumn(inBar, 1); Grid.SetRow(l2, 1); Grid.SetRow(outBar, 1); Grid.SetColumn(outBar, 1);
            meters.Children.Add(l1); meters.Children.Add(inBar); meters.Children.Add(l2); meters.Children.Add(outBar);
            dev.Children.Add(meters);
            refreshers.Add(() => { inBar.Value = Math.Max(-60, core.Mic.InLevelDb); outBar.Value = Math.Max(-60, core.Mic.OutLevelDb); });

            var fx = Card(page, "Noise removal and voice");
            Toggle(fx, "Noise removal", () => S.NoiseRemoval, v => { S.NoiseRemoval = v; core.ApplyMic(); S.Save(); }, "Removes keyboard, fans, PC hum, traffic and room noise from your voice.");
            var engine = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            engine.Items.Add(new Item { Key = NoiseEngines.Ai, Text = "RNNoise - AI voice isolation (light)" });
            engine.Items.Add(new Item { Key = NoiseEngines.DfnCpu, Text = "DeepFilterNet 3 - studio quality (uses more CPU)" });
            engine.Items.Add(new Item { Key = NoiseEngines.Classic, Text = "Classic - statistical (lowest CPU)" });
            engine.SelectionChanged += (s, e) => { var it = engine.SelectedItem as Item; if (!loading && it != null) { S.NsEngine = it.Key; core.ApplyMic(); S.Save(); } };
            refreshers.Add(() => engine.SelectedItem = engine.Items.OfType<Item>().FirstOrDefault(i => i.Key == (S.NsEngine == NoiseEngines.DfnGpu ? NoiseEngines.DfnCpu : S.NsEngine)));
            fx.Children.Add(LabeledRow("Engine", engine, "RNNoise is light and good. DeepFilterNet 3 removes more noise and keeps your voice fuller, at more CPU."));
            // Multi-channel mics (audio interfaces, array mics): which inputs carry the voice.
            var chan = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var chanNote = Note("");
            int chanCount = -1;
            chan.SelectionChanged += (s, e) => { var it = chan.SelectedItem as Item; if (!loading && it != null && it.Key != S.MicChannel) { S.MicChannel = it.Key; core.ApplyMic(); S.Save(); } };
            refreshers.Add(() =>
            {
                int n = core.Mic.InputChannelCount;
                if (n != chanCount)
                {
                    chanCount = n;
                    chan.Items.Clear();
                    chan.Items.Add(new Item { Key = InputChannels.Auto, Text = "Auto (inputs with your voice)" });
                    chan.Items.Add(new Item { Key = InputChannels.Mix, Text = "Mix all inputs" });
                    for (int i = 1; i <= Math.Max(2, n); i++) chan.Items.Add(new Item { Key = i.ToString(), Text = "Input " + i });
                }
                string m = string.IsNullOrEmpty(S.MicChannel) ? InputChannels.Auto : S.MicChannel;
                var sel = chan.Items.OfType<Item>().FirstOrDefault(i => i.Key == m);
                if (sel != null && chan.SelectedItem != sel && !chan.IsDropDownOpen) chan.SelectedItem = sel;
                chan.IsEnabled = n > 1;
                chanNote.Text = n > 1 ? core.Mic.Chain.Channels.Describe() : "Your mic sends one channel.";
            });
            fx.Children.Add(LabeledRow("Mic input", chan, "For audio interfaces and array mics: Auto uses only the inputs that carry your voice (an empty second input would make you 6 dB quieter and add noise; an out-of-phase channel would cancel your voice)."));
            fx.Children.Add(chanNote);
            SliderRow(fx, "Strength", 0, 100, () => S.Strength, v => { S.Strength = (int)v; core.ApplyMic(); S.Save(); }, v => v.ToString("0") + "%", "How far background noise is turned down.");
            Toggle(fx, "Noise gate (silence between words)", () => S.Gate, v => { S.Gate = v; core.ApplyMic(); S.Save(); }, "Mutes what's left of the background while you don't talk.");
            SliderRow(fx, "Gate threshold", -80, -20, () => S.GateThreshold, v => { S.GateThreshold = (int)v; core.ApplyMic(); S.Save(); }, v => v.ToString("0") + " dB");
            Toggle(fx, "Auto level (steady loudness)", () => S.AutoLevel, v => { S.AutoLevel = v; core.ApplyMic(); S.Save(); }, "Lifts a quiet voice and tames a loud one, so friends hear you at the same level.");
            SliderRow(fx, "Volume", -20, 12, () => S.VolumeDb, v => { S.VolumeDb = (int)v; core.ApplyMic(); S.Save(); }, v => (v > 0 ? "+" : "") + v.ToString("0") + " dB");
            Toggle(fx, "Make \"StreamLite Mic\" the default microphone", () => S.MakeDefaultMic, v => { S.MakeDefaultMic = v; core.ApplyMic(); S.Save(); },
                "Apps that use the default microphone then get the clean one automatically.");
            return page;
        }

        // ------------------------------------------------------------------ Headphones

        Control BuildHeadphones()
        {
            var page = Page("Headphones");
            var dev = Card(page, null);
            dev.Children.Add(LabeledRow("Headphones / speakers", DeviceCombo(Pactl.Sinks, () => core.RealOutput, id => core.SetOutput(id)), "Where StreamLite plays the processed sound."));
            Toggle(dev, "Headphone EQ (correction + game, music and chat profiles)", () => S.HeadphoneEq, v => { S.HeadphoneEq = v; core.ApplyOutputs(); },
                "Everything plays through \"StreamLite Headphones (EQ)\": your headphones corrected to neutral plus the profile for what you're doing.");
            var st = Note(""); dev.Children.Add(st);
            refreshers.Add(() => st.Text = S.HeadphoneEq ? "Sound plays through \"StreamLite Headphones (EQ)\" (now the default output).   Status: " + core.Eq.Status : "Off - apps play straight to your headphones.");

            var corr = Card(page, "Headphone correction");
            corr.Children.Add(Note("Flattens your headphones using their measured response (AutoEq: thousands of headphones and earbuds measured on calibrated rigs), so every profile sounds the way it was designed."));
            var name = new TextBlock { Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap };
            corr.Children.Add(name);
            refreshers.Add(() => { var c = S.Profiles.CorrectionFor(core.RealOutput); name.Text = c != null && c.Filters != null && c.Filters.Count > 0 ? c.Name : "None yet for these headphones - find them."; });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var find = new Button { Content = "Find my headphones..." };
            find.Click += async (s, e) =>
            {
                string devName = (Pactl.Sinks().FirstOrDefault(d => d.Name == core.RealOutput) ?? new PulseDevice { Description = "" }).Description;
                var dlg = new HeadphoneFinder(devName);
                await dlg.ShowDialog(this);
                if (dlg.ChosenFilters == null) return;
                S.Profiles.SetCorrection(core.RealOutput, dlg.ChosenName, dlg.ChosenFilters);   // just for these headphones
                S.Save(); core.Reapply();
            };
            row.Children.Add(find);
            var corrOn = Toggle(row, "Use it", () => S.Profiles.ActiveCorrection(core.RealOutput) != null, v => { var c = S.Profiles.CorrectionFor(core.RealOutput); if (c != null) c.On = v; S.Save(); core.Reapply(); });
            corr.Children.Add(row);

            var prof = Card(page, "Profiles");
            prof.Children.Add(Note("Each game gets its own headphone EQ, switched automatically: Music while Spotify plays, Chat while Discord, Zoom, TeamSpeak or Mumble is open, otherwise Movies & videos. Games win."));
            Toggle(prof, "Auto-detect games, music and chat apps", () => S.Profiles.AutoSwitch, v => { S.Profiles.AutoSwitch = v; if (v) S.Profiles.ManualProfile = null; S.Save(); core.Reapply(); });
            var now = new TextBlock { Foreground = Palette.Good };
            prof.Children.Add(LabeledRow("Now using", now));
            var pick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var p in S.Profiles.Profiles) pick.Items.Add(p.Name);
            pick.SelectionChanged += (s, e) =>
            {
                var n = pick.SelectedItem as string;
                if (loading || n == null || n == core.ActiveProfile) return;
                S.Profiles.AutoSwitch = false; S.Profiles.ManualProfile = n; S.Save(); core.Reapply();
            };
            prof.Children.Add(LabeledRow("Profile", pick, "Pick one by hand (turns auto-detect off until you turn it back on)."));
            var basis = Note(""); prof.Children.Add(basis);
            refreshers.Add(() =>
            {
                now.Text = core.ActiveProfile + (core.DetectedExe != null ? "  (" + core.DetectedExe + ")" : S.Profiles.AutoSwitch ? "  (nothing detected)" : "  (picked by hand)");
                if (!pick.IsDropDownOpen) pick.SelectedItem = core.ActiveProfile;
                var p = S.Profiles.Profiles.FirstOrDefault(x => x.Name == core.ActiveProfile);
                basis.Text = p == null ? "" : p.Basis + (string.IsNullOrEmpty(p.Tips) ? "" : "\nTip: " + p.Tips);
            });
            return page;
        }

        // ------------------------------------------------------------------ 3D audio

        Control BuildGame()
        {
            var page = Page("3D Audio");
            var top = Card(page, null);
            Toggle(top, "Game 3D audio", () => S.Game.Enabled, v => { S.Game.Enabled = v; core.ApplyOutputs(); },
                "Turns games' 7.1 surround into 3D sound for your headphones with real measured heads (SADIE II), including true 360-degree placement.");
            var st = Note(""); top.Children.Add(st);
            refreshers.Add(() => st.Text = S.Game.Enabled
                ? "Games play to \"StreamLite Game Audio (7.1)\" (the default output while 3D is on) - set the game's audio to 7.1 / surround. Discord, browsers and music stay out of 3D.   Status: " + core.Game.Status + (core.Game.Processor.Rendering360 ? " (true 360)" : "")
                : "Off. Turn it on, then pick 7.1 / surround in your game's audio settings.");

            var main = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*") };
            var cubeBox = new StackPanel { Spacing = 6 };
            var cube = new CubeView(core.Game.Processor, () => core.Game.Running) { Width = 330, Height = 320 };
            cubeBox.Children.Add(cube);
            var caption = Note("Live sound cube"); cubeBox.Children.Add(caption);
            refreshers.Add(() => caption.Text = core.Game.Processor.Radar.TestSphere ? "Test: " + core.Game.Processor.TestLabel : "Live sound cube - right-drag to turn");
            var controls = new StackPanel { Spacing = 10, Margin = new Thickness(14, 0, 0, 0) };
            Grid.SetColumn(controls, 1);
            main.Children.Add(cubeBox); main.Children.Add(controls);
            page.Children.Add(new Border { Background = Palette.Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 14), Child = main });

            var preset = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var n in GamePresets.Names) preset.Items.Add(n);
            preset.SelectionChanged += (s, e) => { var n = preset.SelectedItem as string; if (!loading && n != null) { GamePresets.Apply(S.Game, n); S.Game.Preset = n; core.PushGameParams(); S.Save(); } };
            refreshers.Add(() => preset.SelectedItem = S.Game.Preset);
            controls.Children.Add(LabeledRow("Preset", preset));
            var head = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            head.Items.Add(new Item { Key = HrtfSet.Kemar, Text = "KEMAR (sharper cues)" });
            head.Items.Add(new Item { Key = HrtfSet.Ku100, Text = "Neumann KU100 (natural)" });
            head.SelectionChanged += (s, e) => { var it = head.SelectedItem as Item; if (!loading && it != null) { S.Game.Hrtf = it.Key; core.PushGameParams(); S.Save(); } };
            refreshers.Add(() => head.SelectedItem = head.Items.OfType<Item>().FirstOrDefault(i => i.Key == S.Game.Hrtf));
            controls.Children.Add(LabeledRow("Head model", head, "Measured dummy heads. Try both with the above/below test and keep the one where up and down sound clearer."));
            Action push = () => { core.PushGameParams(); S.Save(); };
            SliderRow(controls, "Cue sharpening", 0, 100, () => S.Game.CueSharpen, v => { S.Game.CueSharpen = (int)v; push(); }, v => v + "%", "Deepens the height and front/back cues your ears use.");
            SliderRow(controls, "Footstep boost", 0, 100, () => S.Game.FootstepBoost, v => { S.Game.FootstepBoost = (int)v; push(); }, v => v + "%", "Lifts quiet footsteps and gear sounds.");
            SliderRow(controls, "Loud sound tamer", 0, 100, () => S.Game.LoudTamer, v => { S.Game.LoudTamer = (int)v; push(); }, v => v + "%", "Turns down explosions and gunfire so footsteps after them stay audible.");
            SliderRow(controls, "Room", 0, 100, () => S.Game.Room, v => { S.Game.Room = (int)v; push(); }, v => v == 0 ? "Off (dry)" : v + "%");
            Toggle(controls, "True 360° (exact angles)", () => S.Game.Render360, v => { S.Game.Render360 = v; push(); });
            var tests = new WrapPanel();
            foreach (var t in new[] { Tuple.Create("Test footsteps", 0), Tuple.Create("Test 360° walk", 1), Tuple.Create("Test above / below", 2) })
            {
                var b = new Button { Content = t.Item1, Margin = new Thickness(0, 0, 8, 8) };
                int mode = t.Item2;
                b.Click += (s, e) =>
                {
                    var p = core.Game.Processor;
                    if (p.TestRunning) { p.StopTest(); core.PushGameParams(); return; }
                    if (!S.Game.Enabled) { S.Game.Enabled = true; core.ApplyOutputs(); }
                    if (mode == 2) p.StartSphereTest(); else p.StartTest(mode == 1);
                };
                tests.Children.Add(b);
            }
            controls.Children.Add(tests);
            return page;
        }

        // ------------------------------------------------------------------ Settings

        Control BuildSettings()
        {
            var page = Page("Settings");
            var start = Card(page, "Startup");
            Toggle(start, "Start StreamLite when I log in (in the background)", () => Autostart.Enabled, v => Autostart.Set(v));
            Toggle(start, "Quit when I close the window (otherwise it keeps running in the background)", () => S.CloseAction == "exit", v => { S.CloseAction = v ? "exit" : "background"; S.Save(); });
            start.Children.Add(Note("While StreamLite runs in the background your mic stays clean. Start StreamLite again (or click its tray icon) to open this window; use Quit in the tray menu or below to stop it."));
            var quit = new Button { Content = "Quit StreamLite" };
            quit.Click += (s, e) => { QuitOnClose = true; Close(); };
            start.Children.Add(quit);

            var upd = Card(page, "Updates and bug reports");
            Toggle(upd, "Update StreamLite when it starts", () => S.UpdateOnStartup, v => { S.UpdateOnStartup = v; S.Save(); },
                "When StreamLite starts it asks GitHub for the newest release, checks the download (SHA-256) and installs it the way you installed StreamLite (AppImage, tarball, .deb or .rpm), then restarts. Packages ask for your password once.");
            var us = Note(""); upd.Children.Add(us);
            refreshers.Add(() => us.Text = Updater.Status ?? ("You have StreamLite " + Program.Version + "." +
                (Updater.InstallKind == null ? " This copy wasn't installed by a StreamLite package, so updates are only announced."
                : Updater.InstallKind == "arch" ? " Arch package: you're told about updates; rebuild the package to install one." : "")));
            var ub = new WrapPanel();
            var check = new Button { Content = "Check now", Margin = new Thickness(0, 0, 8, 8) };
            check.Click += (s, e) => App.CheckForUpdates(true);
            var report = new Button { Content = "Report a bug", Margin = new Thickness(0, 0, 8, 8) };
            report.Classes.Add("accent");
            ToolTip.SetTip(report, "Opens the bug report form on GitHub with your StreamLite version and distro filled in. Describe what happened and what you expected.");
            report.Click += (s, e) => App.ReportBug();
            var details = new Button { Content = "Copy details", Margin = new Thickness(0, 0, 8, 8) };
            ToolTip.SetTip(details, "Copies your StreamLite version, distro, sound server and audio devices - paste it into the bug report. Nothing is sent anywhere by this button.");
            details.Click += async (s, e) =>
            {
                try
                {
                    var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clip != null) { await clip.SetTextAsync(Linux.Diagnostics.Report(core)); us.Text = "Details copied - paste them into the bug report (Ctrl+V)."; }
                }
                catch (Exception ex) { us.Text = "Couldn't copy: " + ex.Message; }
            };
            ub.Children.Add(check); ub.Children.Add(report); ub.Children.Add(details);
            foreach (var l in new[] { Tuple.Create("Known issues", Updater.IssuesPage), Tuple.Create("What's new", Updater.ReleasesPage) })
            {
                var b = new Button { Content = l.Item1, Margin = new Thickness(0, 0, 8, 8) };
                string url = l.Item2; b.Click += (s, e) => App.Open(url);
                ub.Children.Add(b);
            }
            upd.Children.Add(ub);

            var about = Card(page, "About");
            var ver = Note(""); about.Children.Add(ver);
            refreshers.Add(() => { if (ver.Text.Length == 0) try { ver.Text = "StreamLite " + Program.Version + " for Linux - sound server: " + Pactl.ServerName() + ". Settings: " + AppSettings.Folder; } catch { ver.Text = "StreamLite " + Program.Version; } });
            var links = new WrapPanel();
            foreach (var l in new[] { Tuple.Create("Manual & help", Updater.Manual), Tuple.Create("Credits", Updater.Manual + "#credits"), Tuple.Create("GitHub", Updater.Home) })
            {
                var b = new Button { Content = l.Item1, Margin = new Thickness(0, 0, 8, 8) };
                string url = l.Item2; b.Click += (s, e) => App.Open(url);
                links.Children.Add(b);
            }
            about.Children.Add(links);
            about.Children.Add(Note("Free software under the GNU GPL v3. Noise removal: RNNoise and DeepFilterNet 3. 3D audio: SADIE II HRTFs (University of York). Headphone corrections: AutoEq."));
            return page;
        }
    }

    /// <summary>~/.config/autostart/streamlite.desktop (the freedesktop autostart standard - GNOME, KDE, XFCE...).</summary>
    internal static class Autostart
    {
        static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "autostart", "streamlite.desktop"); } }

        public static bool Enabled { get { return File.Exists(FilePath); } }

        public static void Set(bool on)
        {
            if (!on) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
            string exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath ?? "streamlite";
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, "[Desktop Entry]\nType=Application\nName=StreamLite\nComment=Clean mic, 3D game audio and headphone EQ\nExec=\"" + exe + "\" --minimized\nIcon=streamlite\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
        }
    }
}
