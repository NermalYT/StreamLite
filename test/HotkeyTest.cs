// Hotkeys without touching the system (no hooks, a copy of default settings): key + modifier matching, auto-repeat,
// mouse buttons, push to talk, recording a new key (and Esc), per-mic mute, and every action that only changes
// settings runs and reports. Usage: run.ps1 HotkeyTest
using System;
using System.IO;
using System.Linq;
using System.Threading;
using StreamLite;

class HotkeyTest
{
    sealed class Inline : SynchronizationContext { public override void Post(SendOrPostCallback d, object s) { d(s); } }
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }
    const int Ctrl = 0xA2, Shift = 0xA0, Alt = 0xA4, M = 0x4D, K = 0x4B, F13 = 0x7C, Esc = 0x1B;

    static void Tap(HotkeyManager h, params int[] keys)
    {
        foreach (var k in keys) h.Press(k, 0, true);
        foreach (var k in keys.Reverse()) h.Press(k, 0, false);
    }

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sl_hotkeytest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", dir);
        SynchronizationContext.SetSynchronizationContext(new Inline());
        var core = new AppCore(true);
        var S = core.S;
        S.ExtraMics.Add(new ExtraMicSetting { Id = "{mic-2}", Name = "Second mic" });
        S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "all", Key = M, Mods = HotkeyManager.ModCtrl });
        S.Hotkeys.Add(new HotkeyBinding { Action = "mic.mute", Arg = "2", Mouse = 4 });
        S.Hotkeys.Add(new HotkeyBinding { Action = "mic.ptt", Key = F13 });
        var h = new HotkeyManager(core, false);
        string note = null; h.Fired += n => note = n;

        Check(core.Engine.PttClosed, "push to talk bound: the mic is closed until its key is held");
        h.Press(F13, 0, true);
        Check(!core.Engine.PttClosed, "holding the push-to-talk key opens the mic");
        h.Press(Shift, 0, true); h.Press(Shift, 0, false);
        Check(!core.Engine.PttClosed, "...and stays open while you press other keys");
        h.Press(F13, 0, false);
        Check(core.Engine.PttClosed, "letting go closes it again");

        Tap(h, Ctrl, M);
        Check(core.Engine.Muted && note == "Microphone muted", "Ctrl + M mutes the virtual mic (\"" + note + "\")");
        h.Press(Ctrl, 0, true); h.Press(M, 0, true); h.Press(M, 0, true); h.Press(M, 0, true); h.Press(M, 0, false); h.Press(Ctrl, 0, false);
        Check(!core.Engine.Muted, "holding Ctrl + M (auto-repeat) toggles once, not on every repeat");
        Tap(h, M);
        Check(!core.Engine.Muted, "M without Ctrl does nothing");
        Tap(h, Ctrl, Shift, M);
        Check(!core.Engine.Muted, "Ctrl + Shift + M is a different hotkey");

        h.Press(0, 4, true); h.Press(0, 4, false);
        Check(core.IsMicMuted("{mic-2}") && note == "Microphone 2 muted", "mouse back button mutes microphone 2 only (\"" + note + "\")");
        h.Press(0, 4, true); h.Press(0, 4, false);
        Check(!core.IsMicMuted("{mic-2}"), "...and unmutes it");

        HotkeyBinding got = new HotkeyBinding(); bool called = false;
        h.Capture(b => { got = b; called = true; });
        h.Press(Shift, 0, true);
        bool swallowed = h.Press(K, 0, true);
        h.Press(K, 0, false); h.Press(Shift, 0, false);
        Check(called && got != null && got.Key == K && got.Mods == HotkeyManager.ModShift && swallowed, "recording a key: Shift + K (" + HotkeyManager.Describe(got) + "), not passed on to other apps");
        called = false;
        h.Capture(b => { got = b; called = true; });
        h.Press(Alt, 0, true); h.Press(Alt, 0, false);
        Check(called && got != null && got.Key == Alt && got.Mods == 0, "recording a modifier on its own: " + HotkeyManager.Describe(got));
        called = false;
        h.Capture(b => { got = b; called = true; });
        h.Press(0, 5, true); h.Press(0, 5, false);
        Check(called && got != null && got.Mouse == 5, "recording a mouse button: " + HotkeyManager.Describe(got));
        called = false;
        h.Capture(b => { got = b; called = true; });
        h.Press(Esc, 0, true); h.Press(Esc, 0, false);
        Check(called && got == null, "Esc cancels recording");
        Tap(h, Ctrl, M);
        Check(core.Engine.Muted, "hotkeys work again after recording");
        Tap(h, Ctrl, M);

        // Every action that only changes settings: runs, reports, and toggles back.
        var safe = new[] { "mic.nr", "mic.gate", "mic.eq", "mic.comp", "mic.deess", "mic.autolevel", "mic.automix", "out.natural", "cam.track", "cam.light", "cam.mirror" };
        foreach (var id in safe)
        {
            var a = HotkeyActions.Find(id);
            string fp1 = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(S);
            string n1 = a.Run(core, null, true), n2 = a.Run(core, null, true);
            string fp2 = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(S);
            Check(n1 != null && n2 != null && n1 != n2 && fp1 == fp2, a.Label + ": \"" + n1 + "\", then \"" + n2 + "\"");
        }
        foreach (var id in new[] { "mic.engine", "mic.style", "out.preset", "out.profile", "cam.bg" })
        {
            var a = HotkeyActions.Find(id);
            var args = a.Args(core);
            string first = a.Run(core, "next", true), second = a.Run(core, "next", true), picked = a.Run(core, args.Last().Key, true);
            Check(first != null && second != null && first != second && picked != null, a.Label + ": next \"" + first + "\", \"" + second + "\"; pick \"" + picked + "\"");
        }
        Check(HotkeyActions.All.Count >= 25, HotkeyActions.All.Count + " actions available");
        Check(HotkeyManager.Describe(new HotkeyBinding { Key = M, Mods = HotkeyManager.ModCtrl | HotkeyManager.ModShift }) == "Ctrl + Shift + M"
            && HotkeyManager.Describe(new HotkeyBinding { Mouse = 4 }) == "Mouse back", "key names: Ctrl + Shift + M, Mouse back");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
