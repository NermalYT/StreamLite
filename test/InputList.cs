// Every recording endpoint Windows knows (active, disabled, unplugged) with its state. Read-only. Usage: InputList
using System;
using StreamLite;
using StreamLite.Interop;

class InputList
{
    static void Main(string[] args)
    {
        foreach (var flow in new[] { EDataFlow.Capture, EDataFlow.Render })
            foreach (var d in AudioDevices.List(flow, 1 | 2 | 4 | 8))
                Console.WriteLine((flow == EDataFlow.Capture ? "in  " : "out ") + "state " + AudioDevices.State(d.Id) + "  " + d.Name + (AudioDevices.LooksVirtual(d) ? "  [virtual]" : "") + (args.Length > 0 ? "  " + d.Id : ""));
    }
}

