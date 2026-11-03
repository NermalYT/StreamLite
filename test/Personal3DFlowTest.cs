// Personalize 3D (the sphere) from the first card to "All set", both parts, answered by a simulated player (clicks, "can't tell",
// snapping the view with the keys, a pause, the buttons, Enter, Esc in practice), with no sound, no mouse and no devices (settings in a temp folder).
// Usage: Personal3DFlowTest
using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

class Personal3DFlowTest
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static Personal3DView view;
    static object Get(string name) { return typeof(Personal3DView).GetField(name, Any).GetValue(view); }
    static void Set(string name, object v) { typeof(Personal3DView).GetField(name, Any).SetValue(view, v); }
    static object Call(string name, params object[] args) { return typeof(Personal3DView).GetMethod(name, Any).Invoke(view, args); }
    static string Stage { get { return Get("stage").ToString(); } }
    static string Round { get { return Get("round").ToString(); } }
    static void Typed(object[] a) { typeof(Personal3DView).GetMethod("TrackNext", Any).Invoke(view, a); }

    [STAThread]
    static void Main(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-flowtest");
        Directory.CreateDirectory(data);
        foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore(true);
        var snap = new Bitmap(1280, 860); var desk = new Bitmap(1600, 1000);
        // (no window around it: the flow never gets as far as giving the window back)
        view = new Personal3DView(core, snap, desk, new Rectangle(160, 70, 1280, 860), new Rectangle(0, 0, 1600, 1000), new PointF(760, 470), 200, 0, 30, null, null) { NoMouse = true, NoAudio = true };
        view.Bounds = new Rectangle(0, 0, 1600, 1000);
        var rnd = new Random(5);
        var phones = (string)Get("phones");
        Console.WriteLine("Headphones: " + (phones ?? "(none on this PC: the flow still runs)"));

        Check(Stage == "Intro" && (string)Get("primaryText") == null, "the first card (its buttons come once the way in has played)");
        Call("ShowButtons", "Start", "Back to StreamLite");
        Call("Key", Keys.Enter);   // Enter = the green button
        Check(Round == "Search" && Stage == "Countdown", "Enter: the test starts with a countdown");
        // Wait for the heads (the countdown would).
        var t0 = DateTime.Now;
        while ((int)Get("loaded") < (int)Get("toLoad") && (DateTime.Now - t0).TotalSeconds < 120) System.Threading.Thread.Sleep(50);
        Check((int)Get("loaded") >= (int)Get("toLoad"), "all " + Get("toLoad") + " heads loaded in " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + " s");

        // The view snaps between the six directions (arrow keys; the mouse does the same).
        Set("captured", true);
        Func<string> facing = () => Get("facing").ToString();
        Call("Key", Keys.Up); bool upOk = facing() == "Up";
        Call("Key", Keys.Down); bool backOk = facing() == "Front";
        Call("Key", Keys.Right); bool rightOk = facing() == "Right";
        Call("Key", Keys.Down); bool downOk = facing() == "Down";
        Call("Key", Keys.Left); bool stayOk = facing() == "Down";
        Call("Key", Keys.Up); bool levelOk = facing() == "Right";
        Check(upOk && backOk && rightOk && downOk && stayOk && levelOk, "arrow keys snap the view: up, back level, right, down, back to the right");
        for (int i = 0; i < 30; i++) Call("EaseView", 16.7);   // half a second of frames
        Check(Math.Abs((float)Get("yaw") - 90) < 0.5 && Math.Abs((float)Get("pitch")) < 0.5, "the camera turns to the direction it faces (yaw " + ((float)Get("yaw")).ToString("0") + ")");

        int answered = 0, unsure = 0, notFront = 0;
        bool paused = false;
        var keys = new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.A, Keys.D, Keys.W, Keys.S };
        foreach (var r in new[] { "Search" })
        {
            Check(Round == r, "part " + r);
            var trials = (IList)Get("trials");
            Call("BeginTrial");
            int guard = 0;
            while (Stage != "Break" && Stage != "Done" && guard++ < 1000)
            {
                if (Stage != "Listen") break;
                // Snap somewhere, then click - or "can't tell" now and then; one pause and resume on the way.
                Set("captured", true);
                for (int k = rnd.Next(4); k > 0; k--) Call("Key", keys[rnd.Next(keys.Length)]);
                if (!paused && r == "Search" && Get("trialIdx").Equals(25)) { Call("Pause"); Check((bool)Get("paused") && (string)Get("primaryText") == "Resume", "Esc pauses, with Resume on the card"); Call("Primary"); Check(!(bool)Get("paused"), "Resume carries on"); paused = true; }
                if (rnd.NextDouble() < 0.15) { Call("CantTell"); unsure++; } else { Call("Answer"); answered++; }
                if (Stage != "Feedback") { Check(false, "after an answer the place is shown (stage " + Stage + ")"); break; }
                Call("NextTrial");
                if (Stage == "Listen" && (facing() != "Front" || Math.Abs((float)Get("yaw")) > 0.01)) notFront++;   // every footstep starts facing forward
            }
            string detail = (string)Get("detail");
            Check(!string.IsNullOrEmpty(detail) && !detail.Contains("NaN") && !detail.Contains("Infinity"), r + " done, its text: " + detail.Split((char)10)[0]);
            if (r == "Search")
            {
                var heads = (IList)Get("heads");
                Check(Stage == "Break" && (string)Get("primaryText") == "Continue", "find your head: " + trials.Count + " footsteps, the card says what's next");
                Check(trials.Count == Math.Max(Sphere3D.SearchSteps, heads.Count) && Get("leader") != null, "the best head so far was in use as it went");
                int heard = 0; foreach (Sphere3D.Variant h in heads) if (h.N > 0) heard++;
                Check(heard == heads.Count, "every head was heard (" + heard + " of " + heads.Count + ")");
                Call("Primary");   // Continue
                Check(Stage == "Menu" && (string)Get("primaryText") == "Finish", "Continue: the head is picked (" + ((Sphere3D.Variant)Get("winner")).HeadName + ", saved: " + core.S.Game.Hrtf + "), and the menu asks what to fine-tune");
            }
        }

        // ---- The menu: the tests answered by clicking, each from its own key, each once.
        Action<Bitmap> draw = b => { using (var g = Graphics.FromImage(b)) view.Render(new GdiCanvas(g, snap, desk), b.Size); };
        var frame = new Bitmap(1600, 1000);
        draw(frame);
        var rows = (RectangleF[])Get("menuRects");
        Check(rows.Length == 6 && rows.All(x => x.Height > 0) && rows[5].Bottom < ((RectangleF)Get("primaryRect")).Top, "six tests on the menu, above the Finish button");
        Func<int, int> at = i => (int)Call("MenuAt", Point.Round(new PointF(rows[i].Left + 20, rows[i].Top + 10)));
        Check(at(2) == 2 && (int)Call("MenuAt", new Point(5, 5)) == -1, "a click finds its row");
        Action answerAll = () =>
        {
            Call("BeginTrial");
            for (int guard = 0; Stage == "Listen" && guard < 200; guard++) { Set("captured", true); if (rnd.NextDouble() < 0.1) Call("CantTell"); else Call("Answer"); Call("NextTrial"); }
        };
        Func<Sphere3D.Dir[]> placesOf = () => ((IEnumerable)Get("trials")).Cast<Sphere3D.Trial>().Select(t => t.S.D).Distinct().ToArray();
        Set("captured", false);
        Call("Key", Keys.D2);
        Check(Round == "Test" && Stage == "Countdown" && Get("test").ToString() == "Sides" && ((IList)Get("trials")).Count == 18 && placesOf().All(d => d == Sphere3D.Dir.Left || d == Sphere3D.Dir.Right), "2: left / right only, 18 footsteps");
        answerAll();
        Check(Stage == "Menu" && ((string)Get("detail")).Contains("head size"), "back on the menu, with what it decided: " + ((string)Get("detail")).Split((char)10).Last());
        draw(frame);
        Check(at(1) == -1, "a test done can't be picked again");
        Call("Key", Keys.D2);
        Check(Stage == "Menu", "... with its key either");
        // Paused in a test: Stop this test drops it, and it can still be done.
        var gs0 = core.S.Game; string kept = gs0.HeadSize + "|" + gs0.CueSharpen + "|" + gs0.FloorContrast;
        Call("Key", Keys.D3);
        Call("BeginTrial");
        Call("Pause");
        Check((string)Get("secondaryText") == "Stop this test", "paused in a test: Stop this test");
        Call("Secondary");
        Check(Stage == "Menu" && !(bool)Get("paused") && gs0.HeadSize + "|" + gs0.CueSharpen + "|" + gs0.FloorContrast == kept, "stopped: back on the menu, nothing changed");
        draw(frame);
        Check(at(2) == 2, "... and it can still be picked");
        Call("Key", Keys.NumPad3);
        Check(placesOf().All(d => d == Sphere3D.Dir.Front || d == Sphere3D.Dir.Behind) && ((IList)Get("trials")).Count == 18, "3: forward / backward only, 18 footsteps (the number pad works too)");
        answerAll();
        // A setting at the end of its range has only two values to try (cue 0%: 0 and 17): fewer footsteps, not a fault.
        Func<Sphere3D.Tuning> tried = () => (Sphere3D.Tuning)Get("tuning");
        Call("Key", Keys.D1);
        int want1 = tried().Contrast.Count * 4 + tried().Cue.Count * 2;
        Check(placesOf().All(d => d == Sphere3D.Dir.Up || d == Sphere3D.Dir.Down) && ((IList)Get("trials")).Count == want1 && want1 >= 12 && want1 <= 18, "1: above / below only, " + want1 + " footsteps (cue " + string.Join(",", tried().Cue.Select(v => v.Cue)) + ")");
        answerAll();
        Call("Key", Keys.D4);
        int want4 = (tried().Size.Count + tried().Cue.Count + tried().Contrast.Count) * 4;
        Check(((IList)Get("trials")).Count == want4 && want4 >= 24 && want4 <= Sphere3D.TuneSteps, "4: all directions, " + want4 + " footsteps (cue " + string.Join(",", tried().Cue.Select(v => v.Cue)) + ", contrast " + string.Join(",", tried().Contrast.Select(v => v.Contrast)) + ")");
        answerAll();
        Check(Stage == "Menu", "four tests done, two to go");

        // ---- Footstep tracking: the view turns freely, a simulated player keeps it on the footsteps.
        Action<Keys, bool> track = (key, lockedLevel) =>
        {
            Call("Key", key);
            Check(Stage == "Countdown" && Get("tracker") != null, key + ": tracking starts with a countdown");
            Call("BeginTrack");
            Check(Stage == "Track", "tracking runs");
            Set("captured", true);
            Set("facing", Sphere3D.Dir.Front);
            view.LookBy(100, -200);
            float y0 = (float)Get("yaw"), p0 = (float)Get("pitch");
            Check(Math.Abs(y0 - 100 * (float)Get("sens")) < 0.01 && (lockedLevel ? p0 == 0 : Math.Abs(p0 - 200 * (float)Get("sens")) < 0.01), lockedLevel ? "the mouse turns left and right, the view stays level" : "the mouse moves the view freely, up and down too");
            Call("EaseView", 16.7);
            Check((float)Get("yaw") == y0, "nothing eases the view back (it's yours)");
            Call("Key", Keys.Left);
            Check((float)Get("yaw") == y0 && Get("facing").ToString() == "Front", "the arrow keys don't turn it");
            view.LookBy(100000, 5000);
            Check((float)Get("yaw") >= 0 && (float)Get("yaw") < 360 && Math.Abs((float)Get("pitch")) <= 85, "the view stays in range");
            var tr = (Sphere3D.Tracker)Get("tracker");
            Sphere3D.Vec last = tr.Pos;
            var args2 = new object[] { tr, null };
            bool levelKept = true;
            draw(frame);   // tracking drawn, nothing shown
            Call("Key", Keys.Space);
            Check(tr.Showing, "Space shows where they are");
            draw(frame);
            while (!tr.Finished)
            {
                // Aimed at the footstep just played, then the next one.
                Set("yaw", (float)((Math.Atan2(last.X, last.Z) * 180 / Math.PI + 360) % 360));
                Set("pitch", lockedLevel ? 0f : (float)(Math.Asin(last.Y / last.Length) * 180 / Math.PI));
                Typed(args2);
                last = (Sphere3D.Vec)args2[1];
                if (Math.Abs(last.Y) > 1e-6) levelKept = false;
            }
            if (lockedLevel) Check(levelKept, "ear-level tracking: every footstep is on the ring");
            Check(tr.MeanDeg < 5, "following exactly scores almost nothing off (" + tr.MeanDeg.ToString("0.0") + " degrees, the shown footsteps count as misses)");
            Call("Tick");
            Check(Get("tracker") == null, "the test ends itself when the walks are through");
        };
        track(Keys.D5, false);
        Check(Stage == "Menu", "five tests done");
        track(Keys.NumPad6, true);

        Check(notFront == 0, "every footstep started with the view facing forward (" + notFront + " didn't)");
        Check(Stage == "Done" && (string)Get("primaryText") == "Back to StreamLite" && (string)Get("secondaryText") == "Practice", "all six done: All set, with Back and Practice");
        var final = (Sphere3D.Variant)Get("final");
        Check(core.S.Game.Hrtf == final.Hrtf && core.S.Game.HeadSize == final.HeadSize && core.S.Game.CueSharpen == final.Cue && core.S.Game.FloorContrast == final.Contrast && (phones == null || core.Personal3D(phones) != null),
            "everything tuned and saved: " + final.HeadName + ", head size " + final.HeadSize + "%, cue " + final.Cue + "%, contrast " + final.Contrast + "%");
        string doneText = (string)Get("detail");
        Check(Sphere3D.Tests.All(x => doneText.Contains(Sphere3D.TestName(x))), "the result card names all six tests");
        Check(!doneText.Contains("NaN") && !doneText.Contains("Infinity"), "the result text has no NaN");
        Check(Get("chart") == null, "with tests done, their results take the room of the heads' bars");
        Console.WriteLine(doneText);
        // Draws at every stage it went through (here: the result card).
        using (var bmp = new Bitmap(1600, 1000)) using (var g = Graphics.FromImage(bmp)) view.Render(new GdiCanvas(g, snap, desk), bmp.Size);

        // Practice: answers shown right away, Esc ends it.
        Call("Secondary");
        Check(Round == "Practice" && Stage == "Countdown", "Practice starts");
        Call("BeginTrial");
        for (int i = 0; i < 5; i++) { Call("Answer"); Check(Stage == "Feedback", "practice answer " + (i + 1) + " shown"); Call("NextTrial"); }
        Set("captured", true);
        Call("Key", Keys.Escape);
        Check(Stage == "Done" && (string)Get("primaryText") == "Back to StreamLite", "Esc ends practice");
        Call("Primary");
        Check(Get("anim").ToString() == "Exit", "Back to StreamLite: the way out plays");

        // Practice as a mode of its own (4.1, the 3D page's Practice button): current settings, nothing tuned or saved.
        var gs = core.S.Game;
        // Continue, then straight to Finish: no tests is fine, the head stays with its bars on the card.
        view = new Personal3DView(core, snap, desk, new Rectangle(160, 70, 1280, 860), new Rectangle(0, 0, 1600, 1000), new PointF(760, 470), 200, 0, 30, null, null) { NoMouse = true, NoAudio = true };
        view.Bounds = new Rectangle(0, 0, 1600, 1000);
        var solo = new Sphere3D.Variant { Hrtf = gs.Hrtf, HeadSize = gs.HeadSize, Cue = gs.CueSharpen, Contrast = gs.FloorContrast, N = 1 };
        solo.Errors.Add(0);
        Set("heads", new System.Collections.Generic.List<Sphere3D.Variant> { solo }); Set("work", solo);
        Call("ShowMenu", new object[] { null });
        Call("Primary");
        Check(Stage == "Done" && Get("chart") != null && ((IList)Get("chart")).Count == 1 && ((string)Get("detail")).Contains("head size " + gs.HeadSize + "%"), "Finish without a test: All set with the head as it is");
        string before = gs.Hrtf + "|" + gs.HeadSize + "|" + gs.CueSharpen + "|" + gs.FloorContrast + "|" + (phones != null && core.Personal3D(phones) != null);
        view = new Personal3DView(core, snap, desk, new Rectangle(160, 70, 1280, 860), new Rectangle(0, 0, 1600, 1000), new PointF(760, 470), 200, 0, 30, null, null) { NoMouse = true, NoAudio = true };
        view.Bounds = new Rectangle(0, 0, 1600, 1000);
        Call("PracticeMode");
        Check(Stage == "Intro" && ((string)Get("headline")).StartsWith("Practice"), "practice mode: its own first card (" + Get("headline") + ")");
        Call("ShowButtons", "Start practice", "Back to StreamLite");
        Call("Key", Keys.Enter);
        Check(Round == "Practice" && Stage == "Countdown", "Enter goes straight to the footsteps (no head search)");
        Call("BeginTrial");
        int practised = 0;
        for (int i = 0; i < 40 && Stage == "Listen"; i++)
        {
            Set("captured", true);
            for (int k = rnd.Next(3); k > 0; k--) Call("Key", keys[rnd.Next(keys.Length)]);
            if (i % 9 == 4) Call("CantTell"); else Call("Answer");
            if (Stage != "Feedback") break;
            Call("NextTrial"); practised++;
        }
        Check(practised == 40, "40 footsteps in a row, as long as you like (" + practised + ")");
        Check(((string)Get("headline")).Contains(" of " + (int)Get("practiceCount") + " right") && (int)Get("practiceCount") == 40, "the score keeps count: " + Get("headline"));
        Set("captured", true);
        Call("Key", Keys.Escape);
        Check(Stage == "Done", "Esc ends practice");
        string after = gs.Hrtf + "|" + gs.HeadSize + "|" + gs.CueSharpen + "|" + gs.FloorContrast + "|" + (phones != null && core.Personal3D(phones) != null);
        Check(after == before, "nothing tuned or changed: " + after);
        Console.WriteLine("  (" + answered + " clicked, " + unsure + " can't tell)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
