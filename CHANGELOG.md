# Changelog

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
