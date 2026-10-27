using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace StreamLite.Camera
{
    /// <summary>
    /// "StreamLite Camera": a Windows 11 virtual camera (MFCreateVirtualCamera) that any app (Discord, OBS, Teams,
    /// browsers, the Camera app) can pick. Windows' Frame Server runs our media source (StreamLiteCam.dll, native) and it
    /// reads the frames we publish here: NV12 in the shared memory section Global\StreamLiteCamFrame. The source writes
    /// back a heartbeat while an app is watching, so the camera and its AI effects only run when someone uses them.
    /// Registering the source machine-wide needs administrator rights once. Without them afterwards the frames go
    /// through a temporary file in ProgramData instead of the Global\ section (only admins may create those).
    /// </summary>
    internal sealed class VirtualCamera : IDisposable
    {
        public const string Clsid = "{7D1F6A52-3B9E-4C3A-9F61-2E5A8C47D319}";
        public const string FriendlyName = "StreamLite Camera";
        public const int Width = 1920, Height = 1080;
        const int HeaderSize = 64;
        const uint Magic = 0x31434C53;   // 'SLC1'

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfsensorgroup.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateVirtualCamera(int type, int lifetime, int access, string friendlyName, string sourceId, IntPtr categories, uint categoryCount, out IntPtr camera);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int StartFn(IntPtr self, IntPtr callback);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int VoidFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint ReleaseFn(IntPtr self);
        [DllImport("kernel32")] static extern ulong GetTickCount64();
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);
        [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

        IntPtr cam;
        MemoryMappedFile map;
        MemoryMappedViewAccessor view;
        IntPtr basePtr;
        int seq;
        public string Error;

        /// <summary>Where the source DLL lives: the Frame Server (Local Service) can't read user profiles.</summary>
        static string SharedDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamLite", "cam"); } }
        static string BundledDll { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cam", "StreamLiteCam.dll"); } }

        public static bool Supported
        {
            get { return Environment.OSVersion.Version.Build >= 22000 && File.Exists(BundledDll); }
        }

        /// <summary>
        /// Copies the media source next to ProgramData (a new file name per build, so an old copy still loaded by the
        /// Frame Server never blocks an update), lets Local Service read it and registers it machine-wide.
        /// </summary>
        public static string Register()
        {
            string src = BundledDll;
            if (!File.Exists(src)) return "StreamLiteCam.dll is missing next to StreamLite.exe.";
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(File.ReadAllBytes(src));
            string tag = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            string want = Path.Combine(SharedDir, "StreamLiteCam-" + tag + ".dll");
            using (var k = Registry.LocalMachine.OpenSubKey(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32"))
                if (k != null && string.Equals(k.GetValue("") as string, want, StringComparison.OrdinalIgnoreCase) && File.Exists(want)) return null;   // this build is registered
            if (!Elevation.IsElevated) return NeedsAdmin;
            Directory.CreateDirectory(SharedDir);
            var ds = Directory.GetAccessControl(SharedDir);
            var local = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
            ds.AddAccessRule(new FileSystemAccessRule(local, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(SharedDir, ds);
            string dst = Path.Combine(SharedDir, "StreamLiteCam-" + tag + ".dll");
            if (!File.Exists(dst)) File.Copy(src, dst, true);
            using (var k = Registry.LocalMachine.CreateSubKey(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32"))
            {
                k.SetValue("", dst);
                k.SetValue("ThreadingModel", "Both");
            }
            foreach (var old in Directory.GetFiles(SharedDir, "StreamLiteCam-*.dll"))
                if (!string.Equals(old, dst, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }   // still loaded: next time
            return null;
        }

        public const string NeedsAdmin = "Setting up StreamLite Camera needs Windows' permission once.";

        /// <summary>
        /// Without admin rights: Windows asks for permission once and a helper copy of StreamLite registers the camera
        /// (instead of telling you to restart as administrator). null = registered.
        /// </summary>
        public static string RegisterWithPrompt()
        {
            string err = Register();
            if (err != NeedsAdmin) return err;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--register-camera") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { return "Windows' permission prompt was declined - turn the camera off and on to be asked again."; }
            catch (Exception ex) { return ex.Message; }
            err = Register();
            return err == NeedsAdmin ? "The camera couldn't be set up - turn it off and on to try again." : err;
        }

        public static void Unregister()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\" + Clsid, false); } catch { }
            try { if (Directory.Exists(SharedDir)) foreach (var f in Directory.GetFiles(SharedDir)) try { File.Delete(f); } catch { } } catch { }
        }

        /// <summary>Registers (if needed), creates the shared frame and adds the camera to Windows for this session.</summary>
        public bool Start()
        {
            try
            {
                string err = Register();
                if (err != null) { Error = err; return false; }
                OpenShared();
                int hr = MFStartup(0x00020070, 0);
                if (hr < 0) { Error = "Media Foundation didn't start (0x" + hr.ToString("X8") + ")."; return false; }
                hr = MFCreateVirtualCamera(0, 0, 0, FriendlyName, Clsid, IntPtr.Zero, 0, out cam);   // software source, session lifetime, current user
                if (hr < 0) { Error = "Windows refused to add the camera (0x" + hr.ToString("X8") + ")."; cam = IntPtr.Zero; return false; }
                hr = Call<StartFn>(36)(cam, IntPtr.Zero);   // IMFVirtualCamera::Start
                if (hr < 0) { Error = "The camera didn't start (0x" + hr.ToString("X8") + ")."; Remove(); return false; }
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        T Call<T>(int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(cam);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
        }

        void Remove()
        {
            if (cam == IntPtr.Zero) return;
            try { Call<VoidFn>(38)(cam); } catch { }        // Remove (not Shutdown: that would shut the source down twice)
            try { Call<ReleaseFn>(2)(cam); } catch { }
            cam = IntPtr.Zero;
        }

        void OpenShared()
        {
            var sec = new MemoryMappedFileSecurity();
            sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), MemoryMappedFileRights.ReadWrite, AccessControlType.Allow));
            sec.AddAccessRule(new AccessRule<MemoryMappedFileRights>(WindowsIdentity.GetCurrent().User, MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            long size = HeaderSize + (long)Width * Height * 3 / 2;
            // Without admin rights CreateOrOpen on Global\ doesn't fail - it retries for many seconds - so don't try it.
            if (!Elevation.IsElevated) map = FileBacked(size);
            else try { map = MemoryMappedFile.CreateOrOpen(@"Global\StreamLiteCamFrame", size, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, sec, HandleInheritability.None); }
            catch (UnauthorizedAccessException) { map = FileBacked(size); }
            view = map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            var h = view.SafeMemoryMappedViewHandle;
            bool ok = false;
            h.DangerousAddRef(ref ok);
            basePtr = h.DangerousGetHandle();
            WriteHeader();
        }

        /// <summary>
        /// Without admin rights: a temporary file (kept in memory by Windows, deleted when StreamLite closes it) that the
        /// camera source maps instead. Readable/writable by Local Service (the Frame Server) and this user only.
        /// </summary>
        static MemoryMappedFile FileBacked(long size)
        {
            string path = Path.Combine(SharedDir, "frame.bin");
            string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;LS)(A;;GA;;;" + WindowsIdentity.GetCurrent().User.Value + ")";
            var sd = new RawSecurityDescriptor(sddl);
            var bytes = new byte[sd.BinaryLength]; sd.GetBinaryForm(bytes, 0);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var sa = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pin.AddrOfPinnedObject() };
                // GENERIC_READ|WRITE, share read/write/delete, CREATE_ALWAYS, TEMPORARY | DELETE_ON_CLOSE
                var h = CreateFileW(path, 0xC0000000, 7, ref sa, 2, 0x100 | 0x04000000, IntPtr.Zero);
                if (h.IsInvalid) throw new IOException("Couldn't create " + path + " (error " + Marshal.GetLastWin32Error() + ").");
                var fs = new FileStream(h, FileAccess.ReadWrite);
                fs.SetLength(size);
                return MemoryMappedFile.CreateFromFile(fs, null, size, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, false);
            }
            finally { pin.Free(); }
        }

        void WriteHeader()
        {
            Marshal.WriteInt32(basePtr, 0, (int)Magic);
            Marshal.WriteInt32(basePtr, 4, Width);
            Marshal.WriteInt32(basePtr, 8, Height);
        }

        /// <summary>Milliseconds since an app last took a frame (int.MaxValue if never).</summary>
        public long SinceLastConsumer
        {
            get
            {
                if (basePtr == IntPtr.Zero) return int.MaxValue;
                ulong t = (ulong)Marshal.ReadInt64(basePtr, 24);
                return t == 0 ? int.MaxValue : (long)(GetTickCount64() - t);
            }
        }

        /// <summary>Size the watching app asked for (it may be smaller than what we publish).</summary>
        public void ConsumerSize(out int w, out int h)
        {
            w = basePtr == IntPtr.Zero ? 0 : Marshal.ReadInt32(basePtr, 32);
            h = basePtr == IntPtr.Zero ? 0 : Marshal.ReadInt32(basePtr, 36);
        }

        /// <summary>Publishes one NV12 frame (Width x Height): Y plane then interleaved UV.</summary>
        public void Publish(byte[] y, byte[] uv)
        {
            if (basePtr == IntPtr.Zero) return;
            int s = Interlocked.Increment(ref seq) * 2 - 1;      // odd: writing
            Marshal.WriteInt32(basePtr, 12, s);
            Thread.MemoryBarrier();
            Marshal.Copy(y, 0, basePtr + HeaderSize, Width * Height);
            Marshal.Copy(uv, 0, basePtr + HeaderSize + Width * Height, Width * Height / 2);
            Thread.MemoryBarrier();
            Marshal.WriteInt64(basePtr, 16, (long)GetTickCount64());
            Marshal.WriteInt32(basePtr, 12, s + 1);
        }

        public void Dispose()
        {
            Remove();
            if (view != null) { view.SafeMemoryMappedViewHandle.DangerousRelease(); view.Dispose(); view = null; }
            if (map != null) { map.Dispose(); map = null; }
            basePtr = IntPtr.Zero;
        }
    }
}
