using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Drawing;

using System.Drawing.Drawing2D;

using System.IO;

using System.Linq;

using System.Net;

using System.Threading;

using System.Windows.Forms;

using StreamLite.Audio;

using StreamLite.Interop;



namespace StreamLite.UI

{

    /// <summary>"3D Audio" page: virtual surround for games, directional EQ, footstep booster and the sound radar.</summary>

    internal sealed class GameAudioPage : Panel

    {

        readonly AppCore core;

        readonly ToggleSwitch onToggle, limiterToggle, stereoVirtToggle, linkToggle;

        readonly DarkCombo sinkCombo, phonesCombo, presetCombo, hrtfCombo;

        readonly Label status, presetInfo, dirTitle, dynLive, dirGainVal, radarCaption;

        readonly FlatButton installBtn, testBtn;

        readonly SoundRadarView radar;
        Card fineCard;
        readonly Label sendsNote;

        static readonly string[] ChannelNames = { "front L", "front R", "center", "bass", "rear L", "rear R", "side L", "side R" };

        /// <summary>What the game sends right now, which channels carry sound, and what that means for direction.</summary>
        string Sends(GameAudioEngine e)
        {
            if (core.SpatialWarning != null) return core.SpatialWarning;
            if (!e.Running || e.Processor.Current == SpatialProcessor.Source.Silent || e.Status.StartsWith("Waiting"))
                return "Set games to 7.1 / Speakers: that's how they hand StreamLite each sound's direction, rendered in true 360 degrees. Games with\ntheir own HRTF (CS2, Valorant...) go straight to your headphones (set per game in Headphones > Game profiles).";
            var on = Enumerable.Range(0, SpatialProcessor.Slots).Where(i => e.ChannelDb[i] > -60).ToList();
            bool surround = on.Any(i => i != SpatialProcessor.FL && i != SpatialProcessor.FR);
            if (!surround)
                return core.S.Game.Enabled && core.S.Game.VirtualizeStereo
                    ? "The game sends stereo; \"Play stereo in 3D\" plays it through two virtual speakers in front of you. Left / right only -\nfor true 360 degrees, set the game to 7.1 / Speakers (or, if it has its own 3D, switch \"Play stereo in 3D\" off)."
                    : "The game sends stereo: either its own 3D (HRTF - that can carry height) or plain stereo. StreamLite keeps it as it is.\nFor true 360 degrees from StreamLite, set the game to 7.1 / Speakers.";
            bool sides = on.Contains(SpatialProcessor.SL) || on.Contains(SpatialProcessor.SR);
            string mix = "The game sends a " + (sides ? "7.1" : "5.1") + " channel mix (" + string.Join(", ", on.Select(i => ChannelNames[i])) + ")";
            if (e.Plain) return mix + ": 3D audio is off, so it's folded down to stereo.\nSwitch Game 3D audio on to hear each sound at its angle around you.";
            return mix + ": StreamLite places each sound at its exact\nangle around you. A channel mix has no height - above / below only come from games with their own 3D.";
        }

        readonly EqEditor eq;

        readonly FlatSlider dirGain;

        readonly List<FlatButton> dirButtons = new List<FlatButton>();

        readonly List<Action> loaders = new List<Action>();

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 };

        string dir = "behind";

        bool loading;

        int tick;

        static readonly string[] DirLabels = { "Front", "Center", "Left", "Right", "Behind", "Bass" };



        GameAudioSettings G { get { return core.S.Game; } }



        public GameAudioPage(AppCore core)

        {

            this.core = core;

            Location = new Point(Theme.Px(28), Theme.Px(18));

            Size = new Size(Theme.Px(690), Theme.Px(1758));

            BackColor = Theme.Bg;

            Theme.Label(this, "3D Audio", 0, 0, Theme.Title);

            Theme.Help(Theme.Label(this, "Game 3D audio", 530, 10, Theme.Bold, Theme.Muted),

                "Turns true 360\u00B0 audio on: games play into StreamLite's game device, StreamLite places every sound at its exact angle around your head with measured head responses and plays it on your headphones.\nWhen it's off (or the app quits), Windows playback goes straight back to your headphones.");

            onToggle = Place(this, new ToggleSwitch(), 640, 8);

            onToggle.CheckedChanged += (s, e) => { if (loading) return; G.Enabled = onToggle.Checked; core.RestartGameAudio(); RefreshStatus(); };



            // ---- Devices + status

            var dev = NewCard(54, 196);

            Theme.Help(Theme.Label(dev, "Game audio device", 20, 20, Theme.Bold),

                "A virtual 7.1 speaker device that games play into (made the Windows default while 3D audio is on). Normally \"" + AppCore.GameOutName + "\" (VB-Audio Hi-Fi Cable) - never the cable your microphone uses, or friends would hear your game.");

            sinkCombo = Place(dev, new DarkCombo(), 200, 16, 300);

            sinkCombo.DropDown += (s, e) => Fill(sinkCombo, core.GameSinkCandidates(), core.GameSinkId);

            sinkCombo.SelectionChangeCommitted += (s, e) => { var d = sinkCombo.SelectedItem as AudioDeviceInfo; if (d == null) return; G.SinkId = d.Id; core.RestartGameAudio(); RefreshStatus(); };

            installBtn = Place(dev, new FlatButton("Install Hi-Fi Cable (free)", true), 510, 14, 160);

            Theme.Help(installBtn, "Downloads VB-Audio Hi-Fi Cable (free, signed driver by VB-Audio, the makers of VB-CABLE) and installs it. Windows will ask for permission.\nIt adds a separate virtual device for game sound, so your mic cable stays clean.");

            installBtn.Click += (s, e) => InstallHiFiCable();

            Theme.Help(Theme.Label(dev, "Headphones", 20, 58, Theme.Bold), "Where the 3D sound is played - your real headphones, headset or earbuds. Your headphone EQ (Equalizer APO) still applies after the 3D processing.");

            phonesCombo = Place(dev, new DarkCombo(), 200, 54, 300);

            phonesCombo.DropDown += (s, e) => Fill(phonesCombo, core.Outputs().Where(d => !AudioDevices.LooksVirtual(d)).ToList(), core.GamePhonesId);

            phonesCombo.SelectionChangeCommitted += (s, e) => { var d = phonesCombo.SelectedItem as AudioDeviceInfo; if (d == null) return; G.HeadphonesId = d.Id; core.RestartGameAudio(); RefreshStatus(); };

            testBtn = Place(dev, new FlatButton("Test footsteps"), 510, 52, 160);

            Theme.Help(testBtn, "Footsteps walk a full circle around you three times: at ear level, on the floor above you and on the floor below you - every step from its exact angle and height (measured head responses, true 360\u00B0). Works without a game running; the sound sphere lights up where each step is.\nGames that send 7.1 carry no height, so in games you hear above/below only from games with their own 3D audio; cue sharpening deepens it.");

            testBtn.Click += (s, e) =>

            {

                if (StopTestIfRunning()) return;

                if (!core.Game.Running) { G.Enabled = true; onToggle.SetSilently(true); core.RestartGameAudio(); }

                core.Game.Processor.StartRingsTest();

                core.PushGameParams();   // the test always uses your game settings, even in natural mode

            };

            status = new Label { ForeColor = Theme.Text, Font = Theme.Body, BackColor = Color.Transparent, Location = new Point(Theme.Px(20), Theme.Px(94)), Size = new Size(Theme.Px(650), Theme.Px(40)), UseMnemonic = false };

            dev.Controls.Add(status);

            sendsNote = Theme.Label(dev, "Set games to 7.1 / Speakers: that's how they hand StreamLite each sound's direction, rendered in true 360°. Games with\ntheir own HRTF (CS2, Valorant...) go straight to your headphones (set per game in Headphones > Game profiles).", 20, 140, Theme.Small, Theme.Muted);
            sendsNote.Size = new Size(Theme.Px(650), Theme.Px(44));
            Theme.Help(sendsNote, "What StreamLite can get from a game: a channel mix - stereo, 5.1 or 7.1 - in which the game pans each sound between speakers. From 5.1 / 7.1 StreamLite works out each sound's exact angle around you and renders it with measured head responses (true 360 degrees). A channel mix carries no height, so above / below can't come through it; games with their own 3D (HRTF) can do height and play straight to your headphones.\nSome games can also send 'audio objects' with full 3D positions - but Windows only hands those to spatial sound engines registered with Microsoft (Windows Sonic, Dolby Atmos, DTS), not to other apps, so the 7.1 path is what works with StreamLite.");



            // ---- Radar + main controls

            var main = NewCard(264, 444);

            radar = new SoundRadarView(core) { Location = new Point(Theme.Px(14), Theme.Px(14)), Size = new Size(Theme.Px(300), Theme.Px(300)) };

            main.Controls.Add(radar);

            Theme.Help(radar, "Sound sphere: you're the white dot in the middle, nose to the FRONT. Blue lines above you, the green EAR LEVEL ring, orange lines below.\nRight-drag to turn the sphere and look from any side; right double-click puts the view back.\nGreen bars on the ear-level edge = how loud each direction is (in True 360\u00B0 mode: every angle; otherwise each virtual speaker); dots = sudden sounds (footsteps, shots), fading over 2 seconds. The footsteps test lights up the floor and spot each step comes from.\nWith stereo games only left/right is known (front and back can't be separated in stereo).");

            radarCaption = Theme.Label(main, "Live sound radar", 20, 322, Theme.Small, Theme.Muted, 300);

            dynLive = Theme.Label(main, "", 20, 342, Theme.Small, Theme.Muted, 300);
            var personal = Place(main, new FlatButton("Tune 3D to my ears", true), 20, 362, 170);
            Theme.Help(personal, "Tuning (about 4 minutes): a blind listening test on a sphere - footsteps from in front, behind, left, right, above or below you, and you snap the view to where you heard them and click. It finds which of 20 measured heads your ears place sounds with best, then tunes your head size, cue sharpening and floor contrast - and keeps it all for these headphones.");
            personal.Click += (s, e) => OpenPersonalize3D();
            // Practice (4.1): your settings as they are, nothing tuned - as long as you like.
            var practice = Place(main, new FlatButton("Practice"), 200, 362, 120);
            Theme.Help(practice, "Practice: footsteps from all six directions with your 3D settings as they are - nothing is tuned or changed. Each answer is shown right away; your score keeps count. Go as long as you like, Esc ends it.");
            practice.Click += (s, e) => OpenPractice3D();
            // Open sphere (3.5.7): no walls, no muffling.
            var openToggle = Place(main, new ToggleSwitch(), 20, 404);
            var openLabel = Theme.Label(main, "Open sphere (no walls)", 74, 404, Theme.Bold);
            Responsive.Fixed(openLabel); Responsive.Fixed(openToggle);
            Theme.Help(openLabel,
                "Every sound on an open sphere around you, nothing in between: no room reflections, and footsteps the game muffled behind a wall, a floor or a crate get their treble back (up to 12 dB over 1.8 kHz, on every speaker alike, so where they are doesn't move). Steps above or below you stay crisp instead of dull.\n" +
                "StreamLite only hears the sound the game sends: how much quieter the game makes a sound behind a wall, and whether a 7.1 mix carries height, is the game's.\nLoud sounds and films are left alone. Off: the Room slider adds its studio room again.");
            openToggle.CheckedChanged += (s, e) => { if (loading) return; G.OpenSphere = openToggle.Checked; core.PushGameParams(); LoadAll(); };
            loaders.Add(() => openToggle.SetSilently(G.OpenSphere));



            int x = 330;

            Theme.Help(Theme.Label(main, "Preset", x, 18, Theme.Bold), "Starting points for the directional EQ and footstep settings. Fine-tune everything below afterwards.");

            presetCombo = Place(main, new DarkCombo(), x + 80, 14, 260);

            presetCombo.Items.AddRange(GamePresets.Names);

            presetCombo.SelectionChangeCommitted += (s, e) =>

            {

                GamePresets.Apply(G, (string)presetCombo.SelectedItem);

                core.PushGameParams(); LoadAll();

            };

            presetInfo = new Label { ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent, Location = new Point(Theme.Px(x), Theme.Px(46)), Size = new Size(Theme.Px(340), Theme.Px(58)), UseMnemonic = false };

            main.Controls.Add(presetInfo);

            loaders.Add(() => { presetCombo.SelectedItem = G.Preset; presetInfo.Text = GamePresets.Describe(G.Preset); });



            Theme.Help(Theme.Label(main, "Head model", x, 112, Theme.Bold),

                "Which measured head the 3D sound is made from (SADIE II database, University of York), every one at its full measurement (every direction, 48 kHz, 24-bit):\n- Neumann KU100: very neutral, best 'outside your head' feel.\n- KEMAR: a mannequin with sharper ear cues.\n- Human heads 3-20: 18 real people. Ears differ - the one closest to yours places sounds best. Personalize 3D finds it.\n- Custom: any HeSuVi / Impulcifer 14- or 16-channel .wav (e.g. your own Impulcifer measurement or a HeSuVi preset).\n" +
                "After Personalize 3D the heads are listed best first, each with how accurately you placed footsteps with it (on these headphones).");

            hrtfCombo = Place(main, new DarkCombo(), x + 100, 108, 240);
            hrtfCombo.DropDownWidth = Theme.Px(320);

            // Best first with your accuracy after Personalize 3D (4.1.2); the usual order before it.
            var heads = new List<string>();
            Action fillHeads = () =>
            {
                var all = HrtfSet.Heads();
                var score = core.HeadScores(core.GamePhonesId ?? G.HeadphonesId);
                heads.Clear();
                heads.AddRange(all.Where(score.ContainsKey).OrderByDescending(h => score[h]).Concat(all.Where(h => !score.ContainsKey(h))));
                hrtfCombo.Items.Clear();
                foreach (var h in heads)
                {
                    double a;
                    hrtfCombo.Items.Add((h == HrtfSet.Ku100 ? "Neumann KU100 (neutral)" : h == HrtfSet.Kemar ? "KEMAR (sharper cues)" : HrtfSet.DisplayName(h)) + (score.TryGetValue(h, out a) ? "  -  " + Sphere3D.Percent(a) : ""));
                }
                hrtfCombo.Items.Add("Custom HeSuVi file...");
            };
            Func<int> headIndex = () => G.Hrtf == HrtfSet.Custom ? heads.Count : Math.Max(0, heads.IndexOf(G.Hrtf));

            loaders.Add(() => { fillHeads(); hrtfCombo.SelectedIndex = headIndex(); });

            hrtfCombo.SelectionChangeCommitted += (s, e) =>

            {

                if (hrtfCombo.SelectedIndex == heads.Count) { if (!PickCustom()) { loading = true; hrtfCombo.SelectedIndex = headIndex(); loading = false; } }

                else { G.Hrtf = heads[Math.Max(0, hrtfCombo.SelectedIndex)]; G.HeadSize = 100; }
                core.RememberPersonal3D();

                core.PushGameParams();

            };

            // ---- Fine-tune 3D (4.1: its own card, folded away until you want it)
            var fine = NewCard(722, 190);
            fineCard = fine;
            var fineTitle = Theme.Help(Theme.Label(fine, "Fine-tune 3D", 20, 16, Theme.Section), "The settings 3D presets and Personalize 3D set for you - adjust them by hand here.");

            Row(fine, 20, 56, "Room", 0, 150, () => G.Room, v => G.Room = v, v => G.OpenSphere ? "Off" : v == 0 ? "Off (dry)" : v + "%",   // open sphere: no room (its help says why; the longer text didn't fit)

                "Adds the reflections of a real studio room (measured with the same head). A little room makes sound feel outside your head instead of inside it.\n0 = completely dry and most precise, 15-30% = natural, more = spacious.\nOnly with Open sphere off - the open sphere has no walls to reflect from.");

            Row(fine, 20, 100, "Cue sharpening", 0, 100, () => G.CueSharpen, v => { G.CueSharpen = v; core.RememberPersonal3D(); }, v => v == 0 ? "Off" : v + "%",

                "Height and front/back cue sharpener. Your ears judge above/below and front/behind from tiny peaks and dips between 4 and 14 kHz.\nThis deepens them, for game HRTFs and StreamLite's 3D alike. 30-50% helps a lot; very high can sound slightly metallic.");

            Row(fine, 20, 144, "Footstep boost", 0, 100, () => G.FootstepBoost, v => G.FootstepBoost = v, v => v == 0 ? "Off" : v + "%",

                "Lifts quiet sounds - footsteps, reloads, distant movement - by up to 15 dB, mostly in the 180 Hz - 5 kHz footstep range.\nWorks across all directions together, so it never changes where a sound seems to be.");

            Row(fine, 350, 56, "Loud sound tamer", 0, 100, () => G.LoudTamer, v => G.LoudTamer = v, v => v == 0 ? "Off" : v + "%",

                "Pulls down gunfire, explosions and other very loud sounds so they don't drown out footsteps (and save your ears).");

            Row(fine, 350, 100, "Reaction speed", 0, 100, () => G.DynSpeed, v => G.DynSpeed = v, v => v + "%",

                "How fast the booster lets go after a loud sound. Faster = quiet footsteps come back sooner after a shot; slower = smoother, less 'pumping'.");

            Row(fine, 350, 144, "Floor contrast", 0, 100, () => G.FloorContrast, v => { G.FloorContrast = v; core.RememberPersonal3D(); }, v => v == 0 ? "Off" : v + "%",
                "How clearly the floor above and the floor below stand apart from ear level. Measured head responses alone place height only for ears like the dummy head's - most people hear every floor at ear level. This adds cues that work for nearly everyone: the floor above sounds airier (a lift around 8 kHz, which ears read as overhead), the floor below with the ear's notches lower - nothing muffled. Every floor stays equally loud.\nIt works where StreamLite knows the height: the footsteps test and the listening test. In games, height comes from the game itself: games with their own 3D (HRTF) carry it in their sound (Cue sharpening deepens it); a 7.1 mix has no height at all.");
            var naturalToggle = Place(main, new ToggleSwitch(), x, 158);

            var naturalLabel = Theme.Label(main, "Natural sound for videos and films", x + 54, 158, Theme.Bold);

            Responsive.Fixed(naturalLabel); Responsive.Fixed(naturalToggle);   // label stays beside its switch at any width

            Theme.Help(naturalLabel,

                "When no game is running (or a film/story profile is active), footstep boost, the loud-sound tamer, cue sharpening and the directional EQ pause, so videos, films and story games sound the way they were mixed - still in 3D. They come back the moment a competitive game starts.");

            naturalToggle.CheckedChanged += (s, e) => { if (loading) return; G.NaturalWhenNoGame = naturalToggle.Checked; core.PushGameParams(); RefreshStatus(); };

            loaders.Add(() => naturalToggle.SetSilently(G.NaturalWhenNoGame));



            // ---- Output, stereo games, apps kept out of 3D

            var lay = NewCard(926, 214);

            var layTitle = Theme.Help(Theme.Label(lay, "Output & apps", 20, 16, Theme.Section),
                "Surround games are always rendered in true 360°: every sound is placed at the exact angle the game meant, with the measured head response for that angle (0.25° steps) - no fixed virtual speakers. Wide ambience stays wide.\nIt runs at the lowest delay your PC can keep up with, by itself: if it ever hears a dropout it adds a little safety and lets it go again later.");

            Row(lay, 20, 50, "Master volume", -20, 12, () => G.MasterDb, v => G.MasterDb = v, v => (v > 0 ? "+" : "") + v + " dB",

                "Overall volume of the 3D sound. The limiter keeps boosts from clipping.");

            limiterToggle = Place(lay, new ToggleSwitch(), 20, 96);

            Theme.Help(Theme.Label(lay, "Limiter", 74, 96, Theme.Bold), "Prevents distortion when boosts and loud sounds add up. Leave it on.");

            limiterToggle.CheckedChanged += (s, e) => { if (loading) return; G.Limiter = limiterToggle.Checked; core.PushGameParams(); };

            loaders.Add(() => limiterToggle.SetSilently(G.Limiter));

            Theme.Label(lay, "Stereo games", 360, 16, Theme.Bold, Theme.Muted);

            Row(lay, 360, 42, "Width", 50, 200, () => G.Width, v => G.Width = v, v => v + "%",

                "Stereo games only: widens (above 100%) or narrows the left/right image. Wider makes off-center sounds easier to place.");

            stereoVirtToggle = Place(lay, new ToggleSwitch(), 360, 96);

            Theme.Help(Theme.Label(lay, "Play stereo in 3D", 414, 96, Theme.Bold),

                "For stereo games WITHOUT their own 3D audio: places their sound in front of you, outside your head, with the same true 360° renderer.\nLeave off for games that already use HRTF/headphone mode.");

            stereoVirtToggle.CheckedChanged += (s, e) => { if (loading) return; G.VirtualizeStereo = stereoVirtToggle.Checked; core.PushGameParams(); };

            loaders.Add(() => stereoVirtToggle.SetSilently(G.VirtualizeStereo));

            // "16D" (3.5.6; 4.1: for music apps or every app): the sound slowly circling your head.
            Theme.Help(Theme.Label(main, "16D audio", x, 202, Theme.Bold),
                "The sound slowly circles your head - \"16D audio\" on any song or video.\nMusic apps: Spotify and the Music profile's other apps, while music plays.\nAll apps: everything but voice chat (Discord, Zoom...).\nOnly while no game runs - a game always gets normal 3D. Also on a hotkey (Hotkeys page).");
            var mode16 = Place(main, new DarkCombo(), x + 100, 198, 240);
            mode16.Items.AddRange(new object[] { "Off", "Music apps", "All apps" });
            mode16.SelectionChangeCommitted += (s, e) => { G.Mode16D = mode16.SelectedIndex == 1 ? "music" : mode16.SelectedIndex == 2 ? "all" : "off"; G.Music16D = G.Mode16D != "off"; core.SaveSoon(); core.RouteBypassApps(); core.PushGameParams(); };
            loaders.Add(() => mode16.SelectedIndex = G.Mode16D == "music" ? 1 : G.Mode16D == "all" ? 2 : 0);
            // Which apps play in 3D (4.1.2): only games - plus the apps you list - or every app but the ones you list.
            Theme.Help(Theme.Label(lay, "Apps in 3D", 20, 138, Theme.Bold),
                "Only games (the default): games play in 3D - a game profile's, one Windows' Game Bar knows, or one from Steam, Epic, GOG, Xbox, Riot, EA, Ubisoft... Every other app (voice chat, music, browsers, videos) plays straight to your headphones, as it is. Add apps below to hear them in 3D too, e.g. discord.\n" +
                "Every app: everything plays in 3D but the apps listed below.\n" +
                "Program names without .exe, separated by commas. Changes apply within a few seconds to running apps.");
            var appsMode = Place(lay, new DarkCombo(), 130, 134, 340);
            appsMode.Items.AddRange(new object[] { "Only games, and these apps:", "Every app but these:", "Every app" });
            var bypassBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Body, Location = new Point(Theme.Px(20), Theme.Px(172)), Width = Theme.Px(650) };
            lay.Controls.Add(bypassBox);
            Action showApps = () =>
            {
                bool games = appsMode.SelectedIndex == 0;
                bypassBox.Enabled = appsMode.SelectedIndex != 2;
                bypassBox.Text = games ? G.Also3DApps ?? "" : G.BypassApps ?? "";
                Theme.Help(bypassBox, games ? "Apps that play in 3D too (e.g. discord, spotify). Every other app that isn't a game plays as it is." : "Apps that skip 3D audio (e.g. spotify, discord, chrome).");
            };
            appsMode.SelectionChangeCommitted += (s, e) =>
            {
                G.GamesOnly = appsMode.SelectedIndex == 0;
                if (!G.GamesOnly) G.BypassEnabled = appsMode.SelectedIndex == 1;
                loading = true; showApps(); loading = false;
                core.SaveSoon(); core.RouteBypassApps();
            };
            loaders.Add(() => { appsMode.SelectedIndex = G.GamesOnly ? 0 : G.BypassEnabled ? 1 : 2; showApps(); });
            bypassBox.TextChanged += (s, e) => { if (loading) return; if (G.GamesOnly) G.Also3DApps = bypassBox.Text; else G.BypassApps = bypassBox.Text; core.SaveSoon(); };
            bypassBox.Leave += (s, e) => core.RouteBypassApps();
            // Direct to headphones (built in from 4.1): StreamLite plays to them exclusively (past Windows' mixer) and mixes the apps itself.
            Theme.Help(Theme.Label(main, "Direct to headphones (built in)", x, 246, Theme.Bold),
                "Normally Windows mixes every app's sound in 10 ms steps before it reaches your headphones. With this on, StreamLite plays straight to them in their own small steps (3 ms on most): game sound reaches you about 7 to 10 ms sooner.\n" +
                "StreamLite is then the only thing playing to your headphones and mixes every app itself. The apps listed above (voice chat, music, browsers) play through another output of this PC, which StreamLite mutes meanwhile, and reach you as they are - about 40 ms after the game, which you won't notice in calls or music.\n" +
                "While it's on, no other app can use your headphones: an app set to play to them in its own settings (Discord's output, a browser) gets no sound - it needs \"Default\" there instead. If your headphones can't do it, StreamLite simply plays the normal way (Health says why).\n" +
                "Discord screen shares: sharing an app's window sends just that app's sound, and a whole-screen share sends every app's sound once - StreamLite's own copy never ends up in it.");
            var directNote = Theme.Label(main, "", x, 270, Theme.Small, Theme.Muted, 340);
            var directTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            directTimer.Tick += (s, e) =>
            {
                if (!Visible) return;
                string st = core.DirectStatus();
                directNote.Text = st.StartsWith("exclusive") ? "On: " + core.Game.ExclusivePeriodMs.ToString("0.#") + " ms steps" + (core.SideOutputName != null ? ", voice chat/music via " + core.SideOutputName : "") : st.StartsWith("not possible") ? "Normal output right now: see Health" : !core.Game.Running ? "Starts with the sound" : "Starting...";
            };
            directTimer.Start();



            // ---- Directional EQ

            var deq = NewCard(1154, 590);

            var deqTitle = Theme.Help(Theme.Label(deq, "Directional EQ", 20, 16, Theme.Section),

                "A separate volume and EQ for sounds from each direction. Surround games: applied per speaker before it's placed around you.\nStereo games: Left/Center/Right are applied by where each sound sits in the stereo image (Front/Behind need surround).");

            for (int i = 0; i < SpatialProcessor.Dirs.Length; i++)

            {

                string d = SpatialProcessor.Dirs[i];

                var b = Place(deq, new FlatButton(DirLabels[i]), 160 + i * 74, 12, 70);

                b.Click += (s, e) => SelectDir(d);

                dirButtons.Add(b);

                Responsive.Fixed(b);

                Theme.Help(b, DirHelp(d));

            }

            dirTitle = Theme.Label(deq, "", 20, 54, Theme.Bold, Theme.Text, 170);

            dirGain = Place(deq, new FlatSlider { Minimum = -24, Maximum = 24, DefaultValue = 0 }, 200, 52, 300);

            dirGainVal = Theme.Label(deq, "", 510, 54, Theme.Body, Theme.Text, 60);

            Theme.Help(dirGain, "Volume of sounds from this direction (-12 to +12 dB). Double-click to reset.");

            dirGain.ValueChanged += (s, e) =>

            {

                dirGainVal.Text = FormatDb(dirGain.Value / 2.0);

                if (loading) return;

                G.DirGain[dir] = dirGain.Value / 2.0;

                MirrorSides();

                core.PushGameParams();

            };

            linkToggle = Place(deq, new ToggleSwitch(), 570, 52);

            Theme.Help(Theme.Label(deq, "Link L/R", 620, 54, Theme.Small, Theme.Muted), "When on, editing Left also sets Right (and vice versa), so both sides stay identical.");

            linkToggle.CheckedChanged += (s, e) => { if (loading) return; G.LinkSides = linkToggle.Checked; MirrorSides(); core.PushGameParams(); };

            loaders.Add(() => linkToggle.SetSilently(G.LinkSides));

            eq = new EqEditor(DirPresets(), false, false, false) { Location = new Point(Theme.Px(5), Theme.Px(88)) };

            eq.Changed += (s, e) => { MirrorSides(); core.PushGameParams(); };

            eq.UseFixedPreampNote("Boosts here are relative to other directions; the 3D limiter keeps the total from clipping.");

            deq.Controls.Add(eq);



            Height = deq.Bottom + Theme.Px(20);
            // 4.1: the everyday settings first - fine-tuning, output and the directional EQ fold away until you open them.
            Fold.Make(fine, fineTitle, "3d.fine", core.S.OpenFolds, core.SaveSoon);
            Fold.Make(lay, layTitle, "3d.output", core.S.OpenFolds, core.SaveSoon, true);
            Fold.Make(deq, deqTitle, "3d.direq", core.S.OpenFolds, core.SaveSoon, true);
            Fold.Settle(this);

            timer.Tick += (s, e) => Tick();

            VisibleChanged += (s, e) => { if (Visible) { LoadAll(); timer.Start(); } else timer.Stop(); };

        }



        static string DirHelp(string d)

        {

            switch (d)

            {

                case "front": return "Sounds in front of you (front-left and front-right speakers).";

                case "center": return "Sounds straight ahead (center channel; in stereo games: sounds in the middle of the image).";

                case "left": return "Sounds to your left (left side speaker; in stereo games: sounds panned left).";

                case "right": return "Sounds to your right (right side speaker; in stereo games: sounds panned right).";

                case "behind": return "Sounds behind you (both rear speakers). Boosting 1 kHz and 8-16 kHz here makes 'behind' more obvious (Blauert's back bands).";

                default: return "The bass/LFE channel (explosions, rumble). It isn't placed in any direction. Turning it down makes footsteps easier to hear.";

            }

        }



        static Dictionary<string, double[]> DirPresets()

        {

            return new Dictionary<string, double[]>

            {

                { "Flat", new double[10] },

                { "Footsteps", new double[] { -5, -4, -2, 0, 0, 1, 3, 4, 2, 0 } },

                { "Behind cues (1k + air)", new double[] { 0, 0, 0, 0, -1, 4, 1, -2, 3, 4 } },

                { "Front cues (400 + 4k)", new double[] { 0, 0, 0, 1, 3, -1, 0, 3, 0, 0 } },

                { "Less rumble", new double[] { -8, -6, -3, -1, 0, 0, 0, 0, 0, 0 } },

                { "Brighter", new double[] { 0, 0, 0, 0, 0, 0, 1, 3, 4, 3 } },

            };

        }



        void SelectDir(string d)

        {

            dir = d;

            int i = Array.IndexOf(SpatialProcessor.Dirs, d);

            for (int k = 0; k < dirButtons.Count; k++) dirButtons[k].BackColor = k == i ? Theme.AccentDim : Theme.Field;

            dirTitle.Text = "Sounds from: " + DirLabels[i];

            loading = true;

            double g; G.DirGain.TryGetValue(d, out g);

            dirGain.Value = (int)Math.Round(g * 2);

            dirGainVal.Text = FormatDb(g);

            loading = false;

            eq.SetProfile(G.DirEq[d]);

        }



        static string FormatDb(double v) { return (v > 0 ? "+" : "") + v.ToString("0.#") + " dB"; }



        void MirrorSides()

        {

            if (!G.LinkSides || (dir != "left" && dir != "right")) return;

            string other = dir == "left" ? "right" : "left";

            G.DirEq[other] = G.DirEq[dir].Clone();

            G.DirEq[other].AutoPreamp = false;

            G.DirGain[other] = G.DirGain[dir];

        }



        bool PickCustom()

        {

            using (var dlg = new OpenFileDialog { Title = "Load HeSuVi / Impulcifer HRIR (14 or 16 channel WAV)", Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*" })

            {

                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return false;

                try

                {

                    var set = HrtfSet.LoadHeSuVi(dlg.FileName);

                    G.CustomHrir = dlg.FileName; G.Hrtf = HrtfSet.Custom;

                    DarkMessage.Show(FindForm(), "Loaded " + set.Name + " (" + (set.Taps / 48.0).ToString("0") + " ms responses, 7 speakers).", "Custom HRIR", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return true;

                }

                catch (Exception ex)

                {

                    DarkMessage.Show(FindForm(), ex.Message, "Could not load that file", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    return false;

                }

            }

        }



        // ---- helpers



        Card NewCard(int y, int h)

        {

            var c = new Card { Location = new Point(0, Theme.Px(y)), Size = new Size(Theme.Px(690), Theme.Px(h)) };

            Controls.Add(c);

            return c;

        }



        static T Place<T>(Control parent, T c, int x, int y, int w = 0) where T : Control

        {

            c.Location = new Point(Theme.Px(x), Theme.Px(y));

            if (w > 0) c.Width = Theme.Px(w);

            parent.Controls.Add(c);

            return c;

        }



        void Row(Control parent, int x, int y, string name, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string help)

        {

            Theme.Help(Theme.Label(parent, name, x, y + 2, Theme.Body), help);

            var sl = Place(parent, new FlatSlider { Minimum = min, Maximum = max, DefaultValue = get() }, x + 120, y, 150);

            var val = Theme.Label(parent, "", x + 278, y + 2, Theme.Small, Theme.Muted, 56);

            Theme.Help(sl, help);

            sl.ValueChanged += (s, e) =>

            {

                val.Text = fmt(sl.Value);

                if (loading) return;

                set(sl.Value);

                core.PushGameParams();

            };

            loaders.Add(() => { sl.Value = get(); val.Text = fmt(get()); });

        }



        static void Fill(DarkCombo combo, List<AudioDeviceInfo> list, string selectedId)

        {

            combo.BeginUpdate();

            combo.Items.Clear();

            foreach (var d in list) combo.Items.Add(d);

            combo.SelectedItem = list.FirstOrDefault(d => d.Id == selectedId);

            combo.EndUpdate();

        }



        public void LoadAll()

        {

            loading = true;

            onToggle.SetSilently(G.Enabled);

            foreach (var l in loaders) l();

            var outs = core.Outputs();   // one device scan for the whole page
            var sinks = core.GameSinkCandidates(outs);
            haveSink = sinks.Count > 0; haveSinkAt = Environment.TickCount;

            Fill(sinkCombo, sinks, core.GameSinkId);

            Fill(phonesCombo, outs.Where(d => !AudioDevices.LooksVirtual(d)).ToList(), core.GamePhonesId);

            loading = false;

            SelectDir(dir);

            RefreshStatus();

        }



        bool haveSink; int haveSinkAt = int.MinValue;

        void RefreshStatus()

        {

            var e = core.Game;

            // The device scan at most every few seconds (this runs about once a second while the page is open).
            if (unchecked(Environment.TickCount - haveSinkAt) > 4000 || haveSinkAt == int.MinValue) { haveSink = core.GameSinkCandidates().Count > 0; haveSinkAt = Environment.TickCount; }

            installBtn.Visible = !haveSink;

            string s;

            if (core.GameError != null) { s = core.GameError; status.ForeColor = Theme.Warn; }

            else if (!G.Enabled && core.MultiOut) { s = "3D is off: your " + (core.S.ExtraOutputs.Count + 1) + " outputs play plain stereo (Speakers & Headphones > Outputs).\nTurn on Game 3D audio (top right) to hear games in 3D on all of them."; status.ForeColor = Theme.Muted; }
            else if (!G.Enabled) { s = "Off. Turn on Game 3D audio (top right) to hear games in 3D."; status.ForeColor = Theme.Muted; }

            else

            {

                status.ForeColor = Theme.Text;

                string src;

                switch (e.Processor.Current)

                {

                    case SpatialProcessor.Source.Surround71: src = "game sends 7.1 surround -> " + (e.Processor.Rendering360 ? "true 360\u00B0" : "preparing true 360\u00B0..."); break;

                    case SpatialProcessor.Source.Surround51: src = "game sends 5.1 surround -> " + (e.Processor.Rendering360 ? "true 360\u00B0" : "preparing true 360\u00B0..."); break;

                    case SpatialProcessor.Source.Stereo:

                        src = G.VirtualizeStereo ? "stereo sound -> true 360\u00B0 (in front of you)" : core.NaturalSound ? "stereo sound -> played as is (natural)" : "stereo sound -> left/center/right EQ";

                        break;

                    default: src = "no game sound yet"; break;

                }

                if (e.Status.StartsWith("Waiting")) src = "device is ready (" + (e.InputChannels == 8 ? "7.1" : e.InputChannels + " ch") + ") - start a game";

                s = e.Status + " - " + src + "\nDelay " + e.LatencyMs.ToString("0") + " ms  -  CPU " + e.CpuPercent.ToString("0.0") + "%  -  input " + (e.InputChannels > 0 ? e.InputChannels + " ch" : "-") + (e.Underruns > 0 ? "  -  dropouts " + e.Underruns : "");

                if (e.Processor.TestRunning) s += "  -  test: " + e.Processor.TestLabel;

            }

            status.Text = s;
            string sends = Sends(e);
            if (sendsNote.Text != sends) sendsNote.Text = sends;
            sendsNote.ForeColor = core.SpatialWarning != null ? Theme.Warn : Theme.Muted;

            presetInfo.Text = core.GameOwnHrtf
                ? "Paused: \"" + core.Profiles.ActiveName + "\" uses the game's own 3D (HRTF), so its sound plays as the game mixed it - no second 3D, footstep boost or directional EQ on top."
                : core.NaturalSound

                ? "Paused: \"" + core.Profiles.ActiveName + "\" is active, so 3D plays sound as it was mixed (no footstep boost, tamer, cue sharpening or directional EQ). Back when a game starts."

                : GamePresets.Describe(G.Preset);

            testBtn.Text = e.Processor.TestRunning ? "Stop test" : "Test footsteps";

            
            

        }



        bool testWasRunning;



        void Tick()

        {

            bool testing = core.Game.Processor.TestRunning;

            if (testing != testWasRunning) { testWasRunning = testing; core.PushGameParams(); }   // test ended by itself

            if (radar.Changed()) radar.Invalidate();

            if (++tick % 12 == 0)

            {

                RefreshStatus();

                var d = core.Game.Processor.Dynamics;

                bool sphere = core.Game.Processor.Radar.TestSphere;
                string cap = sphere ? "Test: " + core.Game.Processor.TestLabel : "Live sound radar";
                if (radarCaption.Text != cap) { radarCaption.Text = cap; radarCaption.ForeColor = sphere ? Theme.Accent : Theme.Muted; }
                dynLive.Text = core.Game.Running ? "Footstep boost now: +" + d.LiveBoostDb.ToString("0") + " dB   Loud tamer: " + d.LiveCutDb.ToString("0") + " dB" : "";

            }

        }



        // ---- Hi-Fi Cable install



        public void InstallHiFiCable()

        {

            installBtn.Enabled = false;

            installBtn.Text = "Downloading...";

            var form = FindForm();

            var th = new Thread(() =>

            {

                string err = null;

                try

                {

                    string dirPath = Path.Combine(Path.GetTempPath(), "StreamLite-HiFiCable");

                    Directory.CreateDirectory(dirPath);

                    string zip = Path.Combine(dirPath, "HiFiCableAsioBridgeSetup_v1007.zip");

                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                    using (var wc = new WebClient()) wc.DownloadFile("https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip", zip);

                    string ex = Path.Combine(dirPath, "x");

                    if (Directory.Exists(ex)) Directory.Delete(ex, true);

                    System.IO.Compression.ZipFile.ExtractToDirectory(zip, ex);

                    string setup = Directory.GetFiles(ex, "HiFiCableAsioBridgeSetup*.exe", SearchOption.AllDirectories).FirstOrDefault();

                    if (setup == null) throw new FileNotFoundException("Setup not found in the download.");

                    form.BeginInvoke(new Action(() => installBtn.Text = "Installing (approve the prompt)..."));

                    var p = Process.Start(new ProcessStartInfo(setup, "-i -h") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(setup) });

                    p.WaitForExit();

                    // Give Windows a moment to publish the new endpoints.

                    for (int i = 0; i < 20 && core.GameSinkCandidates().Count == 0; i++) Thread.Sleep(500);

                    if (core.GameSinkCandidates().Count == 0)

                    {

                        // Silent mode not supported or not finished: open the installer normally.

                        p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(setup) });

                        p.WaitForExit();

                        for (int i = 0; i < 20 && core.GameSinkCandidates().Count == 0; i++) Thread.Sleep(500);

                    }

                    if (core.GameSinkCandidates().Count == 0) err = "Hi-Fi Cable didn't show up yet. If the installer asked to reboot, restart Windows and come back here.";

                }

                catch (System.ComponentModel.Win32Exception) { err = "Install cancelled."; }

                catch (Exception ex) { err = "Install failed: " + ex.Message; }

                form.BeginInvoke(new Action(() =>

                {

                    installBtn.Enabled = true;

                    installBtn.Text = "Install Hi-Fi Cable (free)";

                    if (err != null) DarkMessage.Show(form, err, "Hi-Fi Cable", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    core.RestartGameAudio();

                    LoadAll();

                }));

            }) { IsBackground = true };

            th.Start();

        }


        /// <summary>A test button: stops the test if it's this one; if another test is playing it stops that and starts this.</summary>
        /// <summary>Personalize 3D, zooming in from this page's radar - or from a copy of it (from: the setup's; startNow:
        /// straight into the test, its instructions were on the setup's page).</summary>
        /// <summary>Personalize 3D's practice (4.1): your settings as they are, nothing tuned, as long as you like.</summary>
        public void OpenPractice3D() { Personal3DView.Open(core, radar, () => LoadAll(), false, true); }

        public void OpenPersonalize3D(Action closed = null, Control from = null, bool startNow = false)
        {
            Personal3DView.Open(core, from ?? radar, () => { LoadAll(); if (closed != null) closed(); }, startNow);
        }

        bool StopTestIfRunning()
        {
            var p = core.Game.Processor;
            if (!p.TestRunning) return false;
            
            p.StopTest();
            core.PushGameParams();
            return true;
        }
    }



    /// <summary>
    /// 3D sound sphere (a cube until 4.0.7): you in the middle, the green ear-level ring, blue lines above, orange below -
    /// as Personalize 3D draws it. Game sound sits on the ear-level ring (7.1 / 360° audio carries no height); the above /
    /// below test's footsteps are drawn where they really are on the sphere. Right-drag turns it to look from any side; a
    /// right double-click puts the view back. (SoundCube still does the turning and projection.)
    /// </summary>
    internal sealed class SoundRadarView : Control, IZoomSource
    {
        readonly AppCore core;
        readonly SoundCube cube = new SoundCube();
        float cx, cy, R;
        bool dragging; Point dragFrom; double yaw0, pitch0;
        readonly List<RectangleF> labels = new List<RectangleF>();   // this paint's labels, so none overlap

        /// <summary>Draws a label unless it would sit on one already drawn (edge-on views stack them).</summary>
        void Label(Graphics g, string text, PointF at, Color c, bool alignRight)
        {
            var size = g.MeasureString(text, Theme.Small);
            var r = new RectangleF(alignRight ? at.X - size.Width : at.X - size.Width / 2, at.Y - size.Height / 2, size.Width, size.Height);
            foreach (var o in labels) if (o.IntersectsWith(r)) return;
            labels.Add(r);
            using (var b = new SolidBrush(c)) g.DrawString(text, Theme.Small, b, r.Location);
        }

        static readonly Color AboveCol = Color.FromArgb(110, 165, 240), LevelCol = Color.FromArgb(110, 210, 130), BelowCol = Color.FromArgb(235, 170, 90);

        public SoundRadarView(AppCore core)
        {
            this.core = core;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        PointF P(double x, double y, double z, out double depth)
        {
            double sx, sy; cube.Project(x, y, z, out sx, out sy, out depth);
            return new PointF(cx + (float)(R * sx), cy + (float)(R * sy));
        }

        /// <summary>Personalize 3D zooms in here: where you are (the sphere's centre) as last drawn, its radius
        /// in pixels, and how it's turned.</summary>
        public PointF Centre { get { return P(0, 0, 0); } }
        public float Radius { get { return R; } }
        public double ViewYaw { get { return cube.Yaw; } }
        public double ViewPitch { get { return cube.Pitch; } }
        PointF P(double x, double y, double z) { double d; return P(x, y, z, out d); }

        /// <summary>A direction at ear level, on the sphere's green ring at scale 1 (4.0.7: a sphere, not a cube's floor).</summary>
        PointF Dir(double azDeg, double z, double scale)
        {
            double a = azDeg * Math.PI / 180;
            return P(-Math.Sin(a) * scale, Math.Cos(a) * scale, z);
        }

        /// <summary>A line of height (elevation, degrees) round the sphere: back half fainter.</summary>
        void Latitude(Graphics g, double elDeg, Color c, float width, int alpha)
        {
            double z = Math.Sin(elDeg * Math.PI / 180), r = Math.Cos(elDeg * Math.PI / 180);
            for (int i = 0; i < 72; i++)
            {
                double a0 = 2 * Math.PI * i / 72, a1 = 2 * Math.PI * (i + 1) / 72;
                Line(g, r * Math.Sin(a0), r * Math.Cos(a0), z, r * Math.Sin(a1), r * Math.Cos(a1), z, Color.FromArgb(alpha, c), width, false);
            }
        }

        int lastRightDown; bool rightArmed; Point lastRightAt;   // the first press of a possible double-click

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Right) return;
            // Right double-click resets the view. Detected here: Windows doesn't reliably report a right double-click
            // to a control that captures the mouse on the first press.
            int now = Environment.TickCount;
            var near = SystemInformation.DoubleClickSize;
            bool second = e.Clicks >= 2 || (rightArmed && unchecked(now - lastRightDown) <= SystemInformation.DoubleClickTime &&
                Math.Abs(e.X - lastRightAt.X) <= near.Width && Math.Abs(e.Y - lastRightAt.Y) <= near.Height);
            rightArmed = !second; lastRightDown = now; lastRightAt = e.Location;
            if (second) { cube.Reset(); dragging = false; Invalidate(); return; }
            dragging = true; dragFrom = e.Location; yaw0 = cube.Yaw; pitch0 = cube.Pitch;
            Capture = true; Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            // A real drag isn't the first half of a double-click.
            if (Math.Abs(e.X - dragFrom.X) > SystemInformation.DoubleClickSize.Width || Math.Abs(e.Y - dragFrom.Y) > SystemInformation.DoubleClickSize.Height) rightArmed = false;
            cube.Yaw = yaw0 + (e.X - dragFrom.X) * 0.6;
            cube.Pitch = pitch0 + (e.Y - dragFrom.Y) * 0.6;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right || !dragging) return;
            dragging = false; Capture = false; Cursor = Cursors.Default;
            Invalidate();   // the "turning..." hint goes back
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Alt+Tab or a dialog mid-drag: stop turning.
            if (dragging && !Capture) { dragging = false; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Right) { cube.Reset(); Invalidate(); }
        }

        long lastSig = long.MinValue;

        /// <summary>True when something on the sphere moved since the last paint (saves redrawing a still picture).</summary>
        public bool Changed()
        {
            var radar = core.Game.Processor.Radar;
            long sig = core.Game.Running ? 1 : 0;
            sig = sig * 31 + (radar.StereoMode ? 1 : 0);
            for (int s = 0; s < SpatialProcessor.Slots; s++)
                sig = sig * 31 + (radar.SlotUsed[s] ? (long)Math.Round(Math.Max(-70, radar.SlotDb[s]) / 2) : -999);   // 2 dB steps
            var ringNow = radar.Ring;
            if (ringNow != null) { sig = sig * 31 + 7; foreach (var v in ringNow) sig = sig * 31 + (long)Math.Round(Math.Max(-70, v) / 2); }
            if (radar.TestSphere) sig = sig * 31 + (long)(radar.TestAz * 7 + radar.TestEl * 1000) + (Environment.TickCount / 40);
            var blips = radar.Snapshot(Environment.TickCount, 2000);
            if (blips.Count > 0) sig ^= Environment.TickCount;       // fading dots animate
            if (sig == lastSig) return false;
            lastSig = sig;
            return true;
        }

        void Line(Graphics g, double x0, double y0, double z0, double x1, double y1, double z1, Color c, float width, bool dashBack)
        {
            double d0, d1;
            var a = P(x0, y0, z0, out d0); var b = P(x1, y1, z1, out d1);
            bool back = d0 + d1 < -0.05;
            using (var pen = new Pen(Color.FromArgb(back ? c.A / 2 : c.A, c), width))
            {
                if (back && dashBack) pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, a, b);
            }
        }

        readonly List<Tuple<string, PointF, Color, bool>> floorLabels = new List<Tuple<string, PointF, Color, bool>>();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Card);
            labels.Clear(); floorLabels.Clear();
            cx = Width / 2f + Theme.Px(10); cy = Height / 2f; R = Math.Min(Width - Theme.Px(60), Height - Theme.Px(40)) / 2f / 1.15f;
            var radar = core.Game.Processor.Radar;
            bool on = core.Game.Running || radar.TestSphere;
            int testFloor = radar.TestSphere ? SoundCube.Floor(radar.TestEl) : 99;

            // The sphere (4.0.7, as Personalize 3D's): lines of height every 30 degrees - blue above, the green ear-level
            // ring, orange below - and lines of direction; the part the above / below test plays in lights up.
            for (int el = -60; el <= 60; el += 30)
            {
                var col = el > 0 ? AboveCol : el < 0 ? BelowCol : LevelCol;
                bool lit = (el > 0 && testFloor == 1) || (el < 0 && testFloor == -1) || (el == 0 && testFloor == 0);
                Latitude(g, el, col, el == 0 ? 2.4f : lit ? 1.8f : 1.1f, el == 0 ? 230 : lit ? 230 : 120);
            }
            for (int m = 0; m < 12; m++)
            {
                double a = m * Math.PI / 6;
                for (int i = 0; i < 24; i++)
                {
                    double e0 = -Math.PI / 2 + Math.PI * i / 24, e1 = -Math.PI / 2 + Math.PI * (i + 1) / 24;
                    var col = e0 + e1 > 0.01 ? AboveCol : e0 + e1 < -0.01 ? BelowCol : LevelCol;
                    Line(g, Math.Sin(a) * Math.Cos(e0), Math.Cos(a) * Math.Cos(e0), Math.Sin(e0), Math.Sin(a) * Math.Cos(e1), Math.Cos(a) * Math.Cos(e1), Math.Sin(e1), Color.FromArgb(m % 3 == 0 ? 110 : 55, col), 1f, false);
                }
            }
            Line(g, 0, 0, -1, 0, 0, 1, Color.FromArgb(110, AboveCol), 1f, true);   // up / down through your head
            var ringPts = Enumerable.Range(0, 72).Select(i => Dir(i * 5, 0, 1)).ToArray();
            using (var fill = new SolidBrush(Color.FromArgb(testFloor == 0 ? 60 : 24, LevelCol))) g.FillPolygon(fill, ringPts);
            floorLabels.Add(Tuple.Create("ABOVE", P(0, 0, 1.12), Color.FromArgb(testFloor == 1 ? 255 : 200, AboveCol), testFloor == 1));
            floorLabels.Add(Tuple.Create("EAR LEVEL", ringPts.OrderBy(p => p.X).First(), Color.FromArgb(testFloor == 0 ? 255 : 200, LevelCol), testFloor == 0));
            floorLabels.Add(Tuple.Create("BELOW", P(0, 0, -1.12), Color.FromArgb(testFloor == -1 ? 255 : 200, BelowCol), testFloor == -1));

            if (radar.StereoMode && on && !radar.TestSphere)
                using (var shade = new SolidBrush(Color.FromArgb(90, 20, 24, 27))) g.FillPolygon(shade, ringPts);
            // Game sound on the ear-level floor's edge: the 360° ring (5° segments) or the virtual speakers.
            var ringDb = radar.Ring;
            if (on && !radar.TestSphere)
            {
                if (ringDb != null && !radar.StereoMode)
                {
                    int n = ringDb.Length; float step = 360f / n;
                    for (int b = 0; b < n; b++)
                    {
                        float lvl = Math.Max(0, Math.Min(1, (ringDb[b] + 70) / 55f));
                        if (lvl <= 0.01f) continue;
                        using (var pen = new Pen(Color.FromArgb((int)(30 + 210 * lvl), Theme.Accent), Theme.Px(3) + 5 * lvl) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(pen, Dir(b * step, 0, 1), Dir((b + 1) * step, 0, 1));
                    }
                }
                else
                    for (int s = 0; s < SpatialProcessor.Slots; s++)
                    {
                        if (!radar.SlotUsed[s]) continue;
                        float lvl = Math.Max(0, Math.Min(1, (radar.SlotDb[s] + 70) / 55f));
                        float az = radar.SlotAz[s];
                        using (var pen = new Pen(Color.FromArgb((int)(40 + 200 * lvl), Theme.Accent), Theme.Px(3) + 5 * lvl) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            for (int k = -2; k < 2; k++) g.DrawLine(pen, Dir(az + k * 6, 0, 1), Dir(az + (k + 1) * 6, 0, 1));
                    }
                for (int s = 0; s < SpatialProcessor.Slots; s++)
                {
                    if (!radar.SlotUsed[s]) continue;
                    var sp = Dir(radar.SlotAz[s], 0, 1.1);
                    using (var b = new SolidBrush(Color.FromArgb(110, Theme.Muted))) g.FillEllipse(b, sp.X - 2.5f, sp.Y - 2.5f, 5, 5);
                }
                // Sudden sounds (footsteps, shots) at their direction, inside the floor.
                long now = Environment.TickCount;
                foreach (var b in radar.Snapshot(now, 2000))
                {
                    float age = (now - b.Time) / 2000f;
                    float size = Theme.Px(6) + Math.Max(0, (b.Db + 60) / 4f);
                    var p = Dir(b.Az, 0, 0.7);
                    using (var br = new SolidBrush(Color.FromArgb(Math.Max(0, (int)(255 * (1 - age))), 255, 196, 80))) g.FillEllipse(br, p.X - size / 2, p.Y - size / 2, size, size);
                }
            }

            // You: in the middle of the ear-level floor, nose towards the front.
            var head = P(0, 0, 0); var nose = P(0, 0.22, 0);
            using (var me = new SolidBrush(Theme.Text))
            {
                g.FillEllipse(me, head.X - 5, head.Y - 5, 10, 10);
                var dx = nose.X - head.X; var dy = nose.Y - head.Y; float len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-3f; float ux = dx / len, uy = dy / len;
                if (len > 3) g.FillPolygon(me, new[] { nose, new PointF(head.X - uy * 5, head.Y + ux * 5), new PointF(head.X + uy * 5, head.Y - ux * 5) });
            }

            // Above / below test: the footstep in its cell on its floor, with a stem to ear level.
            if (radar.TestSphere)
            {
                // Where it really is on the sphere, with a stem down (or up) to ear level.
                double ta = radar.TestAz * Math.PI / 180, te = radar.TestEl * Math.PI / 180, d;
                double x = -Math.Sin(ta) * Math.Cos(te), y = Math.Cos(ta) * Math.Cos(te), z = Math.Sin(te);
                var at = P(x * 0.92, y * 0.92, z * 0.92, out d);
                var foot = P(x * 0.92, y * 0.92, 0);
                if (Math.Abs(radar.TestEl) > 5)
                {
                    using (var stem = new Pen(Color.FromArgb(170, 255, 196, 80), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(stem, foot, at);
                    using (var shadow = new SolidBrush(Color.FromArgb(120, 255, 196, 80))) g.FillEllipse(shadow, foot.X - 3, foot.Y - 2, 6, 4);
                }
                float pulse = 1 + 0.25f * (float)Math.Sin(Environment.TickCount / 90.0);
                float size = Theme.Px(9) * pulse * (d > 0 ? 1.15f : 0.9f);
                using (var glow = new SolidBrush(Color.FromArgb(70, 255, 196, 80))) g.FillEllipse(glow, at.X - size, at.Y - size, 2 * size, 2 * size);
                using (var dot = new SolidBrush(Color.FromArgb(255, 255, 196, 80))) g.FillEllipse(dot, at.X - size / 2, at.Y - size / 2, size, size);
            }

            var f2 = Theme.Small;
            // Labels: the lit floor first, then directions (not when they point straight at you), then the other floors.
            foreach (var fl in floorLabels.Where(x => x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, fl.Item1 == "EAR LEVEL");
            var dirCol = Color.FromArgb(220, LevelCol);
            foreach (var d in new[] { new { T = "FRONT", X = 0.0, Y = 1.18 }, new { T = "BEHIND", X = 0.0, Y = -1.18 }, new { T = "L", X = -1.15, Y = 0.0 }, new { T = "R", X = 1.15, Y = 0.0 } })
            {
                var at = P(d.X, d.Y, 0);
                float dx = at.X - head.X, dy = at.Y - head.Y;
                if (dx * dx + dy * dy > R * R * 0.3f * 0.3f) Label(g, d.T, at, dirCol, false);
            }
            foreach (var fl in floorLabels.Where(x => !x.Item4)) Label(g, fl.Item1, fl.Item2, fl.Item3, fl.Item1 == "EAR LEVEL");
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                using (var tb = new SolidBrush(Theme.Muted))
                {
                    if (radar.TestSphere) { }   // the caption under the sphere names the position
                    else if (!core.Game.Running) g.DrawString("3D audio is off", Theme.Body, tb, Width / 2f, Height - Theme.Px(12), sf);
                    else if (radar.StereoMode) g.DrawString("stereo: front/back unknown", f2, tb, Width / 2f, Height - Theme.Px(12), sf);
                }
            }
            using (var hint = new SolidBrush(Color.FromArgb(150, Theme.Muted)))
                g.DrawString(dragging ? "turning..." : "right-drag to turn", f2, hint, Theme.Px(2), Theme.Px(2));
        }
    }
}
