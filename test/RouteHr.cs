using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// Calls the per-app routing API directly and prints every HRESULT (the app ignores them).
class RouteHr
{
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string src, uint length, out IntPtr hstring);

    static void Main(string[] args)
    {
        var phones = AudioDevices.List(EDataFlow.Render).First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0);
        var p = Process.GetProcessesByName(args[0]).First();
        IntPtr cls; const string name = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(name, (uint)name.Length, out cls);
        Guid iid = typeof(IAudioPolicyConfigFactory21H2).GUID; IntPtr fp;
        int hr = RoGetActivationFactory(cls, ref iid, out fp);
        Console.WriteLine("RoGetActivationFactory 0x" + hr.ToString("X8"));
        var f = (IAudioPolicyConfigFactory21H2)Marshal.GetObjectForIUnknown(fp);
        string packed = @"\\?\SWD#MMDEVAPI#" + phones.Id + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        IntPtr hs; WindowsCreateString(packed, (uint)packed.Length, out hs);
        hr = f.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.Render, ERole.Multimedia, hs);
        Console.WriteLine("Set multimedia 0x" + hr.ToString("X8"));
        hr = f.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.Render, ERole.Console, hs);
        Console.WriteLine("Set console 0x" + hr.ToString("X8"));
        string cur; hr = f.GetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.Render, ERole.Multimedia, out cur);
        Console.WriteLine("Get 0x" + hr.ToString("X8") + " -> " + (cur ?? "(null)"));
    }
}
