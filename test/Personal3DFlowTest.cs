// Personalize 3D from the first card to "All set", every round, answered by a simulated player (clicks, "can't tell",
// a pause, the buttons, Enter, Esc in practice), with no sound, no mouse and no devices (settings in a temp folder).
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
    static void Main()
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
        Check(Round == "Search1" && Stage == "Countdown", "Enter starts round 1 with a countdown");
        // Wait for the heads (the countdown would).
        var t0 = DateTime.Now;
        while ((int)Get("loaded") < (int)Get("toLoad") && (DateTime.Now - t0).TotalSeconds < 120) System.Threading.Thread.Sleep(50);
        Check((int)Get("loaded") >= (int)Get("toLoad"), "all " + Get("toLoad") + " heads loaded in " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + " s");

        int answered = 0, unsure = 0;
        bool paused = false;
        foreach (var r in new[] { "Search1", "Search2", "Search3", "Tune" })
        {
            Check(Round == r, "round " + r);
            var trials = (IList)Get("trials");
            Call("BeginTrial");
            int guard = 0;
            while (Stage != "Break" && guard++ < 1000)
            {
                if (Stage != "Listen") break;
                // Look somewhere, then click - or "can't tell" now and then; one pause and resume on the way.
                Set("yaw", (float)(rnd.NextDouble() * 360)); Set("pitch", (float)(rnd.NextDouble() * 120 - 60));
                if (!paused && r == "Search2") { Set("captured", true); Call("Pause"); Check((bool)Get("paused") && (string)Get("primaryText") == "Resume", "Esc pauses, with Resume on the card"); Call("Primary"); Check(!(bool)Get("paused"), "Resume carries on"); paused = true; }
                if (rnd.NextDouble() < 0.15) { Call("CantTell"); unsure++; } else { Call("Answer"); answered++; }
                if (Stage != "Feedback") { Check(false, "after an answer the place is shown (stage " + Stage + ")"); break; }
                Call("NextTrial");
            }
            Check(Stage == "Break" && (string)Get("primaryText") == "Continue", r + " done: " + trials.Count + " footsteps, the card says what's next");
            string detail = (string)Get("detail");
            Check(!string.IsNullOrEmpty(detail) && !detail.Contains("NaN") && !detail.Contains("Infinity"), "  its text: " + detail.Split('\n')[0]);
            Call("Primary");   // Continue
            if (r == "Search3") Check(Round == "Tune", "the head is picked: " + ((Building3D.Variant)Get("winner")).HeadName + " (saved: " + core.S.Game.Hrtf + ")");
        }
        Check(Round == "Walks", "walks after tuning");
        var walks = (IList)Get("walks");
        Call("BeginWalk");
        for (int i = 0; i < walks.Count; i++)
        {
            var w = (Building3D.Walk)walks[i];
            w.ErrSum = rnd.Next(5, 40) * 50; w.N = 50;
            Call("EndWalk");
            if (i < walks.Count - 1) { Check(Stage == "Between", "walk " + (i + 1) + " of " + walks.Count); Call("BeginWalk"); }
        }
        Check(Stage == "Done" && (string)Get("primaryText") == "Back to StreamLite" && (string)Get("secondaryText") == "Practice", "All set, with Back and Practice");
        var final = (Building3D.Variant)Get("final");
        Check(core.S.Game.Hrtf == final.Hrtf && core.S.Game.HeadSize == final.HeadSize && (phones == null || core.Personal3D(phones) != null), "saved: " + final.HeadName + ", head size " + final.HeadSize + "%, cue " + final.Cue + "%, contrast " + final.Contrast + "%");
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
