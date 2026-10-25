// Windows spatial sound per output (Windows Sonic / Dolby Atmos / DTS): max audio objects, 0 = off. Read-only.
using System;
using StreamLite;
using StreamLite.Interop;

class SpatialProbe
{
    static void Main()
    {
        foreach (var d in AudioDevices.List(EDataFlow.Render))
            Console.WriteLine(AudioDevices.SpatialObjects(d.Id).ToString().PadLeft(4) + "  " + d.Name);
    }
}
