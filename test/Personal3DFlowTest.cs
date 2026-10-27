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
        foreach (var r in new[] { "Search", "Tune" })
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
                Check(Round == "Tune" && ((IList)Get("trials")).Count == Sphere3D.TuneSteps, "the head is picked: " + ((Sphere3D.Variant)Get("winner")).HeadName + " (saved: " + core.S.Game.Hrtf + "), then " + Sphere3D.TuneSteps + " footsteps of tuning");
            }
        }
        Check(notFront == 0, "every footstep started with the view facing forward (" + notFront + " didn't)");
        Check(Stage == "Done" && (string)Get("primaryText") == "Back to StreamLite" && (string)Get("secondaryText") == "Practice", "All set, with Back and Practice");
        var final = (Sphere3D.Variant)Get("final");
        Check(core.S.Game.Hrtf == final.Hrtf && core.S.Game.HeadSize == final.HeadSize && core.S.Game.CueSharpen == final.Cue && core.S.Game.FloorContrast == final.Contrast && (phones == null || core.Personal3D(phones) != null),
            "everything tuned and saved: " + final.HeadName + ", head size " + final.HeadSize + "%, cue " + final.Cue + "%, contrast " + final.Contrast + "%");
        var chart = (IList)Get("chart");
        Check(chart != null && chart.Count > 0 && chart.Count <= 10, "the chart shows " + (chart == null ? 0 : chart.Count) + " heads");
        Check(!((string)Get("detail")).Contains("NaN"), "the result text has no NaN");
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
        Console.WriteLine("  (" + answered + " clicked, " + unsure + " can't tell)");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
