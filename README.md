# StreamLite

**A clean mic, 3D game audio and better-sounding headphones - free, for Windows and Linux.**

StreamLite sits between your microphone and Discord, OBS, Zoom, TeamSpeak or your game and removes keyboard clicks,
fans, PC hum, traffic and room noise with AI. It also turns games' 7.1 surround into 3D sound for your headphones,
corrects your headphones to sound neutral, and switches headphone profiles for games, music and voice chat on its own.

**[Download](https://github.com/NermalYT/StreamLite/releases/latest)** ·
**[Manual](https://nermalyt.github.io/StreamLite/)** ·
**[Report a bug](https://github.com/NermalYT/StreamLite/issues/new?template=bug_report.yml)** ·
[Credits](THIRD-PARTY-NOTICES.md)

![StreamLite's 3D Audio page with the live sound sphere](docs/img/windows-3d.png)

## What it does

- **Clean microphone.** Voice isolation is built in: DeepFilterNet 3 (studio quality, on your graphics card on Windows)
  takes out fans, keyboards, traffic and even a loud machine next to you. Includes a noise gate, auto level, compressor,
  de-esser and EQ. Apps pick **StreamLite Mic** as their microphone, and your real mic stays usable by other apps.
- **Studio voice.** Any mic made to sound like a broadcast mic: it learns how your voice comes through this mic,
  takes out the mic's colouring (thin, boxy, harsh, muffled), rebuilds the treble cheap mics cut off and catches
  plosive pops.
- **Record a sound to remove (Windows).** A TV, kids or music that noise removal lets through: stay quiet for 8 seconds
  while it plays, and StreamLite learns it and takes it out of your mic.
- **Tune to your voice (Windows).** Five short recordings (at your mic, further away, loud, quiet, your silly voice),
  read like a teleprompter: big text, each word turns green the moment you say it. Then StreamLite sets itself up for
  your voice with the best noise removal and quality your PC can run.
- **Only my voice (Windows).** Someone else talking in your room (a girlfriend, a brother) is turned down: a
  speaker-recognition model learns your voice (your silly voices too) and tells it from theirs.
- **Across the room.** Talking from your bed with the mic on the desk: your voice comes out as loud as at the desk, with
  fans still taken out. A toggle, off for people who talk right at their mic.
- **Guided setup (Windows).** The first time StreamLite opens it takes over your primary monitor and walks you through
  everything, one step at a time: your mic and headphones, the headphone EQ engine (Equalizer APO, installed and
  switched on for you), **Tune to my voice** from your mic's raw sound, your headphones' EQ, and **Personalize 3D**.
  Leave half way and it picks up where you left off next time. Updating with your settings saved? **Keep my settings**
  skips it.
- **3D game audio.** Games' 7.1 surround becomes 3D sound on any headphones, using 20 measured heads at full
  resolution (SADIE II: the KU100 and KEMAR dummy heads with 8802 directions each, and 18 real people). True 360°
  placement, footstep boost and a loud-sound tamer. **Open sphere** takes out walls and floors: footsteps come
  straight from their direction, never muffled. A live **sound sphere** shows where sounds come from around you;
  drag it to look from any side. **StreamLite is the audio engine:** game and app sound plays through it straight to
  your headphones on the lowest-delay path (Direct to headphones is built in), and Discord's full-screen share still
  gets the sound. **16D** for music apps or for all apps is one choice.
- **Personalize 3D (Windows).** You fly into the sphere and someone walks in one spot in front of you, behind, left,
  right, above or below - with Counter-Strike 2's own recorded footsteps when CS2 is installed. Every footstep starts
  with you facing forward; snap the view to where you hear them and click. In about 4 minutes it finds which of 20
  measured heads fits your ears, then tunes your head size, cue sharpening and floor contrast. **Practice** mode plays
  the same test with your current settings, without tuning anything, for as long as you like.
- **Headphone correction for (almost) every headphone.** StreamLite recognises headsets by name and uses their
  measured correction by itself. "Find my headphones" searches thousands of AutoEq measurements and, behind them,
  squig.link's ~150 reviewer databases (gaming headsets like the HyperX Cloud III, Razer BlackShark V3 or Corsair HS80
  included) - StreamLite makes the EQ from the raw measurement. Still missing? **Missing? Report it** opens a
  ready-made report.
- **Automatic profiles.** Each game gets its own headphone EQ. **Music** applies while Spotify plays, **Chat** applies
  while Discord, Zoom, Teams, TeamSpeak, Mumble or another chat app is open, and **Movies & videos** applies otherwise.
  **Presets together** plays them at the same time: your game's preset on the game's sound and Music's on Spotify.
- **AI camera (Windows 11).** A "StreamLite Camera" virtual webcam with background blur, AI green screen and auto light,
  that works like an AI gimbal camera (Insta360 Link 2 style) with any webcam: tracking one person or a group, head /
  half body / whole body framing, hand gestures (each with its own switch), **air drawing** (make a V, then write in
  the air with your index finger), camera animations (flip upside down, spin, roll, zoom punch, shake), tracking and
  pause zones, presets, whiteboard and desk view, levelling, upside-down mounting, HDR, face exposure and privacy.
- **AI quality (Settings).** One choice for voice isolation and the AI camera: **Low VRAM** (about 210 MB of video
  memory), **Normal** (about 430-490 MB), **High VRAM** (about 860-890 MB, the camera's cut-out at full HD) or
  **Full CPU** (no video memory, slowest).
- **Any headset, any mic.**
  - USB, Bluetooth and analog headsets, audio interfaces and array mics all work.
  - Auto picks the mic input that carries your voice.
  - Each headset keeps its own correction, and each mic its own settings (also as a file for a friend's PC).
  - Plug in or unplug devices and StreamLite follows.
- **Hotkeys for everything.** Mic, outputs, 16D, 3D and camera actions (flip upside down, spin) on any key or mouse
  button.
- **Takes care of itself.** It installs the free drivers it needs on Windows, updates itself when it starts, and has a
  **Report a bug** button.

## Screenshots

| | |
|---|---|
| ![The guided setup's Personalize 3D step: the sphere you fly into](docs/img/windows-setup-3d.png) | ![Personalize 3D: flying into the sphere, the six spots footsteps play from](docs/img/windows-personalize.png) |
| The setup's 3D step: drag the sphere, click Start and you fly into it | Inside Personalize 3D: you in the middle, the six directions around you |
| ![The Headphones page with headphone correction, game profiles and Presets together](docs/img/windows-headphones.png) | ![The Microphone page](docs/img/windows-mic.png) |
| Headphones: correction, profiles and Presets together | Microphone: AI noise removal, studio voice, EQ |
| ![Tune to my voice: the teleprompter, words turning green as you read](docs/img/windows-voice.png) | ![The Camera page's tracking and gestures cards: a switch per gesture, air drawing, animations](docs/img/windows-camera-gestures.png) |
| Tune to my voice: read along, each word turns green as you say it | Camera: a switch per gesture, air drawing, animations |

## Install

| System | Download | How |
|---|---|---|
| **Windows 10/11** (64-bit) | `StreamLite-<version>-x64.msi` | Double-click it. Windows SmartScreen may say "Windows protected your PC": click **More info → Run anyway** (StreamLite isn't code-signed yet). |
| Windows, no install | `StreamLite-<version>-win-x64-portable.zip` | Unzip anywhere and run `StreamLite.exe`. |
| **Ubuntu, Debian, Mint, Pop!_OS, Zorin** | `streamlite_<version>_amd64.deb` | `sudo apt install ./streamlite_<version>_amd64.deb` |
| **Fedora, Nobara** | `streamlite-<version>-1.x86_64.rpm` | `sudo dnf install ./streamlite-<version>-1.x86_64.rpm` |
| **openSUSE** | `streamlite-<version>-1.x86_64.rpm` | `sudo zypper install --allow-unsigned-rpm ./streamlite-<version>-1.x86_64.rpm` |
| **Arch, Manjaro, EndeavourOS, CachyOS** | `PKGBUILD` | Put it in an empty folder, then run `makepkg -si` |
| **Any Linux** (one file) | `StreamLite-<version>-x86_64.AppImage` | `chmod +x StreamLite-*.AppImage` and run it |
| **Any Linux** (no root) | `streamlite-<version>-linux-x64.tar.gz` | Unpack it and run `./install.sh`, or use the one-liner below |

One-line install for any Linux distro, for your user only (no password):

```sh
curl -fsSL https://raw.githubusercontent.com/NermalYT/StreamLite/main/packaging/linux/get.sh | sh
```

Linux needs PipeWire (with `pipewire-pulse`) or PulseAudio, which every mainstream desktop has. The packages pull in
everything else.

## Quick start

1. Start StreamLite. On Windows the guided setup opens by itself and does the mic, voice, headphone EQ and 3D audio
   for you (about 6 minutes). On Linux, pick **your microphone** on the Microphone page.
   - On Windows, click **Install VB-CABLE (free)** if StreamLite asks for it. It's the virtual cable your clean mic
     travels through.
2. In Discord, go to *Settings → Voice & Video* and set **Input Device** to **StreamLite Mic**. Set Discord's own
   noise suppression to **None** and turn off its automatic gain control, so your voice isn't processed twice. OBS, Zoom, TeamSpeak and games work the same way.
3. Optional:
   - **Headphones → Find my headphones** corrects your headphones.
   - **3D Audio** gives games 3D sound. Set the game's audio output to 7.1 / surround.

The [manual](https://nermalyt.github.io/StreamLite/) covers everything else, with fixes for common problems.

## Bugs and ideas

Use **Report a bug** in StreamLite. It's in the sidebar, under Settings, and in the tray menu. It opens the
[bug form](https://github.com/NermalYT/StreamLite/issues/new?template=bug_report.yml) with your version and system
already filled in. You need a free GitHub account. Ideas are welcome
[here](https://github.com/NermalYT/StreamLite/issues/new?template=feature_request.yml).

## Building it yourself

- **Windows:** run `.\build.ps1`. It uses the C# compiler that ships with Windows, so no SDK is needed. Then:
  - `.\install.ps1` installs it for your user.
  - `.\packaging\windows\build-msi.ps1` builds the MSI and the zip. This needs the .NET SDK and WiX 5.
  - `.\test\all.ps1` runs the offline tests.
- **Linux:** run `bash packaging/linux/build-linux.sh`. It needs the .NET 8 SDK. It builds the tarball, .deb,
  AppImage and PKGBUILD, plus the .rpm if `rpmbuild` is installed. After that:
  - `linux/publish/streamlite --selftest` checks the audio path.

Pushing a version tag (`v2.1.0`) makes GitHub Actions do the following:

- Build everything and run the tests.
- Install each Linux package on Ubuntu, Debian, Fedora, openSUSE and Arch.
- Publish the release with `SHA256SUMS.txt`, which the updater checks.

## Credits

StreamLite is built on the work of a lot of generous people:

- RNNoise (Xiph.Org, Mozilla)
- DeepFilterNet (Hendrik Schröter)
- SADIE II (University of York)
- AutoEq (Jaakko Pasanen) and squig.link's reviewers
- ONNX Runtime and DirectML (Microsoft)
- Robust Video Matting (ByteDance)
- VB-Audio's virtual cables
- Equalizer APO
- Avalonia, EarTrumpet, Impulcifer and VCamSample

[CREDITS.md](CREDITS.md) lists everything each release uses, with links to the source and the exact model files; the
app shows the same list (**Credits**, above **Report a bug**). Every license is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

StreamLite is free software under the [GNU GPL v3 or later](LICENSE). It also grants an extra permission to combine it
with ONNX Runtime, DirectML and Windows; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
