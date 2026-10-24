using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using StreamLite.Camera;

// Starts the virtual camera step by step (in this process's rights) and times each step, to find where it fails or hangs.
class CamStartTest
{
    static void Step(string name, Action a)
    {
        var sw = Stopwatch.StartNew();
        Console.Write(name + "... ");
        var t = new Thread(() => { try { a(); Console.Write("ok "); } catch (Exception ex) { Console.Write("FAILED: " + (ex.InnerException ?? ex).GetType().Name + " " + (ex.InnerException ?? ex).Message + " "); } });
        t.Start();
        if (!t.Join(15000)) { Console.WriteLine("HUNG (>15 s)"); Environment.Exit(1); }
        Console.WriteLine(sw.ElapsedMilliseconds + " ms");
    }

    static void Main()
    {
        Console.WriteLine("elevated: " + StreamLite.Elevation.IsElevated);
        string reg = null;
        Step("Register", () => { reg = VirtualCamera.Register(); if (reg != null) throw new Exception(reg); });
        var cam = new VirtualCamera();
        Step("OpenShared", () => typeof(VirtualCamera).GetMethod("OpenShared", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cam, null));
        cam.Dispose();
        var cam2 = new VirtualCamera();
        bool ok = false;
        Step("Start (whole)", () => { ok = cam2.Start(); if (!ok) throw new Exception(cam2.Error); });
        if (ok) { Thread.Sleep(1000); Console.WriteLine("camera added; removing"); }
        Step("Dispose", () => cam2.Dispose());
    }
}
