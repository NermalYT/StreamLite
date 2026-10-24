using System;
using System.Diagnostics;
using StreamLite.Interop;
class RouteProbe
{
    [STAThread]
    static void Main()
    {
        foreach (var n in new[] { "DiscordPTB", "chrome", "spotify", "BroadcastLite" })
            foreach (var p in Process.GetProcessesByName(n))
            {
                string id = null;
                try { id = AppRouting.Current(p.Id); } catch (Exception ex) { id = "error " + ex.Message; }
                Console.WriteLine(n + " " + p.Id + " -> " + (id ?? "(default)"));

            }
        Console.WriteLine("default render: " + AudioDevices.GetDefaultId(EDataFlow.Render, ERole.Console));
    }
}

