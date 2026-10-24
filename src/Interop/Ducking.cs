using Microsoft.Win32;

namespace StreamLite.Interop
{
    /// <summary>
    /// Windows' "When Windows detects communications activity" (Sound control panel > Communications): by default it
    /// turns every other sound down 80% while a call app (Discord, Teams...) has a voice stream open - so games get
    /// quiet whenever a friend talks. 0 mute, 1 reduce 80% (default), 2 reduce 50%, 3 do nothing.
    /// </summary>
    internal static class Ducking
    {
        const string Key = @"Software\Microsoft\Multimedia\Audio";

        public static int Preference
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    {
                        object v = k != null ? k.GetValue("UserDuckingPreference") : null;
                        return v is int ? (int)v : 1;
                    }
                }
                catch { return 1; }
            }
        }

        /// <summary>true: Windows leaves other sounds alone during calls; false: back to Windows' default.</summary>
        public static bool Set(bool keepVolume)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (keepVolume) k.SetValue("UserDuckingPreference", 3, RegistryValueKind.DWord);
                    else k.DeleteValue("UserDuckingPreference", false);
                }
                return true;
            }
            catch { return false; }
        }

        public static string Describe(int p)
        {
            return p == 3 ? "Windows leaves your game alone during calls." : p == 0 ? "Windows mutes everything else during calls."
                : p == 2 ? "Windows turns everything else down 50% during calls." : "Windows turns everything else down 80% during calls (its default).";
        }
    }
}
