using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using StreamLite.Audio;
using V = StreamLite.Audio.Building3D.Vec;

namespace StreamLite.UI
{
    /// <summary>
    /// "Personalize 3D": a small first-person room (Building3D) where you can only look around. Footsteps play at exact
    /// spots on the floor above, your floor or the floor below; you look where you hear them and click. Round 1: still
    /// footsteps, blind, with the head models and cue sharpening mixed in - the best becomes your setting. Round 2: you
    /// follow footsteps that walk, with the winner against one alternative. Saved per pair of headphones.
    /// Drawn with plain flat polygons, only when something changes - next to no CPU or GPU.
    /// </summary>
    internal sealed class Personal3DWorld : Form
    {
        enum Stage { Intro, Countdown, Listen, Feedback, Round1Done, Track, Between, Done }

        readonly AppCore core;
        readonly string phones, phonesName;
        readonly StepRenderer renderer = new StepRenderer();
        readonly Random rnd = new Random();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly FlatButton primary = new FlatButton("Start", true), secondary = new FlatButton("Close");
        StereoTimeline audio;
        Thread conductor;
        volatile bool closing;

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
        // What plays now: a still source or a walk, with which setting, from which frame.
        volatile object playing; volatile Building3D.Variant playVariant; long playStart;
        Building3D.Answer hover;
        // Shown after a click: where it really was.
        Building3D.Source? reveal; Building3D.Answer? lastAnswer;
        readonly double room;

        public Personal3DWorld(AppCore core)
        {
            this.core = core;
            phones = core.GamePhonesId ?? core.RealDefaultOutput();
            var d = phones != null ? core.Outputs().FirstOrDefault(o => o.Id == phones) : null;
            phonesName = d != null ? d.Name : "your headphones";
            room = core.S.Game.ToConfig().Room;
            Text = "StreamLite - Personalize 3D";
            BackColor = Color.FromArgb(18, 20, 24);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.Px(1280), Theme.Px(800));
            WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Controls.Add(primary); Controls.Add(secondary);
            primary.Width = Theme.Px(220); secondary.Width = Theme.Px(160);
            primary.Click += (s, e) => Primary();
            secondary.Click += (s, e) => Close();
            Layout += (s, e) => PlaceButtons();
            timer.Tick += (s, e) => Tick();
            headline = "Personalize 3D";
            detail = "Put on " + phonesName + " and pause music and games. You stand in the middle of a building and can only look around (mouse).\n\n" +
                "Footsteps play somewhere - upstairs, on your floor or downstairs. Look where you hear them and click:\n" +
                "   upstairs: look up through the glass ceiling and click the room they're in\n" +
                "   downstairs: look down through the glass floor and click the room\n" +
                "   your floor: click the wall in their direction (the band at eye height)\n\n" +
                "Round 1: 24 footsteps standing still (about 4 minutes). Round 2: follow walking footsteps with the crosshair (about a minute).\n" +
                "StreamLite tries its head models and cue settings blind and keeps whatever lets you place sounds best, for these headphones.\n\n" +
                "Esc: pause.   + / - : mouse speed.";
            if (phones == null) { detail = "No headphones found."; primary.Enabled = false; }
            FormClosing += (s, e) => { closing = true; Release(); timer.Stop(); if (audio != null) audio.Dispose(); };
            Deactivate += (s, e) => { if (captured && !NoMouse) Pause(); };
            timer.Start();
        }

        void PlaceButtons()
        {
            int y = ClientSize.Height - Theme.Px(90);
            primary.Location = new Point(ClientSize.Width / 2 - primary.Width - Theme.Px(8), y);
            secondary.Location = new Point(ClientSize.Width / 2 + Theme.Px(8), y);
        }

        void ShowButtons(string a, string b) { primary.Text = a; primary.Visible = a != null; secondary.Text = b; secondary.Visible = b != null; }

        // ------------------------------------------------------------------ flow

        void Primary()
        {
            if (paused) { Resume(); return; }
            switch (stage)
            {
                case Stage.Intro: StartRound1(); break;
                case Stage.Round1Done: StartRound2(); break;
                case Stage.Done: Close(); break;
            }
        }

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
            Invalidate();
        }

        void BeginTrial()
        {
            var t = trials[trialIdx];
            reveal = null; lastAnswer = null;
            stage = Stage.Listen;
            headline = "Round 1 - " + (trialIdx + 1) + " of " + trials.Count;
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
            // Round 1 done: the best setting now, and round 2 ready.
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
                "Right floor " + right + " of " + trials.Count + ", right room / direction " + rooms + " of " + trials.Count + (fb > 0 ? ", front and back swapped " + fb + "x" : "") + ".",
                ""
            };
            foreach (var v in variants) lines.Add("  " + v + ":  average error " + (v.Mean * 100).ToString("0") + (v.FloorWrong > 0 ? ", wrong floor " + v.FloorWrong + "x" : ""));
            lines.Add("");
            lines.Add("Now set for " + phonesName + ": " + best + ".");
            lines.Add("Round 2 (about 1 minute): footsteps walk - keep the crosshair on them. It compares this with " + (alt.Cue != best.Cue ? "cue sharpening " + alt.Cue + "%" : "") + " and floor contrast " + alt.Contrast + "%.");
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
            headline = "Round 2 - walk " + (walkIdx + 1) + " of " + walks.Count;
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
            ShowButtons("Close", null);
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
            if (paused || closing) return;
            // Nothing to step through (only the intro): nothing to advance.
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
                        // Where the steps are now, as heard (the audio runs ~50 ms behind what's scheduled).
                        double t = (audio.Now - playStart) / 48000.0 - 0.05;
                        if (t > Building3D.Walk.Seconds + 0.4) { EndWalk(); break; }
                        if (t > 1.0 && t <= Building3D.Walk.Seconds)
                        {
                            var look = Building3D.Eye + Building3D.Forward(yaw, pitch);
                            w.ErrSum += Building3D.AngleBetween(look, w.At(t)); w.N++;
                        }
                        break;
                    }
            }
            if (audio != null && audio.Error != null && stage != Stage.Done && stage != Stage.Intro)
            {
                Stop(); Release(); stage = Stage.Done; headline = "Couldn't play"; detail = audio.Error; ShowButtons("Close", null); Invalidate();
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
                var what = playing;
                if (what == null || audio == null) { next = -1; cur = null; Thread.Sleep(10); continue; }
                if (!ReferenceEquals(what, cur)) { cur = what; next = Interlocked.Read(ref playStart); }
                long now = audio.Now;
                if (next - now < 4800)
                {
                    Building3D.Vec pos; int floor;
                    var walk = what as Building3D.Walk;
                    if (walk != null) { pos = walk.At((next - playStart) / 48000.0); floor = walk.Floor; }
                    else { var s = (Building3D.Source)what; pos = s.Pos; floor = s.Floor; }
                    float[] l, r;
                    try { renderer.Render(playVariant, pos, floor, yaw, pitch, room, step++, out l, out r); audio.Add(l, r, Math.Max(next, now + 960)); }
                    catch { }
                    // Still footsteps: someone stepping in place, a step every half second (a little uneven, like a person).
                    next += walk != null ? 24000 : 22000 + jitter.Next(4000);
                }
                Thread.Sleep(4);
            }
        }

        // ------------------------------------------------------------------ mouse look

        void Capture()
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
            if (stage == Stage.Intro || stage == Stage.Round1Done || stage == Stage.Done || paused) return;
            paused = true; Release();
            if (playing != null) { pausedWhat = playing; Stop(); }
            ShowButtons("Resume", "Quit");
            Invalidate();
        }
        object pausedWhat;

        void Resume()
        {
            paused = false;
            ShowButtons(null, null);
            Capture();
            if (pausedWhat != null && (stage == Stage.Listen || stage == Stage.Track)) { var w = pausedWhat; pausedWhat = null; if (w is Building3D.Walk) { ((Building3D.Walk)w).ErrSum = 0; ((Building3D.Walk)w).N = 0; } Play(w, playVariant); }
            Invalidate();
        }

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
            if (paused) return;
            if (!captured && (stage == Stage.Listen || stage == Stage.Track || stage == Stage.Countdown)) { Capture(); return; }
            if (e.Button == MouseButtons.Left && stage == Stage.Listen) Answer();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { if (paused) Close(); else if (captured) Pause(); else Close(); }
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) sens = Math.Min(0.6f, sens * 1.2f);
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) sens = Math.Max(0.03f, sens / 1.2f);
        }

        // ------------------------------------------------------------------ drawing

        static readonly Color Wall = Color.FromArgb(196, 202, 210), WallBand = Color.FromArgb(160, 168, 180), Border = Color.FromArgb(40, 44, 52),
            OtherWall = Color.FromArgb(92, 98, 110), Glass = Color.FromArgb(46, 170, 210, 255), Rim = Color.FromArgb(230, 240, 245, 250),
            FloorA = Color.FromArgb(112, 168, 96), FloorB = Color.FromArgb(98, 150, 84), CeilA = Color.FromArgb(150, 180, 220), CeilB = Color.FromArgb(132, 164, 206),
            Hover = Color.FromArgb(255, 214, 70), Truth = Color.FromArgb(60, 220, 120), Wrong = Color.FromArgb(240, 90, 80);

        float focal; PointF mid; V cf, cr, cu;

        bool ToCam(V p, out double x, out double y, out double z)
        {
            V v = p - Building3D.Eye; x = v.Dot(cr); y = v.Dot(cu); z = v.Dot(cf);
            return z > 0.05;
        }

        PointF Proj(double x, double y, double z) { return new PointF((float)(mid.X + x / z * focal), (float)(mid.Y - y / z * focal)); }

        /// <summary>A flat polygon in the world, clipped at the near plane.</summary>
        void Poly(Graphics g, Color c, params V[] pts)
        {
            var cam = pts.Select(p => { double x, y, z; ToCam(p, out x, out y, out z); return new[] { x, y, z }; }).ToList();
            var outp = new List<double[]>();
            const double near = 0.05;
            for (int i = 0; i < cam.Count; i++)
            {
                var a = cam[i]; var b = cam[(i + 1) % cam.Count];
                bool ain = a[2] >= near, bin = b[2] >= near;
                if (ain) outp.Add(a);
                if (ain != bin) { double t = (near - a[2]) / (b[2] - a[2]); outp.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near }); }
            }
            if (outp.Count < 3) return;
            var sp = outp.Select(q => Proj(q[0], q[1], q[2])).ToArray();
            using (var br = new SolidBrush(c)) g.FillPolygon(br, sp);
        }

        /// <summary>A line in the world (both ends in front of you; clipped at the near plane otherwise).</summary>
        void Line(Graphics g, Color c, V a, V b)
        {
            double ax, ay, az, bx, by, bz;
            bool ain = ToCam(a, out ax, out ay, out az), bin = ToCam(b, out bx, out by, out bz);
            if (!ain && !bin) return;
            const double near = 0.05;
            if (!ain) { double t = (near - az) / (bz - az); ax += (bx - ax) * t; ay += (by - ay) * t; az = near; }
            if (!bin) { double t = (near - bz) / (az - bz); bx += (ax - bx) * t; by += (ay - by) * t; bz = near; }
            using (var p = new Pen(c, 3f)) g.DrawLine(p, Proj(ax, ay, az), Proj(bx, by, bz));
        }

        void Label(Graphics g, V p, string s, Font f, Color c)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            var q = Proj(x, y, z); var sz = g.MeasureString(s, f);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, q.X - sz.Width / 2, q.Y - sz.Height / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            mid = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
            focal = (float)(ClientSize.Height / 2.0 / Math.Tan(37.5 * Math.PI / 180));
            cf = Building3D.Forward(yaw, pitch); cr = Building3D.Right(yaw); cu = Building3D.Up(yaw, pitch);
            double X = Building3D.HalfX, Z = Building3D.HalfZ, H = Building3D.Ceiling, band0 = 1.3, band1 = 1.9;
            double yTop = Building3D.StepY[2], yBot = Building3D.StepY[0];
            // The outer walls of all three floors: yours bright with a band at ear height (aim there for your floor),
            // the floors above and below darker.
            var walls = new[] { new[] { new V(-X, 0, Z), new V(X, 0, Z) }, new[] { new V(X, 0, Z), new V(X, 0, -Z) }, new[] { new V(X, 0, -Z), new V(-X, 0, -Z) }, new[] { new V(-X, 0, -Z), new V(-X, 0, Z) } };
            foreach (var w in walls)
            {
                V a = w[0], b = w[1];
                Poly(g, OtherWall, new V(a.X, yBot, a.Z), new V(b.X, yBot, b.Z), new V(b.X, 0, b.Z), new V(a.X, 0, a.Z));
                Poly(g, OtherWall, new V(a.X, H, a.Z), new V(b.X, H, b.Z), new V(b.X, yTop, b.Z), new V(a.X, yTop, a.Z));
                Poly(g, Wall, a, b, new V(b.X, H, b.Z), new V(a.X, H, a.Z));
                Poly(g, WallBand, new V(a.X, band0, a.Z), new V(b.X, band0, b.Z), new V(b.X, band1, b.Z), new V(a.X, band1, a.Z));
            }
            // The rooms above and below, where their floors really are (you see their outlines through your glass
            // ceiling and floor), numbered.
            Poly(g, Border, new V(-X, yTop, -Z), new V(X, yTop, -Z), new V(X, yTop, Z), new V(-X, yTop, Z));
            Poly(g, Border, new V(-X, yBot, -Z), new V(X, yBot, -Z), new V(X, yBot, Z), new V(-X, yBot, Z));
            double hs = Building3D.RoomSize / 2 - 0.09;
            for (int r = 0; r < 12; r++)
            {
                double cx, cz; Building3D.RoomCenter(r, out cx, out cz);
                bool alt = ((r % 4) + r / 4) % 2 == 0;
                foreach (int floor in new[] { 0, 2 })
                {
                    double y = floor == 2 ? yTop : yBot;
                    Color c = floor == 2 ? (alt ? CeilA : CeilB) : (alt ? FloorA : FloorB);
                    if (captured && hover.Floor == floor && hover.Room == r && stage == Stage.Listen) c = Hover;
                    if (reveal.HasValue && reveal.Value.Floor == floor && reveal.Value.Room == r) c = Truth;
                    else if (reveal.HasValue && lastAnswer.HasValue && lastAnswer.Value.Floor == floor && lastAnswer.Value.Room == r) c = Wrong;
                    Poly(g, c, new V(cx - hs, y, cz - hs), new V(cx + hs, y, cz - hs), new V(cx + hs, y, cz + hs), new V(cx - hs, y, cz + hs));
                }
            }
            using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                for (int r = 0; r < 12; r++)
                {
                    double cx, cz; Building3D.RoomCenter(r, out cx, out cz);
                    Label(g, new V(cx, yTop - 0.01, cz), (r + 1).ToString(), f, Color.FromArgb(170, 30, 40, 60));
                    Label(g, new V(cx, yBot + 0.01, cz), (r + 1).ToString(), f, Color.FromArgb(170, 20, 40, 20));
                }
            // Your floor and ceiling: glass with a rim.
            Poly(g, Glass, new V(-X, 0, -Z), new V(X, 0, -Z), new V(X, 0, Z), new V(-X, 0, Z));
            Poly(g, Glass, new V(-X, H, -Z), new V(X, H, -Z), new V(X, H, Z), new V(-X, H, Z));
            foreach (var w in walls)
            {
                Line(g, Rim, new V(w[0].X, 0, w[0].Z), new V(w[1].X, 0, w[1].Z));
                Line(g, Rim, new V(w[0].X, H, w[0].Z), new V(w[1].X, H, w[1].Z));
            }            using (var f = new Font("Segoe UI", 16f, FontStyle.Bold))
            {
                Label(g, new V(0, 2.4, Z - 0.01), "FRONT", f, Color.FromArgb(90, 20, 30, 40));
                Label(g, new V(0, 2.4, -Z + 0.01), "BACK", f, Color.FromArgb(90, 20, 30, 40));
                Label(g, new V(-X + 0.01, 2.4, 0), "LEFT", f, Color.FromArgb(90, 20, 30, 40));
                Label(g, new V(X - 0.01, 2.4, 0), "RIGHT", f, Color.FromArgb(90, 20, 30, 40));
            }
            // Your floor: where you aim on the wall, where it really was.
            if (captured && stage == Stage.Listen && hover.Floor == 1) Marker(g, hover.Point, Hover);
            if (reveal.HasValue && reveal.Value.Floor == 1)
            {
                var s = reveal.Value.Pos;
                Marker(g, new V(s.X, 0.02, s.Z), Truth);
                if (lastAnswer.HasValue && lastAnswer.Value.Floor == 1) Marker(g, lastAnswer.Value.Point, Wrong);
            }
            if (reveal.HasValue && reveal.Value.Floor != 1 && lastAnswer.HasValue && lastAnswer.Value.Floor == 1) Marker(g, lastAnswer.Value.Point, Wrong);
            // Crosshair.
            if (captured)
                using (var p = new Pen(Color.White, 2f))
                {
                    g.DrawLine(p, mid.X - 10, mid.Y, mid.X + 10, mid.Y); g.DrawLine(p, mid.X, mid.Y - 10, mid.X, mid.Y + 10);
                }
            Hud(g);
        }

        void Marker(Graphics g, V p, Color c)
        {
            double x, y, z; if (!ToCam(p, out x, out y, out z)) return;
            var q = Proj(x, y, z); float r = (float)Math.Max(6, Math.Min(30, 0.25 / z * focal));
            using (var b = new SolidBrush(c)) g.FillEllipse(b, q.X - r, q.Y - r, 2 * r, 2 * r);
            using (var pen = new Pen(Color.FromArgb(30, 30, 30), 2f)) g.DrawEllipse(pen, q.X - r, q.Y - r, 2 * r, 2 * r);
        }

        void Hud(Graphics g)
        {
            bool overlay = stage == Stage.Intro || stage == Stage.Round1Done || stage == Stage.Done || paused;
            if (overlay)
                using (var b = new SolidBrush(Color.FromArgb(200, 12, 14, 18))) g.FillRectangle(b, ClientRectangle);
            using (var big = new Font("Segoe UI", 26f, FontStyle.Bold))
            using (var body = new Font("Segoe UI", 13f))
            using (var white = new SolidBrush(Color.White))
            using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            {
                if (overlay)
                {
                    string h = paused ? "Paused" : headline, d = paused ? "Resume to carry on (this footstep plays again), or quit." : detail;
                    var hs = g.MeasureString(h, big);
                    float top = ClientSize.Height * 0.18f;
                    g.DrawString(h, big, white, (ClientSize.Width - hs.Width) / 2, top);
                    var ds = g.MeasureString(d, body, ClientSize.Width - 160);
                    g.DrawString(d, body, white, new RectangleF((ClientSize.Width - ds.Width) / 2, top + hs.Height + 24, ds.Width + 4, ds.Height + 8));
                    return;
                }
                // In the building: the round at the top, what to do at the bottom.
                Shadowed(g, headline, body, white, shadow, 20, 16);
                var dsz = g.MeasureString(detail, body);
                Shadowed(g, detail, body, white, shadow, (ClientSize.Width - dsz.Width) / 2, ClientSize.Height - 60);
                if (stage == Stage.Countdown)
                {
                    int left = Math.Max(1, (int)Math.Ceiling((stageEnd - DateTime.Now).TotalSeconds));
                    using (var huge = new Font("Segoe UI", 96f, FontStyle.Bold))
                    {
                        var s = left.ToString(); var sz = g.MeasureString(s, huge);
                        Shadowed(g, s, huge, white, shadow, (ClientSize.Width - sz.Width) / 2, (ClientSize.Height - sz.Height) / 2 - 80);
                    }
                    var hint = afterCountdown == Stage.Listen ? "Get ready - footsteps are coming. Look around to get used to the room." : "Get ready to follow the footsteps.";
                    var hz = g.MeasureString(hint, body);
                    Shadowed(g, hint, body, white, shadow, (ClientSize.Width - hz.Width) / 2, ClientSize.Height / 2 + 40);
                }
            }
        }

        static void Shadowed(Graphics g, string s, Font f, Brush b, Brush sh, float x, float y) { g.DrawString(s, f, sh, x + 2, y + 2); g.DrawString(s, f, b, x, y); }
    }
}
