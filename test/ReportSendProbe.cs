using System;
using System.IO;
using StreamLite;

// Sends one real report through StreamLite's own code to the report service (run it while the service is pointed at
// the private repository, so nothing lands on the public list): a screenshot of StreamLite, a photo that must be left
// out, a Discord name and "I have a video". Usage: run.ps1 ReportSendProbe <screenshot> <photo>
class ReportSendProbe
{
    static void Main(string[] args)
    {
        var r = new BugReports.Report
        {
            Kind = "Bug", Area = "Something else",
            Title = "[test] Report service check - please ignore",
            What = "A test from the setup of the report service. This damn thing should stay readable.",
            Steps = "1. Send a report\n2. Check the issue",
            Discord = "streamlite.test", Video = true, Details = "probe",
        };
        foreach (var a in args) r.Pictures.Add(a);
        var res = BugReports.Send(r);
        Console.WriteLine(res.Error != null ? "error: " + res.Error : "sent #" + res.Number + " " + res.Url + " (left out: " + res.LeftOut + ")");
        Environment.Exit(res.Error != null ? 1 : 0);
    }
}
