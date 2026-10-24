// The mic cable's kernel-streaming render pin: can a second program open it while StreamLite holds it, and how does
// the driver's play position move (step size and timing)? Runs a second pin that plays only silence (adds nothing to
// the mic). Usage: KsOpenProbe [ms]
using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using StreamLite;
using StreamLite.Interop;

class KsOpenProbe
{
    static void Main(string[] args)
    {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 2000;
        var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLite", "settings.json"))) as System.Collections.Generic.Dictionary<string, object>;
        string feed = json["VirtualOutId"] as string;
        string ks = AudioDevices.KsFilterPath(feed);
        Console.WriteLine("feed state " + AudioDevices.State(feed) + ", KS path " + (ks ?? "none"));
        if (ks == null) return;
        using (var r = new KsRender())
        {
            if (!r.Open(ks, 48000, 20)) { Console.WriteLine("could not open: " + r.Error); return; }
            Console.WriteLine("opened a second pin instance (" + r.Bits + "-bit" + (r.Float ? " float" : "") + ")");
            var steps = r.TracePosition(ms);
            if (steps.Count < 2) { Console.WriteLine("play position didn't move (" + r.Error + ")"); return; }
            var s = steps.Skip(1).ToList();
            Console.WriteLine(string.Format("{0} position changes in {1} ms: step {2:0}..{3:0} frames (median {4:0}), every {5:0.00}..{6:0.00} ms (median {7:0.00})",
                s.Count, ms, s.Min(x => x[0]), s.Max(x => x[0]), s.Select(x => x[0]).OrderBy(x => x).ElementAt(s.Count / 2),
                s.Min(x => x[1]), s.Max(x => x[1]), s.Select(x => x[1]).OrderBy(x => x).ElementAt(s.Count / 2)));
        }
    }
}
