using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
#if LINUX
using File = System.IO.File;
#endif
using Microsoft.Win32;

namespace StreamLite
{
    /// <summary>
    /// Is an app a game? (4.1.2: 3D audio is for games only by default.) It is when a game profile has it, when Windows'
    /// Game Bar knows it as a game (it keeps a list of the games played on this PC), or when it runs from a game store's
    /// library folder (Steam, Epic, GOG, Xbox, Riot, EA, Ubisoft, Rockstar...). The stores' own launchers aren't games.
    /// </summary>
    internal static class GameDetect
    {
        static readonly string[] Libraries =
        {
            @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\", @"\xboxgames\", @"\riot games\",
            @"\ea games\", @"\origin games\", @"\ubisoft game launcher\games\", @"\rockstar games\", @"\battle.net\games\",
            @"\amazon games\library\", @"\itch\apps\",
        };

        /// <summary>Launchers, stores and their helpers: they play sound (store pages, trailers) but aren't games.</summary>
        static readonly HashSet<string> Launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "galaxyclient", "galaxyclient helper", "riotclientservices",
            "riotclientux", "riotclientuxrender", "riot client", "eadesktop", "eabackgroundservice", "origin", "upc", "ubisoftconnect",
            "battle.net", "agent", "rockstarservice", "launcher", "playnite.desktopapp", "playnite.fullscreenapp", "xboxpcapp", "gamingservices",
            "amazon games", "itch", "socialclubhelper", "unitycrashhandler64", "unitycrashhandler32", "crashreportclient",
        };

        /// <summary>A game? name = program name without .exe; path = its full path (or null); games = game profiles' programs.</summary>
        public static bool IsGame(string name, string path, ICollection<string> games)
        {
            name = GameProfiles.NormalizeExe(name);
            if (name == null || Launchers.Contains(name) || name.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (games != null && games.Contains(name)) return true;
            if (!string.IsNullOrEmpty(path))
            {
                var p = path.Replace('/', '\\').ToLowerInvariant();
                if (Libraries.Any(l => p.Contains(l))) return true;
            }
            return GameBarGames().Contains(name);
        }

        /// <summary>A running app's full path (works for most apps, also without administrator rights), or null.</summary>
        public static string PathOf(int pid)
        {
#if LINUX
            // Linux: /proc/<pid>/exe (for a Windows game under Wine / Proton, its .exe is on the command line).
            try
            {
                var cmd = File.ReadAllText("/proc/" + pid + "/cmdline").Split('\0').FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (cmd != null) return cmd.Replace('\\', '/');
                var b = new byte[1024]; int n = readlink("/proc/" + pid + "/exe", b, b.Length);
                return n > 0 ? Encoding.UTF8.GetString(b, 0, n) : null;
            }
            catch { return null; }
#else
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
            }
            finally { CloseHandle(h); }
#endif
        }

        static HashSet<string> gameBar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static DateTime gameBarRead = DateTime.MinValue;

        /// <summary>Games Windows' Game Bar recognized on this PC (HKCU\System\GameConfigStore\Children), read once a minute.</summary>
        static HashSet<string> GameBarGames()
        {
            if ((DateTime.UtcNow - gameBarRead).TotalSeconds < 60) return gameBar;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
                    if (k != null)
                        foreach (var sub in k.GetSubKeyNames())
                            using (var c = k.OpenSubKey(sub))
                            {
                                var exe = c == null ? null : c.GetValue("MatchedExeFullPath") as string;
                                var n = GameProfiles.NormalizeExe(exe);
                                if (n != null && !Launchers.Contains(n)) set.Add(n);
                            }
            }
            catch { }
            gameBar = set; gameBarRead = DateTime.UtcNow;
            return set;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("libc", SetLastError = true)] static extern int readlink(string path, byte[] buf, int size);
    }
}
