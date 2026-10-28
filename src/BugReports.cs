using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace StreamLite
{
    /// <summary>
    /// Bug reports from inside StreamLite, no account needed (4.1.2): the report goes to StreamLite's report service (a
    /// Cloudflare Worker, cloudflare/report-worker), which posts it on StreamLite's public issue list. Bad language is
    /// starred out, and screenshots are shown only once the service has checked they're family-friendly screenshots of an
    /// app or an error. A Discord name, if you leave one, goes only to the developer. Videos aren't sent: "I have a video"
    /// tells the developer to ask you for it on Discord.
    /// </summary>
    internal static class BugReports
    {
        public const string Service = "https://streamlite-reports.nermalyt.workers.dev/report";
        public const int MaxPictures = 4, MaxPictureBytes = 3 * 1024 * 1024, MaxSide = 1920;

        public sealed class Report
        {
            public string Kind, Area, Title, What, Steps, Discord, Details;
            public bool Video;
            public List<string> Pictures = new List<string>();
        }

        public sealed class Result { public int Number, LeftOut; public string Url, Error; }

        /// <summary>
        /// A picture as it's sent: drawn again as a fresh PNG (or JPEG when that's much smaller) at most MaxSide pixels
        /// across, so nothing hidden in the file - camera, place, names - goes with it. null = not a picture.
        /// </summary>
        public static byte[] Prepare(string path)
        {
            try
            {
                using (var src = System.Drawing.Image.FromFile(path))
                {
                    double s = Math.Min(1.0, (double)MaxSide / Math.Max(src.Width, src.Height));
                    int w = Math.Max(1, (int)Math.Round(src.Width * s)), h = Math.Max(1, (int)Math.Round(src.Height * s));
                    using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.Black);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        using (var png = new MemoryStream())
                        {
                            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                            if (png.Length <= 1536 * 1024) return png.ToArray();
                            var jpeg = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg");
                            using (var jpg = new MemoryStream())
                            using (var q = new System.Drawing.Imaging.EncoderParameters(1))
                            {
                                q.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                                bmp.Save(jpg, jpeg, q);
                                return jpg.Length < png.Length ? jpg.ToArray() : png.ToArray();
                            }
                        }
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Discord names: today's (2-32 of a-z 0-9 _ .) or the old Name#1234. Empty is fine.</summary>
        public static bool DiscordOk(string s)
        {
            s = (s ?? "").Trim().TrimStart('@');
            if (s.Length == 0) return true;
            return (Regex.IsMatch(s, @"^[A-Za-z0-9_.]{2,32}$") && !s.Contains("..")) || Regex.IsMatch(s, @"^[^@#:`]{2,32}#\d{4}$");
        }

        /// <summary>Sends it (blocks; call it off the window's thread).</summary>
        public static Result Send(Report r)
        {
            var pics = new List<string>();
            foreach (var p in r.Pictures)
            {
                var b = Prepare(p);
                if (b == null) return new Result { Error = Path.GetFileName(p) + " couldn't be opened as a picture." };
                if (b.Length > MaxPictureBytes) return new Result { Error = Path.GetFileName(p) + " is too big - a smaller screenshot, please." };
                pics.Add(Convert.ToBase64String(b));
            }
            var body = new Dictionary<string, object>
            {
                { "kind", r.Kind }, { "area", r.Area }, { "title", r.Title }, { "what", r.What }, { "steps", r.Steps ?? "" },
                { "discord", (r.Discord ?? "").Trim().TrimStart('@') }, { "video", r.Video }, { "details", r.Details ?? "" },
                { "version", Program.Version.ToString() }, { "system", Updater.SystemDescription() }, { "images", pics.ToArray() },
            };
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try
            {
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    wc.Headers[HttpRequestHeader.UserAgent] = "StreamLite/" + Program.Version;
                    var d = js.DeserializeObject(wc.UploadString(Service, js.Serialize(body))) as Dictionary<string, object>;
                    object n, u, l;
                    return new Result
                    {
                        Number = d != null && d.TryGetValue("number", out n) ? Convert.ToInt32(n) : 0,
                        Url = d != null && d.TryGetValue("url", out u) ? u as string : null,
                        LeftOut = d != null && d.TryGetValue("leftOut", out l) ? Convert.ToInt32(l) : 0,
                    };
                }
            }
            catch (WebException ex)
            {
                string msg = null;
                try { using (var sr = new StreamReader(ex.Response.GetResponseStream())) { var d = js.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>; object e; if (d != null && d.TryGetValue("error", out e)) msg = e as string; } } catch { }
                return new Result { Error = msg ?? "The report service couldn't be reached (" + ex.Message + ")." };
            }
            catch (Exception ex) { return new Result { Error = ex.Message }; }
        }
    }
}
