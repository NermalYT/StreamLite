using System;
using System.Linq;
using StreamLite.Audio;

// Which directions the built-in HRTF sets actually measured (horizontal plane and overall).
class HrtfGrid
{
    static void Main()
    {
        foreach (var kind in new[] { HrtfSet.Ku100, HrtfSet.Kemar })
        {
            var s = HrtfSet.Get(kind, null);
            var horiz = Enumerable.Range(0, s.Az.Length).Where(i => Math.Abs(s.El[i]) < 0.5).Select(i => s.Az[i]).OrderBy(a => a).ToList();
            Console.WriteLine(s.Name + ": " + s.Az.Length + " directions, " + s.Taps + " taps, room dirs " + (s.RoomAz != null ? s.RoomAz.Length : 0));
            Console.WriteLine("  elevations: " + string.Join(" ", s.El.Distinct().OrderBy(e => e).Select(e => e.ToString("0"))));
            Console.WriteLine("  horizontal azimuths (" + horiz.Count + "): " + string.Join(" ", horiz.Select(a => a.ToString("0.#"))));
            if (s.RoomAz != null) Console.WriteLine("  room azimuths: " + string.Join(" ", s.RoomAz.Select(a => a.ToString("0"))));
        }
    }
}
