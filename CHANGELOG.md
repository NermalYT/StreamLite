# Changelog

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
