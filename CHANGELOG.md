# Changelog

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
