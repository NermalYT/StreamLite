# Changelog

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
    DeepFilterNet 20 ms on the CPU or 30 ms on the GPU, Classic 21 ms. `status.txt` has the full breakdown.
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
