using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StreamLite;
using StreamLite.Audio;
using StreamLite.UI;

// The 3D sound cube: right-drag turns it (left-drag doesn't), a right double-click resets it, and it renders from
// the front, the side, above and below with footsteps on each floor. Writes a contact sheet. Usage: CubeViewTest <out.png>
class CubeViewTest
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) fails++; }

    static void Call(Control c, string method, MouseEventArgs e) { c.GetType().GetMethod(method, Any).Invoke(c, new object[] { e }); }

    [STAThread]
    static void Main(string[] args)
    {
        string data = Path.Combine(Path.GetTempPath(), "StreamLite-cubetest");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("STREAMLITE_DATA", data);
        var core = new AppCore(true);
        var view = new SoundRadarView(core) { Size = new Size(320, 320) };
        var cube = (SoundCube)typeof(SoundRadarView).GetField("cube", Any).GetValue(view);
        var h = view.Handle;

        double y0 = cube.Yaw, p0 = cube.Pitch;
        Call(view, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0));
        Call(view, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 180, 60, 0));
        Call(view, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 180, 60, 0));
        Check(cube.Yaw == y0 && cube.Pitch == p0, "left-drag doesn't turn the cube");
        Call(view, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 100, 100, 0));
        Call(view, "OnMouseMove", new MouseEventArgs(MouseButtons.Right, 0, 180, 60, 0));
        Call(view, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, 180, 60, 0));
        Check(Math.Abs(cube.Yaw - (y0 + 48)) < 0.01 && Math.Abs(cube.Pitch - (p0 - 24)) < 0.01, "right-drag turns it (yaw " + y0 + " -> " + cube.Yaw + ", pitch " + p0 + " -> " + cube.Pitch + ")");
        Call(view, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 100, 100, 0));
        Call(view, "OnMouseMove", new MouseEventArgs(MouseButtons.Right, 0, 100, 900, 0));
        Call(view, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, 100, 900, 0));
        Check(cube.Pitch <= 85, "pitch stops at 85 degrees (" + cube.Pitch + ")");
        Call(view, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Right, 2, 100, 100, 0));
        Check(cube.Yaw == SoundCube.DefaultYaw && cube.Pitch == SoundCube.DefaultPitch, "right double-click resets the view");

        Check(SoundCube.Floor(45) == 1 && SoundCube.Floor(0) == 0 && SoundCube.Floor(-45) == -1, "floors: +45 above, 0 ear level, -45 below");
        Check(SoundCube.CellName(0, 45) == "above, front" && SoundCube.CellName(90, 0) == "ear level, left" && SoundCube.CellName(225, -45) == "below, behind-right"
            && SoundCube.CellName(0, 90) == "above, centre", "cells: " + SoundCube.CellName(0, 45) + " / " + SoundCube.CellName(90, 0) + " / " + SoundCube.CellName(225, -45) + " / " + SoundCube.CellName(0, 90));

        // Contact sheet: 6 views, each with a footstep on a different floor.
        var shots = new[]
        {
            new { Yaw = SoundCube.DefaultYaw, Pitch = SoundCube.DefaultPitch, Az = 0.0, El = 45.0, Name = "default view, footstep above-front" },
            new { Yaw = 0.0, Pitch = 0.0, Az = 90.0, El = -45.0, Name = "from behind, level: below-left" },
            new { Yaw = 90.0, Pitch = 10.0, Az = 180.0, El = 0.0, Name = "from the side: ear level, behind" },
            new { Yaw = 0.0, Pitch = 85.0, Az = 315.0, El = 0.0, Name = "from above: ear level, front-right" },
            new { Yaw = -40.0, Pitch = -40.0, Az = 0.0, El = -90.0, Name = "from below: straight underfoot" },
            new { Yaw = 160.0, Pitch = 30.0, Az = 270.0, El = 45.0, Name = "from the front: above-right" },
        };
        var radar = core.Game.Processor.Radar;
        var sheet = new Bitmap(3 * 320, 2 * 340);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.Black);
            for (int i = 0; i < shots.Length; i++)
            {
                var s = shots[i];
                cube.Yaw = s.Yaw; cube.Pitch = s.Pitch;
                radar.TestSphere = true; radar.TestAz = (float)s.Az; radar.TestEl = (float)s.El;
                var bmp = new Bitmap(320, 320);
                view.DrawToBitmap(bmp, new Rectangle(0, 0, 320, 320));
                g.DrawImage(bmp, (i % 3) * 320, (i / 3) * 340);
                g.DrawString(s.Name, SystemFonts.DefaultFont, Brushes.White, (i % 3) * 320 + 4, (i / 3) * 340 + 322);
            }
        }
        string outp = args.Length > 0 ? args[0] : "cube_views.png";
        sheet.Save(outp, ImageFormat.Png);
        Console.WriteLine("  wrote " + Path.GetFullPath(outp));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    }
}
