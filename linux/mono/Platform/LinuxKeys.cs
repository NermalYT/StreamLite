using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamLite.Linux
{
    /// <summary>
    /// Global hotkeys on Linux: every key and mouse button, whichever app is in front, from X11's raw input events
    /// (XInput 2) - the same thing Windows' keyboard and mouse hooks hand StreamLite. Keys arrive as Windows' key codes, so
    /// hotkeys are the same on both systems (and exported hotkeys work on either). Needs an X11 session or XWayland.
    /// </summary>
    internal static class LinuxKeys
    {
        const string X = "libX11.so.6", Xi = "libXi.so.6";
        [DllImport(X)] static extern int XInitThreads();
        [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X)] static extern int XCloseDisplay(IntPtr d);
        [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr d);
        [DllImport(X)] static extern bool XQueryExtension(IntPtr d, string name, out int opcode, out int ev, out int err);
        [DllImport(X)] static extern int XPending(IntPtr d);
        [DllImport(X)] static extern int XNextEvent(IntPtr d, IntPtr ev);
        [DllImport(X)] static extern bool XGetEventData(IntPtr d, IntPtr cookie);
        [DllImport(X)] static extern void XFreeEventData(IntPtr d, IntPtr cookie);
        [DllImport(X)] static extern int XFlush(IntPtr d);
        [DllImport(X)] static extern ulong XkbKeycodeToKeysym(IntPtr d, byte keycode, int group, int level);
        [DllImport(Xi)] static extern int XIQueryVersion(IntPtr d, ref int major, ref int minor);
        [DllImport(Xi)] static extern int XISelectEvents(IntPtr d, IntPtr win, ref XIEventMask mask, int n);

        [StructLayout(LayoutKind.Sequential)] struct XIEventMask { public int deviceid; public int mask_len; public IntPtr mask; }

        const int GenericEvent = 35, XI_RawKeyPress = 13, XI_RawKeyRelease = 14, XI_RawButtonPress = 15, XI_RawButtonRelease = 16, XIAllMasterDevices = 1;

        /// <summary>Why hotkeys can't work here (no X11 / XInput 2), or null while they run.</summary>
        public static volatile string Problem;

        /// <summary>Feeds input(vk, mouse, down) until stop() is true. Returns when it can't run (Problem says why).</summary>
        public static void Run(Func<int, int, bool, bool> input, Func<bool> stop)
        {
            IntPtr d = IntPtr.Zero, ev = IntPtr.Zero, bits = IntPtr.Zero;
            try
            {
                XInitThreads();
                d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) { Problem = "no X11 display (hotkeys need an X11 session or XWayland)"; return; }
                int op, e1, e2;
                if (!XQueryExtension(d, "XInputExtension", out op, out e1, out e2)) { Problem = "XInput isn't available"; return; }
                int major = 2, minor = 0;
                if (XIQueryVersion(d, ref major, ref minor) != 0) { Problem = "XInput 2 isn't available"; return; }
                bits = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(bits, (1 << XI_RawKeyPress) | (1 << XI_RawKeyRelease) | (1 << XI_RawButtonPress) | (1 << XI_RawButtonRelease));
                var mask = new XIEventMask { deviceid = XIAllMasterDevices, mask_len = 4, mask = bits };
                XISelectEvents(d, XDefaultRootWindow(d), ref mask, 1);
                XFlush(d);
                Problem = null;
                ev = Marshal.AllocHGlobal(256);
                while (!stop())
                {
                    if (XPending(d) == 0) { Thread.Sleep(4); continue; }
                    XNextEvent(d, ev);
                    if (Marshal.ReadInt32(ev, 0) != GenericEvent || Marshal.ReadInt32(ev, 32) != op) continue;
                    if (!XGetEventData(d, ev)) continue;
                    try
                    {
                        int type = Marshal.ReadInt32(ev, 36);
                        IntPtr data = Marshal.ReadIntPtr(ev, 48);
                        int detail = Marshal.ReadInt32(data, 56);
                        if (type == XI_RawKeyPress || type == XI_RawKeyRelease)
                        {
                            int vk = Vk(XkbKeycodeToKeysym(d, (byte)detail, 0, 0));
                            if (vk != 0) input(vk, 0, type == XI_RawKeyPress);
                        }
                        else if (type == XI_RawButtonPress || type == XI_RawButtonRelease)
                        {
                            int b = detail == 2 ? 3 : detail == 8 ? 4 : detail == 9 ? 5 : 0;   // middle, back, forward
                            if (b != 0) input(0, b, type == XI_RawButtonPress);
                        }
                    }
                    finally { XFreeEventData(d, ev); }
                }
            }
            catch (Exception ex) { Problem = "hotkeys stopped: " + ex.Message; }
            finally
            {
                if (ev != IntPtr.Zero) Marshal.FreeHGlobal(ev);
                if (bits != IntPtr.Zero) Marshal.FreeHGlobal(bits);
                if (d != IntPtr.Zero) try { XCloseDisplay(d); } catch { }
            }
        }

        static readonly Dictionary<ulong, int> Special = new Dictionary<ulong, int>
        {
            { 0xFFE1, 0xA0 }, { 0xFFE2, 0xA1 }, { 0xFFE3, 0xA2 }, { 0xFFE4, 0xA3 }, { 0xFFE9, 0xA4 }, { 0xFFEA, 0xA5 }, { 0xFE03, 0xA5 },
            { 0xFFEB, 0x5B }, { 0xFFEC, 0x5C }, { 0xFFE7, 0x5B }, { 0xFFE8, 0x5C }, { 0xFF67, 0x5D },
            { 0xFF1B, 0x1B }, { 0x20, 0x20 }, { 0xFF0D, 0x0D }, { 0xFF8D, 0x0D }, { 0xFF09, 0x09 }, { 0xFF08, 0x08 },
            { 0xFF51, 0x25 }, { 0xFF52, 0x26 }, { 0xFF53, 0x27 }, { 0xFF54, 0x28 }, { 0xFF50, 0x24 }, { 0xFF57, 0x23 }, { 0xFF55, 0x21 }, { 0xFF56, 0x22 },
            { 0xFF63, 0x2D }, { 0xFFFF, 0x2E }, { 0xFF13, 0x13 }, { 0xFF61, 0x2C }, { 0xFF14, 0x91 }, { 0xFFE5, 0x14 }, { 0xFF7F, 0x90 },
            { 0xFFAA, 0x6A }, { 0xFFAB, 0x6B }, { 0xFFAD, 0x6D }, { 0xFFAE, 0x6E }, { 0xFFAF, 0x6F },
            { 0x2D, 0xBD }, { 0x3D, 0xBB }, { 0x5B, 0xDB }, { 0x5D, 0xDD }, { 0x3B, 0xBA }, { 0x27, 0xDE }, { 0x60, 0xC0 }, { 0x5C, 0xDC },
            { 0x2C, 0xBC }, { 0x2E, 0xBE }, { 0x2F, 0xBF },
            { 0x1008FF12, 0xAD }, { 0x1008FF11, 0xAE }, { 0x1008FF13, 0xAF }, { 0x1008FF14, 0xB3 }, { 0x1008FF17, 0xB0 }, { 0x1008FF16, 0xB1 }, { 0x1008FF15, 0xB2 },
        };

        /// <summary>An X keysym as Windows' virtual-key code (0 = not one StreamLite binds).</summary>
        internal static int Vk(ulong ks)
        {
            if (ks >= 0x61 && ks <= 0x7A) return (int)(ks - 0x20);           // a-z -> A-Z
            if (ks >= 0x41 && ks <= 0x5A) return (int)ks;
            if (ks >= 0x30 && ks <= 0x39) return (int)ks;                    // 0-9
            if (ks >= 0xFFBE && ks <= 0xFFD5) return (int)(0x70 + ks - 0xFFBE);   // F1-F24
            if (ks >= 0xFFB0 && ks <= 0xFFB9) return (int)(0x60 + ks - 0xFFB0);   // keypad 0-9
            int vk; return Special.TryGetValue(ks, out vk) ? vk : 0;
        }
    }
}
