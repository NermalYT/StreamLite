using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Reads a USB device's link speed and its UVC (video) format/frame descriptors straight from its parent hub,
// the same way Microsoft's USBView does. Usage: UsbProbe <hub instance id> <port number>
class UsbProbe
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid cls, string devId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List(ref Guid cls, string devId, char[] buffer, int len, int flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);

    const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;
    const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;
    const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x220410;

    static void Main(string[] args)
    {
        string hubId = args[0];
        int port = int.Parse(args[1]);
        var guid = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8"); // GUID_DEVINTERFACE_USB_HUB
        int len;
        CM_Get_Device_Interface_List_Size(out len, ref guid, hubId, 0);
        var buf = new char[len];
        CM_Get_Device_Interface_List(ref guid, hubId, buf, len, 0);
        string path = new string(buf).Split('\0')[0];
        using (var h = CreateFile(path, 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open hub " + path);

            var info = new byte[512];
            BitConverter.GetBytes(port).CopyTo(info, 0);
            int ret;
            if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, info, info.Length, info, info.Length, out ret, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "connection info");
            int bcdUsb = BitConverter.ToUInt16(info, 4 + 2);
            string[] speeds = { "Low (1.5 Mbps)", "Full (12 Mbps)", "High / USB 2.0 (480 Mbps)", "SuperSpeed / USB 3 (5 Gbps)" };
            Console.WriteLine("Device on port " + port + ": USB " + (bcdUsb >> 8) + "." + ((bcdUsb >> 4) & 0xF) + " device, VID " + BitConverter.ToUInt16(info, 4 + 8).ToString("X4") + " PID " + BitConverter.ToUInt16(info, 4 + 10).ToString("X4"));
            Console.WriteLine("Link speed (EX): " + (info[23] < speeds.Length ? speeds[info[23]] : info[23].ToString()));

            var v2 = new byte[16];
            BitConverter.GetBytes(port).CopyTo(v2, 0);
            BitConverter.GetBytes(16).CopyTo(v2, 4);           // Length
            BitConverter.GetBytes(0x7).CopyTo(v2, 8);          // supported protocols: USB110 | USB200 | USB300
            if (DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, v2, 16, v2, 16, out ret, IntPtr.Zero))
            {
                int protocols = BitConverter.ToInt32(v2, 8), flags = BitConverter.ToInt32(v2, 12);
                Console.WriteLine("Port supports USB3: " + ((protocols & 4) != 0) + ", device operating at SuperSpeed: " + ((flags & 1) != 0) + ", device capable of SuperSpeed: " + ((flags & 2) != 0));
            }

            byte[] cfg = GetConfig(h, port, 9);
            int total = BitConverter.ToUInt16(cfg, 2);
            cfg = GetConfig(h, port, total);
            DumpUvc(cfg);
        }
    }

    static byte[] GetConfig(SafeFileHandle h, int port, int length)
    {
        int hdr = 4 + 8;
        var req = new byte[hdr + length];
        BitConverter.GetBytes(port).CopyTo(req, 0);
        req[4] = 0x80; req[5] = 6;                               // GET_DESCRIPTOR
        BitConverter.GetBytes((ushort)(2 << 8)).CopyTo(req, 6);  // configuration descriptor 0
        BitConverter.GetBytes((ushort)length).CopyTo(req, 10);
        int ret;
        if (!DeviceIoControl(h, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, req, req.Length, req, req.Length, out ret, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "get config descriptor");
        var outp = new byte[ret - hdr];
        Array.Copy(req, hdr, outp, 0, outp.Length);
        return outp;
    }

    static void DumpUvc(byte[] d)
    {
        string fmt = null;
        int i = 0;
        while (i + 2 < d.Length && d[i] > 0)
        {
            int l = d[i], type = d[i + 1];
            if (type == 0x24 && l > 3)
            {
                int sub = d[i + 2];
                if (sub == 0x04 || sub == 0x10)
                {
                    var g = new byte[16]; Array.Copy(d, i + 5, g, 0, 16);
                    string cc = System.Text.Encoding.ASCII.GetString(g, 0, 4);
                    fmt = (sub == 0x04 ? "Uncompressed " : "Frame-based ") + cc;
                    Console.WriteLine("Format: " + fmt);
                }
                else if (sub == 0x06) { fmt = "MJPEG"; Console.WriteLine("Format: MJPEG"); }
                else if (sub == 0x05 || sub == 0x07 || sub == 0x11)
                {
                    int w = BitConverter.ToUInt16(d, i + 5), hgt = BitConverter.ToUInt16(d, i + 7);
                    int typeOff = sub == 0x11 ? 21 : 25, intOff = 26;
                    int n = d[i + typeOff];
                    var fps = new List<string>();
                    if (n == 0) fps.Add("continuous " + Math.Round(1e7 / BitConverter.ToUInt32(d, i + intOff + 4)) + "-" + Math.Round(1e7 / BitConverter.ToUInt32(d, i + intOff)) + " fps");
                    else for (int k = 0; k < n; k++) fps.Add(Math.Round(1e7 / BitConverter.ToUInt32(d, i + intOff + 4 * k)).ToString());
                    Console.WriteLine("   " + w + "x" + hgt + " @ " + string.Join("/", fps) + " fps");
                }
            }
            else if (type == 0x04 && l >= 9)
                Console.WriteLine("Interface " + d[i + 2] + " alt " + d[i + 3] + " class " + d[i + 5].ToString("X2") + " sub " + d[i + 6].ToString("X2") + " endpoints " + d[i + 4]);
            else if (type == 0x05 && l >= 7)
                Console.WriteLine("   endpoint 0x" + d[i + 2].ToString("X2") + " " + (new[] { "control", "isochronous", "bulk", "interrupt" })[d[i + 3] & 3] + " maxPacket " + (BitConverter.ToUInt16(d, i + 4) & 0x7FF) + " x" + (((BitConverter.ToUInt16(d, i + 4) >> 11) & 3) + 1));
            i += l;
        }
    }
}
