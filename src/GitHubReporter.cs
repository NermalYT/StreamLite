using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StreamLite
{
    /// <summary>
    /// Bug reports from inside StreamLite (4.1.1): you sign in with GitHub once (GitHub's device sign-in - a short code you
    /// type on github.com, the only step outside StreamLite), and the report goes straight onto StreamLite's issue list
    /// under your name. The sign-in is kept on this PC only, encrypted for your Windows account (DPAPI).
    /// ClientId is the public id of StreamLite's GitHub App (Issues: read and write, installed on the repository, device
    /// flow on); empty = not set up yet, the report opens GitHub's form in the browser instead.
    /// </summary>
    internal static class GitHubReporter
    {
        public const string ClientId = "";
        public const string Repo = "NermalYT/StreamLite";
        public static bool CanSignIn { get { return ClientId.Length > 0; } }

        static string TokenFile { get { return Path.Combine(AppSettings.Folder, "github.bin"); } }

        static WebClient Client(string token = null)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "StreamLite";
            wc.Headers[HttpRequestHeader.Accept] = "application/json";
            if (token != null) wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            return wc;
        }

        static Dictionary<string, object> Json(string s) { return new JavaScriptSerializer().DeserializeObject(s) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }

        /// <summary>The kept sign-in (null when there's none).</summary>
        public static string Token
        {
            get
            {
                try { return File.Exists(TokenFile) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser)) : null; }
                catch { return null; }
            }
        }

        static void Keep(string token)
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        }

        public static void SignOut() { try { File.Delete(TokenFile); } catch { } }

        /// <summary>Who's signed in (GitHub login), or null.</summary>
        public static string Login(string token)
        {
            try { using (var wc = Client(token)) return Str(Json(wc.DownloadString("https://api.github.com/user")), "login"); }
            catch { return null; }
        }

        public sealed class DeviceCode { public string Code, Url, DeviceId; public int Interval, ExpiresIn; }

        /// <summary>Step 1: the code to type on github.com/login/device.</summary>
        public static DeviceCode Start()
        {
            using (var wc = Client())
            {
                var d = Json(wc.UploadString("https://github.com/login/device/code", "client_id=" + Uri.EscapeDataString(ClientId)));
                return new DeviceCode { Code = Str(d, "user_code"), Url = Str(d, "verification_uri") ?? "https://github.com/login/device", DeviceId = Str(d, "device_code"),
                    Interval = Math.Max(5, int.Parse(Str(d, "interval") ?? "5")), ExpiresIn = int.Parse(Str(d, "expires_in") ?? "900") };
            }
        }

        /// <summary>Step 2: waits until you've typed the code (null: cancelled, expired or refused).</summary>
        public static string Wait(DeviceCode c, Func<bool> cancelled)
        {
            var until = DateTime.UtcNow.AddSeconds(c.ExpiresIn);
            int interval = c.Interval;
            while (DateTime.UtcNow < until && !cancelled())
            {
                Thread.Sleep(interval * 1000);
                if (cancelled()) return null;
                try
                {
                    using (var wc = Client())
                    {
                        var d = Json(wc.UploadString("https://github.com/login/oauth/access_token", "client_id=" + Uri.EscapeDataString(ClientId) +
                            "&device_code=" + Uri.EscapeDataString(c.DeviceId) + "&grant_type=urn:ietf:params:oauth:grant-type:device_code"));
                        string token = Str(d, "access_token"), err = Str(d, "error");
                        if (token != null) { Keep(token); return token; }
                        if (err == "slow_down") interval += 5;
                        else if (err != null && err != "authorization_pending") return null;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Posts the report. Its address (https://github.com/.../issues/N), or throws with GitHub's message.</summary>
        public static string Post(string token, string title, string body, string label)
        {
            var issue = new Dictionary<string, object> { { "title", title }, { "body", body } };
            if (!string.IsNullOrEmpty(label)) issue["labels"] = new[] { label };
            using (var wc = Client(token))
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                try { return Str(Json(wc.UploadString("https://api.github.com/repos/" + Repo + "/issues", new JavaScriptSerializer().Serialize(issue))), "html_url"); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.Unauthorized) SignOut();   // signed out on github.com: sign in again
                    string msg = null;
                    try { using (var sr = new StreamReader(ex.Response.GetResponseStream())) msg = Str(Json(sr.ReadToEnd()), "message"); } catch { }
                    throw new InvalidOperationException(msg ?? ex.Message);
                }
            }
        }
    }
}
