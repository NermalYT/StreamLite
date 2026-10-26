# Game spatial audio: what StreamLite gets and what it does with it

## What a game can send

| What the game sends | Where it carries direction | Height (above / below) | What StreamLite does |
| --- | --- | --- | --- |
| **7.1 / 5.1 channel mix** (game set to 7.1 / Speakers) | The game pans each sound between the two nearest speakers | No - a channel mix is flat | Works out each sound's angle from the speaker levels and renders it at that angle with a measured head response (true 360°) |
| **Stereo with the game's own 3D** (HRTF: CS2, Valorant...) | Already binaural - made for headphones | Yes, if the game does it | Leaves it alone (set the game's profile to play straight to your headphones) |
| **Plain stereo** | Left / right only | No | Leaves it alone; set the game to 7.1 for 360° |
| **Audio objects** (Windows Sonic / Dolby Atmos / DTS) | Full 3D position per sound | Yes | Not available to StreamLite - see below |

The 3D Audio page shows which of these the game playing right now sends, from the channels that actually carry
sound: for example "The game sends a 7.1 channel mix (front L, front R, center, rear L, rear R, side L, side R)".

## Why not audio objects

Games that support Windows spatial sound (`ISpatialAudioClient`) can send up to a few hundred sound objects, each
with its own 3D position. Windows hands those objects only to spatial sound engines registered with Microsoft -
Windows Sonic, Dolby Atmos for Headphones, DTS Headphone:X. A virtual audio device like "StreamLite - Out" gets the
game's channel mix instead. There's no supported way for another app to receive the objects, so StreamLite keeps
the 7.1 path, which every game with surround sound supports.

## When Windows spatial sound gets in the way

- **On for "StreamLite - Out":** games send their objects to Windows' engine, which mixes them down before
  StreamLite gets the sound - the directions StreamLite then sees are a second-hand copy.
- **On for your headphones:** StreamLite's 3D gets made 3D a second time.

StreamLite reads whether spatial sound is on for each device (the number of objects Windows allows there). If it is,
the 3D page, the setup check (Health page) and the Health page's "What happened" warn you. To switch it off:
Windows Settings > System > Sound > the device > Spatial sound: Off.

## How it's checked

- **Offline tests** (`test/Personal3DTest.cs`): footsteps panned between speakers the way a 7.1 game pans them,
  through StreamLite's real game path, compared with the head model placing the same sound directly:
  - left: the same level difference between the ears (4.3 dB vs 4.3 dB with the default head)
  - in front sounds like a sound placed in front (0.4 dB apart in the 2-12 kHz direction cues), not like one behind
    (4.0 dB apart); behind likewise (3.3 vs 6.9 dB)
  - above and below sound the same (a channel mix has no height)
- **Your own ears:** Personalize 3D (3D Audio page) is a small first-person building. Footsteps play at exact spots
  upstairs, on your floor or downstairs; you look where you hear them and click. It compares the head models, cue
  sharpening and floor contrast blind (still footsteps, then walking ones you follow) and keeps what lets you place
  sounds best, per pair of headphones (`test/Personal3DWorldTest.cs` checks the geometry, the scoring and the
  decisions with simulated listeners).
