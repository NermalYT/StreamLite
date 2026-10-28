using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace StreamLite.Interop
{
    /// <summary>
    /// From a SYSTEM process (the MSI's install step): starts a program as the person signed in to Windows, with their
    /// admin rights (their elevated token), on their desktop and with their environment - exactly as if they had run it
    /// themselves and said yes to the Windows prompt. For installers that set things up "for the current user" (a
    /// scheduled task, a per-user setting), which fails or goes to the wrong place when run as SYSTEM.
    /// </summary>
    internal static class UserProcess
    {
        public static bool IsSystem { get { try { using (var id = WindowsIdentity.GetCurrent()) return id.IsSystem; } catch { return false; } } }

        /// <summary>
        /// Runs exe with args as the signed-in user, elevated, and waits (up to timeoutMs). False (with why) when it
        /// can't: not running as SYSTEM, nobody signed in, the user isn't an administrator, or Windows refused.
        /// </summary>
        /// <summary>The signed-in user's account (SID), from SYSTEM - for the admin task the installer makes for them. Null when nobody is signed in.</summary>
        public static string SignedInUserSid()
        {
            if (!IsSystem) return null;
            uint session = SignedInSession();
            IntPtr user;
            if (session == uint.MaxValue || !WTSQueryUserToken(session, out user)) return null;
            try { using (var id = new WindowsIdentity(user)) return id.User != null ? id.User.Value : null; }
            catch { return null; }
            finally { CloseHandle(user); }
        }

        public static bool RunElevated(string exe, string args, int timeoutMs, out int exitCode, out string why)
        {
            exitCode = -1; why = null;
            IntPtr user = IntPtr.Zero, run = IntPtr.Zero, env = IntPtr.Zero;
            var pi = new PROCESS_INFORMATION();
            try
            {
                if (!IsSystem) { why = "not running as SYSTEM"; return false; }
                uint session = SignedInSession();
                if (session == uint.MaxValue) { why = "nobody is signed in"; return false; }
                if (!WTSQueryUserToken(session, out user)) { why = "no token for the signed-in user (" + new Win32Exception().Message + ")"; return false; }
                // An administrator with UAC has two tokens: the everyday one and the elevated one linked to it.
                int type = TokenInt(user, TokenElevationType);
                if (type == 3)   // limited: take the linked (full) token
                {
                    int len;
                    IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        if (!GetTokenInformation(user, TokenLinkedToken, buf, IntPtr.Size, out len)) { why = "no elevated token (" + new Win32Exception().Message + ")"; return false; }
                        run = Marshal.ReadIntPtr(buf);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                else { run = user; user = IntPtr.Zero; }
                if (TokenInt(run, TokenElevation) == 0) { why = "the signed-in user isn't an administrator"; return false; }
                if (!CreateEnvironmentBlock(out env, run, false)) env = IntPtr.Zero;
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = @"winsta0\default", dwFlags = STARTF_USESHOWWINDOW, wShowWindow = 0 };
                string cmd = "\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args);
                if (!CreateProcessAsUser(run, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exe), ref si, out pi))
                { why = "couldn't start it as the user (" + new Win32Exception().Message + ")"; return false; }
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0) { TerminateProcess(pi.hProcess, 1); why = Path.GetFileName(exe) + " didn't finish in " + timeoutMs / 60000 + " minutes"; return false; }
                uint code; GetExitCodeProcess(pi.hProcess, out code); exitCode = (int)code;
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
            finally
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (run != IntPtr.Zero) CloseHandle(run);
                if (user != IntPtr.Zero) CloseHandle(user);
            }
        }

        /// <summary>The session of the person at the PC (the console), else the first active one (remote desktop).</summary>
        static uint SignedInSession()
        {
            uint console = WTSGetActiveConsoleSessionId();
            IntPtr t;
            if (console != uint.MaxValue && WTSQueryUserToken(console, out t)) { CloseHandle(t); return console; }
            IntPtr list; int count;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out list, out count)) return uint.MaxValue;
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(list + i * size, typeof(WTS_SESSION_INFO));
                    if (s.State == 0) return s.SessionId;   // WTSActive
                }
            }
            finally { WTSFreeMemory(list); }
            return uint.MaxValue;
        }

        static int TokenInt(IntPtr token, int cls)
        {
            int len; IntPtr buf = Marshal.AllocHGlobal(4);
            try { return GetTokenInformation(token, cls, buf, 4, out len) ? Marshal.ReadInt32(buf) : -1; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        const int TokenElevationType = 18, TokenLinkedToken = 19, TokenElevation = 20;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
        const int STARTF_USESHOWWINDOW = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] struct WTS_SESSION_INFO { public uint SessionId; public IntPtr pWinStationName; public int State; }

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int returned);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr env);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
