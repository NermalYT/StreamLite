using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using StreamLite;
using StreamLite.Audio;

// 4.1.2: only games play in 3D by default (GameDetect + the "also in 3D" list), the head list's accuracy percentages,
// and bug-report pictures re-drawn without hidden details. Usage: run.ps1 GamesOnlyTest
class GamesOnlyTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }

    static void Main()
    {
        // ---- what counts as a game
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs2", "FortniteClient-Win64-Shipping" };
        Check(GameDetect.IsGame("cs2", null, games), "a game profile's program is a game");
        Check(GameDetect.IsGame("cs2.exe", null, games), "... also written with .exe");
        Check(GameDetect.IsGame("SomeIndie", @"D:\SteamLibrary\steamapps\common\Some Indie\SomeIndie.exe", games), "anything in a Steam library is a game");
        Check(GameDetect.IsGame("VALORANT-Win64-Shipping", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", games), "Riot's games are games");
        Check(GameDetect.IsGame("Game", @"C:\XboxGames\Some Game\Content\Game.exe", games), "Xbox app games are games");
        Check(!GameDetect.IsGame("steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", games), "Steam's own store window isn't a game");
        Check(!GameDetect.IsGame("EpicGamesLauncher", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", games), "Epic's launcher isn't a game (though it sits in Epic Games)");
        Check(!GameDetect.IsGame("UnityCrashHandler64", @"D:\SteamLibrary\steamapps\common\X\UnityCrashHandler64.exe", games), "a game's crash reporter isn't a game");
        Check(!GameDetect.IsGame("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", games), "a browser isn't a game");
        Check(!GameDetect.IsGame("Discord", @"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe", games), "Discord isn't a game");
        Check(GameDetect.PathOf(System.Diagnostics.Process.GetCurrentProcess().Id) != null, "a running app's path can be read");

        // ---- accuracy in the head list: tenths only when it isn't whole
        Check(Sphere3D.Percent(98.6) == "98.6%" && Sphere3D.Percent(80) == "80%" && Sphere3D.Percent(79.96) == "80%" && Sphere3D.Percent(0) == "0%", "98.6%, 80%, 80% (79.96), 0%");
        var good = new Sphere3D.Variant { Hrtf = HrtfSet.Ku100, N = 6, ErrSum = 0.2 };
        var bad = new Sphere3D.Variant { Hrtf = HrtfSet.Kemar, N = 6, ErrSum = 4.5 };
        Check(Sphere3D.Accuracy(good) > Sphere3D.Accuracy(bad), "a head you placed footsteps better with scores higher (" + Sphere3D.Accuracy(good) + " vs " + Sphere3D.Accuracy(bad) + ")");
        Check(Sphere3D.Ranked(new List<Sphere3D.Variant> { bad, good })[0] == good, "... the same order the head search picks by");
        Check(Sphere3D.Accuracy(new Sphere3D.Variant { N = 2, ErrSum = 9 }) == 0, "never below 0%");

        string data = Path.Combine(Path.GetTempPath(), "StreamLite-gamesonlytest");
        Directory.CreateDirectory(data); foreach (var f in Directory.GetFiles(data)) File.Delete(f);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore(true);
        core.SaveHeadScores("phones-1", new[] { good, bad, new Sphere3D.Variant { Hrtf = "h12" } });
        var sc = core.HeadScores("phones-1");
        Check(sc.Count == 2 && sc.ContainsKey(HrtfSet.Ku100) && sc[HrtfSet.Ku100] == Sphere3D.Accuracy(good), "head scores saved per headphones (heads never tried left out): " + core.S.Game.HeadScores["phones-1"]);
        Check(core.HeadScores("other-phones").Count == 0 && core.HeadScores(null).Count == 0, "other headphones: no scores");

        // ---- which apps play around 3D
        Check(core.S.Game.GamesOnly, "only games in 3D is the default");
        core.S.Profiles.Together = false;
        core.S.Game.NotGames = new List<string> { "chrome", "Discord", "Spotify" };
        core.S.Game.Also3DApps = "discord.exe";
        var plain = core.PlainApps();
        Check(plain.Contains("chrome") && plain.Contains("Spotify") && !plain.Contains("Discord"), "found apps play around 3D, but not one on the also-in-3D list (" + string.Join(", ", plain) + ")");
        core.S.Game.BypassApps = "vlc";
        Check(!core.PlainApps().Contains("vlc"), "the old keep-out list doesn't count while only games are in 3D");
        core.S.Game.GamesOnly = false; core.S.Game.BypassEnabled = true;
        Check(core.PlainApps().SequenceEqual(new[] { "vlc" }), "every app in 3D but these: the keep-out list again");
        core.S.Game.BypassEnabled = false;
        Check(core.PlainApps().Length == 0, "every app in 3D: nothing kept out");
        // Switched back to every app: found apps go back into 3D when they run - a running one now, the others wait.
        string me = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        core.S.Game.NotGames = new List<string> { me, "NotRunningApp" };
        typeof(AppCore).GetMethod("FindNotGames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(core, null);
        Check(core.S.Game.NotGames.SequenceEqual(new[] { "NotRunningApp" }), "off: running apps given back to 3D, the rest at their next start (" + string.Join(", ", core.S.Game.NotGames) + ")");

        // ---- bug report pictures: smaller, and nothing hidden in them goes along
        string big = Path.Combine(data, "big.jpg");
        using (var bmp = new Bitmap(3840, 2160))
        {
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.SteelBlue); g.FillRectangle(Brushes.White, 100, 100, 800, 400); }
            var item = (PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyItem));
            item.Id = 0x013B; item.Type = 2; item.Value = System.Text.Encoding.ASCII.GetBytes("Secret Person\0"); item.Len = item.Value.Length;   // Artist
            bmp.SetPropertyItem(item);
            bmp.Save(big, ImageFormat.Jpeg);
        }
        using (var check = Image.FromFile(big)) Check(check.PropertyIdList.Contains(0x013B), "(the test picture carries a name inside)");
        var sent = BugReports.Prepare(big);
        Check(sent != null && sent.Length <= BugReports.MaxPictureBytes, "a 4K screenshot is sent (" + (sent == null ? 0 : sent.Length / 1024) + " KB)");
        using (var ms = new MemoryStream(sent))
        using (var img = Image.FromStream(ms))
        {
            Check(Math.Max(img.Width, img.Height) == BugReports.MaxSide, "... at most " + BugReports.MaxSide + " pixels across (" + img.Width + "x" + img.Height + ")");
            Check(!img.PropertyIdList.Contains(0x013B) && System.Text.Encoding.ASCII.GetString(sent).IndexOf("Secret Person", StringComparison.Ordinal) < 0, "... without the name hidden inside");
        }
        File.WriteAllText(Path.Combine(data, "clip.png"), "not a picture");
        Check(BugReports.Prepare(Path.Combine(data, "clip.png")) == null, "something that isn't a picture isn't sent");
        Check(BugReports.DiscordOk("") && BugReports.DiscordOk("@cool.name") && BugReports.DiscordOk("Old Name#1234") && !BugReports.DiscordOk("a") && !BugReports.DiscordOk("https://x.y/z"), "Discord names checked like the service does");

        Console.WriteLine(fails == 0 ? "PASS" : fails + " FAILED");
        Environment.Exit(fails);
    }
}
