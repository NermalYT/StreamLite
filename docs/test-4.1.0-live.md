# StreamLite 4.1.0 - live test on the developer's PC (2026-10-28)

Full uninstall of 4.0.9, fresh 4.1.0 MSI from the GitHub release, then the guided setup (twice: a first-time setup,
then a second fresh one for the 4.1.1 changes) and every page driven on the desktop with computer use. Nothing was
played on the speakers (Realtek USB Audio): sound went to the QCC Dongle Pro (Sennheiser MOMENTUM 5) only.

**The voice:** Windows text-to-speech (David / Zira) read each setup script into a 48 kHz WAV, mixed into the mic as it
arrives through a new test-only switch (`STREAMLITE_TEST_MIC`, off unless set) - a stand-in for a person. From the
voice step on, the installed exe was 4.1.0 + that switch (same code otherwise), then 4.1.1 builds as fixes went in.
**Personalize 3D:** answers were placeholders (nobody listened) - the 3D tuning on this PC should be done again by ear.

## Install
- 4.0.9 removed completely: cables, Equalizer APO, camera registration, admin task, settings, files (no leftovers).
- 4.1.0 MSI (SHA-256 checked against the release): exit 0 in ~30 s. VB-CABLE, Hi-Fi Cable and Equalizer APO installed,
  devices renamed StreamLite - In / Out, spare ends hidden, camera registered, admin task made for the signed-in user.
- First start: Launch with Windows added (`--minimized`), Run as administrator on.

## Setup, step by step (both runs)
1. Mic & headphones: tile picker right (Yeti / QCC Dongle Pro preselected; Realtek speakers not picked).
2. Equalizer APO: switched on for the headphones.
3. Your voice: every recording finished by itself as the words were heard (0 / -10 / +6 / -14 dB, silly voice; skips
   work). Result: low cut 80 Hz, noise removal 70 %, compressor, far voice found, voiceprint made. 4.1.1: Record a
   sample -> heard tuned on the headphones, Original A/B, Tune again.
4. Headphone EQ: Find my headphones - "Sennheiser MOMENTUM 5" (StreamLite's own correction).
5. Personalize 3D: head search + tuning, result card, Practice with score, Esc summary. 478-480 fps.
6. (4.1.1) Camera: StreamLite Camera, AI tracking, background, each gesture, what the V does - saved.

## Pages
Microphone (all switches, folds, Tuning & EQ panel - 5 tabs), Headphones (Priority, Auto EQ apps, Presets together,
EQ editor 10-band / 31-point / parametric), 3D Audio (Test footsteps, 16D modes, Fine-tune, Output & apps), Camera
(preview with blur: 30 fps, 18 ms a frame on the GPU from 4K; gestures card), Hotkeys (added F8 -> fired -> removed),
Health (setup check 8/8), Settings (AI quality tiers, folds, licenses, Report a bug).

## Discord / Zoom whole-screen share (ShareLeakProbe, against the running StreamLite)
- Friends' voices (Discord's own sound, played on through StreamLite): **not in the share** (-240 dB) - nobody hears themselves.
- Your mic: in StreamLite - In (-7.7 dBFS peak), **not in the share** (silent) - nobody hears you twice.
- A game: in the share **once** (clicks every 500 ms, no echo from StreamLite's own copy).

## Bugs found - fixed in 4.1.1
1. **The setup never started until the window was clicked** (it waited for the focus Windows doesn't give a window started
   by the installer or the admin task).
2. **The voice step's intro showed the teleprompter** before Start (reported by the user).
3. **A first setup left half way came back as "Set up StreamLite again"** and reset the voice and 3D tuning on Carry on.
4. **Every new install got AI quality "Full CPU"**, even with a 16 GB graphics card (the hardware tuning ran first).
5. **"Looking up the EQ for Headphones (QCC Dongle Pro)" took about 5 minutes** (a dongle's name; every squig.link
   database allowed 100 s).
6. **Leaving StreamLite with Direct to headphones left Windows' communications device on the speakers** - Teams, Zoom
   and calls played out loud on them once StreamLite unmuted them.
7. **Settings weren't saved for minutes at a time**: "save soon" restarted its wait on every call, and something asked
   every half second - the voice tuning, headphone EQ and 3D head of the setup reached the disk only at its end.
8. **"Stop here" after the head search ended the setup unfinished** (no camera step, the whole setup again next start).
9. **The 3D page's Directional EQ card was out of reach** (below the page's end: a fold that grew didn't move what was
   below it).
10. **Text cut off**: the headphone correction's name ("Sennheiser MOMENTUM 5 to neutral (Strea..."), the setup check's
    note next to "Make it the default".
11. **Checkboxes showed no tick** (checked looked unchecked - the welcome page's "I understand").
12. The dimmed window behind a popup showed in Alt+Tab as a blank black window (picking it hid the setup behind black)
    - likely the user's "popups go away when you tab out"; not reproduced otherwise.
13. The end of the setup's restart with admin rights ran inside a closing window and could stop half way (tray icon and
    hotkeys gone). On this PC StreamLite already had admin rights, so no restart was needed.
14. Polish: Priority and Auto EQ apps lists in StreamLite's style; pages capped at 1000 px wide (switches drifted
    apart on big monitors); the welcome / camera popups' spacing; Continue grey while it can't be pressed.

## Wishes from the user - in 4.1.1
- A thank-you page from the developer first (Continue after "I understand").
- The camera in the setup, after 3D audio.
- The steps along the bottom of the setup screen can be clicked to go back (or forward again) to any step reached.
- After the voice recordings: record a sample, hear it tuned (Original to compare), Tune again.
- Report a bug inside StreamLite with GitHub sign-in (kind, part, what happened, steps, pictures). Needs the GitHub App's
  client id to post from the app; until then it opens GitHub's form filled in. GitHub doesn't let apps upload pictures:
  those are added on the report's page.

## Not changed / to look at
- Game 3D audio stays off after the setup tunes it (as in 4.1.0).
- "Direct to headphones" plays voice chat and music through the muted speakers device (now safe on exit); a PC with
  nothing but loud speakers as the second output is the case to keep in mind.
- Auto level sits at its maximum (+20 dB) while nobody talks; noise removal keeps the room silent (-87 dB out).
