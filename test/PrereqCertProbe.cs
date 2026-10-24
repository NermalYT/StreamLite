// Which VB-Audio certificates the installer would trust for a downloaded package folder (read-only). Usage: PrereqCertProbe <folder>
using System;
using StreamLite;
class PrereqCertProbe { static void Main(string[] a) { foreach (var c in Prerequisites.VbAudioCertificates(a[0])) Console.WriteLine(c.Subject + "  " + c.Thumbprint + "  until " + c.NotAfter.ToString("yyyy-MM-dd")); } }
