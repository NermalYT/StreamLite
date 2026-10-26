using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using V = StreamLite.Audio.Building3D.Vec;

namespace StreamLite.UI
{
    /// <summary>
    /// "Personalize 3D", inside StreamLite: the window grows to fill its screen, zooms into the middle of the live
    /// sound radar and flies you into it - the radar's cube becomes the building (Building3D): the floor above in the
    /// radar's blue, your floor's ear level in its green, the floor below in its orange. You can only look around.
    /// Footsteps play at exact spots; you look where you hear them and click. Round 1: still footsteps, blind, with the
    /// head models and cue sharpening mixed in - the best becomes your setting. Round 2: you follow footsteps that walk,
    /// with the winner against one alternative. Saved per pair of headphones. Leaving flies back out into the radar.
    /// Flat translucent polygons and lines, drawn only when something moves.
    /// </summary>
    internal sealed class Personal3DView : Control
    {
        enum Stage { Intro, Countdown, Listen, Feedback, Round1Done, Track, Between, Done }
        enum Anim { None, Enter, Exit }

        // The radar it flies out of: a picture of the window, where the radar's centre was, its size and how it's turned.
        readonly Bitmap snap;
        readonly Rectangle snapScreen;
        readonly PointF radarScreen;
        readonly float radarR;
        readonly double radarYaw, radarPitch;
        readonly MainForm host;
        readonly Action onClosed;
        FormWindowState homeState;
        Rectangle homeBounds, normalBounds, fullBounds;

        readonly AppCore core;
        readonly string phones, phonesName;
        readonly StepRenderer renderer = new StepRenderer();
        readonly Random rnd = new Random();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();
        readonly FlatButton primary = new FlatButton("Start", true), secondary = new FlatButton("Back to StreamLite");
        StereoTimeline audio;
        Thread conductor;
        volatile bool closing;
        Anim anim = Anim.None;
        /// <summary>Tests: a fixed moment of the animation (seconds) instead of the clock; NaN = the clock.</summary>
        internal double AnimAt = double.NaN;

        // Where you look (degrees), read by the audio thread for every step.
        volatile float yaw, pitch;
        float sens = 0.12f;
        bool captured, paused;
        /// <summary>Tests: pictures only - the real mouse cursor is never hidden, locked or released.</summary>
        internal bool NoMouse;
        Point center;

        Stage stage = Stage.Intro, afterCountdown;
        DateTime stageEnd;
        string headline = "", detail = "";
        List<Building3D.Trial> trials; List<Building3D.Variant> variants; int trialIdx;
        List<Building3D.Walk> walks; int walkIdx;
        Building3D.Variant best, alt, final;
        bool floorsMixedUp;
        volatile object playing; volatile Building3D.Variant playVariant; long playStart;
        Building3D.Answer hover;
        Building3D.Source? reveal; Building3D.Answer? lastAnswer;
        readonly double room;
        object pausedWhat;

        // In: the window grows, zooms into the radar, the radar's picture becomes the building, you fly in, the card.
        const double GrowS = 0.42, ZoomS = 0.75, CrossS = 0.35, FlyS = 1.35, RevealS = 0.35;
        // Out: fly out, the building becomes the radar's picture, zoom out, the window shrinks.
        const double OutFlyS = 0.95, OutCrossS = 0.3, OutZoomS = 0.5, OutShrinkS = 0.38;

        /// <summary>Opens it over StreamLite's window, from the radar (onClosed: refresh the page afterwards).</summary>
        public static void Open(AppCore core, SoundRadarView radar, Action onClosed)
        {
            var form = radar.FindForm() as MainForm;
            if (form == null || form.Controls.OfType<Personal3DView>().Any()) return;
            // The window as it looks now, and the radar's centre on screen.
            var r = form.Bounds;
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            // Drawn by the window itself (a screen copy would also catch anything lying on top of it).
            try { form.DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height)); }
            catch { using (var g = Graphics.FromImage(bmp)) g.Clear(Theme.Bg); }
            var c = radar.PointToScreen(Point.Round(radar.Centre));
            var v = new Personal3DView(core, bmp, r, c, radar.Radius, radar.ViewYaw, radar.ViewPitch, form, onClosed);
            v.Begin();
        }

        internal Personal3DView(AppCore core, Bitmap snap, Rectangle snapScreen, PointF radarScreen, float radarR, double radarYaw, double radarPitch, MainForm host, Action onClosed)
        {
            this.core = core; this.snap = snap; this.snapScreen = snapScreen; this.radarScreen = radarScreen; this.radarR = radarR;
            this.radarYaw = radarYaw; this.radarPitch = radarPitch; this.host = host; this.onClosed = onClosed;
            phones = core.GamePhonesId ?? core.RealDefaultOutput();
            var d = phones != null ? core.Outputs().FirstOrDefault(o => o.Id == phones) : null;
            phonesName = d != null ? d.Name : "your headphones";
            room = core.S.Game.ToConfig().Room;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Controls.Add(primary); Controls.Add(secondary);
            primary.Width = Theme.Px(200); secondary.Width = Theme.Px(200);
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Secondary();
            Resize += (s, e) => PlaceButtons();
            ShowButtons(null, null);
            timer.Tick += (s, e) => Tick();
            headline = "Personalize 3D";
            detail = "Put on " + phonesName + " and pause music and games. You stand in the middle of the radar, made into a building, and can only look around (mouse).\n\n" +
                "Footsteps play somewhere. Look where you hear them and click:\n" +
                "   above (blue): look up and click the room they're in\n" +
                "   below (orange): look down and click the room\n" +
                "   your floor (green): click the wall in their direction, on the green band\n\n" +
                "Round 1: 24 footsteps standing still (about 4 minutes). Round 2: follow walking footsteps with the crosshair (about a minute). " +
                "StreamLite tries its head models and cue settings blind and keeps whatever lets you place sounds best, for these headphones.\n\n" +
                "Esc: pause.   + / - : mouse speed.";
            if (phones == null) detail = "No headphones found.";
        }

        // ------------------------------------------------------------------ in and out

        void Begin()
        {
            homeState = host.WindowState;
            homeBounds = host.Bounds;
            normalBounds = host.WindowState == FormWindowState.Normal ? host.Bounds : host.RestoreBounds;
            fullBounds = Screen.FromRectangle(homeBounds).Bounds;
            Bounds = new Rectangle(Point.Empty, host.ClientSize);
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            host.Controls.Add(this);
            BringToFront();
            host.SetChrome(false);
            if (host.WindowState != FormWindowState.Normal) { var b = host.Bounds; host.WindowState = FormWindowState.Normal; host.Bounds = b; }
            anim = Anim.Enter; animClock.Restart();
            timer.Start();
            Focus();
        }

        /// <summary>Back into the radar and the window as it was.</summary>
        void Leave()
        {
            if (anim == Anim.Exit) return;
            closing = true; playing = null;
            Release();
            if (audio != null) { audio.Dispose(); audio = null; }
            ShowButtons(null, null);
            anim = Anim.Exit; animClock.Restart();
            timer.Start();
        }

        void Finish()
        {
            timer.Stop();
            host.Bounds = homeBounds;
            if (homeState == FormWindowState.Maximized)
            {
                host.WindowState = FormWindowState.Maximized;
                SetRestore(host, normalBounds);
            }
            host.SetChrome(true);
            host.Controls.Remove(this);
            snap.Dispose();
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
        static Rectangle Lerp(Rectangle a, Rectangle b, double t)
        {
            return Rectangle.FromLTRB((int)Math.Round(a.Left + (b.Left - a.Left) * t), (int)Math.Round(a.Top + (b.Top - a.Top) * t), (int)Math.Round(a.Right + (b.Right - a.Right) * t), (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t));
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

        void Animate()
        {
            double grow, zoom, cross, fly, reveal; Progress(out grow, out zoom, out cross, out fly, out reveal);
            double t = AnimTime;
            if (host != null && (anim == Anim.Enter ? t <= GrowS + 0.05 : t >= ExitLength - OutShrinkS - 0.02))
            {
                var b = Lerp(homeBounds, fullBounds, grow);
                if (host.Bounds != b) host.Bounds = b;
            }
            if (anim == Anim.Enter && t >= GrowS + ZoomS - CrossS * 0.6 + FlyS + RevealS)
            {
                anim = Anim.None;
                ShowButtons(phones != null ? "Start" : null, "Back to StreamLite");
                PlaceButtons();
            }
            else if (anim == Anim.Exit && t >= ExitLength) { Finish(); return; }
            Invalidate();
        }

        void PlaceButtons()
        {
            var card = CardRect();
            int y = card.Bottom - Theme.Px(60);
            if (!secondary.Visible) primary.Location = new Point(card.Left + (card.Width - primary.Width) / 2, y);
            else
            {
                primary.Location = new Point(card.Left + card.Width / 2 - primary.Width - Theme.Px(6), y);
                secondary.Location = new Point(card.Left + card.Width / 2 + Theme.Px(6), y);
                if (!primary.Visible) secondary.Location = new Point(card.Left + (card.Width - secondary.Width) / 2, y);
            }
        }

        void ShowButtons(string a, string b)
        {
            primary.Text = a ?? ""; primary.Visible = a != null; secondary.Text = b ?? ""; secondary.Visible = b != null;
            PlaceButtons();
        }

        // ------------------------------------------------------------------ flow

        void Primary()
        {
            if (paused) { Resume(); return; }
            switch (stage)
            {
                case Stage.Intro: StartRound1(); break;
                case Stage.Round1Done: StartRound2(); break;
                case Stage.Done: Leave(); break;
            }
        }

        void Secondary() { Leave(); }

        void EnsureAudio()
        {
            if (audio != null) return;
            audio = new StereoTimeline(phones);
            conductor = new Thread(Conduct) { IsBackground = true, Name = "Personalize 3D steps", Priority = ThreadPriority.AboveNormal };
            conductor.Start();
        }

        void StartRound1()
        {
            EnsureAudio();
            trials = Building3D.Round1(core.S.Game.FloorContrast, rnd, out variants);
            trialIdx = 0;
            Countdown(Stage.Listen, 5);
        }

        void Countdown(Stage next, int seconds)
        {
            ShowButtons(null, null);
            Capture();
            headline = next == Stage.Listen ? "Round 1" : "Round 2"; detail = "";
            stage = Stage.Countdown; afterCountdown = next; stageEnd = DateTime.Now.AddSeconds(seconds);
            Focus();
            Invalidate();
        }

        void BeginTrial()
        {
            var t = trials[trialIdx];
            reveal = null; lastAnswer = null;
            stage = Stage.Listen;
            headline = "Round 1  ·  " + (trialIdx + 1) + " of " + trials.Count;
            detail = "Look where the footsteps are and click.";
            Play(t.S, t.V);
            Invalidate();
        }

        void Answer()
        {
            if (stage != Stage.Listen) return;
            var t = trials[trialIdx];
            Stop();
            var a = Building3D.Aim(yaw, pitch);
            Building3D.Record(t, a);
            reveal = t.S; lastAnswer = a;
            stage = Stage.Feedback; stageEnd = DateTime.Now.AddSeconds(1.6);
            detail = t.Sc.Text;
            Invalidate();
        }

        void NextTrial()
        {
            trialIdx++;
            if (trialIdx < trials.Count) { BeginTrial(); return; }
            int cur = core.S.Game.FloorContrast;
            best = Building3D.Decide1(trials, core.S.Game.Hrtf, core.S.Game.CueSharpen, cur);
            floorsMixedUp = trials.Count(x => !x.Sc.FloorRight) >= trials.Count * 0.15;
            alt = Building3D.Alternative(best, floorsMixedUp);
            Apply(best);
            Release();
            stage = Stage.Round1Done;
            headline = "Round 1 done";
            int right = trials.Count(x => x.Sc.FloorRight), rooms = trials.Count(x => x.Sc.RoomRight), fb = trials.Count(x => x.Sc.FrontBack);
            var lines = new List<string>
            {
                "Right floor " + right + " of " + trials.Count + ", right room or direction " + rooms + " of " + trials.Count + (fb > 0 ? ", front and back swapped " + fb + "x" : "") + ".",
                ""
            };
            foreach (var v in variants) lines.Add("   " + v + ":  average error " + (v.Mean * 100).ToString("0") + (v.FloorWrong > 0 ? ", wrong floor " + v.FloorWrong + "x" : ""));
            lines.Add("");
            lines.Add("Now set for " + phonesName + ": " + best + ".");
            lines.Add("Round 2 (about a minute): footsteps walk - keep the crosshair on them. It compares this with " + (alt.Cue != best.Cue ? "cue sharpening " + alt.Cue + "% and " : "") + "floor contrast " + alt.Contrast + "%.");
            detail = string.Join("\n", lines);
            ShowButtons("Start round 2", "Finish");
            Invalidate();
        }

        void StartRound2()
        {
            walks = Building3D.Round2(best, alt, rnd); walkIdx = 0;
            Countdown(Stage.Track, 3);
        }

        void BeginWalk()
        {
            var w = walks[walkIdx];
            reveal = null; lastAnswer = null;
            stage = Stage.Track;
            headline = "Round 2  ·  walk " + (walkIdx + 1) + " of " + walks.Count;
            detail = "Keep the crosshair on the footsteps (" + Building3D.FloorNames[w.Floor] + ").";
            Play(w, w.V);
            Invalidate();
        }

        void EndWalk()
        {
            Stop();
            walkIdx++;
            if (walkIdx < walks.Count) { stage = Stage.Between; stageEnd = DateTime.Now.AddSeconds(1.5); detail = "Next walk..."; Invalidate(); return; }
            final = Building3D.Decide2(walks, best, alt);
            Apply(final);
            Release();
            stage = Stage.Done; headline = "All set";
            double a = MeanOf(best), b = MeanOf(alt);
            detail = "Following the walks you were on average " + a.ToString("0") + "° off with " + best + ",\nand " + b.ToString("0") + "° off with " + alt + ".\n\n" +
                "Saved for " + phonesName + ": " + final + ".\nIt applies to games' surround sound and to everything StreamLite places in 3D.";
            ShowButtons("Back to StreamLite", null);
            Invalidate();
        }

        double MeanOf(Building3D.Variant v) { double s = 0; int n = 0; foreach (var w in walks.Where(x => x.V == v)) { s += w.ErrSum; n += w.N; } return n == 0 ? 0 : s / n; }

        void Apply(Building3D.Variant v)
        {
            core.S.Game.Hrtf = v.Hrtf; core.S.Game.CueSharpen = v.Cue; core.S.Game.FloorContrast = v.Contrast;
            core.SavePersonal3D(phones, v.Hrtf, v.Cue, v.Contrast);
            core.PushGameParams();
            core.SaveSoon();
        }

        void Tick()
        {
            if (anim != Anim.None) { Animate(); return; }
            if (paused || closing) return;
            if ((stage == Stage.Feedback || stage == Stage.Listen) && trials == null) return;
            if ((stage == Stage.Track || stage == Stage.Between) && walks == null) return;
            if (stage == Stage.Countdown && (afterCountdown == Stage.Listen ? trials == null : walks == null)) return;
            var now = DateTime.Now;
            switch (stage)
            {
                case Stage.Countdown:
                    if (now >= stageEnd) { if (afterCountdown == Stage.Listen) BeginTrial(); else BeginWalk(); }
                    else Invalidate();
                    break;
                case Stage.Feedback:
                    if (now >= stageEnd) NextTrial();
                    break;
                case Stage.Between:
                    if (now >= stageEnd) BeginWalk();
                    break;
                case Stage.Track:
                    {
                        var w = walks[walkIdx];
                        double t = (audio.Now - playStart) / 48000.0 - 0.05;
                        if (t > Building3D.Walk.Seconds + 0.4) { EndWalk(); break; }
                        if (t > 1.0 && t <= Building3D.Walk.Seconds)
                        {
                            var look = Building3D.Eye + Building3D.Forward(yaw, pitch);
                            w.ErrSum += Building3D.AngleBetween(look, w.At(t)); w.N++;
                        }
                        Invalidate();   // the walking dot isn't drawn, but the time is
                        break;
                    }
            }
            if (audio != null && audio.Error != null && stage != Stage.Done && stage != Stage.Intro)
            {
                Stop(); Release(); stage = Stage.Done; headline = "Couldn't play"; detail = audio.Error; ShowButtons("Back to StreamLite", null); Invalidate();
            }
        }

        // ------------------------------------------------------------------ sound

        void Play(object what, Building3D.Variant v)
        {
            playVariant = v;
            playStart = audio.Now + 4800;
            playing = what;
        }

        void Stop() { playing = null; if (audio != null) audio.Clear(); }

        /// <summary>Audio thread: the next footstep, rendered for where your head points the moment it lands.</summary>
        void Conduct()
        {
            long next = -1; int step = 0; object cur = null; var jitter = new Random();
            while (!closing)
            {
                var what = playing; var au = audio;
                if (what == null || au == null) { next = -1; cur = null; Thread.Sleep(10); continue; }
                if (!ReferenceEquals(what, cur)) { cur = what; next = Interlocked.Read(ref playStart); }
                long now = au.Now;
                if (next - now < 4800)
                {
                    V pos; int floor;
                    var walk = what as Building3D.Walk;
                    if (walk != null) { pos = walk.At((next - playStart) / 48000.0); floor = walk.Floor; }
                    else { var s = (Building3D.Source)what; pos = s.Pos; floor = s.Floor; }
                    float[] l, r;
                    try { renderer.Render(playVariant, pos, floor, yaw, pitch, room, step++, out l, out r); au.Add(l, r, Math.Max(next, now + 960)); }
                    catch { }
                    next += walk != null ? 24000 : 22000 + jitter.Next(4000);
                }
                Thread.Sleep(4);
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
            if (stage == Stage.Intro || stage == Stage.Round1Done || stage == Stage.Done || paused || anim != Anim.None) return;
            paused = true; Release();
            if (playing != null) { pausedWhat = playing; Stop(); }
            ShowButtons("Resume", "Back to StreamLite");
            Invalidate();
        }

        void Resume()
        {
            paused = false;
            ShowButtons(null, null);
            Capture(); Focus();
            if (pausedWhat != null && (stage == Stage.Listen || stage == Stage.Track))
            {
                var w = pausedWhat; pausedWhat = null;
                var walk = w as Building3D.Walk; if (walk != null) { walk.ErrSum = 0; walk.N = 0; }
                Play(w, playVariant);
            }
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (captured && !NoMouse && !primary.Focused && !secondary.Focused) Pause(); }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Escape || base.IsInputKey(keyData); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!captured) return;
            var p = Cursor.Position;
            int dx = p.X - center.X, dy = p.Y - center.Y;
            if (dx == 0 && dy == 0) return;
            yaw = (yaw + dx * sens) % 360f;
            pitch = Math.Max(-89f, Math.Min(89f, pitch - dy * sens));
            Cursor.Position = center;
            hover = Building3D.Aim(yaw, pitch);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (paused || anim != Anim.None) return;
            Focus();
            if (!captured && (stage == Stage.Listen || stage == Stage.Track || stage == Stage.Countdown)) { Capture(); return; }
            if (e.Button == MouseButtons.Left && stage == Stage.Listen) Answer();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (anim != Anim.None) return;
            if (e.KeyCode == Keys.Escape) { if (paused) Leave(); else if (captured) Pause(); else Leave(); }
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) sens = Math.Min(0.6f, sens * 1.2f);
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) sens = Math.Max(0.03f, sens / 1.2f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { closing = true; timer.Stop(); timer.Dispose(); Release(); if (audio != null) { audio.Dispose(); audio = null; } }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ drawing: the radar, made into a building

        static readonly Color AboveCol = Color.FromArgb(110, 165, 240), LevelCol = Color.FromArgb(110, 210, 130), BelowCol = Color.FromArgb(235, 170, 90),
            Pillar = Color.FromArgb(120, 130, 140), Gold = Color.FromArgb(255, 196, 80), Wrong = Color.FromArgb(240, 96, 86);

        // The camera.
        V camEye, cf, cr, cu; float focal; PointF mid; float fade = 1;

        void Camera(V eye, double yawDeg, double pitchDeg, double fovDeg, Size size)
        {
            camEye = eye; cf = Building3D.Forward(yawDeg, pitchDeg); cr = Building3D.Right(yawDeg); cu = Building3D.Up(yawDeg, pitchDeg);
            mid = new PointF(size.Width / 2f, size.Height / 2f);
            focal = (float)(size.Height / 2.0 / Math.Tan(fovDeg / 2 * Math.PI / 180));
        }

        bool ToCam(V p, out double x, out double y, out double z) { V v = p - camEye; x = v.Dot(cr); y = v.Dot(cu); z = v.Dot(cf); return z > 0.05; }
        PointF Proj(double x, double y, double z) { return new PointF((float)(mid.X + x / z * focal), (float)(mid.Y - y / z * focal)); }

        /// <summary>One thing to draw: a translucent polygon or a line, sorted back to front by its distance.</summary>
        sealed class Prim { public V[] Pts; public Color C; public float Width; public bool Line; public double Dist; }
        readonly List<Prim> prims = new List<Prim>();

        void Fill(Color c, int alpha, params V[] pts) { if (alpha > 0) prims.Add(new Prim { Pts = pts, C = Color.FromArgb(Math.Min(255, (int)(alpha * fade)), c) }); }
        void Line(Color c, int alpha, float w, V a, V b) { if (alpha > 0) prims.Add(new Prim { Pts = new[] { a, b }, C = Color.FromArgb(Math.Min(255, (int)(alpha * fade)), c), Width = w, Line = true }); }

        void DrawPrim(Graphics g, Prim p)
        {
            var cam = new List<double[]>(p.Pts.Length);
            foreach (var q in p.Pts) { double x, y, z; ToCam(q, out x, out y, out z); cam.Add(new[] { x, y, z }); }
            const double near = 0.05;
            if (p.Line)
            {
                var a = cam[0]; var b = cam[1];
                if (a[2] < near && b[2] < near) return;
                if (a[2] < near) { double t = (near - a[2]) / (b[2] - a[2]); a = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near }; }
                if (b[2] < near) { double t = (near - b[2]) / (a[2] - b[2]); b = new[] { b[0] + (a[0] - b[0]) * t, b[1] + (a[1] - b[1]) * t, near }; }
                using (var pen = new Pen(p.C, p.Width)) g.DrawLine(pen, Proj(a[0], a[1], a[2]), Proj(b[0], b[1], b[2]));
                return;
            }
            var outp = new List<double[]>();
            for (int i = 0; i < cam.Count; i++)
            {
                var a = cam[i]; var b = cam[(i + 1) % cam.Count];
                bool ain = a[2] >= near, bin = b[2] >= near;
                if (ain) outp.Add(a);
                if (ain != bin) { double t = (near - a[2]) / (b[2] - a[2]); outp.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near }); }
            }
            if (outp.Count < 3) return;
            using (var br = new SolidBrush(p.C)) g.FillPolygon(br, outp.Select(q => Proj(q[0], q[1], q[2])).ToArray());
        }

        void Text(Graphics g, V p, string s, Font f, Color c, bool right = false)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            var q = Proj(x, y, z); var sz = g.MeasureString(s, f);
            using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, (int)(c.A * fade)), c))) g.DrawString(s, f, b, right ? q.X - sz.Width : q.X - sz.Width / 2, q.Y - sz.Height / 2);
        }

        /// <summary>The building in the radar's colours, from wherever the camera is.</summary>
        void Scene(Graphics g)
        {
            prims.Clear();
            double X = Building3D.HalfX, Z = Building3D.HalfZ, H = Building3D.Ceiling, ear = Building3D.Ear;
            double yTop = Building3D.StepY[2], yBot = Building3D.StepY[0];
            bool inside = Math.Abs(camEye.X) < X && Math.Abs(camEye.Z) < Z && camEye.Y > yBot && camEye.Y < yTop;
            // The floors above and below: a plane each with its 12 rooms (the radar's floors, as rooms).
            foreach (int floor in new[] { 0, 2 })
            {
                double y = floor == 2 ? yTop : yBot; var col = floor == 2 ? AboveCol : BelowCol;
                Fill(col, 34, new V(-X, y, -Z), new V(X, y, -Z), new V(X, y, Z), new V(-X, y, Z));
                for (int r = 0; r < 12; r++)
                {
                    double cx, cz; Building3D.RoomCenter(r, out cx, out cz); double hs = Building3D.RoomSize / 2 - 0.05;
                    int a = 52; Color c = col;
                    if (captured && stage == Stage.Listen && hover.Floor == floor && hover.Room == r) { a = 150; }
                    if (reveal.HasValue && reveal.Value.Floor == floor && reveal.Value.Room == r) { c = Gold; a = 190; }
                    else if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value.Floor == floor && lastAnswer.Value.Room == r) { c = Wrong; a = 170; }
                    Fill(c, a, new V(cx - hs, y, cz - hs), new V(cx + hs, y, cz - hs), new V(cx + hs, y, cz + hs), new V(cx - hs, y, cz + hs));
                }
                for (int i = 1; i < Building3D.Cols; i++) { double x = -X + i * Building3D.RoomSize; Line(col, 190, 1.4f, new V(x, y, -Z), new V(x, y, Z)); }
                for (int i = 1; i < Building3D.Rows; i++) { double z = -Z + i * Building3D.RoomSize; Line(col, 190, 1.4f, new V(-X, y, z), new V(X, y, z)); }
                foreach (var e in Edges(y)) Line(col, 230, 2f, e[0], e[1]);
            }
            // Your floor: the walls faintly, the ear-level band and line in green (the radar's ear level), and the
            // ear-level plane itself when you look at it from outside.
            var walls = Edges(0);
            foreach (var w in walls)
            {
                V a = w[0], b = w[1];
                Fill(LevelCol, 18, a, b, new V(b.X, H, b.Z), new V(a.X, H, a.Z));
                Fill(LevelCol, 64, new V(a.X, 1.3, a.Z), new V(b.X, 1.3, b.Z), new V(b.X, 1.9, b.Z), new V(a.X, 1.9, a.Z));
                Line(LevelCol, 220, 2f, new V(a.X, ear, a.Z), new V(b.X, ear, b.Z));
                Line(LevelCol, 70, 1f, new V(a.X, 0, a.Z), new V(b.X, 0, b.Z));
                Line(LevelCol, 70, 1f, new V(a.X, H, a.Z), new V(b.X, H, b.Z));
            }
            if (Math.Abs(camEye.Y - ear) > 0.08) Fill(LevelCol, inside ? 10 : 40, new V(-X, ear, -Z), new V(X, ear, -Z), new V(X, ear, Z), new V(-X, ear, Z));
            // The building's corners, top to bottom (the radar's pillars), and the line up and down through you.
            foreach (var c in new[] { new[] { -X, -Z }, new[] { X, -Z }, new[] { X, Z }, new[] { -X, Z } })
                Line(Pillar, 150, 1.2f, new V(c[0], yBot, c[1]), new V(c[0], yTop, c[1]));
            if (!inside || Math.Abs(camEye.X) > 0.3 || Math.Abs(camEye.Z) > 0.3) Line(AboveCol, 110, 1f, new V(0, yBot, 0), new V(0, yTop, 0));
            foreach (var p in prims) { double x, y, z; var m = Centroid(p.Pts); ToCam(m, out x, out y, out z); p.Dist = (m - camEye).Length; }
            foreach (var p in prims.OrderByDescending(q => q.Dist)) DrawPrim(g, p);

            // Where it really was (your floor): a gold dot where the steps were.
            if (reveal.HasValue && reveal.Value.Floor == 1) Dot(g, new V(reveal.Value.Pos.X, 0.02, reveal.Value.Pos.Z), Gold, 0.22);
            if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value.Floor == 1) Dot(g, lastAnswer.Value.Point, Wrong, 0.16);
            if (captured && stage == Stage.Listen && hover.Floor == 1) Dot(g, hover.Point, Gold, 0.12);
            // You, seen from outside: the radar's white dot (gone as you reach it).
            double toYou = (camEye - Building3D.Eye).Length;
            if (toYou > 5) { float keep = fade; fade *= (float)Smooth(5, 16, toYou); Dot(g, Building3D.Eye, Theme.Text, 0.25); fade = keep; }

            // Labels like the radar's.
            using (var f = new Font("Segoe UI Semibold", Theme.Small.Size * 1.15f))
            {
                Text(g, new V(-X - 0.2, yTop, -Z), "ABOVE", f, AboveCol, true);
                Text(g, new V(-X - 0.2, ear, -Z), "EAR LEVEL", f, LevelCol, true);
                Text(g, new V(-X - 0.2, yBot, -Z), "BELOW", f, BelowCol, true);
                Text(g, new V(0, 2.25, Z - 0.02), "FRONT", f, Color.FromArgb(220, LevelCol));
                Text(g, new V(0, 2.25, -Z + 0.02), "BEHIND", f, Color.FromArgb(220, LevelCol));
                Text(g, new V(-X + 0.02, 2.25, 0), "L", f, Color.FromArgb(220, LevelCol));
                Text(g, new V(X - 0.02, 2.25, 0), "R", f, Color.FromArgb(220, LevelCol));
                for (int r = 0; r < 12; r++)
                {
                    double cx, cz; Building3D.RoomCenter(r, out cx, out cz);
                    Text(g, new V(cx, yTop - 0.01, cz), (r + 1).ToString(), f, Color.FromArgb(200, AboveCol));
                    Text(g, new V(cx, yBot + 0.01, cz), (r + 1).ToString(), f, Color.FromArgb(200, BelowCol));
                }
            }
        }

        static V[][] Edges(double y)
        {
            double X = Building3D.HalfX, Z = Building3D.HalfZ;
            return new[] { new[] { new V(-X, y, Z), new V(X, y, Z) }, new[] { new V(X, y, Z), new V(X, y, -Z) }, new[] { new V(X, y, -Z), new V(-X, y, -Z) }, new[] { new V(-X, y, -Z), new V(-X, y, Z) } };
        }

        static V Centroid(V[] p) { double x = 0, y = 0, z = 0; foreach (var q in p) { x += q.X; y += q.Y; z += q.Z; } return new V(x / p.Length, y / p.Length, z / p.Length); }

        void Dot(Graphics g, V p, Color c, double radiusM)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            var q = Proj(x, y, z); float r = (float)Math.Max(4, Math.Min(26, radiusM / z * focal));
            using (var glow = new SolidBrush(Color.FromArgb((int)(60 * fade), c))) g.FillEllipse(glow, q.X - 2 * r, q.Y - 2 * r, 4 * r, 4 * r);
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * fade), c))) g.FillEllipse(b, q.X - r, q.Y - r, 2 * r, 2 * r);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Render(g, ClientSize);
        }

        /// <summary>Everything, for the current moment: the picture of StreamLite zooming into the radar, the building.</summary>
        internal void Render(Graphics g, Size size)
        {
            g.Clear(Theme.Bg);
            double grow, zoom, cross, fly, reveal01; Progress(out grow, out zoom, out cross, out fly, out reveal01);
            // The building: first as the radar shows it - from the radar's angle, far away and nearly flat, its floors as
            // big as the zoomed radar's - then flying in to your eyes.
            double u = fly;
            double fov0 = 10, cubeScreen = 0.30;
            double d0 = (size.Height / 2.0 / Math.Tan(fov0 / 2 * Math.PI / 180)) * 3.3 / (cubeScreen * size.Height);
            double dist = d0 * Math.Pow(1 - u, 2);
            double y0 = radarYaw, p0 = -radarPitch;
            double cy = y0 + (yaw - y0) * Ease(u), cp = p0 + (pitch - p0) * Ease(u);
            var f = Building3D.Forward(cy, cp);
            var target = new V(0, Building3D.Ear * Ease(u), 0);
            Camera(target - f * dist, cy, cp, fov0 + (75 - fov0) * Math.Pow(u, 1.5), size);
            fade = (float)cross;
            if (fade > 0.001f) Scene(g);
            fade = 1;
            // The picture of StreamLite, zooming into the radar's centre until the cube is about as big as the
            // building, then fading into it (and on, a little further, as you fly in).
            if (anim != Anim.None && snap != null && cross < 0.999)
            {
                double zMax = cubeScreen * size.Height / Math.Max(20, radarR);
                double z = (1 + (zMax - 1) * zoom) * (1 + 1.5 * u);
                float a = (float)(1 - cross);
                var origin = PointToScreenSafe();
                var c = new PointF(radarScreen.X - origin.X, radarScreen.Y - origin.Y);
                var to = new PointF(size.Width / 2f, size.Height / 2f);
                var cNow = new PointF((float)(c.X + (to.X - c.X) * zoom), (float)(c.Y + (to.Y - c.Y) * zoom));
                var topLeft = new PointF(snapScreen.X - origin.X, snapScreen.Y - origin.Y);
                var dest = new RectangleF((float)(cNow.X + (topLeft.X - c.X) * z), (float)(cNow.Y + (topLeft.Y - c.Y) * z), (float)(snap.Width * z), (float)(snap.Height * z));
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = a });
                    var old = g.InterpolationMode; g.InterpolationMode = z > 1.01 ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
                    g.DrawImage(snap, Rectangle.Round(dest), 0, 0, snap.Width, snap.Height, GraphicsUnit.Pixel, ia);
                    g.InterpolationMode = old;
                }
            }            if (anim == Anim.None || anim == Anim.Enter && reveal01 > 0) Hud(g, size, anim == Anim.None ? 1f : (float)reveal01);
        }

        Point PointToScreenSafe() { try { return IsHandleCreated ? PointToScreen(Point.Empty) : (host != null ? host.PointToScreen(Point.Empty) : Point.Empty); } catch { return Point.Empty; } }

        Rectangle CardRect()
        {
            int w = Math.Min(Theme.Px(760), ClientSize.Width - Theme.Px(80)), h = Theme.Px(stage == Stage.Round1Done || stage == Stage.Done ? 430 : 470);
            if (paused) h = Theme.Px(200);
            return new Rectangle((ClientSize.Width - w) / 2, Math.Max(Theme.Px(40), (ClientSize.Height - h) / 2), w, h);
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        static Font countFont;

        /// <summary>The cards and lines over the building, in StreamLite's own look.</summary>
        void Hud(Graphics g, Size size, float alpha)
        {
            Func<Color, Color> A = c => Color.FromArgb((int)(c.A * alpha), c);
            bool card = stage == Stage.Intro || stage == Stage.Round1Done || stage == Stage.Done || paused;
            if (card)
            {
                using (var shade = new SolidBrush(Color.FromArgb((int)(150 * alpha), 8, 9, 11))) g.FillRectangle(shade, 0, 0, size.Width, size.Height);
                var r = CardRect();
                using (var path = Rounded(r, Theme.Px(14)))
                {
                    using (var b = new SolidBrush(A(Color.FromArgb(244, Theme.Card)))) g.FillPath(b, path);
                    using (var pen = new Pen(A(Theme.Track), 1.2f)) g.DrawPath(pen, path);
                }
                using (var accent = new SolidBrush(A(Theme.Accent))) g.FillRectangle(accent, r.Left + Theme.Px(28), r.Top + Theme.Px(30), Theme.Px(4), Theme.Px(26));
                string h = paused ? "Paused" : headline;
                string d = paused ? "Resume to carry on - this footstep plays again." : detail;
                using (var tb = new SolidBrush(A(Theme.Text))) g.DrawString(h, Theme.Title, tb, r.Left + Theme.Px(42), r.Top + Theme.Px(26));
                using (var mb = new SolidBrush(A(Color.FromArgb(225, Theme.Text))))
                    g.DrawString(d, Theme.Body, mb, new RectangleF(r.Left + Theme.Px(30), r.Top + Theme.Px(78), r.Width - Theme.Px(60), r.Height - Theme.Px(150)));
                if (stage == Stage.Intro && !paused)
                    using (var sb = new SolidBrush(A(Theme.Muted))) g.DrawString("Headphones: " + phonesName, Theme.Small, sb, r.Left + Theme.Px(30), r.Bottom - Theme.Px(92));
                return;
            }
            // Playing: the round in a pill at the top left, what to do at the bottom, the crosshair.
            var hs = g.MeasureString(headline, Theme.Section);
            var pill = new Rectangle(Theme.Px(20), Theme.Px(18), (int)hs.Width + Theme.Px(28), Theme.Px(36));
            using (var path = Rounded(pill, Theme.Px(18)))
            using (var b = new SolidBrush(Color.FromArgb(200, Theme.Card))) g.FillPath(b, path);
            using (var tb = new SolidBrush(Theme.Text)) g.DrawString(headline, Theme.Section, tb, pill.Left + Theme.Px(14), pill.Top + (pill.Height - hs.Height) / 2);
            if (!string.IsNullOrEmpty(detail))
            {
                var ds = g.MeasureString(detail, Theme.Body);
                var bar = new Rectangle((int)((size.Width - ds.Width) / 2) - Theme.Px(18), size.Height - Theme.Px(70), (int)ds.Width + Theme.Px(36), Theme.Px(38));
                using (var path = Rounded(bar, Theme.Px(19)))
                using (var b = new SolidBrush(Color.FromArgb(200, Theme.Card))) g.FillPath(b, path);
                Color dc = stage == Stage.Feedback && lastAnswer.HasValue && trials != null && trialIdx < trials.Count && trials[trialIdx].Sc != null ? (trials[trialIdx].Sc.FloorRight ? Theme.Accent : Theme.Warn) : Theme.Text;
                using (var tb = new SolidBrush(dc)) g.DrawString(detail, Theme.Body, tb, bar.Left + Theme.Px(18), bar.Top + (bar.Height - ds.Height) / 2);
            }
            if (stage == Stage.Countdown)
            {
                if (countFont == null) countFont = new Font("Segoe UI Semibold", 72f);
                int left = Math.Max(1, (int)Math.Ceiling((stageEnd - DateTime.Now).TotalSeconds));
                var s = left.ToString(); var sz = g.MeasureString(s, countFont);
                float rr = Math.Max(sz.Width, sz.Height) * 0.62f;
                using (var ring = new SolidBrush(Color.FromArgb(170, Theme.Card))) g.FillEllipse(ring, size.Width / 2f - rr, size.Height / 2f - rr - Theme.Px(40), 2 * rr, 2 * rr);
                using (var pen = new Pen(Theme.Accent, Theme.Px(3))) g.DrawEllipse(pen, size.Width / 2f - rr, size.Height / 2f - rr - Theme.Px(40), 2 * rr, 2 * rr);
                using (var tb = new SolidBrush(Theme.Text)) g.DrawString(s, countFont, tb, (size.Width - sz.Width) / 2, size.Height / 2f - sz.Height / 2 - Theme.Px(40));
                var hint = afterCountdown == Stage.Listen ? "Get ready - look around to get used to the room." : "Get ready to follow the footsteps.";
                var hz = g.MeasureString(hint, Theme.Body);
                using (var tb = new SolidBrush(Theme.Muted)) g.DrawString(hint, Theme.Body, tb, (size.Width - hz.Width) / 2, size.Height / 2f + rr - Theme.Px(20));
            }
            if (captured || NoMouse)
                using (var p = new Pen(Color.FromArgb(230, Theme.Text), 2f))
                {
                    float m = Theme.Px(9), gap = Theme.Px(3);
                    g.DrawLine(p, mid.X - m, mid.Y, mid.X - gap, mid.Y); g.DrawLine(p, mid.X + gap, mid.Y, mid.X + m, mid.Y);
                    g.DrawLine(p, mid.X, mid.Y - m, mid.X, mid.Y - gap); g.DrawLine(p, mid.X, mid.Y + gap, mid.X, mid.Y + m);
                }
        }
    }
}
