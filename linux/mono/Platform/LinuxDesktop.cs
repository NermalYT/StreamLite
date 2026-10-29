using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamLite.Linux
{
    /// <summary>
    /// What the Linux desktop around StreamLite can do (asked from X11, also under XWayland), and Mono's own drawing
    /// (scrollbars, dropdown buttons, its file dialog) in StreamLite's dark colours.
    /// </summary>
    internal static class LinuxDesktop
    {
        const string X = "libX11.so.6";
        [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X)] static extern IntPtr XInternAtom(IntPtr d, string name, bool onlyIfExists);
        [DllImport(X)] static extern IntPtr XGetSelectionOwner(IntPtr d, IntPtr atom);
        [DllImport(X)] static extern int XDefaultScreen(IntPtr d);

        static IntPtr display;
        static readonly object gate = new object();

        static bool Owned(string selection)
        {
            lock (gate)
            {
                try
                {
                    if (display == IntPtr.Zero) display = XOpenDisplay(IntPtr.Zero);
                    if (display == IntPtr.Zero) return false;
                    var atom = XInternAtom(display, selection.Replace("{screen}", XDefaultScreen(display).ToString()), false);
                    return atom != IntPtr.Zero && XGetSelectionOwner(display, atom) != IntPtr.Zero;
                }
                catch { return false; }
            }
        }

        static bool? compositor; static int compositorAt;

        /// <summary>A compositor runs (see-through windows work): without one, a dimmed backdrop would be solid black.</summary>
        public static bool HasCompositor
        {
            get
            {
                if (compositor == null || Environment.TickCount - compositorAt > 10000) { compositor = Owned("_NET_WM_CM_S{screen}"); compositorAt = Environment.TickCount; }
                return compositor.Value;
            }
        }

        /// <summary>The desktop shows old-style (XEmbed) tray icons - Mono's tray icon appears there.</summary>
        public static bool HasXEmbedTray { get { return Owned("_NET_SYSTEM_TRAY_S{screen}"); } }

        /// <summary>
        /// Mono draws scrollbars, dropdown buttons, its file dialog and disabled controls in the system colours, which it
        /// makes light (Windows' classic grey). They become StreamLite's dark ones - the way Mono's own themes set them.
        /// </summary>
        public static void ApplyDarkColors()
        {
            try
            {
                // Mono's X11 driver loads its own colour scheme when it starts: start it first, so ours comes after.
                RuntimeHelpers.RunClassConstructor(typeof(Form).Assembly.GetType("System.Windows.Forms.XplatUI").TypeHandle);
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                // Mono 6 reads system colours from corefx's KnownColorTable; older Monos from KnownColors (Update).
                int[] table = null;
                var corefx = typeof(Color).Assembly.GetType("System.Drawing.KnownColorTable");
                if (corefx != null)
                {
                    var ensure = corefx.GetMethod("EnsureColorTable", any);
                    if (ensure != null) ensure.Invoke(null, null);
                    var values = corefx.GetField("s_colorTable", any);
                    if (values != null) table = values.GetValue(null) as int[];
                }
                var known = typeof(Color).Assembly.GetType("System.Drawing.KnownColors");
                var update = known != null ? known.GetMethod("Update", any) : null;
                if (table == null && update == null) return;
                Action<KnownColor, Color> set = (k, c) =>
                {
                    if (table != null && (int)k < table.Length) table[(int)k] = c.ToArgb();
                    if (update != null) try { update.Invoke(null, new object[] { (int)k, c.ToArgb() }); } catch { }
                };
                Color bg = Color.FromArgb(20, 22, 25), card = Color.FromArgb(30, 33, 37), field = Color.FromArgb(42, 46, 52), line = Color.FromArgb(58, 63, 70),
                    text = Color.FromArgb(230, 232, 235), muted = Color.FromArgb(150, 156, 163), accent = Color.FromArgb(87, 203, 120);
                set(KnownColor.Control, card); set(KnownColor.ButtonFace, card); set(KnownColor.MenuBar, card); set(KnownColor.Menu, card);
                set(KnownColor.ControlLight, field); set(KnownColor.ControlLightLight, line); set(KnownColor.ButtonHighlight, line);
                set(KnownColor.ControlDark, Color.FromArgb(14, 15, 17)); set(KnownColor.ControlDarkDark, Color.FromArgb(8, 9, 10)); set(KnownColor.ButtonShadow, Color.FromArgb(14, 15, 17));
                set(KnownColor.ScrollBar, bg); set(KnownColor.Window, field); set(KnownColor.AppWorkspace, bg); set(KnownColor.Desktop, bg);
                set(KnownColor.ControlText, text); set(KnownColor.WindowText, text); set(KnownColor.MenuText, text); set(KnownColor.InfoText, text);
                set(KnownColor.Info, card); set(KnownColor.GrayText, muted); set(KnownColor.InactiveCaption, card); set(KnownColor.InactiveCaptionText, muted);
                set(KnownColor.ActiveCaption, card); set(KnownColor.ActiveCaptionText, text); set(KnownColor.WindowFrame, line);
                set(KnownColor.ActiveBorder, line); set(KnownColor.InactiveBorder, line);
                set(KnownColor.Highlight, Color.FromArgb(38, 92, 56)); set(KnownColor.HighlightText, text); set(KnownColor.MenuHighlight, Color.FromArgb(38, 92, 56));
                set(KnownColor.HotTrack, accent);
            }
            catch { }
        }
    }
}
