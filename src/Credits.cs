using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StreamLite
{
    /// <summary>One project, model or dataset StreamLite uses, and the first version that used it.</summary>
    internal sealed class Credit
    {
        public string Name, Url, What, Authors, License;
        /// <summary>The exact model / data file StreamLite uses, when there is one.</summary>
        public string File;
        /// <summary>First StreamLite version that used it.</summary>
        public string Since = "2.1.0";
        /// <summary>null = Windows and Linux.</summary>
        public string Platform;
        public string Group;
    }

    /// <summary>
    /// Credits for every StreamLite release: who made the code, models and data StreamLite stands on. The in-app Credits
    /// window, CREDITS.md and each GitHub release's Credits section all come from this list (test\CreditsTest keeps
    /// CREDITS.md in sync). A new dependency gets Since = the version that first ships it; never remove an entry -
    /// older versions still used it.
    /// </summary>
    internal static class Credits
    {
        public const string Models = "AI models and measured data", Libraries = "Libraries and runtimes", Ideas = "Code, ideas and research",
            Downloads = "Downloaded when you ask for them", Tools = "Built with";
        public static readonly string[] Groups = { Models, Libraries, Ideas, Downloads, Tools };

        /// <summary>Every public release, oldest first.</summary>
        public static readonly string[] Versions = { "2.1.0", "2.2.0", "2.2.1", "2.2.2", "2.3.0", "2.3.1", "2.4.0", "2.5.0", "2.6.0", "2.7.0", "3.0.0", "3.0.1", "3.0.2", "3.1.0", "3.1.1", "3.2.0", "3.3.0", "3.3.1", "3.4.0" };

        /// <summary>What changed in the credits with each release.</summary>
        public static readonly Dictionary<string, string> Changes = new Dictionary<string, string>
        {
            { "2.1.0", "First public release: everything below." },
            { "2.2.0", "No new third-party code, models or data." },
            { "2.2.1", "No new third-party code, models or data." },
            { "2.2.2", "No new third-party code, models or data." },
            { "2.3.0", "Low VRAM: onnxconverter-common (half-precision model files) and ONNX Runtime's model optimizer build the new deepfilternet3_low.onnx and camfx_1080_low.onnx, which are made from DeepFilterNet 3, Robust Video Matting and UltraFace." },
            { "2.3.1", "This Credits window. No new third-party code, models or data." },
            { "2.4.0", "No new third-party code, models or data. The installer now also downloads VB-CABLE, Hi-Fi Cable and Equalizer APO (already credited) from their makers." },
            { "2.5.0", "No new third-party code, models or data. The Test footsteps rings use the SADIE II head responses (already credited) at their measured elevations." },
            { "2.6.0", "No new third-party code, models or data." },
            { "2.7.0", "No new third-party code, models or data." },
            { "3.0.0", "No new third-party code, models or data. Added: the ITU-R BS.1770 loudness weighting, which StreamLite now uses to play every game profile at the same loudness." },
            { "3.0.1", "No new third-party code, models or data." },
            { "3.0.2", "No new third-party code, models or data." },
            { "3.1.0", "No new third-party code, models or data. Direct to headphones uses Windows' own per-app capture (process loopback) and exclusive mode." },
            { "3.1.1", "No new third-party code, models or data." },
            { "3.2.0", "No new third-party code, models or data. Added: non-negative matrix factorisation (Record a sound to remove), and Microsoft's DNSMOS, which judged the voice-quality fixes during development (not included in StreamLite)." },
            { "3.3.0", "No new third-party code. More of SADIE II (already credited, Apache-2.0): the full KU100 and KEMAR measurements and the 18 measured human heads. Added: the ANSI S3.5 standard speech spectrum and spectral band replication (Studio voice), the Linkwitz-Riley crossover (plosive pops), and h5py, which reads the SADIE II files during development (not included)." },
            { "3.3.1", "No new third-party code, models or data. Added: Olli Niemitalo's allpass Hilbert transformer and the cascade graphic equalizer design of Liski and Välimäki - Studio voice now works with filters and adds no delay." },
            { "3.4.0", "New: MediaPipe's palm detector and hand landmark model (Google, Apache-2.0, as published in the OpenCV model zoo) for hand gestures. Added: Heckbert's square-to-quadrilateral mapping, which the virtual gimbal's views are made of (whiteboard and desk view, levelling, upside-down cameras)." },
        };

        public static readonly Credit[] All =
        {
            // ---- models and data
            new Credit { Group = Models, Name = "DeepFilterNet 3", Url = "https://github.com/Rikorose/DeepFilterNet", What = "AI noise removal (studio-quality model)",
                Authors = "Hendrik Schr\u00f6ter, Tobias Rosenkranz, Alberto N. Escalante-B., Andreas Maier", License = "MIT or Apache-2.0",
                File = "https://huggingface.co/kimtos-labs/denoiser-dfn3" },
            new Credit { Group = Models, Name = "torchDF", Url = "https://github.com/grazder/DeepFilterNet", What = "Streaming ONNX export of DeepFilterNet 3 (the model file StreamLite runs)",
                Authors = "grazder", License = "MIT or Apache-2.0" },
            new Credit { Group = Models, Name = "RNNoise", Url = "https://github.com/xiph/rnnoise", What = "Light AI noise removal (C# port of v0.1.1 and its trained model)",
                Authors = "Jean-Marc Valin, Mozilla, Xiph.Org Foundation", License = "BSD-3-Clause",
                File = "https://github.com/xiph/rnnoise/blob/v0.1.1/src/rnn_data.c" },
            new Credit { Group = Models, Name = "SADIE II HRTF database", Url = "https://www.york.ac.uk/sadie-project/database.html", What = "3D audio: measured head responses at full resolution - the KU100 and KEMAR dummy heads and 18 human heads",
                Authors = "University of York - C. Armstrong, L. Thresh, D. Murphy, G. Kearney", License = "Apache-2.0" },
            new Credit { Group = Models, Name = "Robust Video Matting", Url = "https://github.com/PeterL1n/RobustVideoMatting", What = "AI camera: cuts you out of the room (blur, green screen, picture, video)",
                Authors = "Shanchuan Lin, Linjie Yang, Imran Saleemi, Soumyadip Sengupta (ByteDance)", License = "GPL-3.0", Platform = "Windows",
                File = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx" },
            new Credit { Group = Models, Name = "Ultra-Light-Fast-Generic-Face-Detector-1MB (UltraFace)", Url = "https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB", What = "AI camera: face tracking",
                Authors = "Linzaer", License = "MIT", Platform = "Windows",
                File = "https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/blob/master/models/onnx/version-RFB-320.onnx" },
            new Credit { Group = Models, Name = "MediaPipe Hands (palm detection + hand landmarks)", Url = "https://github.com/opencv/opencv_zoo", What = "AI camera: hand gestures (open palm, V, L) - the 2023 models as published in the OpenCV model zoo",
                Authors = "Google (MediaPipe); OpenCV model zoo", License = "Apache-2.0", Platform = "Windows", Since = "3.4.0",
                File = "https://huggingface.co/opencv/palm_detection_mediapipe" },
            new Credit { Group = Models, Name = "SoundGuys headphone measurements", Url = "https://www.soundguys.com", What = "Built-in correction for the Sennheiser MOMENTUM 5 (B&K 5128 measurement)",
                Authors = "SoundGuys", License = "Measurement used with thanks" },

            // ---- libraries and runtimes
            new Credit { Group = Libraries, Name = "ONNX Runtime", Url = "https://github.com/microsoft/onnxruntime", What = "Runs the AI models (and, from 2.3.0, optimizes the Low VRAM model files)",
                Authors = "Microsoft", License = "MIT" },
            new Credit { Group = Libraries, Name = "DirectML", Url = "https://github.com/microsoft/DirectML", What = "Runs the AI models on any DirectX 12 graphics card",
                Authors = "Microsoft", License = "Microsoft Software License Terms (redistributable)", Platform = "Windows" },
            new Credit { Group = Libraries, Name = ".NET runtime", Url = "https://github.com/dotnet/runtime", What = "Runs StreamLite for Linux (included)",
                Authors = ".NET Foundation and contributors", License = "MIT", Platform = "Linux" },
            new Credit { Group = Libraries, Name = "Avalonia UI", Url = "https://github.com/AvaloniaUI/Avalonia", What = "The Linux interface",
                Authors = "AvaloniaUI O\u00dc and contributors", License = "MIT", Platform = "Linux" },
            new Credit { Group = Libraries, Name = "SkiaSharp / HarfBuzzSharp", Url = "https://github.com/mono/SkiaSharp", What = "Drawing and text for the Linux interface (Skia, HarfBuzz)",
                Authors = "Microsoft / .NET Foundation; Google (Skia); the HarfBuzz authors", License = "MIT; BSD-3-Clause (Skia); MIT (HarfBuzz)", Platform = "Linux" },
            new Credit { Group = Libraries, Name = "Inter", Url = "https://github.com/rsms/inter", What = "Interface font",
                Authors = "Rasmus Andersson", License = "SIL Open Font License 1.1", Platform = "Linux" },
            new Credit { Group = Libraries, Name = "PipeWire / PulseAudio", Url = "https://pipewire.org", What = "Linux audio (your system's libpulse and pactl)",
                Authors = "Wim Taymans and the PipeWire contributors; the PulseAudio developers", License = "MIT / LGPL-2.1", Platform = "Linux" },

            // ---- code, ideas and research
            new Credit { Group = Ideas, Name = "EarTrumpet", Url = "https://github.com/File-New-Project/EarTrumpet", What = "How per-app audio routing works on Windows",
                Authors = "File-New-Project", License = "MIT", Platform = "Windows" },
            new Credit { Group = Ideas, Name = "Impulcifer", Url = "https://github.com/jaakkopasanen/Impulcifer", What = "HeSuVi 14-channel HRIR track order",
                Authors = "Jaakko Pasanen", License = "MIT" },
            new Credit { Group = Ideas, Name = "VCamSample", Url = "https://github.com/smourier/VCamSample", What = "Structure of the Windows 11 virtual camera",
                Authors = "Simon Mourier", License = "MIT", Platform = "Windows" },
            new Credit { Group = Ideas, Name = "Guided Image Filtering", Url = "https://doi.org/10.1109/TPAMI.2012.213", What = "AI camera: denoise, skin smoothing and crisp cut-out edges",
                Authors = "Kaiming He, Jian Sun, Xiaoou Tang", License = "Published research", Platform = "Windows" },
            new Credit { Group = Ideas, Name = "ITU-R BS.1770 loudness weighting", Url = "https://www.itu.int/rec/R-REC-BS.1770", What = "K-weighting: every game profile matched to the same loudness",
                Authors = "International Telecommunication Union", License = "Published standard", Since = "3.0.0" },
            new Credit { Group = Ideas, Name = "Non-negative matrix factorization", Url = "https://doi.org/10.1038/44565", What = "Record a sound to remove: that sound and your voice as learned spectral patterns, separated moment by moment",
                Authors = "Daniel D. Lee, H. Sebastian Seung", License = "Published research", Since = "3.2.0" },
            new Credit { Group = Ideas, Name = "ANSI S3.5-1997 standard speech spectrum", Url = "https://webstore.ansi.org/standards/asa/ansis31997r2017", What = "Studio voice: what speech through a good close mic sounds like (the target your mic is corrected towards)",
                Authors = "Acoustical Society of America / ANSI", License = "Published standard", Since = "3.3.0" },
            new Credit { Group = Ideas, Name = "Spectral band replication", Url = "https://www.aes.org/e-lib/browse.cfm?elib=11229", What = "Studio voice: treble a cheap mic cuts off, rebuilt from the octave below",
                Authors = "Martin Dietz, Lars Liljeryd, Kristofer Kjorling, Oliver Kunz (Coding Technologies)", License = "Published research", Since = "3.3.0" },
            new Credit { Group = Ideas, Name = "Linkwitz-Riley crossover", Url = "https://www.linkwitzlab.com/filters.htm", What = "Studio voice: plosive pops' rumble split off and turned down, the rest adding back up exactly",
                Authors = "Siegfried Linkwitz, Russ Riley", License = "Published research", Since = "3.3.0" },
            new Credit { Group = Ideas, Name = "Hilbert transformer (allpass pair)", Url = "https://yehar.com/blog/", What = "Studio voice: the treble rebuild's shift, with no delay (two allpass chains 90 degrees apart)",
                Authors = "Olli Niemitalo", License = "Published design", Since = "3.3.1" },
            new Credit { Group = Ideas, Name = "Cascade graphic equalizer design", Url = "https://dafx.de/paper-archive/details/Ycir51R86FWcfPNqrjV2kQ", What = "Studio voice: the 25 band filters' gains solved together for the correction (the band interaction taken into account)",
                Authors = "Juho Liski, Vesa Välimäki", License = "Published research", Since = "3.3.1" },
            new Credit { Group = Ideas, Name = "Square-to-quadrilateral mapping", Url = "https://www2.eecs.berkeley.edu/Pubs/TechRpts/1989/5504.html", What = "AI camera: the virtual gimbal's views - whiteboard and desk view squared off, levelling, upside-down cameras",
                Authors = "Paul S. Heckbert", License = "Published research", Platform = "Windows", Since = "3.4.0" },

            // ---- downloaded on request
            new Credit { Group = Downloads, Name = "AutoEq", Url = "https://github.com/jaakkopasanen/AutoEq", What = "Headphone corrections for thousands of headphones (Find my headphones)",
                Authors = "Jaakko Pasanen and the measurement contributors (oratory1990, crinacle, Rtings, Innerfidelity...)", License = "MIT",
                File = "https://github.com/jaakkopasanen/AutoEq/tree/master/results" },
            new Credit { Group = Downloads, Name = "VB-CABLE and Hi-Fi Cable", Url = "https://vb-audio.com/Cable/", What = "The virtual mic cable and the 3D game audio device",
                Authors = "VB-Audio Software (Vincent Burel)", License = "Donationware - please support VB-Audio", Platform = "Windows" },
            new Credit { Group = Downloads, Name = "Equalizer APO", Url = "https://sourceforge.net/projects/equalizerapo/", What = "System-wide headphone EQ",
                Authors = "Jonas Thedering", License = "GPL-2.0", Platform = "Windows" },

            // ---- build tools
            new Credit { Group = Tools, Name = "WiX Toolset", Url = "https://github.com/wixtoolset/wix", What = "The Windows installer (MSI)",
                Authors = ".NET Foundation and contributors", License = "MS-RL", Platform = "Windows" },
            new Credit { Group = Tools, Name = "Zig", Url = "https://github.com/ziglang/zig", What = "Builds the virtual camera's native DLL",
                Authors = "Andrew Kelley and the Zig contributors", License = "MIT", Platform = "Windows" },
            new Credit { Group = Tools, Name = "appimagetool", Url = "https://github.com/AppImage/appimagetool", What = "The Linux AppImage",
                Authors = "Simon Peter and the AppImage contributors", License = "MIT", Platform = "Linux" },
            new Credit { Group = Tools, Name = "DNSMOS P.835", Url = "https://github.com/microsoft/DNS-Challenge", What = "Judged the voice quality of every version of the mic chain while tuning it (development only, not included)",
                Authors = "Chandan K. A. Reddy, Vishak Gopal, Ross Cutler (Microsoft)", License = "CC-BY-4.0", Since = "3.2.0" },
            new Credit { Group = Tools, Name = "h5py", Url = "https://github.com/h5py/h5py", What = "Reads the SADIE II head measurements (tools/make_hrtf.py; development only, not included)",
                Authors = "Andrew Collette and the h5py contributors", License = "BSD-3-Clause", Since = "3.3.0" },
            new Credit { Group = Tools, Name = "ONNX", Url = "https://github.com/onnx/onnx", What = "Builds and adapts the model files (tools/make_camfx.py)",
                Authors = "ONNX Project Contributors (LF AI & Data)", License = "Apache-2.0" },
            new Credit { Group = Tools, Name = "NumPy", Url = "https://github.com/numpy/numpy", What = "Model-building scripts",
                Authors = "NumPy Developers", License = "BSD-3-Clause" },
            new Credit { Group = Tools, Name = "onnxconverter-common", Url = "https://github.com/microsoft/onnxconverter-common", What = "Half-precision Low VRAM model files",
                Authors = "Microsoft", License = "MIT", Since = "2.3.0" },
        };

        static Version V(string v) { return new Version(v); }

        /// <summary>The newest listed release at or below this version (2.3.5 -> 2.3.1).</summary>
        public static string Release(string version)
        {
            var v = V(version);
            string best = Versions[0];
            foreach (var r in Versions) if (V(r) <= v) best = r;
            return best;
        }

        /// <summary>Everything a version uses (optionally only one platform's).</summary>
        public static List<Credit> For(string version, string platform = null)
        {
            var v = V(Release(version));
            return All.Where(c => V(c.Since) <= v && (platform == null || c.Platform == null || c.Platform == platform)).ToList();
        }

        public static bool NewIn(Credit c, string version) { return c.Since == Release(version) && Release(version) != Versions[0]; }

        /// <summary>Markdown list of one version's credits (CREDITS.md section and GitHub release notes).</summary>
        public static string Markdown(string version)
        {
            var sb = new StringBuilder();
            string r = Release(version);
            string change;
            if (Changes.TryGetValue(r, out change)) sb.AppendLine("_" + change + "_").AppendLine();
            var list = For(r);
            foreach (var g in Groups)
            {
                var items = list.Where(c => c.Group == g).ToList();
                if (items.Count == 0) continue;
                sb.AppendLine("**" + g + "**").AppendLine();
                foreach (var c in items)
                    sb.AppendLine("- [" + c.Name + "](" + c.Url + ")" + (c.Platform != null ? " (" + c.Platform + ")" : "") + " - " + c.What + ". " + c.Authors + ". " + c.License + "." +
                        (c.File != null ? " [Model / data](" + c.File + ")." : "") + (NewIn(c, r) ? " **New in " + r + ".**" : ""));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>One version's credits as plain text (the license file, Linux --credits).</summary>
        public static string PlainText(string version)
        {
            var sb = new StringBuilder();
            var list = For(version);
            foreach (var g in Groups)
            {
                var items = list.Where(c => c.Group == g).ToList();
                if (items.Count == 0) continue;
                sb.Append(g).Append(":\r\n");
                foreach (var c in items)
                    sb.Append("  ").Append(c.Name).Append(c.Platform != null ? " (" + c.Platform + ")" : "").Append(" - ").Append(c.What).Append("\r\n    ")
                      .Append(c.Authors).Append(" - ").Append(c.License).Append("\r\n    ").Append(c.Url).Append(c.File != null ? "\r\n    Model / data: " + c.File : "").Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>CREDITS.md: every release, newest first.</summary>
        public static string Document()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Credits by version").AppendLine();
            sb.AppendLine("StreamLite stands on a lot of freely shared work. These are the projects, AI models and data each release uses,");
            sb.AppendLine("with links to their source. The same list is in the app (the **Credits** button above **Report a bug**) and in each");
            sb.AppendLine("GitHub release. Licenses and notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).");
            sb.AppendLine("This file is generated from `src/Credits.cs` (run `test\\run.ps1 CreditsTest write`).").AppendLine();
            for (int i = Versions.Length - 1; i >= 0; i--)
            {
                sb.AppendLine("## StreamLite " + Versions[i]).AppendLine();
                sb.Append(Markdown(Versions[i]));
            }
            return sb.ToString().Replace("\r\n", "\n");
        }
    }
}
