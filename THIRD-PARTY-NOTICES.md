# Credits and third-party notices

StreamLite stands on a lot of excellent, freely shared work. Thank you to everyone below.

## Bundled with StreamLite

| Component | Used for | Authors | License |
|---|---|---|---|
| [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) | 3D audio: measured head responses (KU100 dummy head, KEMAR) | Department of Electronic Engineering, University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney | Apache-2.0 |
| [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) | AI noise removal (studio-quality model) | Hendrik Schröter et al. | MIT OR Apache-2.0 |
| [torchDF](https://github.com/grazder/DeepFilterNet) | Streaming ONNX export of DeepFilterNet 3 | grazder | MIT OR Apache-2.0 |
| [RNNoise](https://github.com/xiph/rnnoise) | AI noise removal (light model, C# port of the network) | Jean-Marc Valin, Mozilla, Xiph.Org Foundation | BSD-3-Clause |
| [ONNX Runtime](https://onnxruntime.ai) | Runs the AI models | Microsoft | MIT |
| [DirectML](https://github.com/microsoft/DirectML) (Windows) | Runs the AI models on any DirectX 12 graphics card | Microsoft | Microsoft Software License Terms (redistributable) |
| [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) | AI green screen / background removal | Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance) | GPL-3.0 |
| [Ultra-Light-Fast-Generic-Face-Detector-1MB](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) | Face tracking for the AI camera | Linzaer | MIT |
| [Avalonia UI](https://avaloniaui.net) (Linux) | The Linux interface | AvaloniaUI OÜ and contributors | MIT |
| [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) | Drawing and text for the Linux interface (Skia, HarfBuzz) | Microsoft / .NET Foundation; Google (Skia); HarfBuzz authors | MIT, BSD-3-Clause (Skia), MIT (HarfBuzz) |
| [Inter](https://rsms.me/inter/) (Linux) | Interface font | Rasmus Andersson | SIL Open Font License 1.1 |
| [.NET runtime](https://github.com/dotnet/runtime) (Linux) | Runs StreamLite for Linux (included, nothing to install) | .NET Foundation and contributors | MIT |

The camera model files (`ai/camfx_*.onnx`) combine Robust Video Matting and UltraFace into one graph; they are built
from the original models by [`tools/make_camfx.py`](tools/make_camfx.py) (the corresponding source; `camfx_1080_low.onnx`
is its `lowvram` variant). `ai/deepfilternet3_low.onnx` is DeepFilterNet 3 rewritten by
[`tools/make_dfn_lean.py`](tools/make_dfn_lean.py) (`gru16`).

## Code and ideas borrowed (with thanks)

| Project | What | Authors | License |
|---|---|---|---|
| [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) | Interface layout for per-app audio routing on Windows | File-New-Project | MIT |
| [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) | HeSuVi 14-channel HRIR track order | Jaakko Pasanen | MIT |
| [VCamSample](https://github.com/smourier/VCamSample) | Structure of the Windows 11 virtual camera media source | Simon Mourier | MIT |

## Downloaded when you ask for them (not bundled)

| Component | Used for | Authors | License |
|---|---|---|---|
| [AutoEq](https://github.com/jaakkopasanen/AutoEq) | Headphone corrections for thousands of headphones ("Find my headphones") | Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity, ...) | MIT |
| [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) | The virtual mic cable and the 3D game audio device | VB-Audio Software (Vincent Burel) | Donationware - please support VB-Audio if you use it a lot |
| [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) | System-wide headphone EQ | Jonas Thedering | GPL-2.0 |

## Built with

[WiX Toolset](https://wixtoolset.org) (MSI installer, MS-RL), [Zig](https://ziglang.org) (builds the virtual camera DLL, MIT),
[AppImageKit / appimagetool](https://github.com/AppImage/appimagetool) (AppImage, MIT), [PipeWire](https://pipewire.org) /
[PulseAudio](https://www.freedesktop.org/wiki/Software/PulseAudio/) (Linux audio, used through the system's libpulse and pactl, MIT / LGPL-2.1).

Built-in headphone measurement data for the Sennheiser MOMENTUM 5 correction: [SoundGuys](https://www.soundguys.com) (B&K 5128).

## StreamLite's own license

StreamLite is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License version 3 or (at your option) any later version - see [LICENSE](LICENSE).

**Additional permission (GPLv3 section 7):** as a special exception, the copyright holders of StreamLite give you
permission to combine StreamLite with ONNX Runtime, Microsoft DirectML and the components of Microsoft Windows, and
to distribute the combination, without the terms of the GPL applying to those components. The terms of each
component's own license still apply to it.
