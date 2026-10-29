# Credits and third-party notices

StreamLite stands on a lot of excellent, freely shared work. Thank you to everyone below. Which release uses what,
with links to the exact model files: [CREDITS.md](CREDITS.md) (also in the app: **Credits**, above **Report a bug**).

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
| [MediaPipe Hands](https://github.com/opencv/opencv_zoo) | Hand gestures for the AI camera (palm detection + hand landmarks, as published in the OpenCV model zoo) | Google (MediaPipe); OpenCV model zoo | Apache-2.0 |
| [3D-Speaker CAM++](https://github.com/modelscope/3D-Speaker) | Only my voice: tells your voice from someone else's (`ai/speaker_campplus.onnx`, the sherpa-onnx export, unmodified) | Alibaba Group (3D-Speaker); ONNX export by k2-fsa/sherpa-onnx | Apache-2.0 |
| [Vosk](https://github.com/alphacep/vosk-api) (Linux) | Tune to my voice: follows the words you read, offline (`speech/libvosk.so`, built on [Kaldi](https://github.com/kaldi-asr/kaldi)) | Alpha Cephei Inc. and the Vosk contributors; the Kaldi authors | Apache-2.0 |
| [Vosk small English model](https://alphacephei.com/vosk/models) (Linux) | The words the speech recognizer knows (`speech/model`, vosk-model-small-en-us-0.15, unmodified) | Alpha Cephei Inc. | Apache-2.0 |

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
| [NVIDIA CUDA and cuDNN](https://developer.nvidia.com/cuda-toolkit) + [ONNX Runtime GPU](https://onnxruntime.ai) (Linux) | The AI on NVIDIA graphics cards (CUDA runtime, cuBLAS, cuFFT, cuRAND, cuDNN; picked in Settings > AI quality) | NVIDIA Corporation; Microsoft | NVIDIA CUDA / cuDNN license agreements; MIT |

## Used from your Linux distro (not bundled)

The Linux installer asks your distro's package manager for these: [Mono](https://www.mono-project.com) (MIT),
[libgdiplus](https://github.com/mono/libgdiplus) (MIT), [FFmpeg](https://ffmpeg.org) (LGPL-2.1+ / GPL-2.0+),
[v4l-utils](https://git.linuxtv.org/v4l-utils.git) (GPL-2.0 / LGPL-2.1), [v4l2loopback](https://github.com/umlaeute/v4l2loopback) (GPL-2.0),
[libXi](https://gitlab.freedesktop.org/xorg/lib/libxi) (MIT), [libnotify](https://gitlab.gnome.org/GNOME/libnotify) (LGPL-2.1),
[DejaVu fonts](https://dejavu-fonts.github.io) (Bitstream Vera license), [zenity](https://gitlab.gnome.org/GNOME/zenity) /
[kdialog](https://invent.kde.org/utilities/kdialog) (LGPL-2.1+ / GPL-2.0+), [Ayatana AppIndicator](https://github.com/AyatanaIndicators/libayatana-appindicator)
(LGPL-2.1 / LGPL-3.0), [Python](https://www.python.org) (PSF-2.0) with [PyGObject](https://pygobject.gnome.org) and GTK (LGPL-2.1+),
[PipeWire](https://pipewire.org) / [PulseAudio](https://www.freedesktop.org/wiki/Software/PulseAudio/) (MIT / LGPL-2.1) and, on Wayland,
the desktop's [xdg-desktop-portal](https://github.com/flatpak/xdg-desktop-portal) (LGPL-2.1+).

## Built with

[WiX Toolset](https://wixtoolset.org) (MSI installer, MS-RL), [Zig](https://ziglang.org) (builds the virtual camera DLL, MIT),
[ONNX](https://github.com/onnx/onnx) and
[NumPy](https://github.com/numpy/numpy) (build the model files, Apache-2.0 / BSD-3-Clause),
[onnxconverter-common](https://github.com/microsoft/onnxconverter-common) (half-precision Low VRAM models since 2.3.0, MIT),
[Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Kaiming He, Jian Sun, Xiaoou Tang; camera filters).

Built-in headphone measurement data for the Sennheiser MOMENTUM 5 correction: [SoundGuys](https://www.soundguys.com) (B&K 5128).

## StreamLite's own license

StreamLite is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License version 3 or (at your option) any later version - see [LICENSE](LICENSE).

**Additional permission (GPLv3 section 7):** as a special exception, the copyright holders of StreamLite give you
permission to combine StreamLite with ONNX Runtime, Microsoft DirectML and the components of Microsoft Windows, and
to distribute the combination, without the terms of the GPL applying to those components. The terms of each
component's own license still apply to it.
