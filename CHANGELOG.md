# Changelog

## 4.0.7

- Presets together (Headphones page, next to "Game profiles"): play several presets at the same time - your game's preset (CS2...) on the game's sound and Music's on Spotify (or Chat's on Discord) - instead of one preset for everything. Needs Auto EQ and 3D audio on: games play through StreamLite, music and chat straight to your headphones. Game sound hears exactly its own preset; the headphone correction stays under both.
- Personalize 3D: every footstep starts with you facing forward (you can still look around while you listen).
- The setup's Personalize 3D page shows the sphere (not the old cube) - drag it to turn it - and Start zooms into it.
- The setup picks up where you left off: leaving half way and coming back skips the steps already done. No extra "Your headphones" question before the EQ; Personalize 3D's countdown is shorter.
- Find my headphones: a backup for models AutoEq hasn't measured - squig.link's ~150 reviewer databases (thousands more models, gaming headsets like the HyperX Cloud III, Razer BlackShark V3, Kraken V4, Corsair HS80, Logitech G435 included). StreamLite makes their EQ from the raw measurement against AutoEq's Harman target. Also used by the setup and when new headphones are plugged in.
- Find my headphones: "Missing? Report it" opens a ready-made report on StreamLite's GitHub page (you send it yourself).
- Still required: anyone installing or updating to 4.0.5 or later sets up the whole app when StreamLite first opens.

## 4.0.6

- Personalize 3D plays real recorded footsteps: Counter-Strike 2's own, read from CS2 on your PC while the test runs (never copied or shipped). Without CS2 it uses StreamLite's footsteps as before.
- The footsteps are someone walking in one spot: 17 different recorded steps at a walking pace, never the same one twice in a row, left and right feet slightly different - not one step on repeat. They stay fixed around your head in one of the six directions.
- In the setup, Personalize 3D has its own page: a live copy of the 3D Audio page's diagram (drag it to turn it), what happens, and Start. Start zooms into that copy and goes straight into the test - the settings stay hidden, instead of the 3D Audio page showing first.
- Still required: anyone installing or updating to 4.0.5 or later sets up the whole app (raw mic voice tuning, headphone EQ, 3D audio) when StreamLite first opens.

## 4.0.5

- **StreamLite takes your mic's raw sound.** Windows runs effects on a microphone before any app gets it - its own
  enhancements and whatever your mic's maker installed. On a Yeti with Logitech G HUB, "Blue VO!CE" gated the room down
  to digital silence (-149 dBFS) before StreamLite ever heard it, so Tune to my voice measured a cleaned mic, not yours.
  StreamLite now asks Windows for the mic's raw stream, which skips all of them (Blue VO!CE, NVIDIA / Realtek / Dolby
  voice effects, Windows' enhancements, Equalizer APO) - the same Yeti now gives StreamLite the real room (-42 dBFS,
  fans and all), and StreamLite's own noise removal does the cleaning. On by default for every mic that offers a raw
  stream (most do); "Raw mic" on the Microphone page turns it off.
- **Everyone sets StreamLite up again.** Because everything is now tuned from the raw mic, updating from any version to
  4.0.5 (or later) starts every voice and 3D setting fresh and runs the full setup: your mic and headphones, your voice
  from the raw mic, your headphones' EQ and 3D audio. New installs get the same setup. It counts as done when
  Personalize 3D has finished; until then StreamLite starts it again every time it opens.

## 4.0.4

- **The setup opens fullscreen on your primary monitor.** The first time StreamLite opens (and for the 4.0 retune), its
  window fills your primary monitor and a setup screen covers all of its settings: "Setting up StreamLite" at the top,
  the five steps along the bottom (mic & headphones, headphone EQ engine, your voice, headphone EQ, 3D audio - done,
  now, still to come), and each step's window in the middle. Personalize 3D flies in from there; when the setup is over
  (or you choose Later), StreamLite becomes a normal window in the middle of your primary monitor.

## 4.0.3

- **Installing keeps your headphones and mic as Windows' defaults.** Installing the virtual cables could make them the
  default devices - "StreamLite - Out" for sound, "Hi-Fi Cable Output" as the mic - so until StreamLite was opened,
  games, Discord and everything else played into nothing and heard no mic. The installer now remembers your default
  playback and mic (for apps, media and voice chat) before installing, and puts back any a cable took over. A cable
  you had chosen as the default yourself stays.

## 4.0.2

- **Equalizer APO no longer pops up its own window.** Its installer always opened its device list ("Testing APO
  installation on devices...") and waited - confusing, and it put it on devices StreamLite doesn't need it on. Now
  StreamLite closes that list the moment it opens and switches Equalizer APO on for your headphones itself, the same way
  that list does (your headphones' own effects keep running inside it, and switching it off puts everything back).
  One Windows permission prompt, no restart - only your sound stops for a moment while Windows' audio restarts.
- **Set up when you first open StreamLite.** A guided setup, step by step: pick your mic and headphones -> Equalizer APO
  installed and switched on for those headphones (and off your mic: StreamLite cleans the mic itself) -> Tune to my
  voice -> your headphones' EQ -> Personalize 3D. Updating from before 4.0 still asks "Reset settings for improved
  tuning technologies?" first, then the same steps.
- Headphones page: "Install Equalizer APO" installs it and switches it on for the headphones shown, and "Switch it on
  for these headphones" does it without the device list (which still opens if Windows doesn't allow it).

## 4.0.1

- **Reinstalling after a full uninstall installs the virtual cables again.** Removing the cables leaves an empty driver
  entry behind in Windows until it's cleaned up, and the installer took that for "already installed" - so after
  uninstalling with the cables and installing again, StreamLite - In / Out were missing. It now checks that the
  driver itself is there.

## 4.0.0

- **A fresh start for the new tuning.** Updating from an older StreamLite asks once: "Reset settings for improved tuning
  technologies?" Yes walks you through it, one step after the other:
  1. pick the microphone you talk into and the headphones you listen on;
  2. Tune to my voice - from your raw mic, every voice setting worked out fresh;
  3. "Retune 3D audio and EQ for your headphones?" - their EQ is found by every name Windows has for them (or you pick
     the model in Find my headphones) and put on;
  4. Personalize 3D (the sphere) with that EQ on: your head, head size, cue sharpening and floor contrast.
  Your devices, profiles, hotkeys, camera and headphone corrections stay; No leaves everything as it is.
- **Big green record button.** Each recording in Tune to my voice starts from a large green button in the middle: it
  counts down from 5, then you read the line out loud. It turns red while recording and says Done once there's
  enough of your voice.
- Lighter and easier to read inside: leftovers of replaced features removed (unused code, fields and settings helpers), methods renamed where their names hid what they do, and the Personalize 3D code about a sixth smaller.
- Everything from 3.5.7-3.5.9: the open sphere (no walls, no muffling), the sphere Personalize 3D that tunes
  everything, and headphone EQ found for many more devices.

## 3.5.9

- **More headphones get their EQ by themselves.** StreamLite now looks a device up by every name Windows has for it:
  the name it shows, your own name for it in Sound settings (a dongle you renamed "Momentum 5" is found as a MOMENTUM 5),
  and what the hardware itself reports (the USB product string behind "USB Audio Device", the driver's name).
- Smarter matching of Windows' names to measured models: Bluetooth LE names ("LE_WH-1000XM5"), short forms
  ("Bose NC 700 HP", "MOMENTUM TW 3"), names without the brand ("G733 Gaming Headset", "PRO X Wireless Gaming Headset"),
  brands of two words ("Bang & Olufsen", "Audio-Technica"), model numbers written together or apart ("HD 660S" /
  "HD 660 S"), and over-ears vs earbuds of one line ("QC Ultra Headphones" / "QC Ultra Earbuds").
- Never a wrong model: a word must start where the model's word starts ("PRO X" no longer matches "KZ EDX Pro"), and a
  model with more to its name is a different one ("A50" isn't the "A50 X") - then Find my headphones asks you.
- Device compatibility checked: every sample format a device can hand over (16 / 24 / 32-bit, 24 in 32, float) has its
  own test now.

## 3.5.8

- **Personalize 3D is a sphere.** You stand in the middle of a sphere and footsteps play on its edge from one of six
  directions around your head - in front, behind, left, right, above or below - in random order. The view isn't free
  any more: it snaps between those six (move the mouse, or use the arrow keys / WASD) and you click the one you heard.
  The footsteps stay fixed around your head, so turning the view only points. A small map in the corner shows which way
  you face; a miss turns the view to where they really were.
- **One shorter test that tunes everything.** No more quick / thorough choice and no walks: 48 footsteps find your head
  out of the 20 measured ones (each footstep goes to the heads still in the running), then 36 tune your head size, cue
  sharpening and floor contrast - all three, every time. About 4 minutes, saved for your headphones after each part.
- No walls in the test either: the footsteps play with no room, as on the open sphere.

## 3.5.7

- **Open sphere: no walls.** New switch on the 3D Audio page, on by default. Every sound sits on an open sphere around
  you with nothing in between:
  - no studio room reflections (the Room slider only counts with Open sphere off);
  - footsteps the game muffled behind a wall, a floor or a crate get their treble back - when a sound is darker than an
    open footstep, up to 12 dB over 1.8 kHz comes back, the same on every speaker so where the sound is doesn't move.
    Loud sounds (gunfire, explosions) and silence are left alone. It also works for games with their own 3D (CS2...):
    the same lift on both ears keeps their 3D as it is;
  - footsteps below you are no longer made muffled "like through a floor" - the below cue now only moves the ear's
    notches down, so steps under you are as crisp as steps beside you.
- StreamLite hears only the sound a game sends: how much quieter the game makes a sound behind a wall, and whether a
  7.1 mix carries height at all, stays the game's.

## 3.5.6

- **16D music.** New switch on the 3D Audio page (and a hotkey): Spotify and other music apps play through 3D audio and
  the song slowly circles your head - "16D audio" on any song. It runs while music plays and no game is running (a game
  always gets normal 3D). Uses the true 360° renderer, so it moves smoothly with no clicks.
- **Tune to my voice works from your raw mic.** Every recording, the voice analysis (pitch, S sounds, levels, room,
  hum) and your voiceprint now start from what the microphone itself picks up - before noise removal, gate, EQ, mute
  or push to talk - so the settings it makes never depend on what you had set. It also works with the mic bypassed or
  the virtual mic off (switched on for the recordings, then back off). Your voiceprint is made from the raw takes with
  noise removed the way Only my voice will hear you. A quiet voice in a noisy room is found by a speech detector, not
  just by loudness.
- **Lock in (hotkey).** Press it in a game: every other app (Discord, music, browser...) drops to 50 % (or 25 %, 75 %,
  muted) while the game stays at 100 %. Press again and every app is back where it was. Also undone when StreamLite closes.
- **Find my headphones lists every model.** The Sennheiser MOMENTUM 5 is built in (StreamLite's own correction), and any
  model nobody has measured yet can still be picked ("use it without a correction"). Wireless dongles ("QCC Dongle Pro")
  no longer fill the search box with the dongle's name - it asks for your headphones' model instead.
- **Admin rights are asked for, not explained.** StreamLite Camera sets itself up with one Windows permission prompt (and
  the installer now registers it), saving the headphone EQ asks Windows once instead of "Apply EQ now", and Settings >
  Run as administrator restarts with admin rights in one click.
- **Equalizer APO: "Enable on this device" works or says why.** A half-removed Equalizer APO (its config folder left,
  its program gone) now counts as not installed, and the button offers to reinstall it instead of doing nothing.
- Hotkeys: the action list is wide enough to read whole names; Lock in and 16D sit with Headphones & 3D.
- Health: a mic shared with other apps by choice is no longer shown as a warning ("starting" is gone from the latency
  line), and a missing virtual camera is a warning in the setup check, not a tick.

## 3.5.5

- **Uninstalling removes the virtual cables completely.** 3.5.3/3.5.4 deleted their driver packages, but VB-Audio's setup
  creates the cable devices by hand, so they kept running on the loaded driver and still showed in the sound lists
  ("Hi-Fi Cable Input", "CABLE In 16ch", "CABLE Input"). The uninstaller now removes the devices themselves first, then
  their drivers.
- Equalizer APO: its uninstaller is waited for, and when its files are still in use the restart message says so too.
- **When a restart is needed, it says so:** a driver that's in use (Hi-Fi Cable's usually is) only lets go when Windows
  restarts. The uninstaller then ends with "Restart Windows to finish removing the virtual cables" and offers to restart
  now.

## 3.5.4

- **Only StreamLite - In and StreamLite - Out - VB-Audio's name nowhere else.** The tidy-up (the installer, the
  one-time "Tidy up" offer, Settings > Audio devices, or StreamLite running as administrator) now also:
  - hides the mic feed ("StreamLite - Mic Feed": StreamLite writes into the cable driver directly; if that ever fails
    without admin rights, Windows asks once to switch it back on),
  - shows the cables as StreamLite Mic / StreamLite Game Audio in **Device Manager** too,
  - removes VB-Audio's leftovers StreamLite doesn't need: the **Settings > Apps** entries ("VBCABLE, The Virtual Audio
    Cable", "ASIO Bridge and Hi-Fi Cable" - StreamLite's uninstaller removes the drivers), the **Start menu** "VB Audio"
    folders and their setup programs in **Program Files** (the drivers themselves live in Windows' driver store),
  - offers to uninstall **Virtual Audio Cable (lite)** when it's on the PC (another maker's cable StreamLite never
    installs; its devices are hidden either way).
  VB-Audio stays credited in StreamLite's Credits. Voicemeeter and other VB-Audio products are left alone.
- **The tidy-up offer no longer gives up** when another note fills the banner (the setup check, an update): it asks again
  every 30 seconds, and Settings > Audio devices has a "Tidy up now" button while anything still shows. After a reinstall,
  its one Windows prompt also restores Run as administrator when that was on (reinstalling removed its startup task).
- **No cable stays a default device:** StreamLite now puts Windows' default mic back on StreamLite - In (when you chose
  that) or your real mic whenever another cable end sits there - a 3.5.2 uninstall left "Hi-Fi Cable Output" as the
  default mic, so apps set to Default heard nothing - and default playback never on the mic feed or another maker's cable.

## 3.5.3

- **Tune to your voice now tunes every voice setting.** It starts from scratch (Remove a specific sound and Only my
  voice off while you record; your old settings come back if you stop half way) and sets, from your recordings:
  - **noise removal** for your room (how far the room lies under your voice: 55% in a very quiet room up to 95% in a
    very noisy one), and **de-hum** when the room hums at 50 or 60 Hz,
  - **low cut** and **de-esser** for your voice (from your pitch; the de-esser only when your S sounds are sharp),
  - the **noise gate** 15 dB under your quietest voice (on only in a noisy room - in a quiet one noise removal leaves
    silence anyway),
  - the **compressor** from just under your normal voice, harder the further your loud and quiet voice lie apart,
  - **auto level**, **Across the room**, a clipping warning, **Studio voice** (it learns your mic again from the
    recordings) and your **voiceprint** for Only my voice.
  Measured offline: a quiet room got no gate and 55% noise removal; a noisy room with 60 Hz hum got de-hum, 85% and the
  gate at -47 dB (your quiet voice at -32 dB).
- **Uninstalling cleans up properly:**
  - It asks whether to also remove what StreamLite installed for itself - VB-CABLE, VB-Audio Hi-Fi Cable and Equalizer
    APO - so Discord, games and Windows no longer list "VB-Audio Virtual Cable" / "Hi-Fi Cable" (Voicemeeter and other
    VB-Audio products stay).
  - No cable stays a default device: 3.5.2 could leave "Hi-Fi Cable Output" as Windows' default microphone. Each default
    (playback and mic, every role) goes back to your real device.
- **Tests:** `test/VoiceSetupTest.cs` (a noisy room with hum, a quiet room), `test/MicPickTest.cs` (stopping the setup half
  way); `StreamLite.exe --uninstall --dry-run` lists every step, now with the default devices and drivers.

## 3.5.2

- **Tune StreamLite to your voice** (once after installing or updating - in place of the Direct to headphones
  question - and any time from the Microphone page): five short recordings, about a minute, two of them skippable:
  right at your mic, from further away, loud, quiet, and your silly voice (a high-pitched Elmo voice, a deep
  character voice...). StreamLite then sets itself up for you:
  - the low cut and de-esser for your pitch (80 / 100 / 120 Hz for low, medium and high voices; de-esser at 6 or 7 kHz),
  - how far auto level may lift your quiet voice,
  - Across the room on when you're a lot quieter from further away (8 dB or more),
  - a warning when your mic clips as you get loud,
  - your voiceprint for Only my voice, with your far and silly voices counted as you, and Only my voice one switch away.
  Measured with Windows' voices (low, medium, high): every setting came out as above.
- **Only my voice for higher and changing voices:**
  - Your thresholds are made for your voice when you learn it (how alike your own pieces come out), so a voice that
    varies more is never taken for someone else.
  - While the mic is turned down, the newest 0.4 s decides on its own whether it's you: you get back in sooner right
    after someone else (their sentence and its echo no longer hold you down). With both talking at once you stay on more
    (for example 75 -> 82% with a woman's voice learned and a man's in the room).
  - Tested with higher voices too: a high-pitched man (182 Hz) and a woman (184 Hz) in the same room - either one
    learned, the other gets through 17% (12-13% with their voice learned too); your own voice stays on 95-100%.
- **Only real devices and StreamLite in your lists:** besides renaming, the installer and the one-time "Tidy up" offer
  now hide VB-CABLE's spare end (CABLE In 16ch), the game cable's recording side and other virtual cables nothing is
  set to use (Virtual Audio Cable's lines, more VB-Audio cables) - never a default device, never Voicemeeter or
  Steam's. "Keep the device list tidy" (Settings) does it with one Windows prompt instead of asking for Run as
  administrator, and off (or uninstalling) brings everything back.
- **Tests:** `test/VoiceSetupTest.cs`; `test/VoiceLockTest.cs` now also with higher voices and a silly voice
  (`test/voices.ps1` speaks each voice higher too).

## 3.5.1

- **Device names for everyone:** 3.5.0's installer renamed the cables right after installing their drivers - before
  Windows had created the devices - so a new install kept VB-Audio's names ("CABLE Output", "Hi-Fi Cable"). It now waits
  for them (up to 30 seconds) and checks the names took. Copies that still show the old names (the portable zip, or a
  3.5.0 install) get a one-time offer at the top of the window: **Rename**, with one Windows permission prompt.
- **A mic that heard nothing:** when no mic was picked by hand, StreamLite took Windows' default mic only if it didn't
  look virtual, otherwise the first input in the list - which could be Stereo Mix or an empty line input. Since 3.5.0
  that also skipped software mics with a real mic behind them (NVIDIA Broadcast, SteelSeries Sonar, Elgato Wave Link).
  Now: Windows' default mic (software mics too), else the mic that was the default before StreamLite, else the most
  mic-like input - never a cable, Stereo Mix or line input first.
- **The Microphone page says when StreamLite hears nothing** from your mic (15 seconds of digital silence), with what to
  check, and when Only my voice turns down everything it hears (a voiceprint of someone else).
- **Reset voice settings** (Microphone page, next to Tuning & EQ): every voice setting back to how StreamLite starts
  out. Across the room, Remove a specific sound and Only my voice switch off; your mics, AI quality and anything recorded
  or learned stay.
- **Tests:** `test/MicPickTest.cs`.

## 3.5.0

- **Installing on friends' PCs:**
  - **Fixed:** "Error installing Update Checker - No mapping between account names and security IDs". The installer
    runs as the system account, and Equalizer APO's Update Checker tried to register itself for that account. Equalizer
    APO is now installed as the signed-in user.
  - **Device names:** apps now list **StreamLite - In / StreamLite - Out** instead of "CABLE Output (VB-Audio Virtual
    Cable)" / "Hi-Fi Cable" names. The installer renames them, because it has the rights to; StreamLite itself usually
    runs without them.
- **Your other devices keep working while StreamLite runs:**
  - Headsets called "... Virtual 7.1 Surround" (Razer, HyperX, Corsair) were taken for virtual cables and couldn't be
    picked as headphones or mic. Virtual devices are now told by their driver.
  - **Direct to headphones** is switched off once for everyone: it locked the headphones, so Discord or a browser set
    to them got no sound. It's still there (labelled "locks them"), and a hotkey can switch it on just while you stream.
  - Browsers and music can't end up on a muted output if Windows can't hand StreamLite each app's sound.
  - **Discord screen shares (measured):** sharing an app's window sends just that app's sound. A whole-screen share
    also picks up StreamLite's 3D copy of the game, unless Direct to headphones is on.
- **Fullscreen button** (and F11, or double-click the title bar).
- **Updates:** when a newer StreamLite is out, **Update to X** stays in the title bar, also after Skip.
- **Each mic keeps its own settings.** Plug in another mic and its own noise removal, EQ, Studio voice and levels load.
  **Settings > Mic presets:** export a mic's settings as a file (.slmic) and import it on a friend's PC, or put any
  preset on any mic, plugged in or not.
- **Headphones:**
  - **Per-headset EQ by itself:** the first time a headset plays, StreamLite looks its model up in AutoEq by the name
    Windows gives it, and uses the measured correction only when the model is certain. It picked the right model
    for 13 of 19 typical Windows names, and nothing for the other 6 (Realtek, USB Audio Device...). Every game
    profile then sits on neutral headphones.
  - **3D EQ and game EQ work together:** the 3D footstep EQ and the footstep booster give way where the game
    profile already lifts the same band. Before, they stacked up to 2.9 dB over the larger lift at 2.5-5 kHz (Footsteps
    preset + Apex profile); now at most 0.6 dB. Cuts stay. A game with its own 3D (CS2, Valorant...) plays as the game
    mixed it in Direct mode.
- **Personalize 3D:**
  - **Quick test:** 50 footsteps, about 4 minutes. Each footstep goes to the heads still in the running (adaptive
    search). The **Thorough test** (120 footsteps) is still there.
  - Your 3D sound **follows the test:** the best head so far is in use while you listen.
  - **Three real floors:** your floor is wooden and clickable right where the steps are, also right next to you.
    Downstairs (stone) shows through a glass circle at your feet, upstairs (carpet) through a glass ceiling. Footsteps
    only play where looking at them picks their floor. A label under the crosshair says what a click picks.
  - Simulated listeners: the thorough test finds your head 169/200, or yours or the close one 198/200. The quick test
    finds yours or the close one 183/200.
- **Across the room** (Microphone page, off by default): auto level may lift a far voice up to 40 dB, following your
  voice's average instead of its loudest syllables, and gives back the treble distance takes off. Measured with a voice
  3 m away (20 dB quieter, the room's echo as loud as the voice) and a fan: it came out at -20.3 dB instead of -26.5
  (at the desk: -19.9). Pauses stayed silent (-78 dBFS) and DNSMOS was unchanged. A room-echo remover was tried and
  left out: after DeepFilterNet it measured worse.
- **Only my voice** (Tuning & EQ > Noise removal): someone else in your room is turned down by who they are, not how
  they sound. 3D-Speaker's CAM++ model learns your voice from 15 seconds of talking. It then compares the mic's last
  second of voice with yours ten times a second (about 9 ms each, on the CPU) and turns the mic down when it's clearly
  someone else.
  - You can also learn their voice, which separates similar voices (two men) much better.
  - After a pause the mic starts open: your first word is never cut, and someone else gets through for about half a
    second at the start of their sentences.
  - Measured with Windows' voices: a man learned with a woman in the room, and the other way round. Your voice stayed on
    99-100% of the time and theirs got through 15-20%. With both talking at once you stayed on 75-78%. Through the
    whole chain (DeepFilterNet, a fan) it was 100% / 18% / 100%.
  - Two men: with only your voice learned, the other gets through 50-74%. With his voice learned too, 22-25%.
  - "Record a sound to remove" can't do this. It learns sounds, and two voices look alike to it.
- **Fixed:** the Camera page could log an error when a camera closed while its values refreshed.
- **Tests:**
  - `test/HeadsetEqTest.cs` (AutoEq by name, EQ blending, virtual devices).
  - `test/MicPresetsTest.cs`.
  - `test/FarVoiceTest.cs`.
  - `test/VoiceLockTest.cs` and `test/VoicePrintProbe.cs` (Windows' David, Mark and Zira voices, spoken by
    `test/voices.ps1`).
  - `test/ShareProbe.cs` (what a Discord screen share captures).
  - `test/HeadSearchDesigns.cs`, `test/ContrastSeeds.cs`.

## 3.4.0

- **StreamLite Camera works like an AI gimbal camera (Insta360 Link 2 style) with any webcam.** No motor needed: a
  "virtual gimbal" moves, zooms and turns a window inside your camera's picture. With a 4K camera, up to 2x costs no
  sharpness (zoom goes to 4x). Camera page > **AI tracking & framing**:
  - **AI tracking:** one person (the closest, or whoever you click on the preview) or a **group** (everyone stays in
    the picture). Small movements don't move the view; real moves are followed smoothly (follow speed). Lost you? It
    holds for 3 seconds, then shows the whole room until you're back.
  - **Framing:** head and shoulders, half body, whole body (from the size of your face) or your own zoom, plus
    headroom.
  - **Gestures** (beside your face, held half a second): open palm = tracking on/off, V = whiteboard on/off,
    L = zoom (move your hand up to zoom in, down to zoom out). MediaPipe's hand models on the CPU, about 4 ms per look,
    8 looks a second, only near a face.
  - **Tracking zone:** tracking never shows anything outside the area you mark. **Pause zones** (up to 6): step into
    one and the camera frames that area and holds still until you step out.
  - **Your own view:** drag the preview to move, mouse wheel to zoom, double-click for the whole picture; **6 presets**
    (also on hotkeys).
  - **Whiteboard:** found automatically (a bright board with clear edges, whole in the picture), squared off, the
    writing made darker and more colourful, never mirrored. **Desk view:** the area you mark, squared off.
  - **Level** (up to 15 degrees) and **upside down** (hanging or overhead cameras).
  - **Vertical 9:16 guide:** for TikTok / Shorts / Reels: the sides outside the middle 9:16 column are darkened, so a
    vertical crop always has you in it (the camera itself stays 16:9).
  - **Privacy:** apps see "Camera paused" instead of you (button, hotkey, or automatically when nobody's in front of
    the camera for 15 seconds).
- **Image:** **HDR** (local tone mapping: dark corners up, a bright window behind you down) and **face exposure** (auto
  light measures your face).
- **Hotkeys:** camera privacy, presets 1-6, whiteboard, desk view, group tracking, reset view, zoom in/out.
- **How:** the camera's effects graph now samples the picture through a perspective warp (GridSample) instead of a
  straight crop: the same picture when nothing is warped (55-76 dB PSNR against 3.3.1's models), 0.7-1.2 ms more per
  frame. Measured on the test PC's 4K camera: 30 fps, 10.7 ms per frame with every effect.
- **Tests:** `test/CameraDirectorTest.cs` (tracking, framings, zones, presets, level, whiteboard and desk view, gestures,
  whiteboard finder - no camera or GPU needed), `test/CamTrackLive.cs` (every mode on your real camera),
  `test/GestureProbe.cs`.

## 3.3.1

- **Your voice reaches the call up to 31 ms sooner.** In a call (DeepFilterNet on, Studio voice on, mic shared) the
  delay StreamLite adds went from about 91 ms to about 60 ms:
  - **Studio voice adds no delay** (was 11 ms). It used to rebuild every 10 ms of sound from its spectrum; now the
    correction is a 25-band graphic EQ whose gains are solved together (each band's filter overlaps its neighbours),
    and the missing treble is shifted up with a pair of allpass filters 90 degrees apart (44-64 dB of mirror image
    rejected). The spectrum is only listened to, on the side. It also matches better: cheap mics end up 3.5 dB from a
    good mic's tone on average (was 3.8).
  - **DeepFilterNet waits for each frame on a fast device** (10 ms less). After a minute in which every frame came back
    within 2.5 ms, the mic waits up to 3 ms for the frame it just handed over instead of keeping one in reserve
    (measured here: 0.9 ms on the graphics card, 1.2 ms on the CPU). One frame not back in time and it keeps one in
    reserve again, at once. Only with the mic's 10 ms packets (shared mode).
  - **The +10 ms after a slow start goes away again.** Late frames while StreamLite and the graphics card start up
    (the first 15 s) no longer count, and the extra frame kept after late frames is dropped after a minute in time
    (it used to stay for the whole session - it happened at nearly every start).
- **Personalize 3D:**
  - **Smoother:** the card's buttons are drawn with the scene. Real buttons on top made Windows recompose the window
    every time they appeared or went (a ~100 ms stutter each time).
  - **Can't get stuck:** if the graphics card can't draw it, the window comes back as it was with a message, and the
    picture shown during the jump to full screen goes after 1.5 s at the latest.
  - **Enter** presses the green button.
- **"Noisy room" from before 3.3.0 fixed in place:** its hard gate at -45 dB (it cut the quiet ends of words) and the
  +10 dB auto level limit (a quiet voice came out ~8 dB lower) become the new expander and +20 dB, once.
- health.log says "Noise removal turned off" (was "now runs on off").
- **Tests:** `test/Personal3DFlowTest.cs` plays Personalize 3D through (every round, buttons, pause, practice),
  `test/DfnBufferTest.cs` (DeepFilterNet's buffer), `test/SsbProbe.cs` (the treble shift).

## 3.3.0

- **"They hear my fan while I talk" fixed.** Two paths let a fan through every word:
  - **Voice styles switched the noise remover to RNNoise** (every style set it), until StreamLite restarted. Styles
    no longer touch it: voice isolation is always the one AI quality picks.
  - **Realtime noise removal is gone.** While you talked, the light RNNoise steered it and DeepFilterNet only cleaned
    your pauses.
  - **Measured** on a voice over a real PC fan (`test/FanLeakTest.cs`, DNSMOS overall / speech / background):
    DeepFilterNet 3.14 / 3.45 / 3.97, realtime 2.45 / 2.76 / 3.67, RNNoise 2.25 / 2.91 / 2.86.
  - **"Noisy room"** now goes near-silent between words smoothly (an expander, not a hard gate at -45 dB that cut quiet
    syllables), and auto level may lift a quiet mic by 20 dB (was 10: a quiet voice came out ~8 dB lower).
  - **A minute of talking** over the fan (`test/LongTalkTest.cs`): the voice level stays within 0.5 dB with every
    style. A voice that sinks the longer you talk comes from a call app's own automatic gain control on top.
- **New: Studio voice** (Microphone page, on by default). Makes any mic sound like a broadcast mic.
  - **How it works:** while you talk it learns how your voice comes through this mic (1/3-octave long-term
    spectrum, after noise removal) and compares it with speech through a good close mic: the standard speech spectrum
    (ANSI S3.5) with a broadcast mic's warmth, presence and air. The difference is the mic's and the room's colouring
    (thin, boxy, harsh, muffled) and it's taken out, smoothly, at most -10 / +8 dB.
  - **Treble rebuilt:** a mic that cuts the treble off (headsets, laptops, Bluetooth, cheap USB mics stop at
    7-13 kHz) gets it back. The octave below the cut is shifted up, phase-continuous, shaped to how speech continues
    and following every syllable.
  - **Plosive pops** ("p", "b" straight into the mic) are caught and their rumble taken out for that moment. A
    Linkwitz-Riley split means the rumble really goes (17 dB), and deep vowels aren't mistaken for pops.
  - **Learned** in about 5 seconds of talking, then adjusted slowly; kept per mic. "Learn again" starts over (after
    moving the mic or changing rooms). Adds 11 ms and 0.5% of one CPU core (no delay from 3.3.1).
  - **Measured** (`test/StudioVoiceTest.cs`: a real voice through simulated cheap mics; RMS difference from the good
    mic's tone, 100 Hz - 16 kHz):

    | Mic | Before | With Studio voice | Treble cut found | Treble above the cut |
    | --- | --- | --- | --- | --- |
    | Headset (thin, stops at 7 kHz) | 21.0 dB | 4.7 dB | 7.9 kHz | 31 dB lost, 30 dB back |
    | Laptop (boxy, harsh, stops at 10 kHz) | 13.6 dB | 5.8 dB | 10.8 kHz | 24 dB lost, 22 dB back |
    | Cheap USB (harsh peaks, stops at 13 kHz) | 6.1 dB | 2.3 dB | 14.6 kHz | |
    | Dull lavalier (muffled) | 16.4 dB | 2.4 dB | 8.5 kHz | 39 dB lost, 37 dB back |

    DNSMOS stays the same or goes up slightly (it hears only up to 8 kHz).

- **3D audio: 20 measured heads, every one at full resolution.**
  - Neumann KU100 and KEMAR are now their full SADIE II measurements: 8802 directions measured (1° steps on the
    horizon), 256 taps, 24-bit. Until now StreamLite used 242 directions, 192 taps, 16-bit. Where SADIE II's second
    measuring grid repeats a direction within 1° of the first it's left out (8162 used), so two slightly different
    measurements of one direction are never blended.
  - New: the 18 real human heads of SADIE II (H3-H20, 2114-2818 directions each). Ears differ, and the head closest
    to yours places sounds best. Pick one on the 3D Audio page, or let Personalize 3D find it.
  - The time between the ears is now found below 1.5 kHz, where the ears hear time. All 20 heads move smoothly, and
    looking up the nearest directions among 8802 is a dot product away.
  - True 360° rendering moves its responses with an exact frequency-domain shift (was a short windowed sinc that
    softened the top octave and cut off the start of responses that begin late, as SADIE's do): a speaker rendered
    either way now matches to -130 dB, and a sound circling you stays as clean as the speaker rendering.
  - Ships as `hrtf\` next to StreamLite (78 MB).
- **Personalize 3D, rebuilt:**
  - **Drawn by the graphics card** on every frame of your monitor: building a frame takes 0.2-0.3 ms (was 10-38 ms
    on the CPU, timer-paced). The window no longer resizes frame by frame either: the growing is drawn over a picture
    of your screen, so the whole animation runs at your monitor's refresh rate. Measured in the app on a 2560 x 1440
    480 Hz monitor: the animations 475 fps, looking around inside 480 fps (the slowest 1% of frames 11-15 ms).
    health.log notes the frame rates after each visit.
  - **Can't see where it was?** When the real spot is off the screen (behind you, straight up), a gold arrow at the
    edge points the way.
  - **Can't tell?** Right-click (or Space): it counts as a miss for the setting being tried, a gold beam shows you
    where the steps were and they play again from there.
  - **4 x 4 rooms** above and below (was 4 x 3).
  - **New footsteps:** a boot on a hard floor like a competitive shooter's, modelled physically: a sub-millisecond heel
    strike ringing 56 floor modes from 140 Hz to 11 kHz, a low thump, grit crunching under the sole, then the ball of
    the foot. Before, the energy was 81% boom below 300 Hz with almost nothing in between; now it's 24% thump, 59% knock
    and 17% crunch and air.
  - **A full personal setup in three parts (about 10 minutes):**
    1. Find your head: all 20 heads, blind, the same footsteps for every head (front/back, up and down, where heads
       differ most), narrowed to 6, then 3, then the winner.
    2. Tune it: your head size (the time between your ears, 88/100/112%), cue sharpening and floor contrast, each
       tried at three settings with the footsteps it changes most. Head size and cue sharpening are decided without
       each setting's single worst footstep (a lapse shouldn't decide).
    3. Follow footsteps that walk, the winner against the runner-up tuned the same way.
    Saved for your headphones after every part. The results show every head's average error.
  - **Practice:** footsteps anywhere, shown right after you answer.
  - **Tested with simulated listeners** (200 runs each): your head reaches the last round 181 times and wins 164 times;
    it or the one closest to it wins 195 times. A listener who places sides better with a bigger head gets 112% 153
    times; one who hears better with more cue sharpening gets it 179 times.

## 3.2.0

- **Mic: a clearly better voice.** Every version of the mic chain was judged with DNSMOS, Microsoft's voice-quality
  model trained on listener ratings (1-5).
  - **The cause:** the realtime noise removal (the default since 3.0.2) steers a 2.7 ms filter with gains that come
    late. On real voices it sounded clearly worse than the noise remover's own path:

    | | Realtime (3.0.2-3.1.1) | Own path (3.2.0) |
    | --- | --- | --- |
    | Your recorded voice, DeepFilterNet | 2.28 | 3.04 |
    | Six test clips (voice, fan, keyboard, TV, kids, room) | 2.95 | 3.24 |
    | What Discord received, live on the test PC | about 2.3 | 3.34 |

  - **The fix:** every install goes back to the noise remover's own path once (DeepFilterNet adds 50 ms). Realtime
    stays a choice for the lowest delay, and its help now says what it costs.
  - **The rest of the chain was measured too:** gate, expander, compressor, de-esser, EQ and auto level all change the
    score by 0.02 or less. They stay as they are.
- **Voice isolation is built in.** There's no noise-removal engine to pick any more (Microphone page and Tuning & EQ):
  StreamLite always runs the best one your PC can, set by AI quality (below). DeepFilterNet 3 on the graphics card;
  on the CPU with Full CPU; RNNoise only on CPUs with fewer than 12 threads.
  - **Next to a loud machine** (`test/WindTest.cs`: a voice beside a synthetic wind turbine with rumble, pulsing blade
    whoosh, gearbox tones and hum, as loud as the voice and 5 dB louder): DNSMOS overall 3.22 and 3.20, background
    4.2 of 5. In pauses the machine is 86-113 dB down.
- **New: AI quality** (Settings, replaces Low VRAM on the Microphone page and the GPU switches on the Camera page). One
  choice for voice isolation and the AI camera together. Each step up looks and sounds better:

  | | Video memory (measured, `test/VramProbe.cs`) | Voice isolation | AI camera |
  | --- | --- | --- | --- |
  | Low VRAM | about 210 MB | DeepFilterNet 3, 16-bit | effects in 16-bit at 1080p |
  | Normal | about 430 MB (490 MB from a 4K camera) | DeepFilterNet 3 | full effects, 1080p or from 4K |
  | High VRAM | about 860-890 MB | DeepFilterNet 3 | the cut-out worked out at full HD instead of a quarter of it: cleaner hair and edges, fewer halos (20 ms a frame instead of 11) |
  | Full CPU | none (slowest) | DeepFilterNet 3 on the CPU | effects on the CPU; the camera plays without them when they can't keep up |

  Existing installs keep what they had (Low VRAM stays Low VRAM, DeepFilterNet on the CPU becomes Full CPU); everyone
  else gets what fits the graphics card (up to 4.5 GB: Low VRAM; 7.5 GB or more: High VRAM).
- **Other apps can use your raw mic again.**
  - 3.0-3.1 always opened the mic exclusively (its smallest packets), so Discord set to the real mic got nothing.
  - New switch on the Microphone page, "Let other apps use the raw mic too", on by default: StreamLite opens the mic
    shared (Windows' 10 ms packets). Mic to cable is 22 ms instead of 7 ms.
  - Off: exclusive as before.
- **Shared-mode mic: no more stuck backlog.**
  - The cable writer topped up after each packet and steered the clock drift only in exclusive mode. In shared mode a
    start-up backlog stayed in the buffer (48 ms seen). Now it always does: 0.2 ms in the buffer.
  - The first 5 seconds of a stream now steer without learning the clocks' difference. Uneven start-up packets used
    to wind it up to 2600 ppm, with about 25 fill-ins.
- **New: Record a sound to remove** (Tuning & EQ > Noise removal).
  - **What it's for:** a sound the noise remover keeps, such as a TV, kids, music or a dishwasher. Voices and music are
    exactly what it's built to let through.
  - **How to use it:** let the sound play, stay quiet and press Record. For 8 seconds StreamLite learns what the noise
    remover leaves of that sound, as spectral patterns (non-negative matrix factorisation).
  - **How it works:** every 10.7 ms your mic is split into that sound and your voice, and the sound is taken out. It's
    pushed down hard where it's alone and gently while you talk.
  - **Your voice:** learned in the background from moments the noise remover calls speech (about a minute of talking),
    so it isn't mistaken for the sound. Until then, built-in voice patterns stand in.
  - **Delay:** adds 21 ms while it's on.
  - **Measured** (`test/NoisePrintTest.cs`, a voice 5 dB above the sound, learned voice):

    | Sound | Left in pauses: DeepFilterNet alone | + learned sound | Voice SI-SDR alone | + learned sound |
    | --- | --- | --- | --- | --- |
    | TV (voices, music) | -2.0 dB | -10.2 dB | 2.4 dB | 6.6 dB |
    | Kids | -0.2 dB | -9.5 dB | -0.7 dB | 1.1 dB |
    | Music | -55.3 dB | -62.0 dB | 10.6 dB | 10.5 dB |
    | Fan | -50.5 dB | nothing to learn | | |

  - **When there's nothing to learn:** for a sound like a fan, Record says the noise remover already takes it out.
- **Personalize 3D: a first-person listening test, inside the radar.**
  - **In the app:** Personalize on the Live sound radar grows the window to fill its monitor, zooms into the radar's
    centre and flies into the building drawn in the radar's own style. No separate window. Leaving plays it backwards
    and puts the window back where it was.
  - **The building:** you stand in the middle of a building and can only look around. Upstairs and downstairs have 12
    rooms each, seen through your glass ceiling and floor. Your own floor is one open room.
  - **Round 1:** footsteps play at exact spots in metres. Look where you hear them and click:
    - a room above;
    - a room below;
    - a wall, for "on my floor, that way".

    24 footsteps, blind, with both head models and two cue sharpening levels mixed in.
  - **Round 2:** footsteps walk, and you follow them with the crosshair. It compares the winner with one alternative
    (cue sharpening and floor contrast).
  - **The result:** the head model, cue sharpening and floor contrast that let you place sounds best, saved for those
    headphones.
  - **Exact placement:**
    - Each step is rendered for where your head points the moment it lands.
    - Head responses between the measured directions are new. The four nearest measurements are lined up by their
      onsets and averaged, with exact frequency-domain shifts.
    - The time between the ears now moves smoothly, at 0.43 samples per degree on average. Nearest-only jumped up to
      3.6 samples.
  - **Light:** flat polygons, redrawn only when you look around.
  - **Tested with simulated listeners:** one that hears better with KEMAR gets KEMAR 187 times in 200, and one that
    mixes up floors gets more floor contrast every time.
- **Direct to headphones:**
  - **Music and voice apps cut out every few seconds.** The mixer looked for apps (10-20 ms of process and session
    lists) on the thread that moves the sound. That now has its own thread. The sound moves when Windows signals a
    packet, with a 100 ms capture buffer. A browser playing Spotify went from 11 gaps a minute to none.
  - **Voice chat played through 3D.** Starting 3D audio first pointed Windows' communications device at the game
    device. Discord followed it and stayed there, so calls went through 3D.
    - Now that device goes straight to the side output.
    - Routed apps also get their own communications device.
    - An app that also plays into the game device isn't added a second time.
  - **0x8889000A while Direct to headphones held the headphones:** the voice test, the old listening test and hearing
    yourself failed with this error. They now play through 3D audio's own stream.
- **Themed everywhere:** every message and question is StreamLite's dark dialog (no grey Windows boxes), and the
  lists in dialogs (credits, cameras, setup import) scroll with the slim dark scrollbar instead of the Windows one.
- **status.txt:** a mic levels line with the loudest level after each stage (raw, noise removal, gate, sent) and how
  often the gate was open.
## 3.1.1

- **Mic to cable: one safety margin instead of two.** Measured on the test PC: 6.6 ms instead of 9.5-10 ms.
  - **What changed:**
    - The mic's buffer used to keep about 4 ms for the mic driver's late packets.
    - The cable writer kept its own 2 ms lead on top of that.
    - Now the cable's lead is the only margin, and the drift between the mic's clock and the cable's is steered from
      it.
  - **A late packet:**
    - The cable's lead rides it out first.
    - If the cable is about to run out, StreamLite fills in by repeating your voice's last pitch period. There's no
      fade to silence.
    - When the packet arrives, the part that was filled in is skipped, so the delay doesn't grow.
    - Short fill-ins (up to 8 ms) don't count as gaps and don't add buffer. That growing buffer was where the 4 ms
      came from.
  - **Clock drift:** the steering from the cable's lead learns the clocks' actual difference (proportional plus
    integral) and remembers it per mic, so it's right from the first second. A one-off (a late packet, the cable
    running ahead) hardly disturbs it, and a fill-in gives back up to 1 ms of lead at once.
  - **Fill-ins:** they start without a step: the first samples are corrected towards where the sound was heading.
  - **Live on the test PC** (Yeti, 20 minutes):
    - steady 6.6 ms mic to cable
    - 3 short fill-ins
    - no dropouts, no gaps
  - **Simulation (`test/ConcealRingTest.cs`):**
    - 3 ms mic packets, with late packets like the Yeti's
    - the clocks 150, 1000 and 2000 ppm apart in either direction
    - VB-CABLE now and then running 1.5 ms ahead

    | | Old | New |
    | --- | --- | --- |
    | Delay in the buffers | 7.2-10.1 ms | 4.8 ms |
    | Silent holes | 2-13 | 0 |
    | Buffer floor | up to 5.1 ms | 0.7 ms |
    | Cable played past what was written | never | never |
    | Clicks | none | none |

  - **Learned buffer:** the mic buffer floor learned by earlier versions is reset once.
- **Packets the mic's driver loses:** the device position jumps, and the sound that was never delivered is filled in
  the same way (`test/PlcTest.cs`: 16-28 dB closer to the lost sound than silence, no clicks). On the test PC the
  Yeti's gaps turned out to be late packets, not lost ones, but other mics (USB hubs, Bluetooth) do lose some.
- **status.txt and health.log** now show late packets filled in and packets the driver lost.
- **docs/driver-plan.md:** what a StreamLite audio driver would gain (about 12 ms more on the mic path and 9 ms on
  the game path) and what it takes: an EV code-signing certificate and Microsoft's attestation signing.

## 3.1.0

- **Direct to headphones** (opt-in; 3D audio > Output, and asked once after installing or updating):
  - **What it does:** StreamLite plays to your headphones in exclusive mode, past Windows' mixer, at their own
    smallest step (3 ms on most) instead of Windows' 10 ms steps.
  - **StreamLite's own mixer:** StreamLite is then the only thing playing to the headphones, so it mixes every app:
    - Games and everything else go through the 3D path as before.
    - Voice chat, music and browsers (the "keep out of 3D" apps) play to a side output, another output of this PC
      that StreamLite mutes meanwhile.
    - StreamLite picks each of those apps up with Windows' per-app capture and mixes them in as they are. They reach
      you about 40 ms after the game: that delay is Windows' own for per-app capture, and you won't notice it in
      calls or music.
    - Without a side output those apps go through 3D audio too.
  - **Your headphone EQ** runs inside StreamLite in this mode, since Windows' effects (Equalizer APO) don't run on
    exclusive streams.
  - **Measured on the test PC** (QCC Dongle Pro): StreamLite's buffer and output queue went from 21.9 to 16.5 ms.
    Windows' own 10 ms mixing step before the headphones is skipped as well; no app can measure that one from inside.
  - **Falls back by itself:** if the headphones can't run in exclusive mode (another app holds them, or Windows doesn't
    allow it), StreamLite plays the normal way and Health says why. The side output's mute is given back when the
    mode ends, also after a crash.
- **3D delay readout:** now measured, from the time sound waits in StreamLite's buffer and in the output queue. The
  old readout was an estimate.
- **Lighter default on smaller CPUs:**
  - DeepFilterNet on the CPU is now picked automatically from 12 CPU threads, up from 8. With realtime noise
    removal it costs about 20% of one fast core (RNNoise steers next to it); on a budget CPU with 8 threads that
    would be over the 6% budget.
  - Smaller CPUs keep RNNoise, which is much lighter. A Ryzen 5 5500 (12 threads) still gets DeepFilterNet.
  - Installs tuned before keep their settings.
- **Tests:** `test/AppMixerTest.cs` covers the mixer. It checks that apps keep their level, that drift correction
  adds no clicks, no gaps, two apps mixing, each process family captured once, and no allocations.

## 3.0.2

- **Realtime noise removal: 2.7 ms instead of 20-60 ms** (on by default; Tuning & EQ > Noise removal > Realtime).
  - **How it works:** your voice goes through a 2.7 ms filter instead of through the noise remover itself, which needs
    its own window and look-ahead (RNNoise 20 ms, Classic 21 ms, DeepFilterNet 50-60 ms). The noise remover still
    listens, on a copy, and steers that filter band by band with the gains it would have applied. A fast detector
    keeps the start of each word while the AI's gains catch up.
  - **With DeepFilterNet:** its gains come 50 ms late, so RNNoise steers while you talk and DeepFilterNet makes your
    pauses as quiet as it does. A pause is when either of them hears no voice: if RNNoise takes a steady noise for a
    voice, DeepFilterNet still removes it, as long as the voice band hasn't risen since it listened.
  - **Noise that starts after silence** (a fan switching on, a muted mic unmuted) is learned as the background
    within 150 ms. It isn't taken for a word starting and held open.
  - **Delay**, measured through the whole voice chain: 2.8-2.9 ms with every engine.
  - **Quality**, on recorded callouts over a fan with keyboard clicks (`test/NsRealtimeTest.cs`):

    | | Fan removed | Clicks removed | Voice intact (SDR) |
    | --- | --- | --- | --- |
    | RNNoise realtime | 38 dB (own path: 38.5) | 37.5 dB (38.8) | 22.8 dB (12.2) |
    | DeepFilterNet realtime | 52 dB (own path: 58) | 49 dB (37) | 21.5 dB (23.1) |

    Word starts keep their level within 1.6 dB. A fan starting after a second of silence: 20 / 38 / 49 dB removed
    (Classic / RNNoise / DeepFilterNet; their own paths 21 / 39 / 51).
  - **Noise removal switched off** now adds no delay at all. Before, the remover's window still delayed your voice.
- **No garbage on the audio threads.** A garbage collection pauses every thread for a moment, the mic's and the 3D
  audio's included, and the audio paths kept triggering them. Now the following allocate nothing while running
  (measured with .NET's per-thread counter, `test/AllocTest.cs`):
  - the mic chain with any noise remover
  - extra mics
  - 3D audio and added outputs
  - DeepFilterNet's inference
  - the camera loop

  Before, 3D audio created 287 KB of garbage a second, the mic page's spectrum graphs about 435 KB on the mic thread,
  and each AI inference fresh native call wrappers.
- **3D audio:**
  - **Renderer:** the 360 degree renderer's delay is halved, 1.3 instead of 2.7 ms. Placement is measured identical;
    the cost is about 2.5% more of one CPU core.
  - **Stereo/7.1 switches:** a game switching between stereo and 7.1 used to rebuild the 3D filters on the audio
    thread (3 ms, a dropout on slower CPUs). Each layout's filters are now built once and swapped in (0.55 ms, like
    any block). In the live test that flipped layouts constantly: 1 gap instead of 13, delay 22 instead of 33 ms.
  - **Sound resuming:** when game sound starts again after silence, the uneven first packets no longer count as gaps.
- **Mic capture:**
  - **Late signals:** if Windows' "packet ready" signal is late but the data is already there, the packet goes on
    at once.
  - **Timing log:** status.txt and health.log show the longest time between mic packets and our longest processing
    time. On the test PC that's 0.5 ms; gaps come from the mic's driver delivering a whole packet late, not from
    StreamLite.
- **Mic latency on the test PC** (Blue Yeti, DeepFilterNet on the GPU): 3.0.1 had 8.9 ms to the virtual cable plus
  50 ms of noise removal, about 59 ms. 3.0.2 has 8.8 ms plus 2.8 ms, about 11.6 ms. What remains isn't
  StreamLite's to remove:
  - the mic's own 3 ms packets
  - the buffer that bridges its late packets
  - the virtual cable's write-ahead: VB-CABLE moves in 1.5 ms steps
- **Across the three hardware tiers** (`docs/performance.md`):
  - Mic: 6.1 instead of 23.4 ms (53 with DeepFilterNet).
  - 3D: 11.9 instead of 13.0 ms.
  - No dropouts.
  - 12-20% more CPU, because the noise remover still runs in full to steer the filter.

## 3.0.1

- **Lower mic and game sound latency.**
  - **Mic buffer:** a short mic gap used to raise the buffer for the rest of the session (+0.5 ms each). On the test PC
    that was 5 ms after a long session. Now a gap is covered at once and the extra is given back within minutes.
    Only gaps that keep coming back (3 within 10 minutes) set a floor, sized to what bridges them. After 30 calm
    minutes a lower floor is tried, slowly; if that brings a gap, the floor that worked is back at once (no
    re-learning with more gaps). The floor is remembered per mic, so the next start doesn't re-learn it with gaps.
  - **3D buffer:** the same rules. Before, every gap was remembered across restarts, including a game's own sound
    pausing while it loads; the test PC had saved 12.75 ms of extra game delay. That is reset once with this update
    and learned again under the new rules.
  - **One-off pauses:** if the input itself stops for a moment, the cover is at most 4 ms. A bigger buffer can't
    help there and only adds delay.
  - **DeepFilterNet on the graphics card:** 10 ms less delay (50 instead of 60 ms). It now keeps 1 frame of buffer
    like on the CPU; it runs a frame in about 1 ms. If frames come back late (3 within 5 s, e.g. a game keeping the
    graphics card busy), it keeps 2 frames for the rest of that call. It switches during a pause in your voice, so
    you never hear it. Tested with the graphics card fully loaded: 3 late frames, then none.
- **No clicks on a gap.** A gap now fades out and back in over 0.7 ms instead of cutting to silence, so a rare
  one is a barely audible dip, not a click.
- **The Health page only warns about real problems.**
  - **Gaps:** each one is still counted, with its length and what explained it: a game loading (from your game
    profiles), the PC busy (over 85% CPU), or the game's own sound pausing. A warning appears only when unexplained
    gaps keep coming (5 within 10 minutes), at most every 30 minutes, with what usually causes them. A one-off or a
    game loading is no longer reported as an error.
  - **Devices:** an added mic or output that drops out and is back within 10 seconds (Bluetooth reconnecting, USB
    re-enumerating) isn't reported. Errors that clear by themselves within 8 seconds aren't either.
  - **DeepFilterNet:** resting between calls and waking up is normal and no longer listed.
  - **Shared mode:** the mic running in shared mode is now a note, not a warning: about 10 ms more delay, nothing
    breaks.
- **health.log** (next to the settings) records everything, routine events included, with time and details.

## 3.0.0

- **Self-calibrating multi-mic.** With two or more microphones, **Calibrate...** in the Microphones list runs a
  20-second test. Everyone is quiet for 3 seconds, then each person talks on their own mic in turn. StreamLite
  measures:
  - each voice's level and each room's noise
  - how much every mic hears the other people (the "bleed" between desks)

  It then suggests volumes that even the voices out, and turns on a smarter auto mix. That mix takes the measured
  bleed out of each mic's level, so a mic gets the mix for its own person's voice only. You see the result first:
  levels, bleed, the doubling left before and after, and how two people talking at once are kept. You choose whether
  to use it, and every volume stays editable afterwards.
  - Tested with two mics hearing each other at -4 and -6 dB, one person 8 dB quieter. The copy of a voice from the
    other mic drops from +12 dB with the plain auto mix to -21 dB. Two people talking at once both keep at least 37%
    of the mix.
  - It allows for the added mics arriving 10-20 ms after microphone 1 (their sync buffer and the distance between
    desks), so the end of a word isn't doubled either.
- **Outputs in sync.** Bluetooth and some USB headphones play later than others, so two people heard the same game a
  moment apart. **Sync outputs...** in the Outputs list fixes that, and the delays are saved per device:
  - **Measure:** hold each output's headphones against your microphone and click Measure. StreamLite plays a few
    clicks on that output only, through its real path, and times them with the mic (to the millisecond in the tests).
  - **Line them up:** holds the faster outputs back, so everyone hears the same moment.
  - **By ear:** play test clicks on every output at once and move each output's delay slider until you hear one click.

  On the test PC the speakers played 33-37 ms after the USB headphones. A 100 ms delay setting moved them by 99-101 ms.
- **Health page.** A new page shows what StreamLite is doing right now, refreshed every second:
  - **Microphone:** mode (exclusive with its packet size, or shared and why), and real latency (capture + buffer +
    cable, plus the noise remover's own delay).
  - **Dropouts** since start and the buffer kept in reserve.
  - **Noise removal:** the engine really running (DeepFilterNet on the GPU / CPU with its time per frame, or RNNoise
    standing in and why).
  - **Added mics and outputs:** their state.
  - **Game audio:** delay and dropouts.
  - **Camera:** frame rate.
  - **Resources:** StreamLite's own CPU, RAM and video memory.

  Below it, **What happened** explains in plain words every time StreamLite switched to a slower or less capable path,
  or back. For example: the mic went to shared mode because another app has it, which adds about 10 ms. **Copy** puts
  everything on the clipboard for a bug report.
- **One-click setup check** (top of the Health page). It checks everything between your mic and your friends and
  between your games and your ears:
  - the drivers (VB-CABLE, Hi-Fi Cable, Equalizer APO and whether it's on for your headphones)
  - the physical mic (running, not blocked by Windows privacy, not sending pure silence)
  - the virtual mic **end to end**: a short test tone is recorded back from "StreamLite - In" exactly as Discord would
    get it. This only happens while no app is recording your mic, so nobody hears it.
  - its Windows volume
  - your headphones **end to end** (a quiet tone through the game device, recorded back from the headphones)
  - Windows' default devices
  - which mic **Discord and OBS really record from**, read from Windows' audio sessions
  - the camera

  Every problem comes with its fix: install the missing driver, open the right settings, set the volume to 100,
  restart the mic or 3D audio, or exactly which menu to change in Discord / OBS. After an update StreamLite runs the
  check once by itself (without tones) and says so in the window if something broke.
  `StreamLite.exe --setup-check` writes the full check to `setup-check.txt` for bug reports.
- **Compare noise removal on the same recording.** The voice test on the Microphone page has a new **Compare** row:
  - **Compare:** play your recording with another noise remover (none, RNNoise, Classic, DeepFilterNet on the CPU or
    GPU) and switch while it loops - the same sentence, back to back. Your live mic keeps its settings until you click
    **Use this one**.
  - **Same loudness** (on by default) plays every version, and the Original, at your recording's speech loudness, so
    you judge the sound and not the volume. The window shows the gain it uses.
  - Your recording is kept on this PC (`voice-sample.wav` next to the settings, never sent anywhere), so you can compare
    again another day.
- **Personalize 3D** (3D Audio page). A 3-minute blind listening test.
  - **Each trial:** footsteps play from in front, behind, left, right, above or below, each time with a different
    setting in random order, and you click where you heard them.
  - **Round 1:** no 3D at all (left/right panning only), the Neumann KU100 head and the KEMAR head. Plus the footsteps
    **as a 7.1 game sends them**: panned between speakers, through StreamLite's real game path with your settings. It
    shows how well you place game sounds - left/right and front/behind come through, above/below can't.
  - **Round 2:** the head that worked best for you with cue sharpening off, 35% and 70%.
  - **Fair comparison:** every version plays at the same loudness, so louder never wins by accident.
  - **Result:** how many you placed right with each setting, your front/behind and above/below mix-ups, and whether 3D
    actually helps you compared with no 3D.
  - **Saved per pair of headphones:** the best setting switches with your headphones, and changing the head model or
    cue sharpening by hand updates it.
- **What the game really sends** (3D Audio page). StreamLite now says exactly what it gets from the game playing right
  now, read from the channels that carry sound:
  - **A 7.1 / 5.1 channel mix**, with the channels in use: StreamLite places each sound at its exact angle around
    you. A channel mix carries no height, so above / below only come from games with their own 3D.
  - **Stereo:** the game's own 3D (HRTF) or plain stereo, kept as it is - with a hint to set the game to 7.1.

  Some games can send "audio objects" with full 3D positions. Windows only hands those to spatial sound engines
  registered with Microsoft (Windows Sonic, Dolby Atmos, DTS), not to other apps, so 7.1 stays StreamLite's path.
  If Windows spatial sound is switched on for "StreamLite - Out" (games then mix their objects down before StreamLite
  gets them) or for your headphones (3D made 3D twice), the 3D page, the setup check and the Health page warn you and
  say how to switch it off. In the tests, a sound panned by a 7.1 mix comes out at the same left/right level as the
  head model placing it directly (4.3 vs 4.3 dB), and in front / behind each sound like their direct placement.
  More in [docs/game-spatial-audio.md](docs/game-spatial-audio.md).
- **Export and import your setup** (Settings > Your setup). A `.streamlite` file holds:
  - your mic chain (noise removal, gate, compressor, de-esser, auto level, voice EQ) and every microphone's volume and EQ
  - every output's volume, EQ and sync delay
  - your headphones' corrections and personal 3D
  - the 3D sound settings, game profiles with their apps, the apps kept out of 3D, hotkeys and the camera look

  Devices are kept **by name only**: no device IDs and no file paths (a custom head model or background picture stays
  on your PC), so the file is safe to share. StreamLite refuses to write a file that would contain either.
  **Import** shows what's in the file first, part by part, each with its own switch. Every device is matched by name on
  this PC ("Microphone (Yeti Nano)" finds "Yeti Nano") and you can pick another one or leave it out. Profiles merge:
  ones with the same name take the file's EQ and apps, yours stay. **Undo import** puts every setting back exactly as
  it was before.
- **Auto EQ when apps overlap** (Speakers & Headphones > Game profiles).
  - **Why this EQ:** under "Now using" StreamLite says which app picked the profile and what else is in use, for
    example "cs2 is running. Also in use: Spotify (Music), Discord (Chat) - lower in your priority order".
  - **Your priorities:** **Priority...** lists every profile with apps. The one higher in the list wins when several
    are in use: a game while it runs, music while it plays, a chat app while it's open. Move Music above your games
    if you want your music EQ while a game idles in the background. A game profile added later joins the games.
  - **No flip-flopping:** a switch happens once the new profile wins two checks in a row (3-6 seconds). A launcher
    or updater that's open for a moment no longer flips your EQ. Picking a profile by hand still switches at once.
  - **Same loudness for every profile** (on by default). Each profile's EQ changes the loudness a little, so going
    from a game to music could jump in volume by up to 2.3 dB with the built-in profiles. StreamLite measures each
    one with your headphones' correction under it (pink noise, K-weighted like a loudness meter) and turns the
    louder ones down to match, never up. In the test the spread drops to 0.4 dB. A profile of your own with a huge
    boost doesn't make every other one quieter.
  - Tested: a simulated 10 minutes with a game, Spotify and Discord overlapping switches exactly 3 times, once for
    each real change (game quit, music paused, music back) and never between songs. A switch mid-sound through the
    3D path (game settings to natural) makes no click, gap or level jump.
- **Tested on three hardware tiers, every release.** A new automated performance test (`test/PerfTest.cs`) runs
  everything at once in real time under a game's load:
  - the mic chain with two added mics
  - 3D audio with two added outputs
  - StreamLite Camera at 30 fps

  It does this on three tiers:
  - **low:** 2 CPU threads, no graphics card
  - **mid:** 4 threads, a 4 GB card
  - **high:** a 32-thread CPU with a 16 GB card

  It measures dropouts, processing time, latency, CPU, RAM and video memory, and compares each release with the one
  before. 3.0.0 vs 2.7.0:
  - no dropouts on any tier
  - mid and high use the same CPU and memory as 2.7.0
  - the low tier dropped from 57% to 13% of the CPU (see the camera below)

  The mic keeps real time on CPU cores 24x slower than the test PC's (7x with DeepFilterNet), and 3D audio on cores
  7x slower. Details and numbers: [docs/performance.md](docs/performance.md).
- **Defaults tuned from the measurements:**
  - **Camera without a graphics card:** the effects manage only 2-8 fps on a CPU, even a 16-core one, and took half
    the CPU. Now, when they can't reach 20 fps, StreamLite Camera plays your camera without effects at the full 30 fps
    (about 3% of the CPU) and says why on the Camera page and the Health page. On the test PC's webcam: 8 fps with
    effects on the CPU, 30 fps without them.
  - **DeepFilterNet on the CPU** stays the default from 8 CPU threads. It keeps real time even on 2 threads, but costs
    15% of one core, and the 8-thread rule keeps it under 6% of the CPU on budget CPUs while you game.
  - **4K camera capture** stays the default on a graphics card: it costs only 6% more GPU time than 1080p.
- **Fix:** with no game sound playing, the footsteps test (and the sync clicks) went to the headphones in 20-40 ms
  bursts. The buffers could run dry between bursts and drop bits of the sound. StreamLite now keeps them fed block by
  block.

## 2.7.0

- **Hotkeys (Windows).** A new **Hotkeys** page: bind a key, a key combination (Ctrl, Alt, Shift, Win + a key), a single
  key such as Left Ctrl or Caps Lock, or the middle / back / forward mouse button to any of 26 actions. They work while
  a game, Discord or any other app is in front, and the app still gets the key.
  - **Microphone:**
    - mute / unmute all microphones or any one of them (microphone 1, 2, 3...)
    - push to talk and push to mute (hold)
    - virtual mic, noise removal, noise gate, mic EQ, compressor, de-esser, auto level, auto mix and hear myself on / off
    - noise remover (next or a specific one), voice style (next or a specific one)
  - **Headphones & 3D:**
    - Game 3D audio on / off
    - 3D preset, headphone profile or Auto EQ (next or a specific one)
    - natural sound for videos on / off, headphone EQ on / off
    - test footsteps
    - mute / unmute any output
  - **Camera:**
    - background (next or a specific one)
    - face tracking, auto light and mirror on / off
  - **StreamLite:** show / hide the window.
  - Each hotkey row has the action, what it applies to (which mic, output, profile...) and its key. Click the key
    button and press what you want; Esc cancels. Two hotkeys on the same key are marked.
  - A small note near the bottom of the screen says what a hotkey did ("Microphone 2 muted"). It never takes the focus
    and can be turned off.
  - Mutes and fades are smooth (~5 ms), so muting never clicks. Push to talk acts the moment the key moves, even while
    StreamLite's window is busy. Mutes start off every time StreamLite starts.
- The Microphones list shows "muted" / "push to talk" on the mic rows, and `status.txt` shows mute states and how many
  hotkeys are bound.
- **Update now or Skip.** When a new version is out, StreamLite now asks inside its window, a few seconds after it
  starts (and every 6 hours while it runs): a prompt at the top of the window with **Update now** and **Skip**. It no
  longer installs updates by itself and no longer opens a separate window to ask.
  - Update now shows the download in the prompt, installs and restarts.
  - Skip doesn't ask again until a newer version is out; Settings > Check now still offers it.
  - While StreamLite runs in the tray, a note says an update is waiting, and the prompt is there when you open the
    window.
- **Settings can't silently reset any more.** If the settings file is damaged (half-written, or edited by hand),
  StreamLite now loads the last copy that worked instead of starting from defaults, and keeps the damaged file as
  `settings.broken-*.json`.

## 2.6.0

- **Several microphones as one (Windows).** Two people at two desks can each talk into their own mic on one PC, and
  Discord, OBS or any app hears both through "StreamLite - In".
  - The Microphone page now has a numbered **Microphones** list. Microphone 1 is your main mic. **+ Add microphone**
    adds the next one, and each row has its own device menu, live level, volume, **EQ** and **Remove**. Only the mics
    you added are listed. Removing microphone 1 moves microphone 2 up.
  - **Auto mix** (on by default) turns each mic up while its person talks and the others down. Each mic also hears the
    other person from a desk away, so mixing everything as is would sound hollow and doubled. Both people can still
    talk at once, and the background noise of several open mics doesn't add up (6 dB lower than a plain mix in the
    tests).
  - Every mic gets the same noise removal, gate, compressor and auto level. Each mic's own EQ applies before mixing;
    Tuning & EQ then applies to everyone.
  - Every mic has its own clock, so each added mic is read through its own drift-corrected buffer: no clicks and no
    slowly growing delay. Added mics stay shared, so other apps can still use them. Unplugged mics wait and join again
    when they're back.
- **Several outputs (Windows).** Play your PC's sound on two pairs of headphones (or headphones and speakers) at once.
  - Speakers & Headphones has a numbered **Outputs** list. Output 1 is your headphones. **+ Add output** adds the next
    one, and each row has its own device menu, volume, **EQ** and **Remove**.
  - With two or more outputs, StreamLite makes "StreamLite - Out" the Windows default, for voice chat too, and plays
    everything on every output: games, Discord, music, videos. Nothing is sent straight to your headphones only.
  - With Game 3D audio on, every output gets the 3D sound; with it off, plain stereo.
  - Each output has its own drift-corrected buffer, which starts from the safety margin your PC has already learned
    for 3D. An added output's EQ is applied by StreamLite itself, so it needs no Equalizer APO setup.
  - Remove the extra outputs (or quit StreamLite) and Windows goes back to your headphones.
- When a recording stream starts, Windows hands out its first packets unevenly for about a second. Gaps in that first
  second no longer count as dropouts or permanently add delay; the mic used to log 1 or 2 right after StreamLite started.
- `status.txt` lists the added mics and outputs with their state and dropouts.
- **Fix:** if StreamLite started at the moment the hidden mic feed was switching state (for example during a quick
  restart), it could pick "StreamLite - Out" (the game device) as the mic's output and keep it. Your voice then went
  into the game sound, and games couldn't play while the mic held that device. The game device is now never
  used for the mic, and a setting saved that way repairs itself on the next start.

## 2.5.0

- **Test footsteps walks all around you, on three floors.** One button now plays someone walking a full circle around
  you at ear level, then a full circle on the floor above you, then one on the floor below you (16 steps per circle,
  clockwise from the front). Every step is a point source at its exact angle and height, straight from the measured
  head responses, and the sound cube lights up where it is. The separate "360° walk" and "above / below" buttons are
  gone. With a custom 7-speaker HeSuVi file (no heights measured) it walks the ear-level circle.
- **Always true 360°.** The "7 virtual speakers" mode, the speaker angle sliders and the 360° precision slider are
  gone: every surround game is placed at the exact angle it meant (0.25° steps, full precision), and "Play stereo in 3D"
  uses the same renderer.
  Games still hand StreamLite their sound as 7.1, because that is the only multichannel format Windows games can send to
  an audio device. That's only the delivery: StreamLite works out each sound's real direction from it and doesn't play
  7 speakers. 7.1 carries no height, so in games you hear above and below from games with their own 3D audio (CS2,
  Valorant...). Cue sharpening deepens their height cues.
- **Auto EQ.** The profile menu (Headphones page and tray) starts with **Auto EQ (follows your apps)**: your headphone EQ
  follows the app you're using. **Auto EQ apps...** lets you add any program to a profile (typed, or picked from the apps
  running now), remove it or move it to another profile. You can also pick the profile used when none of them runs.
  Updates keep your changes. Windows plays every app through one headphone EQ at a time, so when several are in use the
  game wins, then music, then chat.
- **No more latency switches.** "Lowest mic latency" (Settings) and the 3D "Low latency" switch are gone: both always
  run at the lowest delay this PC keeps up with.
  - 3D audio queues one Windows audio period + 2 ms for the headphones (was 1.5 periods) and starts with a 1 ms margin
    (was 3 ms), with Windows' "Pro Audio" priority. That's about 5 ms less than the old default.
  - After a dropout, the margin no longer slips back to the size that just failed 30 seconds later. That made a mic
    dropout about once a minute on some PCs. The 3D margin saved for the next start now also comes back down after 10
    minutes without a dropout, so one old hiccup doesn't add delay forever.
- **AI settings that fit your PC.** On the first start StreamLite looks at your CPU and graphics card once:
  - With 8 or more CPU threads, RNNoise moves up to DeepFilterNet 3 on the CPU (under 2% of the CPU, and only while
    an app records your mic).
  - Graphics cards with up to 4 GB get Low VRAM.

  A noise engine you picked yourself stays. `status.txt` shows what was found ("hardware tuning").
- `status.txt`: the 3D line also shows its delay and CPU use.

## 2.4.0

- **Mic latency down from ~35 ms to ~7.5 ms (Windows).** Your voice reaches the virtual cable about 4.5x sooner.
  Measured on a Blue Yeti, from the moment the mic records a sample to the moment it sits in the cable, while the
  app runs:

  | | Before | 2.4.0 |
  |---|---|---|
  | Waiting in the mic's packet | ~5 ms (10 ms packets) | ~1.5 ms (3 ms packets) |
  | StreamLite's buffer | ~18 ms | ~2 ms |
  | Written ahead into the cable | 8 ms | ~4 ms |
  | **Mic to cable** | **~35 ms** | **~7.5 ms** |

  - The mic runs in **exclusive mode** at its smallest period (3 ms on most USB mics, instead of Windows' 10 ms shared
    packets) and each packet goes into the cable the moment it arrives.
  - The mic threads get Windows' "Pro Audio" priority.
  - If the cable ever catches up, StreamLite adds 0.5 ms of safety by itself and gives it back after 20 calm seconds.
  - Settings → Audio devices → **Lowest mic latency** (on by default). While it's on, other apps can't open the
    physical mic directly; pick "StreamLite Mic" in them (that's your cleaned mic anyway). If another app already has
    the mic, or exclusive mode is off for it in Windows, StreamLite uses shared mode by itself.
  - The Microphone page shows the latency live, for example *Mic to cable 7.4 ms (low latency) + 20 ms noise
    removal*. Noise removal needs its own audio frames, measured and reported separately: RNNoise 20 ms,
    DeepFilterNet 50 ms on the CPU or 60 ms on the GPU (its look-ahead), Classic 21 ms. `status.txt` has the full breakdown.
- **One-window installer (Windows):** the MSI now installs everything StreamLite needs, in its one window with one
  Windows permission prompt. VB-CABLE (the virtual mic), VB-Audio Hi-Fi Cable (3D game audio) and Equalizer APO
  (headphone EQ) are downloaded from their makers and installed silently. What's already installed is skipped.
  - VB-Audio's own signing certificates are added to Windows' trusted publishers first, so Windows doesn't interrupt
    with its "install this device software?" window.
  - A log is kept in `%ProgramData%\StreamLite\prerequisites.log`.
  - If something can't be installed (no internet, for example), StreamLite offers it again in the app.
  - Run `msiexec /i StreamLite.msi SKIPPREREQUISITES=1` to leave them out.

## 2.3.1

- **Credits:** a new **Credits** button above **Report a bug** (Windows and Linux) opens a window that lists every
  project, AI model and dataset StreamLite uses, with links to its source and to the exact model files. Pick any
  release to see what that version used and what was new in it.
- The same per-version list is in [CREDITS.md](CREDITS.md), and every GitHub release now has a **Credits** section,
  including the earlier ones.

## 2.3.0

- **Low VRAM (Windows):** a new switch on the Microphone page, under noise removal (also on the Camera page), for graphics
  cards with little video memory, such as a 4 GB RTX 4060. DeepFilterNet on the GPU and the AI camera then use small
  versions of their models. **Everything together stays under 250 MB of video memory**, down from about 430 MB, at the
  same speed:

  | | Normal | Low VRAM |
  |---|---|---|
  | DeepFilterNet 3 (GPU) | ~150 MB | ~60 MB |
  | + AI camera (1080p) | ~430 MB | ~210 MB |

  Measured on an NVIDIA card with Windows' GPU counters (dedicated video memory). Windows also lists about 60 MB of
  "shared GPU memory"; that is ordinary RAM, not video memory.
  - DeepFilterNet keeps its recurrent layers in 16-bit floats. Your voice comes out within 50 dB of the full model.
  - The AI camera runs its effects in 16-bit floats with fewer full-size images in flight. The picture matches the
    normal model (46-58 dB PSNR). Capture is 1080p while Low VRAM is on (4K capture needs the normal model).
  - DirectML runs these models without the driver's "metacommands", which reserve a lot of memory for no gain here.
  - The CPU engines are unchanged. If the graphics card can't run DeepFilterNet, it still moves to the CPU.

## 2.2.2

- **Updates while StreamLite keeps running:** StreamLite checks again every 6 hours, not only when it starts, because
  most PCs sleep instead of restarting. Updates install only at a quiet moment: when no app is recording your mic
  and no game sound is playing, so a call or a match is never cut off. Until then Settings says the update is
  ready. Linux packages (.deb/.rpm) that need your password just tell you instead of prompting out of the blue.
- After an update, StreamLite restarts through its "Run as administrator" task when it has one, just like a normal
  start.
- `StreamLite.exe --quit` asks a running StreamLite to quit cleanly (it puts your sound devices back first), even when
  it runs as administrator.
- `status.txt` and Copy details now also work when the noise engine is RNNoise.

## 2.2.1

- **Keep game volume during calls (Windows):** by default Windows turns every other sound down 80% while Discord, Teams
  or Zoom has a call open, so your game gets quiet whenever a friend talks. A new switch in Settings → Audio devices
  stops that.
- **DeepFilterNet on any PC:**
  - If your graphics card can't run DeepFilterNet, it now runs on the CPU instead of dropping to RNNoise.
  - If your CPU is too slow for it in real time, StreamLite switches to RNNoise rather than sound choppy.

  The Microphone page says when either happens. Setting `STREAMLITE_NO_GPU=1` keeps the AI off the graphics card
  entirely.
- **Mic hints:**
  - StreamLite warns when your mic clips before it gets the sound, and says where to turn the gain down.
  - On Linux it also tells you when you're using a Bluetooth headset mic.
- **Copy details:** Settings → Updates and bug reports → Copy details puts your version, system, StreamLite's status
  and your audio devices on the clipboard for a bug report, with your user name taken out. On Linux, run
  `streamlite --details`.
- **3D audio** now goes back to your own headphones when you plug them in again after a fallback.

## 2.2.0

Works with more headsets and mics, and keeps working when you swap them.

- **Audio interfaces and multi-channel mics:** StreamLite now reads every channel of your mic. **Mic input: Auto** uses
  only the inputs that carry your voice:
  - An XLR mic on input 1 of an audio interface no longer comes out 6 dB quieter, with input 2's noise mixed in.
  - A laptop mic with an out-of-phase channel can't cancel your voice.

  You can also mix all inputs or pick one yourself. On Windows it's under Tuning > Noise removal; on Linux it's on
  the Microphone page.
- **Every headset keeps its own correction:** "Find my headphones" saves the correction for the device in use. Switch
  between a gaming headset and earbuds and the correction switches with you.
- **Better headset matching:** "Find my headphones" now understands more device names:
  - Bluetooth names that start with the owner's name ("Sam's AirPods Pro")
  - Windows' "2-" duplicates
  - "Buds2" for "Buds 2", and "QC35" for "QuietComfort 35"
  - PipeWire's "(IEC958)" names

  When your exact model isn't measured, it shows the closest. 34 of 34 real device names now find the right model.
- **Plug and unplug freely:**
  - With 3D audio on, plugging in a headset or picking another output in Windows moves 3D audio there.
  - If the clean mic's microphone is unplugged, it switches to another mic, and back when yours returns.
  - On Linux, unplugging the headphones moves the sound to another output right away and can never feed back into
    StreamLite's own devices.
- **Clearer problems:** if Windows' privacy settings block the microphone, the Microphone page says so and has a
  button to the right setting. Exclusive-mode conflicts, unplugged devices and a stopped audio service are explained
  in plain words. Gaming headsets that bring their own 7.1 virtual surround get a hint to turn it off, so games aren't
  made 3D twice.
- **Fixes:**
  - Right double-click on the sound cube resets the view again, and the "turning..." hint goes away.
  - On a fresh Linux account StreamLite no longer saves its settings in the folder it was started from.
  - The Linux app starts on systems without any fonts installed.

## 2.1.0

The first public release, for Windows and Linux.

- **Clean mic:** AI noise removal with RNNoise or DeepFilterNet 3. On Windows, DeepFilterNet runs on the graphics card
  through DirectML. Also included: noise gate, auto level, compressor, de-esser, de-hum and EQ, plus voice styles and
  a voice test recorder.
- **3D game audio:** 7.1 becomes binaural sound with the SADIE II KU100 and KEMAR heads, with true 360° placement,
  cue sharpening, footstep boost, a loud-sound tamer and directional EQ.
  - A new **sound cube** shows sound on three floors and nine positions per floor. Right-drag it to turn it.
- **Headphones:** corrections from AutoEq ("Find my headphones"). Game profiles switch on their own.
  - A **Chat** profile now applies while Discord, Zoom, Teams, TeamSpeak, Mumble, Skype, Slack, Element, Signal,
    Telegram, WhatsApp or Webex is open.
- **Drivers on demand (Windows):** VB-CABLE, Hi-Fi Cable and Equalizer APO are downloaded and installed with one click
  each.
- **Updates:** StreamLite checks GitHub when it starts and installs a newer version the way you installed it. Every
  download is checked against SHA-256 sums. You can turn this off in Settings.
- **Report a bug:** a button in the sidebar, in Settings and in the tray menu opens GitHub's bug form with your
  version and system already filled in.
- **Every device at its best working format:** devices are set to 48 kHz at the highest bit depth that actually works.
- **Linux:** the same noise removal, 3D audio and headphone EQ for PipeWire and PulseAudio. Packages: .deb, .rpm,
  AppImage, tarball and Arch PKGBUILD.
- **AI camera (Windows 11):** the StreamLite Camera virtual webcam with background blur, green screen, face tracking and
  auto light.
