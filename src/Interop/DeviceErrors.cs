using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace StreamLite.Interop
{
    /// <summary>
    /// What Windows audio errors mean in plain words, with what to do about them - and whether Windows' privacy
    /// settings keep desktop apps away from the microphone (then every app hears silence, StreamLite included).
    /// </summary>
    internal static class DeviceErrors
    {
        public const int E_ACCESSDENIED = unchecked((int)0x80070005);
        public const int E_NOTFOUND = unchecked((int)0x80070490);
        public const int DEVICE_INVALIDATED = unchecked((int)0x88890004);
        public const int UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
        public const int DEVICE_IN_USE = unchecked((int)0x8889000A);
        public const int ENDPOINT_CREATE_FAILED = unchecked((int)0x8889000F);
        public const int SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
        public const int CPUUSAGE_EXCEEDED = unchecked((int)0x88890017);
        public const int RESOURCES_INVALIDATED = unchecked((int)0x88890026);

        public static string Describe(int hr, bool mic)
        {
            string what = mic ? "microphone" : "device";
            switch (hr)
            {
                case E_ACCESSDENIED:
                    return mic ? "Windows blocks apps from the microphone - Settings > Privacy & security > Microphone: turn on \"Let desktop apps access your microphone\"."
                        : "Windows refused access to the device (0x80070005).";
                case DEVICE_IN_USE:
                    return "Another app has the " + what + " to itself (exclusive mode). Close it, or in Windows Sound settings > the " + what + " > Advanced, untick \"Allow applications to take exclusive control\".";
                case DEVICE_INVALIDATED:
                case RESOURCES_INVALIDATED:
                case E_NOTFOUND:
                    return "The " + what + " was unplugged or reset - waiting for it to come back.";
                case SERVICE_NOT_RUNNING:
                    return "The Windows audio service isn't running - restart the PC (or start \"Windows Audio\" in Services).";
                case ENDPOINT_CREATE_FAILED:
                    return "Windows couldn't open the " + what + " - its driver may be busy or broken. Unplug and replug it, or restart the PC.";
                case UNSUPPORTED_FORMAT:
                    return "The " + what + "'s driver refuses every audio format StreamLite asked for (0x88890008).";
                case CPUUSAGE_EXCEEDED:
                    return "Windows stopped the audio because the PC was overloaded - close some programs.";
                default:
                    return (mic ? "Microphone" : "Device") + " error 0x" + hr.ToString("X8");
            }
        }

        /// <summary>True when Windows' privacy settings (or a policy) keep desktop apps away from the microphone.</summary>
        public static bool MicBlocked()
        {
            const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            try
            {
                if (Deny(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone")) return true;   // "Microphone access" off for the whole PC
                if (Deny(Registry.CurrentUser, store)) return true;                         // off for this user
                if (Deny(Registry.CurrentUser, store + @"\NonPackaged")) return true;     // "Let desktop apps access your microphone" off
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy"))
                {
                    object v = k != null ? k.GetValue("LetAppsAccessMicrophone") : null;
                    if (v is int && (int)v == 2) return true;                               // forced off by an administrator
                }
            }
            catch { }
            return false;
        }

        static bool Deny(RegistryKey root, string path)
        {
            using (var k = root.OpenSubKey(path))
                return k != null && string.Equals(k.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }

        public static void OpenMicPrivacy()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); } catch { }
        }
    }
}
