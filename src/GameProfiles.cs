using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamLite.Audio;

namespace StreamLite
{
    /// <summary>A headphone EQ (and 3D routing) used while a given game runs.</summary>
    public class GameProfile
    {
        public string Name;
        public List<string> Exes = new List<string>();
        public EqProfile Eq = new EqProfile();
        /// <summary>"game" = the game's own HRTF in plain stereo straight to the headphones; "bl" = StreamLite 3D (7.1).</summary>
        public string ThreeD = "game";
        public string Tips = "";
        public string Basis = "";
        public bool Builtin;
        public int Version;
        /// <summary>The built-in game layer as shipped; differs from the current EQ once you've edited it.</summary>
        public string EqSignature;
        /// <summary>Films, videos and story games: 3D audio stays natural (no footstep boost, loud-sound tamer, cue sharpening or directional EQ).</summary>
        public bool Cinematic;
        /// <summary>Only while the app is actually playing sound (music players that sit open all day).</summary>
        public bool WhilePlaying;
        /// <summary>Voice chat apps: used while one is open and no game (or music) is - they sit open all day.</summary>
        public bool ChatApp;
    }

    /// <summary>The headphone correction picked for one output device (from AutoEq).</summary>
    public class DeviceCorrection
    {
        public string Name;
        public List<EqFilter> Filters;
        public bool On = true;
    }

    public class ProfileSettings
    {
        public bool AutoSwitch = true;
        /// <summary>Before 2.1.1: one correction for every device. Moved into Corrections for the headphones in use.</summary>
        public bool Correction;
        public string CorrectionName;
        public List<EqFilter> CorrectionFilters;
        /// <summary>Headphone correction under every profile, per output device (device id -> correction): each headset keeps its own.</summary>
        public Dictionary<string, DeviceCorrection> Corrections = new Dictionary<string, DeviceCorrection>();
        public string DefaultProfile = GameProfiles.Movies;
        public string ManualProfile;
        public List<GameProfile> Profiles = new List<GameProfile>();

        /// <summary>The correction for an output device, or null when none was picked for it.</summary>
        public DeviceCorrection CorrectionFor(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            if (Corrections == null) Corrections = new Dictionary<string, DeviceCorrection>();
            DeviceCorrection c;
            if (Corrections.TryGetValue(deviceId, out c)) return c;
            // The single correction from before belonged to the headphones in use then.
            if (Corrections.Count == 0 && CorrectionFilters != null && CorrectionFilters.Count > 0)
            {
                c = new DeviceCorrection { Name = CorrectionName, Filters = CorrectionFilters, On = Correction };
                Corrections[deviceId] = c;
                return c;
            }
            return null;
        }

        /// <summary>The correction applied on a device right now (picked and switched on), or null.</summary>
        public DeviceCorrection ActiveCorrection(string deviceId)
        {
            var c = CorrectionFor(deviceId);
            return c != null && c.On && c.Filters != null && c.Filters.Count > 0 ? c : null;
        }

        public void SetCorrection(string deviceId, string name, List<EqFilter> filters)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            CorrectionFor(deviceId);   // migrate first, so the old correction doesn't land on this device later
            Corrections[deviceId] = new DeviceCorrection { Name = name, Filters = filters, On = true };
        }
    }

    /// <summary>
    /// Built-in presets. Each = your headphones' correction (measured response -> neutral, from AutoEq; shared,
    /// toggleable) + a per-game layer. CS2's layer comes from measuring the game's own footstep / weapon / grenade sound files;
    /// the others from published guidance (vendor game EQs, developer statements, localization research).
    /// </summary>
    internal static class GameProfiles
    {
        public const string Movies = "Movies & videos";
        public const string Music = "Music";
        public const string Chat = "Chat";
        public const int BuiltinVersion = 4;

        /// <summary>Voice and video chat apps (process names without .exe).</summary>
        public static readonly string[] ChatExes = { "Discord", "DiscordPTB", "DiscordCanary", "Vesktop", "Legcord", "Zoom", "ms-teams", "Teams",
            "ts3client_win64", "ts3client_win32", "TeamSpeak", "mumble", "Skype", "Slack", "Guilded", "Element", "Signal", "Telegram", "WhatsApp",
            "CiscoCollabHost", "webexmta", "Ventrilo", "Revolt", "Steam Chat" };

        /// <summary>SoundGuys B&amp;K 5128 measurement of the MOMENTUM 5 vs Harman over-ear 2018 (fitted, 0.22 dB RMS to 10 kHz).</summary>
        public static List<EqFilter> Momentum5Correction()
        {
            return new List<EqFilter>
            {
                F("LSC", 49, -7.7, 0.64), F("PK", 824, 1.6, 3.0), F("PK", 1277, -2.2, 2.09), F("PK", 2011, 4.4, 2.87),
                F("PK", 3173, -1.8, 3.0), F("HSC", 8126, 3.5, 0.57), F("PK", 9826, -4.9, 2.78)
            };
        }
        public const string Momentum5Name = "Sennheiser MOMENTUM 5 to neutral (Harman OE 2018)";

        static EqFilter F(string t, double f, double g, double q) { return new EqFilter { Type = t, Freq = f, Gain = g, Q = q }; }

        static GameProfile P(string name, string threeD, string[] exes, string basis, string tips, params EqFilter[] layer)
        {
            var p = new GameProfile { Name = name, ThreeD = threeD, Exes = exes.ToList(), Basis = basis, Tips = tips, Builtin = true, Version = BuiltinVersion };
            p.Eq = new EqProfile { Mode = "parametric", AutoPreamp = true, Enabled = true };
            foreach (var f in layer) p.Eq.Parametric.Add(f);
            p.EqSignature = Signature(p.Eq);
            return p;
        }

        /// <summary>Identifies a game layer (the correction underneath doesn't count).</summary>
        public static string Signature(EqProfile eq)
        {
            if (eq == null) return "";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return (eq.Mode ?? "") + ";" + string.Join(";", eq.Parametric.Select(f => f.Type + "," + f.Freq.ToString("0.##", inv) + "," + f.Gain.ToString("0.##", inv) + "," + f.Q.ToString("0.##", inv)));
        }

        static readonly string[] CinematicNames = { "Story & open world", "Sea of Thieves", Music, Movies };

        public static List<GameProfile> Builtins()
        {
            var list = BuiltinList();
            foreach (var p in list) { p.Cinematic = CinematicNames.Contains(p.Name); p.WhilePlaying = p.Name == Music; p.ChatApp = p.Name == Chat; }
            return list;
        }

        static List<GameProfile> BuiltinList()
        {
            return new List<GameProfile>
            {
                P("Rainbow Six Siege", "bl", new[] { "RainbowSix", "RainbowSix_Vulkan", "RainbowSix_DX11" },
                    "Siege has no in-game HRTF, so StreamLite 3D does the surround. EQ keeps the 150-250 Hz floor-transmitted thump (enemies above/below you), cuts breach rumble and indoor reverb mud, and lifts the 2.8 kHz step click and 5 kHz gear rattle.",
                    "In Siege: Audio > Dynamic Range: Low (Night Mode), Sound output: Speakers/7.1 so StreamLite 3D gets real surround. Music low.",
                    F("LSC", 80, -3, 0.7), F("PK", 180, 2, 1.0), F("PK", 600, -2, 1.0), F("PK", 2800, 2.5, 1.0), F("PK", 5000, 1.5, 1.5)),
                P("Marvel Rivals", "game", new[] { "Marvel-Win64-Shipping" },
                    "Uses the game's 3D Enhancement HRTF. Cuts slam/ult rumble, keeps heavy-hero stomps (220 Hz), softens the 2 kHz hit-tick band and lifts 3.2 kHz light-hero scuffs and dash onsets. 3 kHz kept modest: ability spam there is the main fatigue source.",
                    "In Marvel Rivals: 3D Enhancement ON, Combat Mix: Focus, lower Hit Indicator volume. Windows stays stereo for this game (StreamLite routes it straight to your headphones).",
                    F("LSC", 110, -4, 0.7), F("PK", 220, 1.5, 1.0), F("PK", 500, -1.5, 1.0), F("PK", 2000, -1, 2.0), F("PK", 3200, 2.5, 1.0), F("PK", 5500, 1, 1.5), F("HSC", 9000, 1, 0.7)),
                P("Overwatch 2", "game", new[] { "Overwatch" },
                    "Uses Overwatch's own Dolby Atmos for Headphones. Blizzard muffles footsteps through walls (low-passed), so the 250 Hz step body is lifted; shield/Lucio midrange at 650 Hz is cut; 3 kHz unoccluded step click lifted.",
                    "In Overwatch: Audio Mix: Night Mode, Dolby Atmos for Headphones ON, Music about 20.",
                    F("LSC", 100, -4, 0.7), F("PK", 250, 1.5, 1.0), F("PK", 650, -1.5, 1.0), F("PK", 3000, 2.5, 1.0), F("PK", 6000, 1, 1.5), F("HSC", 9000, 1, 0.7)),
                P("Counter-Strike 2", "game", new[] { "cs2" },
                    "Measured from CS2's own sound files (264 footsteps, 128 weapon and 40 grenade sounds): footstep energy peaks at 250 Hz and beats gunfire by 4-5 dB at 160-400 Hz; gear/suit rattle peaks at 5 kHz; HE grenades sit at 80-125 Hz; guns dominate above 8 kHz. EQ follows that.",
                    "In CS2: EQ profile Crisp (or Natural for long sessions), L/R Isolation about 50%, Steam Audio HRTF is always on. Windows stays stereo for CS2.",
                    F("LSC", 100, -4, 0.7), F("PK", 250, 2.5, 1.0), F("PK", 700, -1.5, 1.2), F("PK", 3000, 2, 1.0), F("PK", 5000, 2, 1.4), F("PK", 9500, -1, 1.5)),
                P("Valorant", "game", new[] { "VALORANT-Win64-Shipping" },
                    "Uses Valorant's HRTF (footsteps and reloads are rendered in 3D). Cuts ability-explosion rumble, narrow notch at 2.6 kHz softens spike/UI beeps, lifts the 3.3 kHz footstep click and 5.5 kHz texture.",
                    "In Valorant: HRTF ON, Speaker configuration Stereo. Turn other 3D processing off (StreamLite routes Valorant straight to your headphones).",
                    F("LSC", 110, -4, 0.7), F("PK", 220, 1.5, 1.0), F("PK", 2600, -1, 4.0), F("PK", 3300, 3, 1.0), F("PK", 5500, 1.5, 1.5), F("HSC", 9000, 1, 0.7)),
                P("Fortnite", "game", new[] { "FortniteClient-Win64-Shipping" },
                    "Uses Fortnite's 3D Headphones. Cuts storm/vehicle/explosion rumble, keeps 300 Hz step body, tames the 1.5 kHz build/edit clack, lifts the 3.2 kHz step click and 6 kHz texture.",
                    "In Fortnite: 3D Headphones ON, Visualize Sound Effects ON, Music low.",
                    F("LSC", 120, -4, 0.7), F("PK", 300, 1.5, 1.0), F("PK", 1500, -1.5, 1.0), F("PK", 3200, 2.5, 1.0), F("PK", 6000, 1.5, 1.5), F("HSC", 9000, 1, 0.7)),
                P("Call of Duty", "game", new[] { "cod", "cod22-cod", "cod23-cod", "cod24-cod", "cod25-cod", "sp22-cod", "sp23-cod", "BlackOps6", "BlackOps7", "BlackOps3", "t6mp", "t6zm", "t6sp" },
                    "For CoD's Enhanced Headphone Mode (Embody HRTF). Cuts killstreak sub-bass but keeps the 160 Hz boot thump, removes 450 Hz gun-body mud, lifts the 2.8 kHz gear jingle.",
                    "In CoD: Audio Mix Headphones, Enhanced Headphone Mode ON (test off too). If you turn EHM off, switch this profile to StreamLite 3D.",
                    F("LSC", 70, -4, 0.7), F("PK", 160, 1, 1.0), F("PK", 450, -2, 1.0), F("PK", 2800, 2.5, 1.0), F("PK", 5000, 1.5, 1.5), F("HSC", 9000, 1, 0.7)),
                P("Battlefield 6", "bl", new[] { "bf6" },
                    "No documented in-game HRTF, so StreamLite 3D does the surround. Strong cut of vehicle/artillery/destruction rumble (the biggest masker in BF), 800 Hz distant-battle bed cut, 3 kHz step and gear click lifted.",
                    "In BF6: Sound System Headphones, Audio Mix War Tapes, Music 0, Tinnitus 0.",
                    F("LSC", 120, -5, 0.7), F("PK", 220, 1.5, 1.0), F("PK", 800, -1.5, 1.0), F("PK", 3000, 3, 1.0), F("PK", 5500, 1.5, 1.5), F("HSC", 9000, 0.5, 0.7)),
                P("THE FINALS", "bl", new[] { "Discovery" },
                    "Cuts destruction/explosion rumble and the 900 Hz debris bed, lifts the 3.2 kHz step click (enemy steps are mixed quiet).",
                    "In THE FINALS: Music low. If you enable an in-game 3D/spatial option, switch this profile to the game's own HRTF.",
                    F("LSC", 120, -5, 0.7), F("PK", 250, 1.5, 1.0), F("PK", 900, -1.5, 1.0), F("PK", 3200, 3, 1.0), F("PK", 6000, 1.5, 1.5), F("HSC", 9000, 0.5, 0.7)),
                P("Apex Legends", "bl", new[] { "r5apex", "r5apex_dx12" },
                    "No in-game HRTF, so StreamLite 3D does the surround. General footstep curve: rumble cut, step body, 3 kHz click, 5 kHz detail.",
                    "In Apex: Music 0, SFX high.",
                    F("LSC", 110, -4, 0.7), F("PK", 250, 1.5, 1.0), F("PK", 700, -1, 1.0), F("PK", 3000, 2.5, 1.0), F("PK", 5000, 1.5, 1.5), F("HSC", 9000, 0.5, 0.7)),
                P("Other shooters", "bl", new[] { "FragPunk", "SolarlandClient-Win64-Shipping", "arma3_x64", "arma3", "DeltaForceClient-Win64-Shipping", "left4dead2", "Banana Shooter", "Redmatch 2", "Aim Hero", "Deceit" },
                    "FragPunk, Farlight 84, Arma 3, Delta Force, Left 4 Dead 2, Banana Shooter, Redmatch 2, Deceit: general footstep curve through StreamLite 3D.",
                    "If the game has its own HRTF / 3D headphones option and you use it, switch this profile to the game's own HRTF.",
                    F("LSC", 110, -4, 0.7), F("PK", 250, 1.5, 1.0), F("PK", 3000, 2.5, 1.0), F("PK", 5000, 1.5, 1.5), F("HSC", 9000, 0.5, 0.7)),
                P("Phasmophobia", "bl", new[] { "Phasmophobia" },
                    "Ghost footsteps and whispers: mild rumble cut, lifted 2-4 kHz detail, room kept natural.",
                    "",
                    F("LSC", 90, -2, 0.7), F("PK", 250, 1, 1.0), F("PK", 3000, 2, 1.0), F("HSC", 9000, 1, 0.7)),
                P("Sea of Thieves", "bl", new[] { "SoTGame" },
                    "Adventure mix: fuller low end for the ocean and cannons, slightly smoother upper mids.",
                    "",
                    F("LSC", 105, 1.5, 0.7), F("PK", 3000, -1, 1.0), F("HSC", 10000, 1, 0.7)),
                P("Horror & co-op", "bl", new[] { "Lethal Company", "Content Warning", "REPO", "PEAK", "MIMESIS", "DeadByDaylight-Win64-Shipping",
                        "Backrooms-Win64-Shipping", "BETGameSteam-Win64-Shipping", "MurkyDiversGameSteam-Win64-Shipping", "Playtime_Multiplayer-Win64-Shipping",
                        "SCPSL", "cursedcompanions", "ChainedTogether-Win64-Shipping", "FiveNightsatFreddys", "FiveNightsatFreddys2", "FiveNightsatFreddys3",
                        "FiveNightsatFreddys4", "SisterLocation", "Pizzeria Simulator", "Ultimate Custom Night", "fnaf9-Win64-Shipping", "Big Walk" },
                    "Proximity voice chat and quiet creature cues: rumble trimmed so jump-scare booms don't bury everything, 1.8 kHz lifted so friends' voices in proximity chat cut through, 3 kHz lifted for footsteps, breathing and whispers, a little air for direction.",
                    "Keep in-game music low. If the game has its own headphones/HRTF option and you use it, switch this profile to the game's own HRTF.",
                    F("LSC", 90, -2, 0.7), F("PK", 250, 1, 1.0), F("PK", 1800, 1, 1.0), F("PK", 3200, 2, 1.0), F("HSC", 9000, 1.5, 0.7)),
                P("Story & open world", "bl", new[] { "GTA5_Enhanced", "GTA5", "RDR2", "Cyberpunk2077", "eldenring", "b1-Win64-Shipping", "GhostOfTsushima",
                        "Spider-Man", "Spider-Man2", "BatmanAK", "BatmanOrigins", "Batman", "DetroitBecomeHuman", "NMS", "Subnautica2-Win64-Shipping",
                        "Palworld-Win64-Shipping", "TheForest", "Project_Plague-Win64-Shipping", "Oregon-Win64-Shipping", "DOOMEternalx64vk",
                        "Dispatch-Win64-Shipping", "hollow_knight", "Hollow Knight Silksong", "Inscryption", "ProjectZomboid64" },
                    "Cinematic games: fuller low end for score and explosions, 2 kHz lifted so dialogue stays clear over the mix, the harsh 3.8 kHz gunfire/metal band softened for long sessions, a touch of air.",
                    "In the game pick 7.1 / Home theater output so StreamLite 3D places sounds around you. Dynamic range: wide / home theater for story games.",
                    F("LSC", 100, 2, 0.7), F("PK", 2000, 1, 0.9), F("PK", 3800, -1, 1.2), F("HSC", 10000, 1, 0.7)),
                P("Racing & sims", "bl", new[] { "BeamNG.drive.x64", "Wreckfest", "Wreckfest_x64", "Ride-Win64-Shipping", "Next Car Game Technology Sneak Peek",
                        "FlightSimulator2024", "PCBS2" },
                    "Engines and tyres: 80-160 Hz engine body lifted, 1.2 kHz lifted for tyre scrub and gear whine (your grip and rev cues), 4 kHz wind/road hiss softened.",
                    "Use the game's 7.1 / surround output so StreamLite 3D can place cars behind you.",
                    F("LSC", 80, 2, 0.7), F("PK", 160, 1.5, 1.0), F("PK", 1200, 1, 1.0), F("PK", 4000, -1, 1.2), F("HSC", 10000, 0.5, 0.7)),
                P("Fighting", "game", new[] { "MK12", "InvincibleVS-Win64-Shipping", "HalfSwordUE5-Win64-Shipping" },
                    "Stereo straight to your headphones (lowest delay for timing). Hit impacts get a little low-end punch, 400 Hz boxiness is cut, 2.5 kHz lifted for announcer and move audio cues.",
                    "",
                    F("LSC", 90, 1.5, 0.7), F("PK", 400, -1, 1.0), F("PK", 2500, 1.5, 1.0), F("HSC", 9000, 1, 0.7)),
                P("Party & casual", "game", new[] { "RobloxPlayerBeta", "Among Us", "Golf With Your Friends", "Gang Beasts", "FallGuys_client_game", "PartyAnimals",
                        "The Jackbox Party Pack", "The Jackbox Party Pack 3", "The Jackbox Party Pack 5", "Drawful 2", "UNO", "Cluedo", "GameOfLife2", "Crab Game",
                        "GarfieldKartNoMulti", "Liar's Bar", "Gamble With Your Friends", "Supermarket Together", "Stardew Valley", "SlimeRancher", "Terraria",
                        "MySingingMonsters", "TS4_x64", "TS4_DX9_x64", "CivilizationVI", "CivilizationVI_DX12", "NBA2K24", "Sledding Game", "Grapples Galore",
                        "BeanBattles", "KnockEmOut", "JustActNatural", "OAC-Win64-Shipping", "OAR-Win64-Shipping", "GONEfishing-Win64-Shipping", "Froggin Around",
                        "Schedule I", "ContrabandPolice", "gmod", "gmod_win64", "portal2", "PenguinHotel-Win64-Shipping", "Headliners-Win64-Shipping" },
                    "Plain stereo straight to your headphones with a gentle, warm and relaxed tuning for long sessions with friends.",
                    "",
                    F("LSC", 105, 1.5, 0.7), F("PK", 3000, -0.5, 1.0), F("HSC", 10000, 1, 0.7)),
                P(Music, "game", new[] { "Spotify" },
                    "While Spotify is playing (games still win when one is running). Punchy but tidy bass: a lift around 70 Hz for kick and bass-line punch, a small cut at 250 Hz so the low end doesn't muddy or drown the mids, gentle 2.5 kHz presence so vocals sit forward, and a clear, open top end from 9 kHz up. On top of your headphone correction.",
                    "Spotify: Settings > Audio quality: Very high (or Lossless), Normalize volume on (Normal), Equalizer off - StreamLite does the EQ. Spotify plays straight to your headphones (never through 3D).",
                    F("PK", 70, 3, 1.0), F("PK", 250, -1.5, 1.0), F("PK", 2500, 1, 1.0), F("HSC", 9000, 2, 0.7)),
                P(Chat, "game", ChatExes,
                    "While a voice chat app is open (Discord, Zoom, Teams, TeamSpeak, Mumble, Skype, Slack, Guilded...) and no game or music is playing. Voices made clear and easy on the ears for long calls: boom and rumble from friends' mics cut below 120 Hz, 300 Hz boxiness reduced, 2.5 kHz speech presence lifted, harsh 'S' sounds and codec hiss softened. On top of your headphone correction.",
                    "In Discord: Voice & Video > Noise Suppression None (StreamLite already cleans your mic), Echo Cancellation off with headphones, Attenuation 0%. Chat apps play straight to your headphones (never through 3D).",
                    F("LSC", 120, -3, 0.7), F("PK", 300, -1.5, 1.0), F("PK", 2500, 2, 1.0), F("PK", 6500, -2, 2.0), F("HSC", 10000, -1, 0.7)),
                P(Movies, "bl", new string[0],
                    "Used when no game, music or chat app is running: your headphone correction with a little extra warmth for films, YouTube and streams, and slightly softer upper mids for long listening.",
                    "",
                    F("LSC", 105, 2.5, 0.7), F("PK", 3000, -1, 1.0), F("HSC", 10000, 1, 0.7)),
            };
        }

        /// <summary>
        /// The profile for what's running: a game (built-in or your own) first, then a music player while it plays,
        /// then an open voice chat app. null = none of them (the default profile applies).
        /// </summary>
        public static GameProfile Pick(IEnumerable<GameProfile> profiles, Func<string, bool> running, Func<string, bool> playing, out string exe)
        {
            exe = null;
            GameProfile music = null, chat = null; string musicExe = null, chatExe = null;
            foreach (var p in profiles)
            {
                var hit = p.Exes.FirstOrDefault(running);
                if (hit == null) continue;
                if (p.WhilePlaying) { if (music == null && playing(hit)) { music = p; musicExe = hit; } continue; }
                if (p.ChatApp) { if (chat == null) { chat = p; chatExe = hit; } continue; }
                exe = hit; return p;
            }
            if (music != null) { exe = musicExe; return music; }
            if (chat != null) { exe = chatExe; return chat; }
            return null;
        }

        /// <summary>Adds missing built-ins and refreshes unedited ones when the built-in set changes.</summary>
        public static void Merge(ProfileSettings ps, EqProfile userEq)
        {
            if (ps.Profiles == null) ps.Profiles = new List<GameProfile>();
            ps.Profiles.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Name));
            var builtins = Builtins();
            foreach (var b in builtins)
            {
                var have = ps.Profiles.FirstOrDefault(p => p.Name == b.Name);
                if (have != null && have.Builtin) { have.Cinematic = b.Cinematic; have.WhilePlaying = b.WhilePlaying; have.ChatApp = b.ChatApp; }
                if (have == null) ps.Profiles.Add(b);
                else if (have.Builtin && have.Version < b.Version)
                {
                    // New games and advice always arrive; the EQ only if you never changed it.
                    if (have.Eq == null) have.Eq = b.Eq;
                    if (have.Exes == null) have.Exes = new List<string>();
                    foreach (var x in b.Exes) if (!have.Exes.Contains(x, StringComparer.OrdinalIgnoreCase)) have.Exes.Add(x);
                    have.Tips = b.Tips; have.Basis = b.Basis;
                    string current = Signature(have.Eq);
                    bool untouched = have.EqSignature != null ? current == have.EqSignature : current == b.EqSignature;
                    if (untouched && current != b.EqSignature) have.Eq.Parametric = b.Eq.Parametric;
                    if (untouched) have.EqSignature = b.EqSignature;
                    have.Version = b.Version;
                }
            }
            // Settings from before corrections were picked per headphone: that was the MOMENTUM 5 one.
            if (ps.Correction && ps.CorrectionFilters == null) { ps.CorrectionFilters = Momentum5Correction(); ps.CorrectionName = Momentum5Name; }
            // Keep the EQ the user had before profiles existed.
            if (userEq != null && !ps.Profiles.Any(p => p.Name == "My EQ") && userEq.AllFilters().Count > 0)
            {
                var mine = new GameProfile { Name = "My EQ", ThreeD = "bl", Basis = "Your own headphone EQ from before game profiles.", Eq = userEq.Clone() };
                ps.Profiles.Add(mine);
            }
            // Built-ins in their intended order (specific games, then genres, then Movies), your own profiles after.
            var order = builtins.Select(b => b.Name).ToList();
            ps.Profiles = ps.Profiles.OrderBy(p => { int i = order.IndexOf(p.Name); return i < 0 ? int.MaxValue : i; }).ToList();
            foreach (var p in ps.Profiles)
            {
                if (p.Eq == null) p.Eq = new EqProfile();
                p.Eq.Repair();
                if (p.Exes == null) p.Exes = new List<string>();
                if (p.ThreeD != "bl") p.ThreeD = "game";
            }
        }
    }

#if !LINUX
    /// <summary>Watches running processes and switches the headphone profile to match the game being played.</summary>
    internal sealed class ProfileManager
    {
        readonly AppCore core;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 3000 };
        public string ActiveName, DetectedExe;
        public event EventHandler Changed;
        string appliedKey;
        readonly Dictionary<string, int> lastHeard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int PlayHoldMs = 15000;     // a pause between songs doesn't flip the EQ back and forth

        public ProfileManager(AppCore core)
        {
            this.core = core;
            timer.Tick += (s, e) => Check(false);
        }

        public void Start() { timer.Start(); Check(true); }
        public void Stop() { timer.Stop(); }

        ProfileSettings PS { get { return core.S.Profiles; } }

        public GameProfile Find(string name) { return PS.Profiles.FirstOrDefault(p => p.Name == name); }

        public void Check(bool force)
        {
            GameProfile target = null; string exe = null;
            if (PS.AutoSwitch)
            {
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pr in Process.GetProcesses()) { try { running.Add(pr.ProcessName); } catch { } finally { pr.Dispose(); } }
                HashSet<string> playing = null;
                target = GameProfiles.Pick(PS.Profiles, x => running.Contains(Bare(x)), hit =>
                {
                    // Music players stay open all day: only while they make sound (plus a short hold).
                    if (playing == null) playing = SafePlaying();
                    int now = Environment.TickCount;
                    if (playing.Contains(Bare(hit))) lastHeard[hit] = now;
                    int t;
                    return lastHeard.TryGetValue(hit, out t) && now - t <= PlayHoldMs;
                }, out exe);
            }
            else target = Find(PS.ManualProfile);
            if (target == null) target = Find(PS.DefaultProfile) ?? Find(GameProfiles.Movies);
            if (target == null) return;
            string dev = core.ProfileDeviceId();
            var corr = PS.ActiveCorrection(dev);
            string key = target.Name + "|" + (corr != null ? corr.Name : "") + "|" + core.GamePhonesId + "|" + dev;
            DetectedExe = exe;
            if (!force && key == appliedKey) return;
            appliedKey = key;
            Apply(target, exe);
        }

        static string Bare(string exe) { return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe; }

        static HashSet<string> SafePlaying()
        {
            try { return Interop.AudioSessions.Playing(); }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>Re-applies the current profile (after edits, correction toggle, device change).</summary>
        public void Reapply() { appliedKey = null; Check(true); }

        void Apply(GameProfile p, string exe)
        {
            ActiveName = p.Name;
            core.PushGameParams();   // 3D dynamics follow the profile (natural for films and story games)
            string dev = core.ProfileDeviceId();
            if (dev != null)
            {
                p.Eq.Extra.Clear();
                var corr = PS.ActiveCorrection(dev);   // these headphones' own correction
                if (corr != null)
                {
                    foreach (var f in corr.Filters) p.Eq.Extra.Add(f.Copy());
                    p.Eq.ExtraName = corr.Name ?? "Headphone correction";
                }
                else p.Eq.ExtraName = "";
                p.Eq.AutoPreamp = true;
                core.S.OutputEq[dev] = p.Eq;
                core.OutputEqChanged();
            }
            // Games with their own HRTF play plain stereo straight to the headphones; the rest go through 3D.
            // (The routing timer retries until Windows accepts it, i.e. once the game has opened audio.)
            core.RouteBypassApps();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }
#endif
}
