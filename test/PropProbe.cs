using System;
using System.Linq;
using System.Runtime.InteropServices;
using StreamLite.Interop;

// Prints every string property of the StreamLite endpoints (read-only).
class PropProbe
{
    static void Main()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            foreach (var d in AudioDevices.List(flow, 3).Where(x => x.Name.StartsWith("StreamLite") || x.Name.Contains("QCC")))
            {
                Console.WriteLine("== " + d.Name + "  [" + d.Adapter + "]");
                IMMDevice dev; en.GetDevice(d.Id, out dev);
                IPropertyStore st; dev.OpenPropertyStore(0, out st);
                int n; st.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    PropertyKey k; st.GetAt(i, out k);
                    PropVariant v; if (st.GetValue(ref k, out v) != 0) continue;
                    if (v.vt == 31) Console.WriteLine("  {" + k.fmtid + "}," + k.pid + " = " + Marshal.PtrToStringUni(v.pointerValue));
                }
            }
    }
}
