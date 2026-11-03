using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using V = StreamLite.Audio.Sphere3D.Vec;

namespace StreamLite.UI
{
    /// <summary>
    /// "Personalize 3D", inside StreamLite: the window grows to fill its screen, zooms into the middle of the live
    /// sound radar and flies you into it - the radar's cube becomes a sphere around you (Sphere3D): blue lines above,
    /// the green ear-level ring, orange lines below. Footsteps play on its edge from one of six directions around your
    /// head - in front, behind, left, right, above, below; the view snaps between those six and you click the one you
    /// heard (or say you can't tell, and it shows you).
    /// 1. Find your head: every measured head (two studio dummy heads, 18 real people), blind, each footstep going to
    ///    the heads still in the running.
    /// 2. Fine-tune it (4.3), your pick from a small menu, up to six tests: above / below, left / right, forward / backward,
    ///    all directions, and footstep tracking - the view turns freely and follows someone walking across the sphere.
    /// Saved per pair of headphones after each part. Leaving flies back out into the radar.
    /// Drawn on the graphics card on every frame of the monitor (SceneHost); test pictures use GDI+.
    /// </summary>
    internal sealed class Personal3DView : Control
    {
        enum Stage { Intro, Loading, Countdown, Listen, Feedback, Break, Menu, Track, Done }
        enum Anim { None, Enter, Exit }
        enum Round { Search, Test, Practice }

        // The radar it flies out of: a picture of the window, the screen behind it, where the radar's centre was, its
        // size and how it's turned.
        readonly Bitmap snap, desk;
        readonly Size snapSize;   // read while drawing: a frame can still be due after snap is disposed (Linux)
        readonly Rectangle snapScreen;
        readonly PointF radarScreen;
        readonly float radarR;
        readonly double radarYaw, radarPitch;
        readonly MainForm host;
        readonly Action onClosed;
        FormWindowState homeState;
        Rectangle homeBounds, normalBounds, fullBounds;
        SceneHost scene;

        readonly AppCore core;
        readonly string phones, phonesName;
        readonly StepRenderer renderer = new StepRenderer();
        readonly Random rnd = new Random();
        /// <summary>The search's best head so far (in use while the search runs).</summary>
        Sphere3D.Variant leader;
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();
        // The card's two buttons, drawn by the graphics card with everything else (real buttons on top of it would
        // make Windows recompose the window whenever they appear or go: a stutter each time).
        string primaryText, secondaryText;
        RectangleF primaryRect, secondaryRect;
        int hot;   // 1 = primary, 2 = secondary
        StereoTimeline audio;
        Thread conductor;
        volatile bool closing;
        Anim anim = Anim.None;
        /// <summary>Tests: a fixed moment of the animation (seconds) instead of the clock; NaN = the clock.</summary>
        internal double AnimAt = double.NaN;

        // The view: the direction it faces (one of six), the way it faced at ear level last, the mouse's push towards
        // the next one (degrees), and the camera's angles easing to the facing direction.
        Sphere3D.Dir facing = Sphere3D.Dir.Front, level = Sphere3D.Dir.Front;
        float pushX, pushY;
        float yaw, pitch;
        float sens = 0.12f;
        bool captured, paused;
        /// <summary>Tests: pictures only - the real mouse cursor is never hidden, locked or released.</summary>
        internal bool NoMouse;
        /// <summary>Tests: no sound (the flow runs without headphones).</summary>
        internal bool NoAudio;
        Point center;

        Stage stage = Stage.Intro, afterCountdown;
        DateTime stageEnd;
        string headline = "", detail = "";
        Round round;
        List<Sphere3D.Variant> heads;
        List<Sphere3D.Trial> trials; int trialIdx;
        Sphere3D.Tuning tuning;
        Sphere3D.Variant winner, runnerUp, final;
        /// <summary>Your head with what the tests have decided so far (saved after each).</summary>
        Sphere3D.Variant work;
        Sphere3D.Test test;
        readonly Sphere3D.Refine refine = new Sphere3D.Refine();
        readonly Dictionary<Sphere3D.Test, string> testResults = new Dictionary<Sphere3D.Test, string>();
        readonly List<string> testLog = new List<string>();
        readonly RectangleF[] menuRects = new RectangleF[Sphere3D.Tests.Length];
        int menuHot = -1;
        /// <summary>The tracking test running (its footsteps come from the audio thread).</summary>
        volatile Sphere3D.Tracker tracker;
        static readonly object TrackSound = new object();
        bool TrackingNow { get { return tracker != null && (stage == Stage.Track || stage == Stage.Countdown); } }
        int doneSteps, totalSteps;
        volatile object playing; volatile Sphere3D.Variant playVariant; long playStart;
        Sphere3D.Source? reveal; Sphere3D.Dir? lastAnswer;
        bool cantTell;
        object pausedWhat;
        volatile int loaded, toLoad;
        /// <summary>Bars on the result card: a head's name and its average error (0..1).</summary>
        List<Tuple<string, int, int>> chart;   // each head: name, footsteps placed right, footsteps
        string chartWinner;

        // In: the window grows, zooms into the radar, the radar's picture becomes the sphere, you fly in, the card.
        const double GrowS = 0.42, ZoomS = 0.75, CrossS = 0.35, FlyS = 1.35, RevealS = 0.35;
        // Out: fly out, the sphere becomes the radar's picture, zoom out, the window shrinks.
        const double OutFlyS = 0.95, OutCrossS = 0.3, OutZoomS = 0.5, OutShrinkS = 0.38;

        /// <summary>Opens it over StreamLite's window, from the radar (onClosed: refresh the page afterwards).</summary>
        public static void Open(AppCore core, Control radar, Action onClosed, bool startNow = false, bool practice = false)
        {
            var form = radar.FindForm() as MainForm;
            if (form == null || form.Controls.OfType<Personal3DView>().Any()) return;
            LastRunCompleted = false;
            // The window as it looks now (drawn by the window itself: a screen copy would also catch anything lying on
            // top of it), the screen it's on (what's around it while it grows), and the radar's centre on screen.
            var r = form.Bounds;
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            try { form.DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height)); }
            catch { using (var g = Graphics.FromImage(bmp)) g.Clear(Theme.Bg); }
            DrawCover(form, radar, bmp);
            var full = Screen.FromRectangle(r).Bounds;
            Bitmap desk = null;
            try { desk = new Bitmap(full.Width, full.Height); using (var g = Graphics.FromImage(desk)) g.CopyFromScreen(full.Location, Point.Empty, full.Size); }
            catch { if (desk != null) desk.Dispose(); desk = null; }
            var z = (IZoomSource)radar;   // the 3D page's radar, or the setup's sphere (4.0.7)
            var c = radar.PointToScreen(Point.Round(z.Centre));
            var v = new Personal3DView(core, bmp, desk, r, full, c, z.Radius, z.ViewYaw, z.ViewPitch, form, onClosed);
            v.startNow = startNow && v.phones != null;
            if (practice) v.PracticeMode();
            try { v.Begin(); }
            catch (Exception ex)
            {
                // The graphics card couldn't start it (or anything else on the way in): the window back as it was.
                try { HealthMonitor.LogLine("Personalize 3D couldn't open: " + ex.Message); } catch { }
                v.Abort();
                DarkMessage.Show(form, "Personalize 3D couldn't start on this PC: " + ex.Message, "Personalize 3D");
            }
        }

        /// <summary>Opened from the setup's page (its instructions were there): straight into the test once you're in.</summary>
        bool startNow;

        /// <summary>The setup's Personalize 3D page: what happens once you click Start (shown under its copy of the diagram).</summary>
        public static string SetupText(AppCore core)
        {
            string id = core.GamePhonesId ?? core.RealDefaultOutput();
            var d = id != null ? core.Outputs().FirstOrDefault(o => o.Id == id) : null;
            int n = HrtfSet.Heads().Count, steps = Sphere3D.TotalSteps(n);
            return "Put on " + (d != null ? d.Name : "your headphones") + " and pause music and games. Click Start and you fly into this diagram: you're in the middle of a sphere.\n\n" +
                "Someone walks in one spot in front of you, behind, left, right, above or below" + (GameFootsteps.FindCs2() != null ? " - Counter-Strike 2's own footsteps" : "") + ". " +
                "Every footstep starts with you facing forward. Snap the view to where you hear them (move the mouse, or arrow keys / WASD) and click. Can't tell? Right-click: it shows you. Esc pauses.\n\n" +
                steps + " footsteps, about " + Math.Max(2, (int)Math.Round(steps * 2.6 / 60)) + " minutes: it finds which of " + n + " measured heads fits your ears. Then you can fine-tune it with up to six more tests of your choice, footstep tracking among them.";
        }

        /// <summary>
        /// The radar is on a page laid over the whole window (the setup's): that page on top of the window's picture -
        /// DrawToBitmap paints overlapping children in the wrong order, so it would show the settings under it instead.
        /// </summary>
        static void DrawCover(Form form, Control radar, Bitmap bmp)
        {
            Control top = radar;
            while (top.Parent != null && top.Parent != form) top = top.Parent;
            if (top.Parent != form || top == radar || !(top is SetupCover)) return;
            try
            {
                using (var page = new Bitmap(Math.Max(1, top.Width), Math.Max(1, top.Height)))
                using (var g = Graphics.FromImage(bmp))
                {
                    top.DrawToBitmap(page, new Rectangle(0, 0, page.Width, page.Height));
                    var at = form.PointToScreen(top.Location);
                    g.DrawImageUnscaled(page, at.X - form.Bounds.X, at.Y - form.Bounds.Y);
                }
            }
            catch { }
        }

        internal Personal3DView(AppCore core, Bitmap snap, Bitmap desk, Rectangle snapScreen, Rectangle full, PointF radarScreen, float radarR, double radarYaw, double radarPitch, MainForm host, Action onClosed)
        {
            this.core = core; this.snap = snap; snapSize = snap.Size; this.desk = desk; this.snapScreen = snapScreen; this.radarScreen = radarScreen; this.radarR = radarR;
            this.radarYaw = radarYaw; this.radarPitch = radarPitch; this.host = host; this.onClosed = onClosed;
            homeBounds = snapScreen; fullBounds = full;
            phones = core.GamePhonesId ?? core.RealDefaultOutput();
            var d = phones != null ? core.Outputs().FirstOrDefault(o => o.Id == phones) : null;
            phonesName = d != null ? d.Name : "your headphones";
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            ShowButtons(null, null);
            int n = HrtfSet.Heads().Count;
            headline = "Personalize 3D";
            int steps = Sphere3D.TotalSteps(n);
            detail = "Put on " + phonesName + " and pause music and games. You're in the middle of a sphere.\n\n" +
                "Someone walks in one spot on its edge, in one of six directions around your head: in front, behind, left, right, above or below" + (GameFootsteps.FindCs2() != null ? " (Counter-Strike 2's own footsteps)" : "") + ".\n" +
                "Snap the view to where you hear them - move the mouse (or use the arrow keys / WASD) - and click.\n" +
                "Every footstep starts with you facing forward. The footsteps stay where they are around your head: turning the view only points.\n" +
                "Can't tell? Right-click (or Space): it shows you where they were and plays them there.\n\n" +
                "Part 1, find your head: " + n + " measured heads" + (n > 2 ? " (2 studio dummy heads and " + (n - 2) + " real people)" : "") + ", tried blind - each footstep goes to the heads still in the running.\n" +
                "Part 2, fine-tune it if you like: pick from up to six more tests - above / below, left / right, forward / backward, all directions, and footstep tracking.\n\n" +
                "Part 1 is " + steps + " footsteps, about " + Math.Max(2, (int)Math.Round(steps * 2.6 / 60)) + " minutes. Saved for these headphones after each test.   Esc: pause.   + / - : mouse speed.";
            if (phones == null) detail = "No headphones found.";
        }

        // ------------------------------------------------------------------ in and out

        void Begin()
        {
            homeState = host.WindowState;
            normalBounds = host.WindowState == FormWindowState.Normal ? host.Bounds : host.RestoreBounds;
            // The screen as it is now, in front of everything for the moment the window jumps to the whole screen and the
            // graphics card draws its first frame (otherwise that moment shows as a dark flash).
            if (desk != null)
            {
                var pic = new Bitmap(desk);
                using (var g = Graphics.FromImage(pic)) g.DrawImage(snap, homeBounds.X - fullBounds.X, homeBounds.Y - fullBounds.Y, snap.Width, snap.Height);
                cover = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = fullBounds, ShowInTaskbar = false, TopMost = true, BackColor = Theme.Bg, BackgroundImage = pic, BackgroundImageLayout = ImageLayout.None };
                cover.Show(); cover.Refresh();
                // If the graphics card never draws a frame, the picture mustn't stay in front of everything.
                coverTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                coverTimer.Tick += (s, e) => { coverFrames = 99; DropCover(); };
                coverTimer.Start();
            }
            scene = new SceneHost(snap, desk) { Dock = DockStyle.Fill };
            scene.Draw = c => Render(c, ClientSize);
            scene.Frame += OnFrame;
            scene.Moved += Look;
            scene.Down += MouseClicked;
            scene.Key += Key;
            Controls.Add(scene);
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            host.Controls.Add(this);
            BringToFront();
            host.SetChrome(false);
            // Straight to the whole screen: the growing is drawn (the screen as it was behind it), not the window resized
            // frame by frame.
            if (host.WindowState != FormWindowState.Normal) host.WindowState = FormWindowState.Normal;
            host.Bounds = fullBounds;
            Bounds = new Rectangle(Point.Empty, host.ClientSize);
            host.Deactivate += HostDeactivate;
            anim = Anim.Enter; animClock.Restart();
            scene.FocusScene();
        }

        Form cover;
        System.Windows.Forms.Timer coverTimer;
        int coverFrames;

        /// <summary>Opening failed half way: everything Begin did, undone.</summary>
        void Abort()
        {
            try { coverFrames = 99; DropCover(); } catch { }
            try { host.Deactivate -= HostDeactivate; } catch { }
            try { if (scene != null) scene.Frame -= OnFrame; } catch { }
            try
            {
                if (host.Controls.Contains(this))
                {
                    host.Bounds = homeBounds;
                    if (homeState == FormWindowState.Maximized) { host.WindowState = FormWindowState.Maximized; SetRestore(host, normalBounds); }
                    host.Controls.Remove(this);
                }
                host.SetChrome(true);
            }
            catch { }
            try { Release(); snap.Dispose(); if (desk != null) desk.Dispose(); Dispose(); } catch { }
            if (onClosed != null) try { onClosed(); } catch { }
        }

        /// <summary>The graphics card has drawn a few frames: the picture in front goes, and the animation starts from 0.</summary>
        void DropCover()
        {
            if (cover == null) return;
            if (++coverFrames < 3) { animClock.Restart(); return; }
            if (coverTimer != null) { coverTimer.Dispose(); coverTimer = null; }
            var c = cover; cover = null;
            var pic = c.BackgroundImage;
            c.Close(); c.Dispose(); if (pic != null) pic.Dispose();
            if (host != null) host.Activate();
            if (scene != null) scene.FocusScene();
        }

        void HostDeactivate(object s, EventArgs e) { if (captured && !NoMouse && cover == null) Pause(); }

        /// <summary>Back into the radar and the window as it was.</summary>
        void FlyOut()
        {
            if (anim == Anim.Exit) return;
            closing = true; playing = null;
            Release();
            if (audio != null) { audio.Dispose(); audio = null; }
            ShowButtons(null, null);
            anim = Anim.Exit; animClock.Restart();
            HrtfSet.Forget(core.S.Game.Hrtf);
        }

        void Finish()
        {
            try { HealthMonitor.LogLine("Personalize 3D frames - animations: " + Stats(animGaps) + "; inside: " + Stats(playGaps) + "; slowest: " + string.Join(", ", slowest.OrderByDescending(k => k.Key).Take(5).Select(k => k.Key.ToString("0") + " ms (" + k.Value + ")"))); } catch { }
            host.Deactivate -= HostDeactivate;
            if (scene != null) { scene.Frame -= OnFrame; }
            host.Bounds = homeBounds;
            if (homeState == FormWindowState.Maximized)
            {
                host.WindowState = FormWindowState.Maximized;
                SetRestore(host, normalBounds);
            }
            host.SetChrome(true);
            host.Controls.Remove(this);
            snap.Dispose(); if (desk != null) desk.Dispose();
            if (onClosed != null) onClosed();
            Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public Point min, max; public Rectangle normal; }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);

        /// <summary>Un-maximizing later goes back to the size the window had before (not the full screen).</summary>
        static void SetRestore(Form f, Rectangle r)
        {
            try
            {
                var p = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (!GetWindowPlacement(f.Handle, ref p)) return;
                var wa = Screen.FromRectangle(r).WorkingArea; var sb = Screen.FromRectangle(r).Bounds;
                // The placement counts in working-area coordinates.
                p.normal = new Rectangle(r.Left - (wa.Left - sb.Left), r.Top - (wa.Top - sb.Top), r.Right - (wa.Left - sb.Left), r.Bottom - (wa.Top - sb.Top));
                SetWindowPlacement(f.Handle, ref p);
            }
            catch { }
        }

        static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        static double Ease2(double t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2; }
        static double EaseOut(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }
        static double Smooth(double a, double b, double t) { t = Math.Max(0, Math.Min(1, (t - a) / (b - a))); return t * t * (3 - 2 * t); }
        static RectangleF Lerp(RectangleF a, RectangleF b, double t)
        {
            return RectangleF.FromLTRB((float)(a.Left + (b.Left - a.Left) * t), (float)(a.Top + (b.Top - a.Top) * t), (float)(a.Right + (b.Right - a.Right) * t), (float)(a.Bottom + (b.Bottom - a.Bottom) * t));
        }

        double AnimTime { get { return double.IsNaN(AnimAt) ? animClock.Elapsed.TotalSeconds : AnimAt; } }

        /// <summary>
        /// Where the animation is: how far the window has grown (0..1), how far the picture of StreamLite has zoomed into
        /// the radar, how far that picture has turned into the building (cross), how far you've flown in (0 = the
        /// building as the radar shows it, 1 = standing in it) and how far the first card has faded in.
        /// </summary>
        void Progress(out double grow, out double zoom, out double cross, out double fly, out double reveal)
        {
            double t = AnimTime;
            if (anim == Anim.Enter)
            {
                grow = EaseOut(t / GrowS);
                zoom = Ease((t - GrowS) / ZoomS);
                cross = Smooth(GrowS + ZoomS - CrossS, GrowS + ZoomS, t);
                fly = Ease2((t - GrowS - ZoomS + CrossS * 0.6) / FlyS);
                reveal = Smooth(0, 1, (t - GrowS - ZoomS + CrossS * 0.6 - FlyS) / RevealS);
            }
            else if (anim == Anim.Exit)
            {
                fly = 1 - Ease(t / OutFlyS);
                cross = 1 - Smooth(OutFlyS - 0.05, OutFlyS + OutCrossS, t);
                zoom = 1 - Ease((t - OutFlyS - OutCrossS * 0.5) / OutZoomS);
                grow = 1 - EaseOut((t - OutFlyS - OutCrossS * 0.5 - OutZoomS) / OutShrinkS);
                reveal = 0;
            }
            else { grow = 1; zoom = 1; cross = 1; fly = 1; reveal = 1; }
        }

        double ExitLength { get { return OutFlyS + OutCrossS * 0.5 + OutZoomS + OutShrinkS; } }

        // Time between the monitor frames we got (ms): while animating, and while playing (health.log at the end), and
        // the slowest few with when they happened.
        readonly List<double> animGaps = new List<double>(), playGaps = new List<double>();
        readonly List<KeyValuePair<double, string>> slowest = new List<KeyValuePair<double, string>>();
        double lastFrameMs = -1, lastGap;

        static string Stats(List<double> g)
        {
            if (g.Count < 10) return "-";
            var s = g.OrderBy(x => x).ToList();
            double med = s[s.Count / 2], p99 = s[(int)(s.Count * 0.99)];
            return g.Count + " frames, every " + med.ToString("0.00") + " ms (" + (1000 / med).ToString("0") + " fps), slowest 1% " + p99.ToString("0.0") + " ms, slowest " + s[s.Count - 1].ToString("0.0") + " ms";
        }

        /// <summary>Every frame of the monitor: the animation, the stage's clock, and a redraw when something moved.</summary>
        void OnFrame()
        {
            if (IsDisposed) return;
            double nowMs = scene != null ? scene.Clock.Elapsed.TotalMilliseconds : 0;
            if (lastFrameMs >= 0)
            {
                double gap = nowMs - lastFrameMs;
                (anim != Anim.None ? animGaps : playGaps).Add(gap);
                if (gap > 25 && slowest.Count < 40) slowest.Add(new KeyValuePair<double, string>(gap, (anim != Anim.None ? anim + " at " + AnimTime.ToString("0.00") + " s" : stage.ToString()) + ", full GCs " + GC.CollectionCount(2) + ", GCs " + GC.CollectionCount(0) + ", " + (nowMs - buttonsAt).ToString("0") + " ms after buttons changed"));
            }
            lastGap = lastFrameMs >= 0 ? nowMs - lastFrameMs : 0;
            lastFrameMs = nowMs;
            DropCover();
            if (anim != Anim.None)
            {
                double t = AnimTime;
                if (anim == Anim.Enter && t >= GrowS + ZoomS - CrossS * 0.6 + FlyS + RevealS)
                {
                    anim = Anim.None;
                    if (startNow) Start(); else if (phones != null) ShowButtons(practiceMode ? "Start practice" : "Start", "Back to StreamLite"); else ShowButtons(null, "Back to StreamLite");
                }
                else if (anim == Anim.Exit && t >= ExitLength) { Finish(); return; }
                Redraw();
                return;
            }
            if (lastGap > 0) EaseView(lastGap);
            Tick();
            // Looking around: every frame of the monitor (WPF would otherwise idle at ~60 Hz between changes).
            if (captured) Redraw();
        }

        void Redraw() { if (scene != null) scene.Dirty = true; else Invalidate(); }

        Rectangle cardRect;

        /// <summary>Where the buttons go on the card (side by side, or one in the middle).</summary>
        void PlaceButtons(Rectangle card)
        {
            float w = Theme.Px(210), h = Theme.Px(34), y = card.Bottom - Theme.Px(62), mid = card.Left + card.Width / 2f, gap = Theme.Px(6);
            bool both = primaryText != null && secondaryText != null;
            primaryRect = primaryText == null ? RectangleF.Empty : new RectangleF(both ? mid - w - gap : mid - w / 2, y, w, h);
            secondaryRect = secondaryText == null ? RectangleF.Empty : new RectangleF(both ? mid + gap : mid - w / 2, y, w, h);
        }

        double buttonsAt = -1e9;
        void ShowButtons(string a, string b)
        {
            if (scene != null) buttonsAt = scene.Clock.Elapsed.TotalMilliseconds;
            primaryText = a; secondaryText = b;
            if (a == null) primaryRect = RectangleF.Empty;
            if (b == null) secondaryRect = RectangleF.Empty;
            hot = 0;
            Redraw();
        }

        bool ButtonsShown { get { return CardStage && anim == Anim.None && (primaryText != null || secondaryText != null); } }

        int ButtonAt(Point p)
        {
            if (!ButtonsShown) return 0;
            if (primaryText != null && primaryRect.Contains(p)) return 1;
            if (secondaryText != null && secondaryRect.Contains(p)) return 2;
            return 0;
        }

        void DrawButtons(ICanvas g, Func<Color, Color> A)
        {
            if (primaryText != null) DrawButton(g, A, primaryRect, primaryText, true, hot == 1);
            if (secondaryText != null) DrawButton(g, A, secondaryRect, secondaryText, false, hot == 2);
        }

        static void DrawButton(ICanvas g, Func<Color, Color> A, RectangleF r, string text, bool main, bool over)
        {
            var fill = main ? (over ? Color.FromArgb(120, 225, 148) : Theme.Accent) : (over ? Theme.FieldHover : Theme.Field);
            g.Rounded(A(fill), Color.Empty, 0, r, Theme.Px(6));
            var f = main ? Theme.Bold : Theme.Body;
            var sz = g.Measure(text, f);
            g.Text(text, f, A(main ? Color.FromArgb(10, 25, 14) : Theme.Text), new PointF(r.Left + (r.Width - sz.Width) / 2, r.Top + (r.Height - sz.Height) / 2));
        }

        // ------------------------------------------------------------------ flow

        void Primary()
        {
            if (paused) { Resume(); return; }
            switch (stage)
            {
                case Stage.Intro: if (practiceMode) StartPractice(); else Start(); break;
                case Stage.Break: Next(); break;
                case Stage.Menu: Done(); break;
                case Stage.Done: FlyOut(); break;
            }
        }

        void Secondary()
        {
            if (stage == Stage.Done && !paused) { StartPractice(); return; }
            // Paused in a test you picked: that test is dropped (nothing changed), the others are still there.
            if (paused && round == Round.Test) { paused = false; pausedWhat = null; ShowMenu(null); return; }
            FlyOut();
        }

        void EnsureAudio()
        {
            if (audio != null || NoAudio) return;
            audio = new StereoTimeline(phones);
            conductor = new Thread(Conduct) { IsBackground = true, Name = "Personalize 3D steps", Priority = ThreadPriority.AboveNormal };
            conductor.Start();
        }

        Sphere3D.Variant Current { get { var g = core.S.Game; return new Sphere3D.Variant { Hrtf = g.Hrtf, Cue = g.CueSharpen, Contrast = g.FloorContrast, HeadSize = g.HeadSize }; } }

        void Start()
        {
            EnsureAudio();
            leader = null;
            var cur = Current;
            heads = HrtfSet.Heads().Select(h => new Sphere3D.Variant { Hrtf = h, Cue = cur.Cue, Contrast = cur.Contrast }).ToList();
            totalSteps = Sphere3D.TotalSteps(heads.Count);
            doneSteps = 0;
            // Every head ready before the first footstep (the full measurements take a moment each).
            Load(heads.Select(h => h.Hrtf).ToList(), 1.0);
            BeginRound(Round.Search);
        }

        /// <summary>Loads heads (at a head size) in the background; the countdown waits for them.</summary>
        void Load(List<string> kinds, params double[] sizes)
        {
            toLoad = kinds.Count * sizes.Length; loaded = 0;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var k in kinds)
                    foreach (var s in sizes)
                    {
                        if (closing) return;
                        try { float[] l, r; HrtfSet.Get(k, null, s).BuildPrecise(0, 0, 0, out l, out r); } catch { }
                        Interlocked.Increment(ref loaded);
                    }
            });
        }

        string RoundName(Round r) { return r == Round.Search ? "Find your head" : r == Round.Test ? Sphere3D.TestName(test) : "Practice"; }

        void BeginRound(Round r)
        {
            round = r;
            // Find your head: every head once (front/back), then each footstep where it tells most (NextTrial adds them).
            if (r == Round.Search) trials = Sphere3D.FirstRound(heads, rnd);
            trialIdx = 0;
            Facing(Sphere3D.Dir.Front);
            Countdown(Stage.Listen, 3);
        }

        void Countdown(Stage next, int seconds)
        {
            ShowButtons(null, null);
            Capture();
            headline = RoundName(round); detail = "";
            stage = Stage.Countdown; afterCountdown = next; stageEnd = DateTime.Now.AddSeconds(seconds);
            if (scene != null) scene.FocusScene();
            Redraw();
        }

        void BeginTrial()
        {
            var t = trials[trialIdx];
            reveal = null; lastAnswer = null; cantTell = false;
            // Every footstep starts with you facing forward (4.0.7): where you looked last never carries over.
            Facing(Sphere3D.Dir.Front); level = Sphere3D.Dir.Front; yaw = 0; pitch = 0;
            stage = Stage.Listen;
            headline = RoundName(round) + (round == Round.Practice ? (practiceCount > 0 ? "  ·  " + PracticeScore() : "") : round == Round.Search
                ? "  ·  " + (trialIdx + 1) + " of " + Math.Max(Sphere3D.SearchSteps, heads.Count) + (leader != null ? "  ·  best so far: " + leader.HeadName : "")
                : "  ·  " + (trialIdx + 1) + " of " + trials.Count);
            detail = "Snap the view to the footsteps (mouse or arrow keys) and click.     Can't tell?  Right-click or Space.";
            Play(t.S, t.V);
            Redraw();
        }

        void Answer()
        {
            if (stage != Stage.Listen) return;
            var t = trials[trialIdx];
            Stop();
            if (round == Round.Practice) { t.Sc = Sphere3D.Judge(facing, t.S); Scored(t.Sc.Right); } else Sphere3D.Record(t, facing);
            reveal = t.S; lastAnswer = facing; cantTell = false;
            // Missed: the view turns to where they really were.
            if (!t.Sc.Right) Facing(t.S.D);
            stage = Stage.Feedback; stageEnd = DateTime.Now.AddSeconds(t.Sc.Right ? 0.8 : round == Round.Practice ? 1.8 : 1.4);
            detail = t.Sc.Text;
            Redraw();
        }

        /// <summary>"Can't tell": scored as a miss for this setting, then shown where it was and played there.</summary>
        void CantTell()
        {
            if (stage != Stage.Listen) return;
            var t = trials[trialIdx];
            if (round == Round.Practice) { t.Sc = new Sphere3D.Score { CantTell = true, Text = "It was " + Sphere3D.Name(t.S.D) + " - listen." }; Scored(false); }
            else Sphere3D.RecordCantTell(t);
            reveal = t.S; lastAnswer = null; cantTell = true;
            Facing(t.S.D);
            stage = Stage.Feedback; stageEnd = DateTime.Now.AddSeconds(3.2);
            detail = t.Sc.Text;
            // The same footsteps again, now that you can see where they are (keeps playing through the reveal).
            Play(t.S, t.V);
            Redraw();
        }

        void NextTrial()
        {
            Stop();
            if (round != Round.Practice) doneSteps++;
            trialIdx++;
            if (round == Round.Practice) { trials.Add(PracticeTrial()); BeginTrial(); return; }
            if (round == Round.Search && trialIdx >= heads.Count)
            {
                // Your 3D sound follows the search: the best head so far is the one in use (and saved), from the
                // moment every head has been heard once.
                var lead = Sphere3D.Ranked(heads.Where(h => h.N > 0).ToList())[0];
                if (leader != lead) { leader = lead; Apply(lead.With(cue: core.S.Game.CueSharpen, contrast: core.S.Game.FloorContrast, headSize: core.S.Game.HeadSize)); }
            }
            if (trialIdx < trials.Count) { BeginTrial(); return; }
            if (round == Round.Search && trials.Count < Math.Max(Sphere3D.SearchSteps, heads.Count)) { trials.Add(Sphere3D.NextTrial(heads, rnd)); BeginTrial(); return; }
            if (round == Round.Search) SearchDone(); else TestDone();
        }

        /// <summary>The card after part 1: your head, and what's next.</summary>
        void SearchDone()
        {
            Release();
            stage = Stage.Break;
            var order = Sphere3D.Ranked(heads.Where(h => h.N > 0).ToList());
            winner = order[0]; runnerUp = order.Count > 1 ? order[1] : order[0];
            work = winner;
            Apply(winner);
            core.SaveHeadScores(phones, order);   // the 3D page lists the heads best first, with these (4.1.2)
            headline = "Your head: " + winner.HeadName;
            detail = string.Join("\n", new[]
            {
                "Out of " + heads.Count + " heads, " + winner.HeadName + " let you place footsteps best (" + RightOf(winner) + Misses(winner) + "; " + runnerUp.HeadName + ": " + RightOf(runnerUp) + ").",
                "Saved for " + phonesName + ".",
                "",
                "Continue to choose what to fine-tune: up to six more tests, each one your pick.",
            });
            Chart(heads, winner.HeadName);
            // The head is picked and saved: 3D audio counts as personalized from here (4.1.1: "Stop here" used to leave
            // the setup not done - no camera step, and the whole setup again next start).
            LastRunCompleted = true;
            ShowButtons("Continue", "Stop here");
            Redraw();
        }

        /// <summary>The end: every setting decided from its own footsteps, saved.</summary>
        /// <summary>The last Personalize 3D went all the way through (the required setup counts it as done).</summary>
        public static bool LastRunCompleted;

        void Done()
        {
            final = work;
            Apply(final);
            LastRunCompleted = true;
            Release();
            stage = Stage.Done; headline = "All set: " + final.HeadName;
            detail = "Your 3D setup for " + phonesName + ":\n" +
                "   " + final.HeadName + (final.Hrtf.StartsWith(HrtfSet.HumanPrefix) ? " (a real person's measured head)" : "") + "\n" +
                "   head size " + final.HeadSize + "%,  cue sharpening " + final.Cue + "%,  floor contrast " + final.Contrast + "%\n\n" +
                (testLog.Count > 0 ? string.Join("\n", testLog) + "\n\n" : "") +
                "It applies to games' surround sound and everything StreamLite places in 3D. Practice: footsteps from all six directions, shown right after you answer.";
            // The heads were on the card after part 1; with tests done their results take the room.
            if (testLog.Count == 0) Chart(heads, final.HeadName); else chart = null;
            ShowButtons("Back to StreamLite", "Practice");
            Redraw();
        }

        // ------------------------------------------------------------------ the tests you pick (4.3)

        /// <summary>The small menu after part 1, and again after each test, until you finish or all six are done.</summary>
        void ShowMenu(string last)
        {
            Stop(); Release();
            tracker = null; chart = null; reveal = null; lastAnswer = null; cantTell = false; menuHot = -1;
            if (testResults.Count >= Sphere3D.Tests.Length) { Done(); return; }
            stage = Stage.Menu;
            Facing(Sphere3D.Dir.Front); level = Sphere3D.Dir.Front;
            headline = "What do you want to fine-tune?";
            detail = "Each test tries your settings without telling you which is playing, and keeps what you did best. Do as many as you like.";
            if (last != null) detail += "\n\n" + last;
            ShowButtons("Finish", null);
            Redraw();
        }

        void StartTest(Sphere3D.Test t)
        {
            if (testResults.ContainsKey(t)) return;
            EnsureAudio();
            test = t; round = Round.Test; chart = null;
            tuning = Sphere3D.TuningFor(t, work, refine);
            // Every head size it tries, ready before the first footstep.
            var sizes = (tuning.Size ?? new List<Sphere3D.Variant> { work }).Select(v => v.HeadSize / 100.0).ToArray();
            Load(new List<string> { work.Hrtf }, sizes);
            doneSteps = 0; reveal = null; lastAnswer = null; cantTell = false;
            Facing(Sphere3D.Dir.Front); level = Sphere3D.Dir.Front;
            if (Sphere3D.Tracking(t))
            {
                trials = null; tracker = new Sphere3D.Tracker(t, tuning, rnd);
                totalSteps = tracker.Total;
                yaw = 0; pitch = 0;
                Countdown(Stage.Track, 3);
            }
            else
            {
                tracker = null;
                trials = Sphere3D.TestRound(t, tuning, rnd); trialIdx = 0;
                totalSteps = trials.Count;
                Countdown(Stage.Listen, 3);
            }
        }

        void BeginTrack()
        {
            stage = Stage.Track; yaw = 0; pitch = 0;
            detail = "Keep the crosshair on the footsteps as they move.     Lost them?  Right-click or Space shows where they are.";
            Play(TrackSound, work);
            Redraw();
        }

        /// <summary>The next tracking footstep (audio thread): scores the view against the last one, and moves on.</summary>
        internal Sphere3D.Variant TrackNext(Sphere3D.Tracker tr, out V pos)
        {
            var v = tr.Next(Sphere3D.Forward(yaw, pitch), out pos);
            doneSteps = tr.Done;
            return v;
        }

        /// <summary>A test is through: what it decided is saved and in use, and it's ticked off on the menu.</summary>
        void TestDone()
        {
            var tr = tracker;
            Func<Sphere3D.Variant, string> score = tr != null
                ? (Func<Sphere3D.Variant, string>)(v => (v.Mean * 90).ToString("0") + "° off")
                : RightOf;
            var before = work;
            work = Sphere3D.DecideTune(work, tuning);
            Sphere3D.Tuned(refine, tuning);
            Apply(work);
            var parts = new List<string>(); var changed = new List<string>();
            Action<string, List<Sphere3D.Variant>, Func<Sphere3D.Variant, int>> setting = (name, vs, get) =>
            {
                if (vs == null) return;
                parts.Add(name + " " + get(work) + "%  (" + string.Join(", ", vs.Select(v => get(v) + "%: " + score(v))) + ")");
                if (get(work) != get(before)) changed.Add(char.ToUpper(name[0]) + name.Substring(1) + " " + get(work) + "%");
            };
            setting("head size", tuning.Size, v => v.HeadSize);
            setting("cue sharpening", tuning.Cue, v => v.Cue);
            setting("floor contrast", tuning.Contrast, v => v.Contrast);
            string off = tr != null ? "you stayed " + tr.MeanDeg.ToString("0") + "° off on average; " : "";
            testLog.Add(Sphere3D.TestName(test) + ": " + off + string.Join("; ", parts));
            testResults[test] = changed.Count > 0 ? string.Join(", ", changed) : "Settings kept";
            ShowMenu(testLog[testLog.Count - 1]);
        }

        int MenuAt(Point p)
        {
            if (stage != Stage.Menu || paused || anim != Anim.None) return -1;
            for (int i = 0; i < menuRects.Length; i++)
                if (menuRects[i].Contains(p) && !testResults.ContainsKey(Sphere3D.Tests[i])) return i;
            return -1;
        }

        int MenuHeight { get { return Theme.Px(50) * Sphere3D.Tests.Length + Theme.Px(10); } }

        void MenuRows(ICanvas g, RectangleF box, Func<Color, Color> A)
        {
            float row = Theme.Px(50);
            for (int i = 0; i < Sphere3D.Tests.Length; i++)
            {
                var t = Sphere3D.Tests[i];
                string result; bool done = testResults.TryGetValue(t, out result);
                var r = new RectangleF(box.Left, box.Top + i * row, box.Width, row - Theme.Px(5));
                menuRects[i] = r;
                g.Rounded(A(menuHot == i && !done ? Theme.FieldHover : Theme.Field), Color.Empty, 0, r, Theme.Px(8));
                g.Text((i + 1).ToString(), Theme.Bold, A(done ? Theme.Muted : Theme.Accent), new PointF(r.Left + Theme.Px(14), r.Top + Theme.Px(12)));
                g.Text(Sphere3D.TestName(t), Theme.Bold, A(done ? Theme.Muted : Theme.Text), new PointF(r.Left + Theme.Px(40), r.Top + Theme.Px(4)));
                g.Text(Sphere3D.TestAbout(t), Theme.Small, A(Theme.Muted), new PointF(r.Left + Theme.Px(40), r.Top + Theme.Px(24)));
                if (done)
                {
                    var sz = g.Measure(result, Theme.Small);
                    g.Text(result, Theme.Small, A(Theme.Accent), new PointF(r.Right - sz.Width - Theme.Px(12), r.Top + Theme.Px(6)));
                }
            }
        }

        static string RightOf(Sphere3D.Variant v) { return v.Errors.Count(e => e == 0) + " of " + v.N + " right"; }
        static string Misses(Sphere3D.Variant v)
        {
            var p = new List<string>();
            if (v.HeightWrong > 0) p.Add("height wrong " + v.HeightWrong + "x");
            if (v.FrontBack > 0) p.Add("front/back swapped " + v.FrontBack + "x");
            if (v.CantTell > 0) p.Add("can't tell " + v.CantTell + "x");
            return p.Count == 0 ? "" : ", " + string.Join(", ", p);
        }
        void Chart(IEnumerable<Sphere3D.Variant> vs, string win)
        {
            // 4.2.2: best first in the order the heads were ranked, each with how many footsteps it got right (its average
            // miss read "0" for every head when they were all perfect).
            var top = Sphere3D.Ranked(vs.Where(v => v.N > 0).ToList()).Take(10).ToList();
            var w = vs.FirstOrDefault(v => v.HeadName == win && v.N > 0);
            if (w != null && !top.Contains(w)) { top[top.Count - 1] = w; }
            chart = top.Select(v => Tuple.Create(v.HeadName, v.Errors.Count(e => e == 0), v.N)).ToList();
            chartWinner = win;
        }

        void Next() { if (round == Round.Search) ShowMenu(null); }

        // ------------------------------------------------------------------ practice: footsteps from all six directions, shown after every answer

        // ---- Practice (4.1: also a mode of its own - your settings as they are, nothing tuned, as long as you like)

        bool practiceMode;
        int practiceRight, practiceCount, practiceStreak, practiceBest;

        /// <summary>Opened for practice: its own first card, and Start goes straight to the footsteps.</summary>
        void PracticeMode()
        {
            practiceMode = true;
            headline = "Practice 3D";
            detail = "Put on " + phonesName + ". Footsteps play from all six directions around your head - in front, behind, left, right, above or below - with your 3D settings exactly as they are: nothing is tuned or changed.\n\n" +
                "Every footstep starts with you facing forward. Snap the view to where you hear them (mouse, arrow keys or WASD) and click; you see right away where they really were. Can't tell? Right-click or Space.\n\n" +
                "Your score keeps count. Go as long as you like - Esc ends it.";
            if (phones == null) detail = "No headphones found.";
        }

        void Scored(bool right)
        {
            practiceCount++;
            if (right) { practiceRight++; practiceStreak++; practiceBest = Math.Max(practiceBest, practiceStreak); } else practiceStreak = 0;
        }

        string PracticeScore()
        {
            return practiceCount == 0 ? "" : practiceRight + " of " + practiceCount + " right (" + (100 * practiceRight / practiceCount) + "%)" + (practiceStreak >= 3 ? ",  streak " + practiceStreak : "");
        }

        void StartPractice()
        {
            EnsureAudio();
            round = Round.Practice;
            trials = new List<Sphere3D.Trial> { PracticeTrial() };
            trialIdx = 0;
            Countdown(Stage.Listen, 2);
        }

        Sphere3D.Trial PracticeTrial()
        {
            var d = Sphere3D.Dirs[rnd.Next(Sphere3D.Dirs.Length)];
            return new Sphere3D.Trial { V = Current, S = Sphere3D.At(d), Stage = "practice" };
        }

        void Apply(Sphere3D.Variant v)
        {
            core.S.Game.Hrtf = v.Hrtf; core.S.Game.CueSharpen = v.Cue; core.S.Game.FloorContrast = v.Contrast; core.S.Game.HeadSize = v.HeadSize;
            core.SavePersonal3D(phones, v.Hrtf, v.Cue, v.Contrast, v.HeadSize);
            core.PushGameParams();
            core.SaveSoon();
        }

        void Tick()
        {
            if (paused || closing) return;
            if ((stage == Stage.Feedback || stage == Stage.Listen) && trials == null) return;
            var now = DateTime.Now;
            if (stage == Stage.Countdown) { if (now >= stageEnd && loaded >= toLoad) { if (afterCountdown == Stage.Track) BeginTrack(); else BeginTrial(); } Redraw(); }
            else if (stage == Stage.Feedback && now >= stageEnd) NextTrial();
            else if (stage == Stage.Track)
            {
                var tr = tracker;
                if (tr != null && tr.Finished) { TestDone(); return; }
                if (tr != null)
                {
                    int secs = (int)Math.Ceiling((tr.Total - tr.Done) * 0.43);
                    headline = RoundName(round) + "  ·  " + (secs / 60) + ":" + (secs % 60).ToString("00") + " left";
                }
            }
            if (audio != null && audio.Error != null && stage != Stage.Done && stage != Stage.Intro)
            {
                Stop(); Release(); stage = Stage.Done; headline = "Couldn't play"; detail = audio.Error; chart = null; ShowButtons("Back to StreamLite", null); Redraw();
            }
        }

        // ------------------------------------------------------------------ sound

        void Play(object what, Sphere3D.Variant v)
        {
            playVariant = v;
            playStart = audio != null ? audio.Now + 4800 : 0;
            playing = what;
        }

        void Stop() { playing = null; if (audio != null) audio.Clear(); }

        /// <summary>Audio thread: someone walking in one spot - the next footstep, about every 0.43 s.</summary>
        void Conduct()
        {
            long next = -1; int step = 0; object cur = null; var jitter = new Random();
            while (!closing)
            {
                var what = playing; var au = audio;
                if (what == null || au == null) { next = -1; cur = null; Thread.Sleep(10); continue; }
                if (!ReferenceEquals(what, cur)) { cur = what; next = Interlocked.Read(ref playStart); }
                long now = au.Now;
                bool track = ReferenceEquals(what, TrackSound);
                // Tracking: rendered as late as is safe, from where the view points right then.
                if (next - now < (track ? 2400 : 4800))
                {
                    float[] l, r;
                    try
                    {
                        if (track)
                        {
                            var tr = tracker;
                            if (tr != null && !tr.Finished)
                            {
                                V pos; var v = TrackNext(tr, out pos);
                                renderer.RenderAt(v, pos, yaw, pitch, step++, out l, out r);
                                au.Add(l, r, Math.Max(next, now + 960));
                            }
                        }
                        else { renderer.Render(playVariant, (Sphere3D.Source)what, step++, out l, out r); au.Add(l, r, Math.Max(next, now + 960)); }
                    }
                    catch { }
                    next += 20400 + jitter.Next(800);   // a walking pace, ~0.43 s a step
                }
                Thread.Sleep(3);
            }
        }

        // ------------------------------------------------------------------ mouse and keys

        new void Capture()
        {
            if (captured || NoMouse) return;
            captured = true;
            Cursor.Hide();
            center = PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            Cursor.Position = center;
            Cursor.Clip = RectangleToScreen(ClientRectangle);
        }

        void Release()
        {
            if (!captured || NoMouse) return;
            captured = false;
            Cursor.Clip = Rectangle.Empty;
            Cursor.Show();
        }

        void Pause()
        {
            if (stage == Stage.Intro || stage == Stage.Break || stage == Stage.Menu || stage == Stage.Done || paused || anim != Anim.None) return;
            paused = true; Release();
            if (playing != null) { pausedWhat = playing; Stop(); }
            ShowButtons("Resume", round == Round.Test ? "Stop this test" : "Back to StreamLite");
            Redraw();
        }

        void Resume()
        {
            paused = false;
            ShowButtons(null, null);
            Capture(); if (scene != null) scene.FocusScene();
            if (pausedWhat != null && (stage == Stage.Listen || stage == Stage.Feedback || stage == Stage.Track))
            {
                var w = pausedWhat; pausedWhat = null;
                Play(w, playVariant);
            }
            Redraw();
        }

        /// <summary>The mouse moved: pushed far enough one way, the view snaps to the next direction (the mouse goes back to the middle).</summary>
        void Look()
        {
            if (!captured)
            {
                var at = PointToClient(Cursor.Position);
                int h = ButtonAt(at), m = MenuAt(at);
                if (h != hot || m != menuHot) { hot = h; menuHot = m; Redraw(); }
                return;
            }
            var p = Cursor.Position;
            int dx = p.X - center.X, dy = p.Y - center.Y;
            if (dx == 0 && dy == 0) return;
            Cursor.Position = center;
            LookBy(dx, dy);
        }

        /// <summary>The mouse moved this far: footstep tracking turns the view freely (ear-level tracking only left and right), otherwise it pushes towards a snap.</summary>
        internal void LookBy(int dx, int dy)
        {
            if (TrackingNow)
            {
                yaw = ((yaw + dx * sens) % 360 + 360) % 360;
                pitch = test == Sphere3D.Test.TrackLevel ? 0 : Math.Max(-85f, Math.Min(85f, pitch - dy * sens));
                Redraw();
                return;
            }
            pushX += dx * sens; pushY -= dy * sens;
            const float SnapDeg = 18;
            if (Math.Abs(pushY) >= SnapDeg && Math.Abs(pushY) > Math.Abs(pushX)) Facing(Sphere3D.Tilt(facing, level, Math.Sign(pushY)));
            else if (Math.Abs(pushX) >= SnapDeg) Facing(Sphere3D.Turn(facing, Math.Sign(pushX)));
            Redraw();
        }

        void Facing(Sphere3D.Dir d)
        {
            facing = d; if (Sphere3D.Level(d)) level = d;
            pushX = pushY = 0;
            Redraw();
        }

        /// <summary>The camera eases to the direction the view faces (about 60 ms), the short way round.</summary>
        void EaseView(double dtMs)
        {
            if (TrackingNow) return;   // the mouse turns the view itself
            double ty, tp; Sphere3D.View(facing, level, out ty, out tp);
            double dYaw = ((ty - yaw) % 360 + 540) % 360 - 180, dPitch = tp - pitch;
            if (Math.Abs(dYaw) < 0.01 && Math.Abs(dPitch) < 0.01) return;
            double k = 1 - Math.Exp(-Math.Min(100, dtMs) / 55.0);
            if (Math.Abs(dYaw) < 0.1 && Math.Abs(dPitch) < 0.1) k = 1;
            yaw = (float)(((yaw + dYaw * k) % 360 + 360) % 360); pitch = (float)(pitch + dPitch * k);
            Redraw();
        }

        void MouseClicked(MouseButtons b)
        {
            if (anim != Anim.None) return;
            if (!captured && b == MouseButtons.Left)
            {
                var pt = PointToClient(Cursor.Position);
                int at = ButtonAt(pt);
                if (at == 1) { Primary(); return; }
                if (at == 2) { Secondary(); return; }
                int row = MenuAt(pt);
                if (row >= 0) { StartTest(Sphere3D.Tests[row]); return; }
            }
            if (paused) return;
            if (!captured && (stage == Stage.Listen || stage == Stage.Countdown || stage == Stage.Track)) { Capture(); return; }
            if (b == MouseButtons.Left && stage == Stage.Listen) Answer();
            else if (b == MouseButtons.Right && stage == Stage.Listen) CantTell();
            else if (b == MouseButtons.Right && stage == Stage.Track && tracker != null) tracker.ShowMe();
        }

        void Key(Keys k)
        {
            if (anim != Anim.None) return;
            if (k == Keys.Escape)
            {
                if (paused) FlyOut();
                else if (round == Round.Practice && stage != Stage.Done && captured) { Stop(); Release(); stage = Stage.Done; headline = practiceCount > 0 ? "Practice: " + practiceRight + " of " + practiceCount + " right" : "All set"; if (practiceCount > 0) { detail = "You placed " + practiceRight + " of " + practiceCount + " footsteps right (" + (100 * practiceRight / practiceCount) + "%)" + (practiceBest >= 3 ? ", " + practiceBest + " in a row at best" : "") + ". Nothing was changed: your 3D settings are as they were."; chart = null; } ShowButtons("Back to StreamLite", "Keep practising"); }
                else if (captured) Pause(); else FlyOut();
            }
            else if (k == Keys.Enter && ButtonsShown && primaryText != null) Primary();
            else if (k == Keys.Enter && stage == Stage.Listen && captured) Answer();
            else if (stage == Stage.Menu && !paused && k >= Keys.D1 && k < Keys.D1 + Sphere3D.Tests.Length) StartTest(Sphere3D.Tests[k - Keys.D1]);
            else if (stage == Stage.Menu && !paused && k >= Keys.NumPad1 && k < Keys.NumPad1 + Sphere3D.Tests.Length) StartTest(Sphere3D.Tests[k - Keys.NumPad1]);
            else if (paused || !captured) { }
            else if (TrackingNow && k == Keys.Space) { if (stage == Stage.Track) tracker.ShowMe(); }
            else if (TrackingNow && k != Keys.Oemplus && k != Keys.Add && k != Keys.OemMinus && k != Keys.Subtract) { }   // only the mouse turns the view
            else if (k == Keys.Left || k == Keys.A) Facing(Sphere3D.Turn(facing, -1));
            else if (k == Keys.Right || k == Keys.D) Facing(Sphere3D.Turn(facing, 1));
            else if (k == Keys.Up || k == Keys.W) Facing(Sphere3D.Tilt(facing, level, 1));
            else if (k == Keys.Down || k == Keys.S) Facing(Sphere3D.Tilt(facing, level, -1));
            else if (k == Keys.Space) CantTell();
            else if (k == Keys.Oemplus || k == Keys.Add) sens = Math.Min(0.6f, sens * 1.2f);
            else if (k == Keys.OemMinus || k == Keys.Subtract) sens = Math.Max(0.03f, sens / 1.2f);
            Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Look(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); MouseClicked(e.Button); }
        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Escape || keyData == Keys.Space || keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); Key(e.KeyCode); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { closing = true; Release(); if (audio != null) { audio.Dispose(); audio = null; } if (scene != null) { scene.Dispose(); scene = null; } }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ drawing: the radar, made into a sphere

        static readonly Color AboveCol = Color.FromArgb(110, 165, 240), LevelCol = Color.FromArgb(110, 210, 130), BelowCol = Color.FromArgb(235, 170, 90),
            Pillar = Color.FromArgb(120, 130, 140), Gold = Color.FromArgb(255, 196, 80), Wrong = Color.FromArgb(240, 96, 86);

        // The camera.
        V camEye, cf, cr, cu; float focal; PointF mid; float fade = 1;

        void Camera(V eye, double yawDeg, double pitchDeg, double fovDeg, Size size)
        {
            camEye = eye; cf = Sphere3D.Forward(yawDeg, pitchDeg); cr = Sphere3D.Right(yawDeg); cu = Sphere3D.Up(yawDeg, pitchDeg);
            mid = new PointF(size.Width / 2f, size.Height / 2f);
            focal = (float)(size.Height / 2.0 / Math.Tan(fovDeg / 2 * Math.PI / 180));
        }

        bool ToCam(V p, out double x, out double y, out double z) { V v = p - camEye; x = v.Dot(cr); y = v.Dot(cu); z = v.Dot(cf); return z > 0.05; }
        PointF Proj(double x, double y, double z) { return new PointF((float)(mid.X + x / z * focal), (float)(mid.Y - y / z * focal)); }

        /// <summary>One thing to draw: a translucent polygon or a line, sorted back to front by its distance.</summary>
        sealed class Prim { public V[] Pts; public Color C; public float Width; public bool Line; public double Dist; public int Layer; }
        readonly List<Prim> prims = new List<Prim>();
        // Drawn layer by layer (the sphere's lines, then the six spots and marks on top), back to front inside each.
        const int LShell = 0, LMarks = 1;
        int layer;

        static Color Fade(Color c, double a) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)a)), c); }
        void Fill(Color c, int alpha, params V[] pts) { if (alpha > 0) prims.Add(new Prim { Pts = pts, C = Fade(c, alpha * fade), Layer = layer }); }
        void Line(Color c, int alpha, float w, V a, V b) { if (alpha > 0) prims.Add(new Prim { Pts = new[] { a, b }, C = Fade(c, alpha * fade), Width = w, Line = true, Layer = layer }); }

        void DrawPrim(ICanvas g, Prim p)
        {
            var cam = new double[p.Pts.Length][];
            for (int i = 0; i < p.Pts.Length; i++) { double x, y, z; ToCam(p.Pts[i], out x, out y, out z); cam[i] = new[] { x, y, z }; }
            const double near = 0.05;
            if (p.Line)
            {
                var a = cam[0]; var b = cam[1];
                if (a[2] < near && b[2] < near) return;
                if (a[2] < near) { double t = (near - a[2]) / (b[2] - a[2]); a = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near }; }
                if (b[2] < near) { double t = (near - b[2]) / (a[2] - b[2]); b = new[] { b[0] + (a[0] - b[0]) * t, b[1] + (a[1] - b[1]) * t, near }; }
                g.Line(p.C, p.Width, Proj(a[0], a[1], a[2]), Proj(b[0], b[1], b[2]));
                return;
            }
            var outp = new List<PointF>(8);
            for (int i = 0; i < cam.Length; i++)
            {
                var a = cam[i]; var b = cam[(i + 1) % cam.Length];
                bool ain = a[2] >= near, bin = b[2] >= near;
                if (ain) outp.Add(Proj(a[0], a[1], a[2]));
                if (ain != bin) { double t = (near - a[2]) / (b[2] - a[2]); outp.Add(Proj(a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near)); }
            }
            if (outp.Count >= 3) g.Polygon(p.C, outp.ToArray());
        }

        static Font labelFont, countFont;

        /// <summary>
        /// The sphere in the radar's colours, from wherever the camera is: lines of height every 30 degrees (blue above,
        /// the green ear-level ring, orange below), lines of direction every 30, and the six spots footsteps play from.
        /// </summary>
        void Scene(ICanvas g)
        {
            prims.Clear();
            double R = Sphere3D.Radius;
            bool aiming = (captured || NoMouse) && stage == Stage.Listen;
            layer = LShell;
            for (int e = -60; e <= 60; e += 30)
            {
                var col = e > 0 ? AboveCol : e < 0 ? BelowCol : LevelCol;
                double y = Math.Sin(e * Math.PI / 180) * R, r = Math.Cos(e * Math.PI / 180) * R;
                var prev = new V(0, y, r);
                for (int i = 1; i <= 48; i++)
                {
                    double a = 2 * Math.PI * i / 48; var q = new V(Math.Sin(a) * r, y, Math.Cos(a) * r);
                    Line(col, e == 0 ? 230 : 110, e == 0 ? 2.6f : 1.3f, prev, q); prev = q;
                }
            }
            for (int m = 0; m < 12; m++)
            {
                double a = m * Math.PI / 6;
                var prev = new V(0, -R, 0);
                for (int i = 1; i <= 24; i++)
                {
                    double e = -Math.PI / 2 + Math.PI * i / 24;
                    var q = new V(Math.Sin(a) * Math.Cos(e) * R, Math.Sin(e) * R, Math.Cos(a) * Math.Cos(e) * R);
                    Line(e > 0.01 ? AboveCol : e < -0.01 ? BelowCol : LevelCol, m % 3 == 0 ? 120 : 65, m % 3 == 0 ? 1.4f : 1f, prev, q); prev = q;
                }
            }
            // The six spots: the one the view faces in gold, where it really was in gold too, a wrong answer in red.
            layer = LMarks;
            var tracking = TrackingNow ? tracker : null;
            bool showTrack = tracking != null && stage == Stage.Track && tracking.Showing;
            if (tracking == null) foreach (var d in Sphere3D.Dirs)
            {
                Color c = d == Sphere3D.Dir.Up ? AboveCol : d == Sphere3D.Dir.Down ? BelowCol : LevelCol; int a = 70;
                if (aiming && facing == d) { c = Gold; a = 150; }
                if (reveal.HasValue && reveal.Value.D == d) { c = Gold; a = 200; }
                else if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value == d) { c = Wrong; a = 180; }
                Disc(c, a, d, 0.55);
            }
            // Where it really was: a gold beam from you to the spot (what "can't tell" shows).
            if (reveal.HasValue) Line(Gold, cantTell ? 230 : 150, cantTell ? 3f : 2f, Sphere3D.Eye + new V(0, -0.05, 0), reveal.Value.Pos);
            if (showTrack) Line(Gold, 200, 2.5f, Sphere3D.Eye + new V(0, -0.05, 0), tracking.Pos);
            foreach (var p in prims) { var m = Centroid(p.Pts); p.Dist = (m - camEye).Length; }
            prims.Sort((x, y) => x.Layer != y.Layer ? x.Layer.CompareTo(y.Layer) : y.Dist.CompareTo(x.Dist));
            foreach (var p in prims) DrawPrim(g, p);

            // Where it really was: a gold dot (pulsing while they play again).
            if (reveal.HasValue)
            {
                double pulse = cantTell ? 1 + 0.25 * Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 9) : 1;
                Dot(g, reveal.Value.Pos * 0.97, Gold, 0.24 * pulse);
                if (cantTell) Redraw();
            }
            if (showTrack) Dot(g, tracking.Pos * 0.97, Gold, 0.24);
            // You, seen from outside: the radar's white dot (gone as you reach it).
            double toYou = (camEye - Sphere3D.Eye).Length;
            if (toYou > 5) { float keep = fade; fade *= (float)Smooth(5, 16, toYou); Dot(g, Sphere3D.Eye, Theme.Text, 0.25); fade = keep; }
            // Each spot's name just above it on the screen (clear of the crosshair).
            foreach (var d in Sphere3D.Dirs)
            {
                double x, y, z; if (!ToCam(Sphere3D.Point(d) * 0.97, out x, out y, out z)) continue;
                if (labelFont == null) labelFont = Theme.Ui(8.5f * 1.15f, true);
                var q = Proj(x, y, z); string s = Sphere3D.Label(d); var sz = g.Measure(s, labelFont);
                float up = (float)(0.55 / z * focal) + Theme.Px(8) + sz.Height;
                g.Text(s, labelFont, Fade(Color.FromArgb(235, Theme.Text), 235 * fade), new PointF(q.X - sz.Width / 2, q.Y - up));
            }
        }

        /// <summary>A round spot lying on the sphere where a direction's footsteps play (radius in m).</summary>
        void Disc(Color c, int alpha, Sphere3D.Dir d, double r)
        {
            V n = Sphere3D.Unit(d), t1 = Sphere3D.Level(d) ? new V(0, 1, 0) : new V(1, 0, 0), t2 = n.Cross(t1);
            var centre = Sphere3D.Eye + n * (Sphere3D.Radius * 0.99);
            var pts = new V[32];
            for (int i = 0; i < pts.Length; i++) { double a = 2 * Math.PI * i / pts.Length; pts[i] = centre + t1 * (Math.Cos(a) * r) + t2 * (Math.Sin(a) * r); }
            Fill(c, alpha, pts);
            for (int i = 0; i < pts.Length; i++) Line(c, Math.Min(255, alpha + 80), 2.4f, pts[i], pts[(i + 1) % pts.Length]);
        }

        static V Centroid(V[] p) { double x = 0, y = 0, z = 0; foreach (var q in p) { x += q.X; y += q.Y; z += q.Z; } return new V(x / p.Length, y / p.Length, z / p.Length); }

        void Dot(ICanvas g, V p, Color c, double radiusM)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            var q = Proj(x, y, z); float r = (float)Math.Max(4, Math.Min(30, radiusM / z * focal));
            g.Ellipse(Fade(c, 60 * fade), new RectangleF(q.X - 2 * r, q.Y - 2 * r, 4 * r, 4 * r));
            g.Ellipse(Fade(c, 255 * fade), new RectangleF(q.X - r, q.Y - r, 2 * r, 2 * r));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (scene != null) return;      // the graphics card draws it
            Render(new GdiCanvas(e.Graphics, snap, desk), ClientSize);
        }

        /// <summary>Everything, for the current moment: the window growing, zooming into the radar, the sphere.</summary>
        internal void Render(ICanvas g, Size size)
        {
            g.Clear(Theme.Bg);
            double grow, zoom, cross, fly, reveal01; Progress(out grow, out zoom, out cross, out fly, out reveal01);
            bool animating = anim != Anim.None;
            var origin = fullBounds.Location;
            // The window growing over the screen as it was (and shrinking back at the end).
            if (animating && grow < 0.999)
            {
                g.Picture(1, new RectangleF(0, 0, size.Width, size.Height), 1);
                var from = new RectangleF(homeBounds.X - origin.X, homeBounds.Y - origin.Y, homeBounds.Width, homeBounds.Height);
                g.Rect(Theme.Bg, Lerp(from, new RectangleF(0, 0, size.Width, size.Height), grow));
            }
            // The sphere: first as the radar shows it - from the radar's angle, far away and nearly flat, as big as the
            // zoomed radar's cube - then flying in to its middle.
            double u = fly;
            double fov0 = 10, cubeScreen = 0.30;
            double d0 = (size.Height / 2.0 / Math.Tan(fov0 / 2 * Math.PI / 180)) * Sphere3D.Radius / (cubeScreen * size.Height);
            double dist = d0 * Math.Pow(1 - u, 2);
            double y0 = radarYaw, p0 = -radarPitch;
            double cy = y0 + (yaw - y0) * Ease(u), cp = p0 + (pitch - p0) * Ease(u);
            var f = Sphere3D.Forward(cy, cp);
            var target = Sphere3D.Eye;
            Camera(target - f * dist, cy, cp, fov0 + (75 - fov0) * Math.Pow(u, 1.5), size);
            fade = (float)cross;
            if (fade > 0.001f) Scene(g);
            fade = 1;
            // The picture of StreamLite, zooming into the radar's centre until the cube is about as big as the
            // sphere, then fading into it (and on, a little further, as you fly in).
            if (animating && cross < 0.999)
            {
                double zMax = cubeScreen * size.Height / Math.Max(20, radarR);
                double z = (1 + (zMax - 1) * zoom) * (1 + 1.5 * u);
                var c = new PointF(radarScreen.X - origin.X, radarScreen.Y - origin.Y);
                var to = new PointF(size.Width / 2f, size.Height / 2f);
                var cNow = new PointF((float)(c.X + (to.X - c.X) * zoom), (float)(c.Y + (to.Y - c.Y) * zoom));
                var topLeft = new PointF(snapScreen.X - origin.X, snapScreen.Y - origin.Y);
                var dest = new RectangleF((float)(cNow.X + (topLeft.X - c.X) * z), (float)(cNow.Y + (topLeft.Y - c.Y) * z), (float)(snapSize.Width * z), (float)(snapSize.Height * z));
                g.Picture(0, dest, (float)(1 - cross));
            }
            if (anim == Anim.None || anim == Anim.Enter && reveal01 > 0) Hud(g, size, anim == Anim.None ? 1f : (float)reveal01);
        }

        bool CardStage { get { return (stage == Stage.Intro && !startNow) || stage == Stage.Break || stage == Stage.Menu || stage == Stage.Done || paused; } }

        /// <summary>The room under the card's text: the heads' bars, or the menu's tests.</summary>
        int ChartHeight { get { return paused ? 0 : stage == Stage.Menu ? MenuHeight : chart != null && chart.Count > 0 ? Theme.Px(26) * chart.Count + Theme.Px(20) : 0; } }

        Rectangle CardRect()
        {
            int w = Math.Min(Theme.Px(stage == Stage.Menu && !paused ? 700 : 860), Math.Max(Theme.Px(300), ClientSize.Width - Theme.Px(80)));
            string d = paused ? PausedText : detail;
            int textH = TextRenderer.MeasureText(d ?? "", Theme.Body, new Size(w - Theme.Px(60), 0), TextFormatFlags.WordBreak).Height;
            int h = Theme.Px(78) + textH + Theme.Px(24) + ChartHeight + (stage == Stage.Intro && !paused ? Theme.Px(30) : 0) + Theme.Px(80);
            h = Math.Min(h, Math.Max(Theme.Px(200), ClientSize.Height - Theme.Px(40)));
            return cardRect = new Rectangle((ClientSize.Width - w) / 2, Math.Max(Theme.Px(20), (ClientSize.Height - h) / 2), w, h);
        }

        const string PausedText = "Resume to carry on - this footstep plays again.";

        /// <summary>The cards and lines over the sphere, in StreamLite's own look.</summary>
        void Hud(ICanvas g, Size size, float alpha)
        {
            Func<Color, Color> A = c => Color.FromArgb((int)(c.A * alpha), c);
            if (CardStage)
            {
                g.Rect(Color.FromArgb((int)(150 * alpha), 8, 9, 11), new RectangleF(0, 0, size.Width, size.Height));
                var r = CardRect();
                g.Rounded(A(Color.FromArgb(244, Theme.Card)), A(Theme.Track), 1.2f, r, Theme.Px(14));
                g.Rect(A(Theme.Accent), new RectangleF(r.Left + Theme.Px(28), r.Top + Theme.Px(30), Theme.Px(4), Theme.Px(26)));
                string h = paused ? "Paused" : headline;
                string d = paused ? PausedText : detail;
                g.Text(h, Theme.Title, A(Theme.Text), new PointF(r.Left + Theme.Px(42), r.Top + Theme.Px(26)));
                var box = new RectangleF(r.Left + Theme.Px(30), r.Top + Theme.Px(78), r.Width - Theme.Px(60), r.Height - Theme.Px(78) - Theme.Px(80) - ChartHeight);
                g.Text(d, Theme.Body, A(Color.FromArgb(225, Theme.Text)), box);
                var under = new RectangleF(r.Left + Theme.Px(30), box.Bottom + Theme.Px(6), r.Width - Theme.Px(60), ChartHeight - Theme.Px(10));
                if (stage == Stage.Menu && !paused) MenuRows(g, under, A);
                else if (ChartHeight > 0) Bars(g, under, alpha);
                if (stage == Stage.Intro && !paused) g.Text("Headphones: " + phonesName, Theme.Small, A(Theme.Muted), new PointF(r.Left + Theme.Px(30), r.Bottom - Theme.Px(100)));
                PlaceButtons(r);
                DrawButtons(g, A);
                return;
            }
            // Playing: overall progress along the top, the round in a pill, what to do at the bottom, the crosshair.
            if (totalSteps > 0 && round != Round.Practice)
            {
                float p = Math.Min(1, doneSteps / (float)totalSteps);
                g.Rect(Color.FromArgb(120, Theme.Card), new RectangleF(0, 0, size.Width, Theme.Px(4)));
                g.Rect(Theme.Accent, new RectangleF(0, 0, size.Width * p, Theme.Px(4)));
            }
            var hs = g.Measure(headline, Theme.Section);
            var pill = new RectangleF(Theme.Px(20), Theme.Px(18), hs.Width + Theme.Px(28), Theme.Px(36));
            g.Rounded(Color.FromArgb(200, Theme.Card), Color.Empty, 0, pill, Theme.Px(18));
            g.Text(headline, Theme.Section, Theme.Text, new PointF(pill.Left + Theme.Px(14), pill.Top + (pill.Height - hs.Height) / 2));
            if (stage == Stage.Loading || stage == Stage.Countdown && loaded < toLoad && DateTime.Now >= stageEnd)
            {
                string s = "Loading the heads...  " + loaded + " of " + toLoad;
                var sz = g.Measure(s, Theme.Body);
                g.Text(s, Theme.Body, Theme.Muted, new PointF((size.Width - sz.Width) / 2, size.Height / 2f));
            }
            if (!string.IsNullOrEmpty(detail))
            {
                var ds = g.Measure(detail, Theme.Body);
                var bar = new RectangleF((size.Width - ds.Width) / 2 - Theme.Px(18), size.Height - Theme.Px(70), ds.Width + Theme.Px(36), Theme.Px(38));
                g.Rounded(Color.FromArgb(200, Theme.Card), Color.Empty, 0, bar, Theme.Px(19));
                Color dc = Theme.Text;
                if (stage == Stage.Feedback && trials != null && trialIdx < trials.Count && trials[trialIdx].Sc != null)
                    dc = cantTell ? Gold : trials[trialIdx].Sc.Right ? Theme.Accent : Theme.Warn;
                g.Text(detail, Theme.Body, dc, new PointF(bar.Left + Theme.Px(18), bar.Top + (bar.Height - ds.Height) / 2));
            }
            if (stage == Stage.Countdown && DateTime.Now < stageEnd)
            {
                if (countFont == null) countFont = Theme.Ui(72f, true);
                int left = Math.Max(1, (int)Math.Ceiling((stageEnd - DateTime.Now).TotalSeconds));
                var s = left.ToString(); var sz = g.Measure(s, countFont);
                float rr = Math.Max(sz.Width, sz.Height) * 0.62f;
                var ring = new RectangleF(size.Width / 2f - rr, size.Height / 2f - rr - Theme.Px(40), 2 * rr, 2 * rr);
                g.Ellipse(Color.FromArgb(170, Theme.Card), ring);
                // The ring empties as the second runs out.
                double frac = (stageEnd - DateTime.Now).TotalSeconds % 1; if (frac <= 0) frac = 1;
                g.Ring(Color.FromArgb(60, Theme.Accent), Theme.Px(3), ring);
                Arc(g, ring, frac);
                g.Text(s, countFont, Theme.Text, new PointF((size.Width - sz.Width) / 2, ring.Top + (ring.Height - sz.Height) / 2));
                var hint = TrackingNow ? (test == Sphere3D.Test.TrackLevel ? "Get ready - the mouse turns you left and right. Follow the footsteps around you." : "Get ready - the mouse looks around freely. Follow the footsteps wherever they go.")
                    : round == Round.Search ? "Get ready - move the mouse (or the arrow keys) to snap the view around." : "Get ready.";
                var hz = g.Measure(hint, Theme.Body);
                g.Text(hint, Theme.Body, Theme.Muted, new PointF((size.Width - hz.Width) / 2, ring.Bottom + Theme.Px(20)));
            }
            if (reveal.HasValue && stage == Stage.Feedback) Pointer(g, size, reveal.Value.Pos * 0.97, Gold);
            var shownTrack = tracker;
            if (stage == Stage.Track && shownTrack != null && shownTrack.Showing) Pointer(g, size, shownTrack.Pos * 0.97, Gold);
            if ((stage == Stage.Listen || stage == Stage.Feedback || stage == Stage.Countdown) && !TrackingNow) Map(g, size);
            if (captured || NoMouse)
            {
                var col = Color.FromArgb(230, Theme.Text);
                float m = Theme.Px(9), gap = Theme.Px(3);
                g.Line(col, 2f, new PointF(mid.X - m, mid.Y), new PointF(mid.X - gap, mid.Y)); g.Line(col, 2f, new PointF(mid.X + gap, mid.Y), new PointF(mid.X + m, mid.Y));
                g.Line(col, 2f, new PointF(mid.X, mid.Y - m), new PointF(mid.X, mid.Y - gap)); g.Line(col, 2f, new PointF(mid.X, mid.Y + gap), new PointF(mid.X, mid.Y + m));
                // What a click picks right now, under the crosshair.
                if (stage == Stage.Listen)
                {
                    string what = Sphere3D.Name(facing); what = char.ToUpper(what[0]) + what.Substring(1);
                    Color wc = facing == Sphere3D.Dir.Up ? AboveCol : facing == Sphere3D.Dir.Down ? BelowCol : LevelCol;
                    var ws = g.Measure(what, Theme.Bold);
                    var pillW = new RectangleF(mid.X - ws.Width / 2 - Theme.Px(10), mid.Y + Theme.Px(44), ws.Width + Theme.Px(20), ws.Height + Theme.Px(6));
                    g.Rounded(Color.FromArgb(170, Theme.Card), Color.Empty, 0, pillW, Theme.Px(10));
                    g.Text(what, Theme.Bold, wc, new PointF(pillW.Left + Theme.Px(10), pillW.Top + Theme.Px(3)));
                }
            }
        }

        /// <summary>
        /// The six directions as a small map in the corner - above over in front, below under it, left, right and behind
        /// beside it: the one the view faces in gold (where they really were too, a wrong answer in red).
        /// </summary>
        void Map(ICanvas g, Size size)
        {
            float w = Theme.Px(70), h = Theme.Px(26), gap = Theme.Px(4);
            float x0 = size.Width - Theme.Px(24) - 4 * w - 3 * gap, y0 = size.Height - Theme.Px(24) - 3 * h - 2 * gap;
            var cells = new[] { Sphere3D.Dir.Left, Sphere3D.Dir.Front, Sphere3D.Dir.Right, Sphere3D.Dir.Behind };
            Action<Sphere3D.Dir, int, int> cell = (d, col, row) =>
            {
                var r = new RectangleF(x0 + col * (w + gap), y0 + row * (h + gap), w, h);
                Color c = Color.FromArgb(170, Theme.Card), t = Theme.Muted;
                if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value == d && reveal.Value.D != d) { c = Wrong; t = Color.FromArgb(20, 10, 10); }
                else if ((reveal.HasValue && reveal.Value.D == d) || (!reveal.HasValue && facing == d)) { c = Gold; t = Color.FromArgb(30, 22, 6); }
                g.Rounded(c, Color.Empty, 0, r, Theme.Px(6));
                string s = Sphere3D.Label(d); var sz = g.Measure(s, Theme.Small);
                g.Text(s, Theme.Small, t, new PointF(r.Left + (r.Width - sz.Width) / 2, r.Top + (r.Height - sz.Height) / 2));
            };
            cell(Sphere3D.Dir.Up, 1, 0);
            for (int i = 0; i < 4; i++) cell(cells[i], i, 1);
            cell(Sphere3D.Dir.Down, 1, 2);
        }

        /// <summary>
        /// Where the steps really were, when it's off the screen (behind you, far to a side, straight up): a gold arrow at
        /// the edge, pointing the way to turn.
        /// </summary>
        void Pointer(ICanvas g, Size size, V p, Color c)
        {
            double x, y, z; bool front = ToCam(p, out x, out y, out z);
            float m = Theme.Px(80);
            if (front)
            {
                var q = Proj(x, y, z);
                if (q.X > m && q.X < size.Width - m && q.Y > m && q.Y < size.Height - m) return;   // on screen: the dot shows it
            }
            double dx = x, dy = -y;
            if (Math.Abs(dx) + Math.Abs(dy) < 1e-6) dx = 1;
            double sc = Math.Min((size.Width / 2.0 - m) / Math.Max(1e-9, Math.Abs(dx)), (size.Height / 2.0 - m) / Math.Max(1e-9, Math.Abs(dy)));
            var at = new PointF((float)(mid.X + dx * sc), (float)(mid.Y + dy * sc));
            double len = Math.Sqrt(dx * dx + dy * dy); float ux = (float)(dx / len), uy = (float)(dy / len);
            float L = Theme.Px(30), W = Theme.Px(17), r = Theme.Px(34);
            g.Ellipse(Color.FromArgb(70, c), new RectangleF(at.X - r, at.Y - r, 2 * r, 2 * r));
            g.Polygon(c, new[] { new PointF(at.X + ux * L * 0.6f, at.Y + uy * L * 0.6f), new PointF(at.X - ux * L * 0.4f - uy * W, at.Y - uy * L * 0.4f + ux * W), new PointF(at.X - ux * L * 0.4f + uy * W, at.Y - uy * L * 0.4f - ux * W) });
        }

        /// <summary>The countdown's ring: what's left of the second, clockwise from the top.</summary>
        static void Arc(ICanvas g, RectangleF r, double frac)
        {
            int n = Math.Max(2, (int)(64 * frac));
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = r.Width / 2;
            var prev = new PointF(cx, cy - rad);
            for (int i = 1; i <= n; i++)
            {
                double a = 2 * Math.PI * frac * i / n;
                var p = new PointF(cx + (float)(Math.Sin(a) * rad), cy - (float)(Math.Cos(a) * rad));
                g.Line(Theme.Accent, Theme.Px(3), prev, p); prev = p;
            }
        }

        /// <summary>Each head's footsteps placed right as a bar (longer = better) with "3 of 4 right", the winner in green.</summary>
        void Bars(ICanvas g, RectangleF box, float alpha)
        {
            if (chart == null || chart.Count == 0) return;
            Func<Color, Color> A = c => Color.FromArgb((int)(c.A * alpha), c);
            float row = Theme.Px(26), nameW = Theme.Px(170), full = box.Width - nameW - Theme.Px(110);
            for (int i = 0; i < chart.Count; i++)
            {
                float y = box.Top + i * row;
                bool win = chart[i].Item1 == chartWinner;
                g.Text(chart[i].Item1, win ? Theme.Bold : Theme.Small, A(win ? Theme.Accent : Theme.Muted), new PointF(box.Left, y + Theme.Px(3)));
                float bw = full * chart[i].Item2 / Math.Max(1, chart[i].Item3);
                g.Rounded(A(win ? Theme.Accent : Color.FromArgb(150, Theme.Track)), Color.Empty, 0, new RectangleF(box.Left + nameW, y + Theme.Px(6), Math.Max(Theme.Px(4), bw), row - Theme.Px(12)), Theme.Px(4));
                g.Text(chart[i].Item2 + " of " + chart[i].Item3 + " right", Theme.Small, A(Theme.Muted), new PointF(box.Left + nameW + Math.Max(Theme.Px(4), bw) + Theme.Px(8), y + Theme.Px(3)));
            }
        }
    }
}
