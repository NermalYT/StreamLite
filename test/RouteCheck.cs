using System;
using System.Diagnostics;
using System.Linq;
using StreamLite.Interop;

// RouteCheck <process name>: routes that running process to the headphones (per-app output device) and reads the
// setting back, to see whether Windows accepts it.
class RouteCheck
{
    static void Main(string[] args)
    {
        string name = args[0];
        var phones = AudioDevices.List(EDataFlow.Render).First(d => d.Name.IndexOf("QCC", StringComparison.OrdinalIgnoreCase) >= 0);
        foreach (var p in Process.GetProcessesByName(name))
            Console.WriteLine("pid " + p.Id + " before: " + (AppRouting.Current(p.Id) ?? "(follows default)"));
        int n = AppRouting.Route(new[] { name }, phones.Id);
        Console.WriteLine("routed " + n);
        foreach (var p in Process.GetProcessesByName(name))
            Console.WriteLine("pid " + p.Id + " after: " + (AppRouting.Current(p.Id) ?? "(follows default)"));
    }
}
