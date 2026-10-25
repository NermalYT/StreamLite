# Credits by version

StreamLite stands on a lot of freely shared work. These are the projects, AI models and data each release uses,
with links to their source. The same list is in the app (the **Credits** button above **Report a bug**) and in each
GitHub release. Licenses and notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
This file is generated from `src/Credits.cs` (run `test\run.ps1 CreditsTest write`).

## StreamLite 3.0.2

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.
- [ITU-R BS.1770 loudness weighting](https://www.itu.int/rec/R-REC-BS.1770) - K-weighting: every game profile matched to the same loudness. International Telecommunication Union. Published standard.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 3.0.1

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.
- [ITU-R BS.1770 loudness weighting](https://www.itu.int/rec/R-REC-BS.1770) - K-weighting: every game profile matched to the same loudness. International Telecommunication Union. Published standard.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 3.0.0

_No new third-party code, models or data. Added: the ITU-R BS.1770 loudness weighting, which StreamLite now uses to play every game profile at the same loudness._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.
- [ITU-R BS.1770 loudness weighting](https://www.itu.int/rec/R-REC-BS.1770) - K-weighting: every game profile matched to the same loudness. International Telecommunication Union. Published standard. **New in 3.0.0.**

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.7.0

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.6.0

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.5.0

_No new third-party code, models or data. The Test footsteps rings use the SADIE II head responses (already credited) at their measured elevations._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.4.0

_No new third-party code, models or data. The installer now also downloads VB-CABLE, Hi-Fi Cable and Equalizer APO (already credited) from their makers._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.3.1

_This Credits window. No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT.

## StreamLite 2.3.0

_Low VRAM: onnxconverter-common (half-precision model files) and ONNX Runtime's model optimizer build the new deepfilternet3_low.onnx and camfx_1080_low.onnx, which are made from DeepFilterNet 3, Robust Video Matting and UltraFace._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.
- [onnxconverter-common](https://github.com/microsoft/onnxconverter-common) - Half-precision Low VRAM model files. Microsoft. MIT. **New in 2.3.0.**

## StreamLite 2.2.2

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.

## StreamLite 2.2.1

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.

## StreamLite 2.2.0

_No new third-party code, models or data._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.

## StreamLite 2.1.0

_First public release: everything below._

**AI models and measured data**

- [DeepFilterNet 3](https://github.com/Rikorose/DeepFilterNet) - AI noise removal (studio-quality model). Hendrik Schröter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier. MIT or Apache-2.0. [Model / data](https://huggingface.co/kimtos-labs/denoiser-dfn3).
- [torchDF](https://github.com/grazder/DeepFilterNet) - Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs). grazder. MIT or Apache-2.0.
- [RNNoise](https://github.com/xiph/rnnoise) - Light AI noise removal (C# port of v0.1.1 and its trained model). Jean-Marc Valin, Mozilla, Xiph.Org Foundation. BSD-3-Clause. [Model / data](https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c).
- [SADIE II HRTF database](https://www.york.ac.uk/sadie-project/database.html) - 3D audio: measured head responses (KU100 dummy head, KEMAR). University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney. Apache-2.0.
- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) (Windows) - AI camera: cuts you out of the room (blur, green screen, picture, video). Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance). GPL-3.0. [Model / data](https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx).
- [Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB) (Windows) - AI camera: face tracking. Linzaer. MIT. [Model / data](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx).
- [SoundGuys headphone measurements](https://www.soundguys.com) - Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement). SoundGuys. Measurement used with thanks.

**Libraries and runtimes**

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files). Microsoft. MIT.
- [DirectML](https://github.com/microsoft/DirectML) (Windows) - Runs the AI models on any DirectX 12 graphics card. Microsoft. Microsoft Software License Terms (redistributable).
- [.NET runtime](https://github.com/dotnet/runtime) (Linux) - Runs StreamLite for Linux (included). .NET Foundation and contributors. MIT.
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Linux) - The Linux interface. AvaloniaUI OÜ and contributors. MIT.
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (Linux) - Drawing and text for the Linux interface (Skia, HarfBuzz). Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors. MIT; BSD-3-Clause (Skia); MIT (HarfBuzz).
- [Inter](https://github.com/rsms/inter) (Linux) - Interface font. Rasmus Andersson. SIL Open Font License 1.1.
- [PipeWire / PulseAudio](https://pipewire.org) (Linux) - Linux audio (your system's libpulse and pactl). Wim Taymans and the PipeWire contributors; the PulseAudio developers. MIT / LGPL-2.1.

**Code, ideas and research**

- [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) (Windows) - How per-app audio routing works on Windows. File-New-Project. MIT.
- [Impulcifer](https://github.com/jaakkopasanen/Impulcifer) - HeSuVi 14-channel HRIR track order. Jaakko Pasanen. MIT.
- [VCamSample](https://github.com/smourier/VCamSample) (Windows) - Structure of the Windows 11 virtual camera. Simon Mourier. MIT.
- [Guided Image Filtering](https://doi.org/10.1109/TPAMI.2012.213) (Windows) - AI camera: denoise, skin smoothing and crisp cut-out edges. Kaiming He, Jian Sun, Xiaoou Tang. Published research.

**Downloaded when you ask for them**

- [AutoEq](https://github.com/jaakkopasanen/AutoEq) - Headphone corrections for thousands of headphones (Find my headphones). Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...). MIT. [Model / data](https://github.com/jaakkopasanen/AutoEq/tree/master/results).
- [VB-CABLE and Hi-Fi Cable](https://vb-audio.com/Cable/) (Windows) - The virtual mic cable and the 3D game audio device. VB-Audio Software (Vincent Burel). Donationware - please support VB-Audio.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (Windows) - System-wide headphone EQ. Jonas Thedering. GPL-2.0.

**Built with**

- [WiX Toolset](https://github.com/wixtoolset/wix) (Windows) - The Windows installer (MSI). .NET Foundation and contributors. MS-RL.
- [Zig](https://github.com/ziglang/zig) (Windows) - Builds the virtual camera's native DLL. Andrew Kelley and the Zig contributors. MIT.
- [appimagetool](https://github.com/AppImage/appimagetool) (Linux) - The Linux AppImage. Simon Peter and the AppImage contributors. MIT.
- [ONNX](https://github.com/onnx/onnx) - Builds and adapts the model files (tools/make_camfx.py). ONNX Project Contributors (LF AI & Data). Apache-2.0.
- [NumPy](https://github.com/numpy/numpy) - Model-building scripts. NumPy Developers. BSD-3-Clause.

