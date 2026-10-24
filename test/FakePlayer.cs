using System;
using System.IO;
using System.Media;
using System.Threading;

// A stand-in music player: "tone" plays a very quiet 1 kHz tone (-60 dBFS) for N seconds, "silent" plays digital
// silence (an open but silent audio session, like a paused player); a third argument keeps it open afterwards.
// Copy it as Spotify.exe to test detection.
class FakePlayer
{
    static void Main(string[] args)
    {
        bool tone = args.Length > 0 && args[0] == "tone";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 6;
        const int fs = 48000;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int n = fs * secs;
        w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(fs); w.Write(fs * 2); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(tone ? Math.Round(32767 * 0.001 * Math.Sin(2 * Math.PI * 1000 * i / fs)) : 0));
        ms.Position = 0;
        using (var p = new SoundPlayer(ms)) p.PlaySync();
        // Then stay open but silent (like a paused player) for the given seconds.
        if (args.Length > 2) Thread.Sleep(int.Parse(args[2]) * 1000);
    }
}
