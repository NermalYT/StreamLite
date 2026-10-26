using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// Footsteps play at exact spots; you look where you hear them and click (or say you can't tell, and it shows you).
    /// 1. Find your head: every measured head (two studio dummy heads, 18 real people), blind, narrowed down round by
    ///    round to the one your ears place sounds with best.
    /// 2. Tune it: your head size, cue sharpening and floor contrast.
    /// 3. Follow footsteps that walk, the winner against the runner-up.
    /// Saved per pair of headphones after each part. Leaving flies back out into the radar.
    /// Drawn on the graphics card on every frame of the monitor (SceneHost); test pictures use GDI+.
    /// </summary>
    internal sealed class Personal3DView : Control
    {
        enum Stage { Intro, Loading, Countdown, Listen, Feedback, Break, Track, Between, Done }
        enum Anim { None, Enter, Exit }
        enum Round { Search1, Search2, Search3, Tune, Walks, Practice }

        // The radar it flies out of: a picture of the window, the screen behind it, where the radar's centre was, its
        // size and how it's turned.
        readonly Bitmap snap, desk;
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
        Round round;
        List<Building3D.Variant> heads, pool;
        List<Building3D.Trial> trials; int trialIdx;
        Building3D.Tuning tuning;
        Building3D.Variant winner, runnerUp, tuned, alt, final;
        List<Building3D.Walk> walks; int walkIdx;
        int doneSteps, totalSteps;
        volatile object playing; volatile Building3D.Variant playVariant; long playStart;
        Building3D.Answer hover;
        Building3D.Source? reveal; Building3D.Answer? lastAnswer;
        bool cantTell;
        readonly double room;
        object pausedWhat;
        volatile int loaded, toLoad;
        /// <summary>Bars on the result card: a head's name and its average error (0..1).</summary>
        List<KeyValuePair<string, double>> chart;
        string chartWinner;

        // In: the window grows, zooms into the radar, the radar's picture becomes the building, you fly in, the card.
        const double GrowS = 0.42, ZoomS = 0.75, CrossS = 0.35, FlyS = 1.35, RevealS = 0.35;
        // Out: fly out, the building becomes the radar's picture, zoom out, the window shrinks.
        const double OutFlyS = 0.95, OutCrossS = 0.3, OutZoomS = 0.5, OutShrinkS = 0.38;

        /// <summary>Opens it over StreamLite's window, from the radar (onClosed: refresh the page afterwards).</summary>
        public static void Open(AppCore core, SoundRadarView radar, Action onClosed)
        {
            var form = radar.FindForm() as MainForm;
            if (form == null || form.Controls.OfType<Personal3DView>().Any()) return;
            // The window as it looks now (drawn by the window itself: a screen copy would also catch anything lying on
            // top of it), the screen it's on (what's around it while it grows), and the radar's centre on screen.
            var r = form.Bounds;
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            try { form.DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height)); }
            catch { using (var g = Graphics.FromImage(bmp)) g.Clear(Theme.Bg); }
            var full = Screen.FromRectangle(r).Bounds;
            Bitmap desk = null;
            try { desk = new Bitmap(full.Width, full.Height); using (var g = Graphics.FromImage(desk)) g.CopyFromScreen(full.Location, Point.Empty, full.Size); }
            catch { if (desk != null) desk.Dispose(); desk = null; }
            var c = radar.PointToScreen(Point.Round(radar.Centre));
            var v = new Personal3DView(core, bmp, desk, r, full, c, radar.Radius, radar.ViewYaw, radar.ViewPitch, form, onClosed);
            v.Begin();
        }

        internal Personal3DView(AppCore core, Bitmap snap, Bitmap desk, Rectangle snapScreen, Rectangle full, PointF radarScreen, float radarR, double radarYaw, double radarPitch, MainForm host, Action onClosed)
        {
            this.core = core; this.snap = snap; this.desk = desk; this.snapScreen = snapScreen; this.radarScreen = radarScreen; this.radarR = radarR;
            this.radarYaw = radarYaw; this.radarPitch = radarPitch; this.host = host; this.onClosed = onClosed;
            homeBounds = snapScreen; fullBounds = full;
            phones = core.GamePhonesId ?? core.RealDefaultOutput();
            var d = phones != null ? core.Outputs().FirstOrDefault(o => o.Id == phones) : null;
            phonesName = d != null ? d.Name : "your headphones";
            room = core.S.Game.ToConfig().Room;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Controls.Add(primary); Controls.Add(secondary);
            primary.Width = Theme.Px(210); secondary.Width = Theme.Px(210);
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Secondary();
            Resize += (s, e) => PlaceButtons();
            ShowButtons(null, null);
            int n = HrtfSet.Heads().Count;
            headline = "Personalize 3D";
            detail = "Put on " + phonesName + " and pause music and games. You stand in the middle of the radar, made into a building, and can only look around (mouse).\n\n" +
                "Footsteps play somewhere. Look where you hear them and click:\n" +
                "   above (blue): look up and click the room they're in\n" +
                "   below (orange): look down and click the room\n" +
                "   your floor (green): click the wall in their direction, on the green band\n" +
                "Can't tell? Right-click (or Space): it shows you where they were and plays them there.\n\n" +
                "1.  Find your head: " + n + " measured heads" + (n > 2 ? " - 2 studio dummy heads and " + (n - 2) + " real people -" : "") + " tried blind and narrowed down to the one your ears place sounds with best (about 6 minutes).\n" +
                "2.  Tune it: your head size, cue sharpening and floor contrast (about 3 minutes).\n" +
                "3.  Follow footsteps that walk: the winner against the runner-up (about a minute).\n\n" +
                "Saved for these headphones after every part.   Esc: pause.   + / - : mouse speed.";
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
            }
            scene = new SceneHost(snap, desk) { Dock = DockStyle.Fill };
            scene.Paint = c => Render(c, ClientSize);
            scene.Frame += OnFrame;
            scene.Moved += Look;
            scene.Down += Click;
            scene.Key += Key;
            Controls.Add(scene);
            primary.BringToFront(); secondary.BringToFront();
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
        int coverFrames;

        /// <summary>The graphics card has drawn a few frames: the picture in front goes, and the animation starts from 0.</summary>
        void DropCover()
        {
            if (cover == null) return;
            if (++coverFrames < 3) { animClock.Restart(); return; }
            var c = cover; cover = null;
            var pic = c.BackgroundImage;
            c.Close(); c.Dispose(); if (pic != null) pic.Dispose();
            if (host != null) host.Activate();
            if (scene != null) scene.FocusScene();
        }

        void HostDeactivate(object s, EventArgs e) { if (captured && !NoMouse && cover == null) Pause(); }

        /// <summary>Back into the radar and the window as it was.</summary>
        void Leave()
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
        double lastFrameMs = -1;

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
            lastFrameMs = nowMs;
            DropCover();
            if (anim != Anim.None)
            {
                double t = AnimTime;
                if (anim == Anim.Enter && t >= GrowS + ZoomS - CrossS * 0.6 + FlyS + RevealS)
                {
                    anim = Anim.None;
                    ShowButtons(phones != null ? "Start" : null, "Back to StreamLite");
                }
                else if (anim == Anim.Exit && t >= ExitLength) { Finish(); return; }
                Redraw();
                return;
            }
            Tick();
            // Looking around: every frame of the monitor (WPF would otherwise idle at ~60 Hz between changes).
            if (captured) Redraw();
        }

        void Redraw() { if (scene != null) scene.Dirty = true; else Invalidate(); }

        Rectangle cardRect;

        void PlaceButtons()
        {
            var card = CardRect();
            int y = card.Bottom - Theme.Px(62);
            if (!secondary.Visible) primary.Location = new Point(card.Left + (card.Width - primary.Width) / 2, y);
            else
            {
                primary.Location = new Point(card.Left + card.Width / 2 - primary.Width - Theme.Px(6), y);
                secondary.Location = new Point(card.Left + card.Width / 2 + Theme.Px(6), y);
                if (!primary.Visible) secondary.Location = new Point(card.Left + (card.Width - secondary.Width) / 2, y);
            }
        }

        double buttonsAt = -1e9;
        void ShowButtons(string a, string b)
        {
            if (scene != null) buttonsAt = scene.Clock.Elapsed.TotalMilliseconds;
            primary.Text = a ?? ""; primary.Visible = a != null; secondary.Text = b ?? ""; secondary.Visible = b != null;
            PlaceButtons();
            if (a != null || b != null) { primary.BringToFront(); secondary.BringToFront(); }
            Redraw();
        }

        // ------------------------------------------------------------------ flow

        void Primary()
        {
            if (paused) { Resume(); return; }
            switch (stage)
            {
                case Stage.Intro: Start(); break;
                case Stage.Break: Next(); break;
                case Stage.Done: Leave(); break;
            }
        }

        void Secondary()
        {
            if (stage == Stage.Done && !paused) { StartPractice(); return; }
            Leave();
        }

        void EnsureAudio()
        {
            if (audio != null) return;
            audio = new StereoTimeline(phones);
            conductor = new Thread(Conduct) { IsBackground = true, Name = "Personalize 3D steps", Priority = ThreadPriority.AboveNormal };
            conductor.Start();
        }

        Building3D.Variant Current { get { var g = core.S.Game; return new Building3D.Variant { Hrtf = g.Hrtf, Cue = g.CueSharpen, Contrast = g.FloorContrast, HeadSize = g.HeadSize }; } }

        void Start()
        {
            EnsureAudio();
            var cur = Current;
            heads = HrtfSet.Heads().Select(h => new Building3D.Variant { Hrtf = h, Cue = cur.Cue, Contrast = cur.Contrast }).ToList();
            totalSteps = heads.Count * Building3D.Search1.Length + Math.Min(heads.Count, Building3D.Keep1) * Building3D.Search2.Length
                + Math.Min(heads.Count, Building3D.Keep2) * Building3D.Search3.Length + 45 + 6;
            doneSteps = 0;
            // Every head ready before the first footstep (the full measurements take a moment each).
            Load(heads.Select(h => h.Hrtf).ToList(), 1.0);
            BeginRound(Round.Search1);
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

        string RoundName(Round r)
        {
            switch (r)
            {
                case Round.Search1: case Round.Search2: case Round.Search3: return "Find your head  ·  round " + ((int)r + 1) + " of 3";
                case Round.Tune: return "Tune it";
                case Round.Walks: return "Follow the footsteps";
                default: return "Practice";
            }
        }

        void BeginRound(Round r)
        {
            round = r;
            switch (r)
            {
                case Round.Search1: pool = heads; trials = Building3D.HeadRound(pool, Building3D.Search1, rnd, "search1"); break;
                case Round.Search2:
                    pool = Building3D.Keep(heads, Building3D.Keep1);
                    HrtfSet.Forget(pool.Select(p => p.Hrtf).Concat(new[] { core.S.Game.Hrtf }).ToArray());
                    trials = Building3D.HeadRound(pool, Building3D.Search2, rnd, "search2"); break;
                case Round.Search3: pool = Building3D.Keep(pool, Building3D.Keep2); trials = Building3D.HeadRound(pool, Building3D.Search3, rnd, "search3"); break;
                case Round.Tune: trials = Building3D.TuneRound(winner, rnd, out tuning); break;
                case Round.Walks: walks = Building3D.Walks(tuned, alt, rnd); walkIdx = 0; break;
            }
            trialIdx = 0;
            Countdown(r == Round.Walks ? Stage.Track : Stage.Listen, r == Round.Search1 ? 5 : 3);
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
            stage = Stage.Listen;
            headline = RoundName(round) + (round == Round.Practice ? "" : "  ·  " + (trialIdx + 1) + " of " + trials.Count);
            detail = "Look where the footsteps are and click.     Can't tell?  Right-click or Space.";
            Play(t.S, t.V);
            Redraw();
        }

        void Answer()
        {
            if (stage != Stage.Listen) return;
            var t = trials[trialIdx];
            Stop();
            var a = Building3D.Aim(yaw, pitch);
            if (round == Round.Practice) t.Sc = Building3D.Judge(a, t.S); else Building3D.Record(t, a);
            reveal = t.S; lastAnswer = a; cantTell = false;
            stage = Stage.Feedback; stageEnd = DateTime.Now.AddSeconds(round == Round.Practice ? 2.0 : 1.3);
            detail = t.Sc.Text;
            Redraw();
        }

        /// <summary>"Can't tell": scored as a miss for this setting, then shown where it was and played there.</summary>
        void CantTell()
        {
            if (stage != Stage.Listen) return;
            var t = trials[trialIdx];
            if (round == Round.Practice) t.Sc = new Building3D.Score { CantTell = true, Text = "It was " + Building3D.Describe(t.S) + " - listen." };
            else Building3D.RecordCantTell(t);
            reveal = t.S; lastAnswer = null; cantTell = true;
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
            if (trialIdx < trials.Count) { BeginTrial(); return; }
            RoundDone();
        }

        /// <summary>The card between rounds: what was found, and what's next.</summary>
        void RoundDone()
        {
            Release();
            stage = Stage.Break; chart = null; chartWinner = null;
            var lines = new List<string>();
            switch (round)
            {
                case Round.Search1:
                    {
                        var keep = Building3D.Keep(heads, Building3D.Keep1);
                        headline = "Round 1 done";
                        lines.Add("All " + heads.Count + " heads heard " + Building3D.Search1.Length + " footsteps each. The " + keep.Count + " you placed best go on:");
                        lines.Add("");
                        foreach (var h in keep) lines.Add("   " + h.HeadName + "  -  average error " + Pct(h.Mean) + Misses(h));
                        lines.Add("");
                        lines.Add("Round 2: " + Building3D.Search2.Length + " footsteps each for these " + keep.Count + ".");
                        Chart(heads, null);
                        break;
                    }
                case Round.Search2:
                    {
                        var keep = Building3D.Keep(pool, Building3D.Keep2);
                        headline = "Round 2 done";
                        lines.Add(keep.Count + " left: " + string.Join(", ", keep.Select(h => h.HeadName)) + ".");
                        lines.Add("");
                        foreach (var h in pool.OrderBy(p => p.Mean)) lines.Add("   " + h.HeadName + "  -  average error " + Pct(h.Mean) + Misses(h));
                        lines.Add("");
                        lines.Add("Round 3: " + Building3D.Search3.Length + " footsteps each, head to head.");
                        Chart(pool, null);
                        break;
                    }
                case Round.Search3:
                    {
                        var order = Building3D.Keep(pool, pool.Count);
                        winner = order[0]; runnerUp = order.Count > 1 ? order[1] : order[0];
                        Apply(winner);
                        headline = "Your head: " + winner.HeadName;
                        lines.Add("Out of " + heads.Count + " heads, " + winner.HeadName + " let you place footsteps best (average error " + Pct(winner.Mean) + "; " + runnerUp.HeadName + " " + Pct(runnerUp.Mean) + ").");
                        lines.Add("Saved for " + phonesName + ".");
                        lines.Add("");
                        lines.Add("Next: tune it - your head size (the time between your ears), cue sharpening (front/back, up/down) and floor contrast, each tried at three settings.");
                        Chart(heads, winner.HeadName);
                        // The head-size versions of the winner, ready before the round starts.
                        Load(new List<string> { winner.Hrtf }, 0.88, 1.12);
                        break;
                    }
                case Round.Tune:
                    {
                        tuned = Building3D.DecideTune(winner, tuning);
                        alt = runnerUp.With(cue: tuned.Cue, contrast: tuned.Contrast, headSize: tuned.HeadSize);
                        Apply(tuned);
                        headline = "Tuned";
                        lines.Add("Head size " + tuned.HeadSize + "%" + Why(tuning.Size, v => v.HeadSize + "%") + ".");
                        lines.Add("Cue sharpening " + tuned.Cue + "%" + Why(tuning.Cue, v => v.Cue + "%") + ".");
                        lines.Add("Floor contrast " + tuned.Contrast + "%" + Why(tuning.Contrast, v => v.Contrast + "%") + ".");
                        lines.Add("Saved for " + phonesName + ".");
                        lines.Add("");
                        lines.Add("Last: footsteps walk - keep the crosshair on them. It compares your setup with the runner-up head (" + runnerUp.HeadName + ") tuned the same way.");
                        Load(new List<string> { alt.Hrtf }, alt.HeadSize / 100.0);
                        break;
                    }
            }
            detail = string.Join("\n", lines);
            ShowButtons("Continue", "Stop here");
            Redraw();
        }

        static string Pct(double mean) { return mean == double.MaxValue ? "-" : (mean * 100).ToString("0"); }
        static string Misses(Building3D.Variant v)
        {
            var p = new List<string>();
            if (v.FloorWrong > 0) p.Add("wrong floor " + v.FloorWrong + "x");
            if (v.FrontBack > 0) p.Add("front/back swapped " + v.FrontBack + "x");
            if (v.CantTell > 0) p.Add("can't tell " + v.CantTell + "x");
            return p.Count == 0 ? "" : " (" + string.Join(", ", p) + ")";
        }
        static string Why(List<Building3D.Variant> vs, Func<Building3D.Variant, string> name)
        {
            return "  (" + string.Join(", ", vs.Select(v => name(v) + ": " + Pct(v.Mean))) + ")";
        }

        void Chart(IEnumerable<Building3D.Variant> vs, string win)
        {
            var top = vs.Where(v => v.N > 0).OrderBy(v => v.Mean).Take(10).ToList();
            var w = vs.FirstOrDefault(v => v.HeadName == win && v.N > 0);
            if (w != null && !top.Contains(w)) { top[top.Count - 1] = w; }
            chart = top.Select(v => new KeyValuePair<string, double>(v.HeadName, v.Mean)).ToList();
            chartWinner = win;
        }

        void Next()
        {
            switch (round)
            {
                case Round.Search1: BeginRound(Round.Search2); break;
                case Round.Search2: BeginRound(Round.Search3); break;
                case Round.Search3: BeginRound(Round.Tune); break;
                case Round.Tune: BeginRound(Round.Walks); break;
            }
        }

        void BeginWalk()
        {
            var w = walks[walkIdx];
            reveal = null; lastAnswer = null;
            stage = Stage.Track;
            headline = RoundName(round) + "  ·  walk " + (walkIdx + 1) + " of " + walks.Count;
            detail = "Keep the crosshair on the footsteps (" + Building3D.FloorNames[w.Floor] + ").";
            Play(w, w.V);
            Redraw();
        }

        void EndWalk()
        {
            Stop();
            walkIdx++; doneSteps++;
            if (walkIdx < walks.Count) { stage = Stage.Between; stageEnd = DateTime.Now.AddSeconds(1.5); detail = "Next walk..."; Redraw(); return; }
            final = Building3D.DecideWalks(walks, tuned, alt);
            Apply(final);
            Release();
            stage = Stage.Done; headline = "All set: " + final.HeadName;
            double a = Building3D.Mean(walks.Where(x => x.V == tuned)), b = Building3D.Mean(walks.Where(x => x.V == alt));
            var all = heads.Where(h => h.N > 0).ToList();
            int floorsRight = 0, floorSteps = 0, fb = 0, unsure = 0;
            foreach (var h in all) { fb += h.FrontBack; unsure += h.CantTell; }
            detail = "Your 3D setup for " + phonesName + ":\n" +
                "   " + final.HeadName + (final.Hrtf.StartsWith(HrtfSet.HumanPrefix) ? " (a real person's measured head)" : "") + ", head size " + final.HeadSize + "%, cue sharpening " + final.Cue + "%, floor contrast " + final.Contrast + "%.\n\n" +
                "Following the walks you were on average " + a.ToString("0") + "° off with it" + (Math.Abs(b - a) > 0.5 ? ", " + b.ToString("0") + "° with " + alt.HeadName : "") + ".\n" +
                "Out of " + heads.Count + " heads, the best let you place footsteps with " + Pct(all.Min(h => h.Mean)) + " average error, the worst " + Pct(all.Max(h => h.Mean)) + ".\n\n" +
                "It applies to games' surround sound and everything StreamLite places in 3D. Practice: footsteps anywhere, shown right after you answer.";
            Chart(heads, final.HeadName);
            ShowButtons("Back to StreamLite", "Practice");
            Redraw();
        }

        // ------------------------------------------------------------------ practice: footsteps anywhere, shown after every answer

        void StartPractice()
        {
            EnsureAudio();
            round = Round.Practice;
            trials = new List<Building3D.Trial> { PracticeTrial() };
            trialIdx = 0;
            Countdown(Stage.Listen, 2);
        }

        Building3D.Trial PracticeTrial()
        {
            var kinds = (Building3D.Kind[])Enum.GetValues(typeof(Building3D.Kind));
            var k = kinds[rnd.Next(kinds.Length)];
            return new Building3D.Trial { V = Current, S = Building3D.SourceFor(k, rnd), K = k, Stage = "practice" };
        }

        void Apply(Building3D.Variant v)
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
            if ((stage == Stage.Track || stage == Stage.Between) && walks == null) return;
            var now = DateTime.Now;
            switch (stage)
            {
                case Stage.Countdown:
                    if (now >= stageEnd && loaded >= toLoad) { if (afterCountdown == Stage.Listen) BeginTrial(); else BeginWalk(); }
                    Redraw();
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
                        Redraw();
                        break;
                    }
            }
            if (audio != null && audio.Error != null && stage != Stage.Done && stage != Stage.Intro)
            {
                Stop(); Release(); stage = Stage.Done; headline = "Couldn't play"; detail = audio.Error; chart = null; ShowButtons("Back to StreamLite", null); Redraw();
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
            if (stage == Stage.Intro || stage == Stage.Break || stage == Stage.Done || paused || anim != Anim.None) return;
            paused = true; Release();
            if (playing != null) { pausedWhat = playing; Stop(); }
            ShowButtons("Resume", "Back to StreamLite");
            Redraw();
        }

        void Resume()
        {
            paused = false;
            ShowButtons(null, null);
            Capture(); if (scene != null) scene.FocusScene();
            if (pausedWhat != null && (stage == Stage.Listen || stage == Stage.Track || stage == Stage.Feedback))
            {
                var w = pausedWhat; pausedWhat = null;
                var walk = w as Building3D.Walk; if (walk != null) { walk.ErrSum = 0; walk.N = 0; }
                Play(w, playVariant);
            }
            Redraw();
        }

        /// <summary>The mouse moved: turn your head by how far it went from the middle, and put it back there.</summary>
        void Look()
        {
            if (!captured) return;
            var p = Cursor.Position;
            int dx = p.X - center.X, dy = p.Y - center.Y;
            if (dx == 0 && dy == 0) return;
            yaw = (yaw + dx * sens) % 360f;
            pitch = Math.Max(-89f, Math.Min(89f, pitch - dy * sens));
            Cursor.Position = center;
            hover = Building3D.Aim(yaw, pitch);
            Redraw();
        }

        void Click(MouseButtons b)
        {
            if (paused || anim != Anim.None) return;
            if (!captured && (stage == Stage.Listen || stage == Stage.Track || stage == Stage.Countdown)) { Capture(); return; }
            if (b == MouseButtons.Left && stage == Stage.Listen) Answer();
            else if (b == MouseButtons.Right && stage == Stage.Listen) CantTell();
        }

        void Key(Keys k)
        {
            if (anim != Anim.None) return;
            if (k == Keys.Escape)
            {
                if (paused) Leave();
                else if (round == Round.Practice && stage != Stage.Done && captured) { Stop(); Release(); stage = Stage.Done; headline = "All set"; ShowButtons("Back to StreamLite", "Practice"); }
                else if (captured) Pause(); else Leave();
            }
            else if (k == Keys.Space) CantTell();
            else if (k == Keys.Oemplus || k == Keys.Add) sens = Math.Min(0.6f, sens * 1.2f);
            else if (k == Keys.OemMinus || k == Keys.Subtract) sens = Math.Max(0.03f, sens / 1.2f);
            Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Look(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); Click(e.Button); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Escape || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); Key(e.KeyCode); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { closing = true; Release(); if (audio != null) { audio.Dispose(); audio = null; } if (scene != null) { scene.Dispose(); scene = null; } }
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

        static Color Fade(Color c, double a) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)a)), c); }
        void Fill(Color c, int alpha, params V[] pts) { if (alpha > 0) prims.Add(new Prim { Pts = pts, C = Fade(c, alpha * fade) }); }
        void Line(Color c, int alpha, float w, V a, V b) { if (alpha > 0) prims.Add(new Prim { Pts = new[] { a, b }, C = Fade(c, alpha * fade), Width = w, Line = true }); }

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

        void Text(ICanvas g, V p, string s, Color c, bool right = false)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            if (labelFont == null) labelFont = new Font("Segoe UI Semibold", Theme.Small.Size * 1.15f);
            var q = Proj(x, y, z); var sz = g.Measure(s, labelFont);
            g.Text(s, labelFont, Fade(c, c.A * fade), new PointF(right ? q.X - sz.Width : q.X - sz.Width / 2, q.Y - sz.Height / 2));
        }

        /// <summary>The building in the radar's colours, from wherever the camera is.</summary>
        void Scene(ICanvas g)
        {
            prims.Clear();
            double X = Building3D.HalfX, Z = Building3D.HalfZ, H = Building3D.Ceiling, ear = Building3D.Ear;
            double yTop = Building3D.StepY[2], yBot = Building3D.StepY[0];
            bool inside = Math.Abs(camEye.X) < X && Math.Abs(camEye.Z) < Z && camEye.Y > yBot && camEye.Y < yTop;
            bool aiming = captured && stage == Stage.Listen;
            // The floors above and below: a plane each with its 16 rooms (the radar's floors, as rooms).
            foreach (int floor in new[] { 0, 2 })
            {
                double y = floor == 2 ? yTop : yBot; var col = floor == 2 ? AboveCol : BelowCol;
                Fill(col, 30, new V(-X, y, -Z), new V(X, y, -Z), new V(X, y, Z), new V(-X, y, Z));
                for (int r = 0; r < Building3D.Rooms; r++)
                {
                    double cx, cz; Building3D.RoomCenter(r, out cx, out cz); double hs = Building3D.RoomSize / 2 - 0.05;
                    int a = 46; Color c = col;
                    if (aiming && hover.Floor == floor && hover.Room == r) { a = 150; }
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
            foreach (var w in Edges(0))
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
            // Where it really was: a gold beam from you to the spot (what "can't tell" shows).
            if (reveal.HasValue) Line(Gold, cantTell ? 230 : 150, cantTell ? 3f : 2f, Building3D.Eye + new V(0, -0.05, 0), new V(reveal.Value.Pos.X, reveal.Value.Pos.Y + 0.02, reveal.Value.Pos.Z));
            foreach (var p in prims) { var m = Centroid(p.Pts); p.Dist = (m - camEye).Length; }
            prims.Sort((a, b) => b.Dist.CompareTo(a.Dist));
            foreach (var p in prims) DrawPrim(g, p);

            // Where it really was: a gold dot where the steps were (pulsing while they play again), your answer in red.
            if (reveal.HasValue)
            {
                double pulse = cantTell ? 1 + 0.25 * Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 9) : 1;
                Dot(g, new V(reveal.Value.Pos.X, reveal.Value.Pos.Y + 0.02, reveal.Value.Pos.Z), Gold, 0.24 * pulse);
                if (cantTell) Redraw();
            }
            if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value.Floor == 1) Dot(g, lastAnswer.Value.Point, Wrong, 0.16);
            if (aiming && hover.Floor == 1) Dot(g, hover.Point, Gold, 0.12);
            // You, seen from outside: the radar's white dot (gone as you reach it).
            double toYou = (camEye - Building3D.Eye).Length;
            if (toYou > 5) { float keep = fade; fade *= (float)Smooth(5, 16, toYou); Dot(g, Building3D.Eye, Theme.Text, 0.25); fade = keep; }

            // Labels like the radar's.
            Text(g, new V(-X - 0.2, yTop, -Z), "ABOVE", AboveCol, true);
            Text(g, new V(-X - 0.2, ear, -Z), "EAR LEVEL", LevelCol, true);
            Text(g, new V(-X - 0.2, yBot, -Z), "BELOW", BelowCol, true);
            Text(g, new V(0, 2.25, Z - 0.02), "FRONT", Color.FromArgb(220, LevelCol));
            Text(g, new V(0, 2.25, -Z + 0.02), "BEHIND", Color.FromArgb(220, LevelCol));
            Text(g, new V(-X + 0.02, 2.25, 0), "L", Color.FromArgb(220, LevelCol));
            Text(g, new V(X - 0.02, 2.25, 0), "R", Color.FromArgb(220, LevelCol));
            for (int r = 0; r < Building3D.Rooms; r++)
            {
                double cx, cz; Building3D.RoomCenter(r, out cx, out cz);
                Text(g, new V(cx, yTop - 0.01, cz), (r + 1).ToString(), Color.FromArgb(200, AboveCol));
                Text(g, new V(cx, yBot + 0.01, cz), (r + 1).ToString(), Color.FromArgb(200, BelowCol));
            }
        }

        static V[][] Edges(double y)
        {
            double X = Building3D.HalfX, Z = Building3D.HalfZ;
            return new[] { new[] { new V(-X, y, Z), new V(X, y, Z) }, new[] { new V(X, y, Z), new V(X, y, -Z) }, new[] { new V(X, y, -Z), new V(-X, y, -Z) }, new[] { new V(-X, y, -Z), new V(-X, y, Z) } };
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

        /// <summary>Everything, for the current moment: the window growing, zooming into the radar, the building.</summary>
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
            if (animating && cross < 0.999)
            {
                double zMax = cubeScreen * size.Height / Math.Max(20, radarR);
                double z = (1 + (zMax - 1) * zoom) * (1 + 1.5 * u);
                var c = new PointF(radarScreen.X - origin.X, radarScreen.Y - origin.Y);
                var to = new PointF(size.Width / 2f, size.Height / 2f);
                var cNow = new PointF((float)(c.X + (to.X - c.X) * zoom), (float)(c.Y + (to.Y - c.Y) * zoom));
                var topLeft = new PointF(snapScreen.X - origin.X, snapScreen.Y - origin.Y);
                var dest = new RectangleF((float)(cNow.X + (topLeft.X - c.X) * z), (float)(cNow.Y + (topLeft.Y - c.Y) * z), (float)(snap.Width * z), (float)(snap.Height * z));
                g.Picture(0, dest, (float)(1 - cross));
            }
            if (anim == Anim.None || anim == Anim.Enter && reveal01 > 0) Hud(g, size, anim == Anim.None ? 1f : (float)reveal01);
        }

        bool CardStage { get { return stage == Stage.Intro || stage == Stage.Break || stage == Stage.Done || paused; } }

        int ChartHeight { get { return chart != null && chart.Count > 0 && !paused ? Theme.Px(26) * chart.Count + Theme.Px(20) : 0; } }

        Rectangle CardRect()
        {
            int w = Math.Min(Theme.Px(860), Math.Max(Theme.Px(300), ClientSize.Width - Theme.Px(80)));
            string d = paused ? PausedText : detail;
            int textH = TextRenderer.MeasureText(d ?? "", Theme.Body, new Size(w - Theme.Px(60), 0), TextFormatFlags.WordBreak).Height;
            int h = Theme.Px(78) + textH + Theme.Px(24) + ChartHeight + (stage == Stage.Intro && !paused ? Theme.Px(30) : 0) + Theme.Px(80);
            h = Math.Min(h, Math.Max(Theme.Px(200), ClientSize.Height - Theme.Px(40)));
            return cardRect = new Rectangle((ClientSize.Width - w) / 2, Math.Max(Theme.Px(20), (ClientSize.Height - h) / 2), w, h);
        }

        const string PausedText = "Resume to carry on - this footstep plays again.";

        /// <summary>The cards and lines over the building, in StreamLite's own look.</summary>
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
                if (ChartHeight > 0) Bars(g, new RectangleF(r.Left + Theme.Px(30), box.Bottom + Theme.Px(6), r.Width - Theme.Px(60), ChartHeight - Theme.Px(10)), alpha);
                if (stage == Stage.Intro && !paused) g.Text("Headphones: " + phonesName, Theme.Small, A(Theme.Muted), new PointF(r.Left + Theme.Px(30), r.Bottom - Theme.Px(100)));
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
                    dc = cantTell ? Gold : trials[trialIdx].Sc.FloorRight && trials[trialIdx].Sc.Error < 0.34 ? Theme.Accent : Theme.Warn;
                g.Text(detail, Theme.Body, dc, new PointF(bar.Left + Theme.Px(18), bar.Top + (bar.Height - ds.Height) / 2));
            }
            if (stage == Stage.Countdown && DateTime.Now < stageEnd)
            {
                if (countFont == null) countFont = new Font("Segoe UI Semibold", 72f);
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
                var hint = afterCountdown == Stage.Listen ? (round == Round.Search1 ? "Get ready - look around to get used to the room." : "Get ready.") : "Get ready to follow the footsteps.";
                var hz = g.Measure(hint, Theme.Body);
                g.Text(hint, Theme.Body, Theme.Muted, new PointF((size.Width - hz.Width) / 2, ring.Bottom + Theme.Px(20)));
            }
            if (reveal.HasValue && stage == Stage.Feedback) Pointer(g, size, new V(reveal.Value.Pos.X, reveal.Value.Pos.Y + 0.02, reveal.Value.Pos.Z), Gold);
            if (captured || NoMouse)
            {
                var col = Color.FromArgb(230, Theme.Text);
                float m = Theme.Px(9), gap = Theme.Px(3);
                g.Line(col, 2f, new PointF(mid.X - m, mid.Y), new PointF(mid.X - gap, mid.Y)); g.Line(col, 2f, new PointF(mid.X + gap, mid.Y), new PointF(mid.X + m, mid.Y));
                g.Line(col, 2f, new PointF(mid.X, mid.Y - m), new PointF(mid.X, mid.Y - gap)); g.Line(col, 2f, new PointF(mid.X, mid.Y + gap), new PointF(mid.X, mid.Y + m));
            }
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

        /// <summary>Each head's average error as a bar (shorter = better), the winner in green.</summary>
        void Bars(ICanvas g, RectangleF box, float alpha)
        {
            if (chart == null || chart.Count == 0) return;
            Func<Color, Color> A = c => Color.FromArgb((int)(c.A * alpha), c);
            float row = Theme.Px(26), nameW = Theme.Px(170);
            double worst = Math.Max(0.05, chart.Max(c => c.Value == double.MaxValue ? 0 : c.Value));
            for (int i = 0; i < chart.Count; i++)
            {
                float y = box.Top + i * row;
                bool win = chart[i].Key == chartWinner;
                g.Text(chart[i].Key, win ? Theme.Bold : Theme.Small, A(win ? Theme.Accent : Theme.Muted), new PointF(box.Left, y + Theme.Px(3)));
                float bw = (float)((box.Width - nameW - Theme.Px(60)) * Math.Min(1, chart[i].Value / worst));
                g.Rounded(A(win ? Theme.Accent : Color.FromArgb(150, Theme.Track)), Color.Empty, 0, new RectangleF(box.Left + nameW, y + Theme.Px(6), Math.Max(Theme.Px(4), bw), row - Theme.Px(12)), Theme.Px(4));
                g.Text(Pct(chart[i].Value), Theme.Small, A(Theme.Muted), new PointF(box.Left + nameW + bw + Theme.Px(8), y + Theme.Px(3)));
            }
        }
    }
}
