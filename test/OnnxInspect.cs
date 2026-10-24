using System;
using System.Linq;
using StreamLite.Audio;

// Prints a model's inputs/outputs. Usage: OnnxInspect <model.onnx> [gpu]
class OnnxInspect
{
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("STREAMLITE_AI", System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\ai")));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var s = new OnnxSession(args[0], args.Length > 1 && args[1] == "gpu"))
        {
            Console.WriteLine("loaded in " + sw.ElapsedMilliseconds + " ms, gpu=" + s.OnGpu);
            for (int i = 0; i < s.InputNames.Length; i++) Console.WriteLine("in  " + s.InputNames[i] + " [" + string.Join(",", s.InputShapes[i]) + "]");
            for (int i = 0; i < s.OutputNames.Length; i++) Console.WriteLine("out " + s.OutputNames[i] + " [" + string.Join(",", s.OutputShapes[i]) + "]");
        }
    }
}
