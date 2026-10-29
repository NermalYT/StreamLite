using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace StreamLite.Linux
{
    /// <summary>
    /// The AI on an NVIDIA graphics card on Linux (4.1.4): Windows runs it through DirectML on any card; Linux runs it
    /// through CUDA, which needs NVIDIA's CUDA libraries and the GPU build of ONNX Runtime - about 1.7 GB, downloaded once
    /// when you pick a graphics-card AI quality, from NuGet and PyPI (NVIDIA's own packages), checked against the
    /// fingerprints below and kept in ~/.local/share/streamlite-gpu. Other graphics cards run the AI on the CPU.
    /// </summary>
    internal static class LinuxGpu
    {
        sealed class Part
        {
            public string Name, Url, Sha256; public long Mb;
            public Func<string, bool> Wanted;   // which files of the package (zip entry names)
        }

        static readonly string[] CudaLibs = { "libcudart.so.12", "libcublasLt.so.12", "libcublas.so.12", "libcurand.so.10", "libcufft.so.11" };

        static readonly Part[] Parts =
        {
            new Part { Name = "ONNX Runtime (GPU)", Mb = 196, Sha256 = "06540847b4f83cc5fd92562263122659452cf62783e9a1477b6000b2d947b542",
                Url = "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.linux/1.24.4/microsoft.ml.onnxruntime.gpu.linux.1.24.4.nupkg",
                Wanted = n => n.StartsWith("runtimes/linux-x64/native/") && (n.EndsWith("/libonnxruntime.so") || n.EndsWith("_providers_shared.so") || n.EndsWith("_providers_cuda.so")) },
            new Part { Name = "CUDA runtime", Mb = 3, Sha256 = "25bba2dfb01d48a9b59ca474a1ac43c6ebf7011f1b0b8cc44f54eb6ac48a96c3",
                Url = "https://files.pythonhosted.org/packages/bc/46/a92db19b8309581092a3add7e6fceb4c301a3fd233969856a8cbf042cd3c/nvidia_cuda_runtime_cu12-12.9.79-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
                Wanted = n => n.EndsWith("/libcudart.so.12") },
            new Part { Name = "cuBLAS", Mb = 554, Sha256 = "e4f53a8ca8c5d6e8c492d0d0a3d565ecb59a751b19cfdaa4f6da0ab2104c1702",
                Url = "https://files.pythonhosted.org/packages/cb/c0/0a517bfe63ccd3b92eb254d264e28fca3c7cab75d07daea315250fb1bf73/nvidia_cublas_cu12-12.9.2.10-py3-none-manylinux_2_27_x86_64.whl",
                Wanted = n => n.EndsWith("/libcublas.so.12") || n.EndsWith("/libcublasLt.so.12") },
            new Part { Name = "cuRAND", Mb = 65, Sha256 = "49b274db4780d421bd2ccd362e1415c13887c53c214f0d4b761752b8f9f6aa1e",
                Url = "https://files.pythonhosted.org/packages/31/44/193a0e171750ca9f8320626e8a1f2381e4077a65e69e2fb9708bd479e34a/nvidia_curand_cu12-10.3.10.19-py3-none-manylinux_2_27_x86_64.whl",
                Wanted = n => n.EndsWith("/libcurand.so.10") },
            new Part { Name = "cuFFT", Mb = 192, Sha256 = "c67884f2a7d276b4b80eb56a79322a95df592ae5e765cf1243693365ccab4e28",
                Url = "https://files.pythonhosted.org/packages/95/f4/61e6996dd20481ee834f57a8e9dca28b1869366a135e0d42e2aa8493bdd4/nvidia_cufft_cu12-11.4.1.4-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
                Wanted = n => n.EndsWith("/libcufft.so.11") },
            new Part { Name = "cuDNN", Mb = 731, Sha256 = "0a4aa3a7d2264256506c6857fc41fc0c499982f78d70195b1cfcc9055c1957cd",
                Url = "https://files.pythonhosted.org/packages/65/e4/c5a205d48ff00ed8b27882bb45d338d8138e976c76385f328a326bbfaeda/nvidia_cudnn_cu12-9.27.0.42-py3-none-manylinux_2_27_x86_64.whl",
                Wanted = n => n.Contains("/lib/libcudnn") && n.EndsWith(".so.9") },
        };

        /// <summary>What the download is, in MB.</summary>
        public static long DownloadMb { get { return Parts.Sum(p => p.Mb); } }
        const string Stamp = "ort-1.24.4 cuda-12.9 cudnn-9.27";

        public static string Folder
        {
            get
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(data, "streamlite-gpu");
            }
        }

        static string ReadyFile { get { return Path.Combine(Folder, "ready.txt"); } }

        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
        [DllImport("libdl.so.2")] static extern int dlclose(IntPtr handle);

        static int nvidia = -1;
        /// <summary>An NVIDIA graphics card with its driver (the driver's CUDA library is there).</summary>
        public static bool HasNvidia
        {
            get
            {
                if (nvidia < 0)
                {
                    try { IntPtr h = dlopen("libcuda.so.1", 1); nvidia = h != IntPtr.Zero ? 1 : 0; if (h != IntPtr.Zero) dlclose(h); }
                    catch { nvidia = 0; }
                    if (Environment.GetEnvironmentVariable("STREAMLITE_NO_GPU") == "1") nvidia = 0;
                }
                return nvidia == 1;
            }
        }

        /// <summary>The CUDA libraries are downloaded (and the card is there to use them).</summary>
        public static bool Ready
        {
            get
            {
                try { return HasNvidia && File.Exists(ReadyFile) && File.ReadAllText(ReadyFile).Trim() == Stamp; }
                catch { return false; }
            }
        }

        static long vram = -1;
        /// <summary>The NVIDIA card's memory (MB), or 0.</summary>
        public static long VramMb
        {
            get
            {
                if (vram >= 0) return vram;
                vram = 0;
                if (!HasNvidia) return 0;
                foreach (var l in Run("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits"))
                {
                    long v; if (long.TryParse(l.Trim(), out v)) vram = Math.Max(vram, v);
                }
                return vram;
            }
        }

        /// <summary>The graphics card's name ("NVIDIA GeForce RTX 4070"), or null.</summary>
        public static string CardName
        {
            get { return HasNvidia ? Run("nvidia-smi", "--query-gpu=name --format=csv,noheader").Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null; }
        }

        /// <summary>Video memory StreamLite uses on the card (MB), or -1 when the driver doesn't say.</summary>
        public static int UsedMb()
        {
            if (!loaded) return -1;
            int pid = Process.GetCurrentProcess().Id; int sum = 0; bool found = false;
            foreach (var l in Run("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits"))
            {
                var f = l.Split(','); int p, mb;
                if (f.Length == 2 && int.TryParse(f[0].Trim(), out p) && p == pid && int.TryParse(f[1].Trim(), out mb)) { sum += mb; found = true; }
            }
            return found ? sum : -1;
        }

        static List<string> Run(string program, string args)
        {
            var l = new List<string>();
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }))
                {
                    p.StandardError.ReadToEndAsync();
                    string all = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) l.AddRange(all.Split('\n').Where(x => x.Trim().Length > 0));
                }
            }
            catch { }
            return l;
        }

        static bool loaded;
        /// <summary>
        /// Before ONNX Runtime starts: its GPU build from the download (it runs the CPU models too), with NVIDIA's libraries
        /// loaded first so it and its CUDA part find them. null = not downloaded (the CPU build in the app runs).
        /// </summary>
        public static string OnnxRuntimePath()
        {
            if (!Ready) return null;
            string lib = Path.Combine(Folder, "lib");
            foreach (var name in CudaLibs) if (!Load(Path.Combine(lib, name))) return null;
            // cuDNN: its front library, then its parts (it opens them by name).
            var cudnn = Directory.GetFiles(lib, "libcudnn*.so.9").OrderBy(f => Path.GetFileName(f) == "libcudnn.so.9" ? 1 : 0).ToList();
            foreach (var f in cudnn.Where(f => Path.GetFileName(f) != "libcudnn.so.9")) Load(f);
            if (!Load(Path.Combine(lib, "libcudnn.so.9"))) return null;
            loaded = true;
            return Path.Combine(lib, "libonnxruntime.so");
        }

        static bool Load(string path)
        {
            try { return File.Exists(path) && dlopen(path, 2 | 0x100) != IntPtr.Zero; }   // RTLD_NOW | RTLD_GLOBAL
            catch { return false; }
        }

        /// <summary>ONNX Runtime's GPU build is the one running (CUDA sessions can be made).</summary>
        public static bool Loaded { get { return loaded; } }

        /// <summary>
        /// Downloads and unpacks everything (on a worker thread): progress(done MB, all MB, what), cancel = stop. Returns
        /// null when done, else what went wrong. A part already there isn't downloaded again.
        /// </summary>
        public static string Download(Action<long, long, string> progress, Func<bool> cancel)
        {
            string lib = Path.Combine(Folder, "lib"), tmp = Path.Combine(Folder, "download");
            try
            {
                Directory.CreateDirectory(lib); Directory.CreateDirectory(tmp);
                try { File.Delete(ReadyFile); } catch { }
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
                long all = DownloadMb, before = 0;
                foreach (var part in Parts)
                {
                    string doneMark = Path.Combine(tmp, part.Sha256 + ".done");
                    if (File.Exists(doneMark)) { before += part.Mb; progress(before, all, part.Name); continue; }
                    string file = Path.Combine(tmp, part.Sha256 + ".zip");
                    string err = Fetch(part, file, b => progress(before + b / 1048576, all, part.Name), cancel);
                    if (err != null) return err;
                    using (var zip = ZipFile.OpenRead(file))
                        foreach (var e in zip.Entries)
                        {
                            if (!part.Wanted(e.FullName)) continue;
                            string to = Path.Combine(lib, e.Name);
                            e.ExtractToFile(to + ".part", true);
                            if (File.Exists(to)) File.Delete(to);
                            File.Move(to + ".part", to);
                        }
                    File.Delete(file);
                    File.WriteAllText(doneMark, part.Name);
                    before += part.Mb;
                }
                foreach (var name in CudaLibs.Concat(new[] { "libcudnn.so.9", "libonnxruntime.so", "libonnxruntime_providers_cuda.so", "libonnxruntime_providers_shared.so" }))
                    if (!File.Exists(Path.Combine(lib, name))) return name + " is missing from the download";
                File.WriteAllText(ReadyFile, Stamp);
                try { Directory.Delete(tmp, true); } catch { }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static string Fetch(Part part, string file, Action<long> got, Func<bool> cancel)
        {
            var req = (HttpWebRequest)WebRequest.Create(part.Url);
            req.UserAgent = "StreamLite";
            req.Timeout = 30000; req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(file))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[1 << 20]; long total = 0; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel()) return "cancelled";
                    dst.Write(buf, 0, n); sha.TransformBlock(buf, 0, n, null, 0);
                    total += n; got(total);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hash != part.Sha256) return part.Name + " didn't download correctly (its fingerprint doesn't match) - try again";
            }
            return null;
        }

        /// <summary>StreamLite starts again once this one has quit (after the download: ONNX Runtime's GPU build loads at start).</summary>
        public static void RestartAfterExit()
        {
            int pid = Process.GetCurrentProcess().Id;
            string cmd = "while kill -0 " + pid + " 2>/dev/null; do sleep 0.2; done; exec " + Interop.Shell.Quote(LinuxApp.Launcher);
            try { Process.Start(new ProcessStartInfo("setsid", "sh -c " + Interop.Shell.Quote(cmd)) { UseShellExecute = false }).Dispose(); } catch { }
        }

        /// <summary>Removes the download (the AI goes back to the CPU at the next start).</summary>
        public static void Remove()
        {
            try { Directory.Delete(Folder, true); } catch { }
        }
    }
}
