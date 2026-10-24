using System;
using StreamLite.Interop;

// Prints the name keys apps read for each endpoint (device description, adapter names, friendly name).
class NameProbe
{
    static void Main()
    {
        var desc = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
        var fr = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        var a1 = new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);
        var a2 = new PropertyKey(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow, 3))
                Console.WriteLine(flow + " st" + AudioDevices.State(d.Id) + " | desc=" + AudioDevices.GetString(d.Id, desc) + " | friendly=" + AudioDevices.GetString(d.Id, fr)
                    + " | adapter1=" + AudioDevices.GetString(d.Id, a1) + " | adapter2=" + AudioDevices.GetString(d.Id, a2));
    }
}
