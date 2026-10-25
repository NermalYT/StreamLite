// Presses and releases a key system-wide (SendInput), like a keyboard would. Usage: KeyPress <vk hex> [hold ms]
using System;
using System.Runtime.InteropServices;
using System.Threading;

class KeyPress
{
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    static void Send(ushort vk, bool up)
    {
        var i = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } };
        uint r = SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        if (r != 1) Console.WriteLine("SendInput failed " + Marshal.GetLastWin32Error());
    }

    static void Main(string[] args)
    {
        ushort vk = Convert.ToUInt16(args[0], 16);
        int hold = args.Length > 1 ? int.Parse(args[1]) : 60;
        Send(vk, false); Thread.Sleep(hold); Send(vk, true);
        Console.WriteLine("pressed 0x" + vk.ToString("X2"));
    }
}
